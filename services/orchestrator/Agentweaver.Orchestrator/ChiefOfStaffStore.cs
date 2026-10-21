using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Stores private Chief of Staff conversations and Priorities for one caller.
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="bullet">
/// <item>Every row is scoped to tenant, issuer, and subject. No read returns another caller's rows.</item>
/// <item>The store never writes run, session, gate, or request tables. Editing a Priority never changes execution.</item>
/// <item>Each Priority change must name the current revision; a stale revision returns <c>409 priority_revision_conflict</c>.</item>
/// <item>Links store owner identity only. Reads resolve them against the owner tables; the caller filters by current access.</item>
/// </list>
/// </remarks>
public sealed class ChiefOfStaffStore
{
    /// <summary>The largest number of Priorities one caller can hold.</summary>
    public const int MaximumPriorities = 100;

    /// <summary>The largest number of links on one Priority.</summary>
    public const int MaximumLinks = 50;

    /// <summary>The largest number of checklist items on one Priority.</summary>
    public const int MaximumChecklistItems = 50;

    /// <summary>The Priority states, in display order.</summary>
    public static readonly ImmutableArray<string> PriorityStates = ["active", "achieved", "dropped"];

    /// <summary>The supported link kinds.</summary>
    public static readonly ImmutableArray<string> LinkKinds = ["run", "conversation", "issue", "pull_request", "artifact"];

    private const int CursorVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Derives the run state exactly as <see cref="ProjectRunReadStore"/> does, so every surface agrees.</summary>
    private static string RunStateSql(string run, string root) => $"""
        CASE
            WHEN {run}.run_id IS NULL THEN NULL
            WHEN {run}.execution_state IN ('failed', 'indeterminate', 'cancelled') THEN {run}.execution_state
            WHEN {root}.lifecycle_state = 'completed' THEN 'completed'
            WHEN {root}.lifecycle_state = 'cancelled' THEN 'cancelled'
            WHEN {root}.turn_state IN ('active', 'presenting') THEN 'active'
            WHEN {root}.turn_state = 'blocked' THEN 'blocked'
            ELSE 'idle'
        END
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    /// <summary>Creates a store over the coordination owner schema.</summary>
    public ChiefOfStaffStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
    }

    /// <summary>
    /// Binds a run that the caller started to a private conversation. Repeating the call returns the same entry.
    /// </summary>
    /// <exception cref="CoordinationException">
    /// <c>404 conversation_run_not_found</c> when the caller did not start the run in this tenant.
    /// </exception>
    public async Task<ChiefOfStaffConversation> BindConversationAsync(
        CoordinationActor actor, string tenantId, string projectId, string runId, CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        CoordinationIdentity.ValidateIdentity(runId, "run_id");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.chief_of_staff_conversations
                (conversation_id, tenant_id, owner_issuer, owner_subject, project_id, run_id)
            SELECT @conversation, q.tenant_id, q.started_by_issuer, q.started_by_subject, q.project_id, q.run_id
            FROM {_schema}.project_run_requests AS q
            WHERE q.tenant_id = @tenant AND q.project_id = @project AND q.run_id = @run
                AND q.started_by_issuer = @issuer AND q.started_by_subject = @subject
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection))
        {
            insert.Parameters.AddWithValue("conversation", NpgsqlDbType.Varchar, NewId());
            AddOwner(insert, actor, tenantId);
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var rows = await ReadConversationsAsync(connection, actor, tenantId, null, projectId, runId, null, 1, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1
            ? rows[0]
            : throw new CoordinationException("conversation_run_not_found", StatusCodes.Status404NotFound);
    }

    /// <summary>Reads one of the caller's conversations, or null when the caller does not own it.</summary>
    public async Task<ChiefOfStaffConversation?> GetConversationAsync(
        CoordinationActor actor, string tenantId, string conversationId, CancellationToken cancellationToken)
    {
        if (!IsOpaqueId(conversationId))
            return null;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadConversationsAsync(
            connection, actor, tenantId, conversationId, null, null, null, 1, cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>Reads one newest-first page of the caller's conversations with full project fields.</summary>
    /// <remarks>The caller must redact entries whose project it can no longer read.</remarks>
    /// <exception cref="CoordinationException">The cursor or page size is invalid.</exception>
    public async Task<ChiefOfStaffConversationPage> ListConversationsAsync(
        CoordinationActor actor, string tenantId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > ProjectRunReadStore.MaximumPageSize)
            throw new CoordinationException("page_size_invalid", StatusCodes.Status400BadRequest);
        var after = cursor is null ? null : DecodeCursor(cursor, pageSize);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadConversationsAsync(
            connection, actor, tenantId, null, null, null, after, pageSize + 1, cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize).ToImmutableArray();
        return new ChiefOfStaffConversationPage(
            items,
            hasMore ? EncodeCursor(new ConversationCursor(
                CursorVersion, pageSize, items[^1].CreatedAt, items[^1].ConversationId)) : null);
    }

    /// <summary>Reads the caller's Priorities in order, with links resolved against owner tables.</summary>
    /// <remarks>Links come back with <c>available</c> or <c>missing</c> access; the caller applies current authority.</remarks>
    public async Task<ChiefOfStaffPriorityList> ListPrioritiesAsync(
        CoordinationActor actor, string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadListAsync(connection, null, actor, tenantId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one of the caller's Priorities, or null when the caller does not own it.</summary>
    public async Task<ChiefOfStaffPriority?> GetPriorityAsync(
        CoordinationActor actor, string tenantId, string priorityId, CancellationToken cancellationToken)
    {
        if (!IsOpaqueId(priorityId))
            return null;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var list = await ReadListAsync(connection, null, actor, tenantId, priorityId, cancellationToken)
            .ConfigureAwait(false);
        return list.Items.SingleOrDefault();
    }

    /// <summary>
    /// Creates a Priority at the end of the caller's list. A retry with the same key and body returns the same Priority.
    /// </summary>
    /// <exception cref="CoordinationException">
    /// <c>409 idempotency_conflict</c> for the same key with another body, or <c>409 priority_limit_reached</c>.
    /// </exception>
    public async Task<(ChiefOfStaffPriority Priority, bool Created)> CreatePriorityAsync(
        CoordinationActor actor, string tenantId, string idempotencyKey, CreateChiefOfStaffPriorityRequest request,
        CancellationToken cancellationToken)
    {
        var checklist = NormalizeChecklist(request.Checklist ?? []);
        var outcome = ValidateOutcome(request.Outcome);
        var notes = ValidateNotes(request.Notes);
        var keyHash = Hash(idempotencyKey);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            outcome,
            notes,
            checklist = checklist.Select(item => new { item.Text, item.Done })
        }, JsonOptions));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockListAsync(connection, transaction, actor, tenantId, cancellationToken).ConfigureAwait(false);

        await using (var existing = new NpgsqlCommand($"""
            SELECT priority_id, create_fingerprint FROM {_schema}.chief_of_staff_priorities
            WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
                AND create_key_hash = @key
            """, connection, transaction))
        {
            AddOwner(existing, actor, tenantId);
            existing.Parameters.AddWithValue("key", NpgsqlDbType.Char, keyHash);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var priorityId = reader.GetString(0);
                if (reader.GetString(1) != fingerprint)
                    throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
                await reader.DisposeAsync().ConfigureAwait(false);
                var replay = await ReadListAsync(connection, transaction, actor, tenantId, priorityId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (replay.Items.Single(), false);
            }
        }

        var id = NewId();
        await using (var insert = new NpgsqlCommand($"""
            WITH current AS (
                SELECT count(*) AS total, coalesce(max(position) + 1, 0) AS next_position
                FROM {_schema}.chief_of_staff_priorities
                WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            )
            INSERT INTO {_schema}.chief_of_staff_priorities
                (priority_id, tenant_id, owner_issuer, owner_subject, outcome, notes, checklist, position,
                 create_key_hash, create_fingerprint)
            SELECT @id, @tenant, @issuer, @subject, @outcome, @notes, @checklist, current.next_position,
                @key, @fingerprint
            FROM current
            WHERE current.total < @maximum
            """, connection, transaction))
        {
            AddOwner(insert, actor, tenantId);
            insert.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, id);
            insert.Parameters.AddWithValue("outcome", NpgsqlDbType.Varchar, outcome);
            insert.Parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)notes ?? DBNull.Value);
            insert.Parameters.AddWithValue("checklist", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(checklist, JsonOptions));
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Char, keyHash);
            insert.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Char, fingerprint);
            insert.Parameters.AddWithValue("maximum", NpgsqlDbType.Integer, MaximumPriorities);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException("priority_limit_reached", StatusCodes.Status409Conflict);
        }
        await BumpOrderRevisionAsync(connection, transaction, actor, tenantId, cancellationToken).ConfigureAwait(false);
        var created = await ReadListAsync(connection, transaction, actor, tenantId, id, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (created.Items.Single(), true);
    }

    /// <summary>Changes Priority content at the expected revision and increments the revision.</summary>
    /// <exception cref="CoordinationException">
    /// <c>404 priority_not_found</c>, <c>409 priority_revision_conflict</c>, or <c>400</c> for invalid content.
    /// </exception>
    public async Task<ChiefOfStaffPriority> UpdatePriorityAsync(
        CoordinationActor actor, string tenantId, string priorityId, UpdateChiefOfStaffPriorityRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = request.Outcome is null ? null : ValidateOutcome(request.Outcome);
        var clearNotes = request.Notes is { Length: 0 };
        var notes = clearNotes ? null : ValidateNotes(request.Notes);
        if (request.State is not null && !PriorityStates.Contains(request.State))
            throw new CoordinationException("priority_state_invalid", StatusCodes.Status400BadRequest);
        var checklist = request.Checklist is null ? (ImmutableArray<ChiefOfStaffChecklistItem>?)null
            : NormalizeChecklist(request.Checklist.Value);
        if (outcome is null && request.Notes is null && request.State is null && checklist is null)
            throw new CoordinationException("priority_update_empty", StatusCodes.Status400BadRequest);

        return await MutatePriorityAsync(actor, tenantId, priorityId, request.ExpectedRevision,
            async (connection, transaction) =>
            {
                await using var update = new NpgsqlCommand($"""
                    UPDATE {_schema}.chief_of_staff_priorities SET
                        outcome = coalesce(@outcome, outcome),
                        notes = CASE WHEN @clearNotes THEN NULL ELSE coalesce(@notes, notes) END,
                        state = coalesce(@state, state),
                        checklist = coalesce(@checklist, checklist)
                    WHERE priority_id = @id
                    """, connection, transaction);
                update.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
                update.Parameters.AddWithValue("outcome", NpgsqlDbType.Varchar, (object?)outcome ?? DBNull.Value);
                update.Parameters.AddWithValue("clearNotes", NpgsqlDbType.Boolean, clearNotes);
                update.Parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)notes ?? DBNull.Value);
                update.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, (object?)request.State ?? DBNull.Value);
                update.Parameters.AddWithValue("checklist", NpgsqlDbType.Jsonb,
                    checklist is null ? DBNull.Value : JsonSerializer.Serialize(checklist.Value, JsonOptions));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the order of the caller's Priorities at the expected order revision.</summary>
    /// <exception cref="CoordinationException">
    /// <c>409 priority_order_conflict</c> for a stale revision, or <c>400 priority_order_invalid</c> when the
    /// identifiers are not exactly the caller's Priorities.
    /// </exception>
    public async Task<ChiefOfStaffPriorityList> ReorderPrioritiesAsync(
        CoordinationActor actor, string tenantId, ReorderChiefOfStaffPrioritiesRequest request,
        CancellationToken cancellationToken)
    {
        var ids = request.PriorityIds.IsDefault ? [] : request.PriorityIds;
        if (ids.Length > MaximumPriorities || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => !IsOpaqueId(id)))
            throw new CoordinationException("priority_order_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var revision = await LockListAsync(connection, transaction, actor, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (revision != request.ExpectedOrderRevision)
            throw new CoordinationException("priority_order_conflict", StatusCodes.Status409Conflict);
        await using (var update = new NpgsqlCommand($"""
            WITH ordered AS (
                SELECT id, ordinality - 1 AS position FROM unnest(@ids::varchar[]) WITH ORDINALITY AS t(id, ordinality)
            ), owned AS (
                SELECT priority_id FROM {_schema}.chief_of_staff_priorities
                WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            )
            UPDATE {_schema}.chief_of_staff_priorities AS p SET position = ordered.position
            FROM ordered
            WHERE p.priority_id = ordered.id
                AND (SELECT count(*) FROM owned) = cardinality(@ids::varchar[])
                AND NOT EXISTS (SELECT 1 FROM owned WHERE owned.priority_id <> ALL(@ids::varchar[]))
            """, connection, transaction))
        {
            AddOwner(update, actor, tenantId);
            update.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar, ids.ToArray());
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != ids.Length)
                throw new CoordinationException("priority_order_invalid", StatusCodes.Status400BadRequest);
        }
        await BumpOrderRevisionAsync(connection, transaction, actor, tenantId, cancellationToken).ConfigureAwait(false);
        var list = await ReadListAsync(connection, transaction, actor, tenantId, null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }

    /// <summary>Adds a typed link at the expected Priority revision and increments the revision.</summary>
    /// <remarks>
    /// The caller must check current read access to <see cref="AddChiefOfStaffPriorityLinkRequest.ProjectId"/>
    /// first. A conversation link must name one of the caller's conversations in that project.
    /// </remarks>
    /// <exception cref="CoordinationException">
    /// <c>400 priority_link_invalid</c>, <c>404 priority_link_target_not_found</c>,
    /// <c>409 priority_link_exists</c>, <c>409 priority_link_limit_reached</c>, or a revision conflict.
    /// </exception>
    public async Task<ChiefOfStaffPriority> AddLinkAsync(
        CoordinationActor actor, string tenantId, string priorityId, AddChiefOfStaffPriorityLinkRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLink(request);
        return await MutatePriorityAsync(actor, tenantId, priorityId, request.ExpectedRevision,
            async (connection, transaction) =>
            {
                if (request.Kind == "conversation")
                {
                    await using var owned = new NpgsqlCommand($"""
                        SELECT 1 FROM {_schema}.chief_of_staff_conversations
                        WHERE conversation_id = @conversation AND project_id = @project AND tenant_id = @tenant
                            AND owner_issuer = @issuer AND owner_subject = @subject
                        """, connection, transaction);
                    AddOwner(owned, actor, tenantId);
                    owned.Parameters.AddWithValue("conversation", NpgsqlDbType.Varchar, request.ConversationId!);
                    owned.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.ProjectId);
                    if (await owned.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                        throw new CoordinationException("priority_link_target_not_found", StatusCodes.Status404NotFound);
                }
                await using var insert = new NpgsqlCommand($"""
                    INSERT INTO {_schema}.chief_of_staff_priority_links
                        (priority_id, link_id, kind, project_id, run_id, conversation_id, item_number, artifact_id)
                    SELECT @id, @link, @kind, @project, @run, @conversation, @number, @artifact
                    WHERE (SELECT count(*) FROM {_schema}.chief_of_staff_priority_links WHERE priority_id = @id) < @maximum
                    ON CONFLICT DO NOTHING
                    RETURNING 1
                    """, connection, transaction);
                insert.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
                insert.Parameters.AddWithValue("link", NpgsqlDbType.Varchar, NewId());
                insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, request.Kind);
                insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.ProjectId);
                insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)request.RunId ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "conversation", NpgsqlDbType.Varchar, (object?)request.ConversationId ?? DBNull.Value);
                insert.Parameters.AddWithValue("number", NpgsqlDbType.Bigint, (object?)request.Number ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "artifact", NpgsqlDbType.Varchar, (object?)request.ArtifactId ?? DBNull.Value);
                insert.Parameters.AddWithValue("maximum", NpgsqlDbType.Integer, MaximumLinks);
                if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    return;
                await using var count = new NpgsqlCommand(
                    $"SELECT count(*) FROM {_schema}.chief_of_staff_priority_links WHERE priority_id = @id",
                    connection, transaction);
                count.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
                throw (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= MaximumLinks
                    ? new CoordinationException("priority_link_limit_reached", StatusCodes.Status409Conflict)
                    : new CoordinationException("priority_link_exists", StatusCodes.Status409Conflict);
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a link at the expected Priority revision and increments the revision.</summary>
    /// <exception cref="CoordinationException"><c>404 priority_link_not_found</c> or a revision conflict.</exception>
    public async Task<ChiefOfStaffPriority> RemoveLinkAsync(
        CoordinationActor actor, string tenantId, string priorityId, string linkId, long expectedRevision,
        CancellationToken cancellationToken) =>
        await MutatePriorityAsync(actor, tenantId, priorityId, expectedRevision,
            async (connection, transaction) =>
            {
                await using var delete = new NpgsqlCommand($"""
                    DELETE FROM {_schema}.chief_of_staff_priority_links WHERE priority_id = @id AND link_id = @link
                    """, connection, transaction);
                delete.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
                delete.Parameters.AddWithValue("link", NpgsqlDbType.Varchar, linkId);
                if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new CoordinationException("priority_link_not_found", StatusCodes.Status404NotFound);
            }, cancellationToken).ConfigureAwait(false);

    /// <summary>Locks one owned Priority, checks its revision, applies a change, and increments the revision.</summary>
    private async Task<ChiefOfStaffPriority> MutatePriorityAsync(
        CoordinationActor actor, string tenantId, string priorityId, long expectedRevision,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> change, CancellationToken cancellationToken)
    {
        if (!IsOpaqueId(priorityId))
            throw new CoordinationException("priority_not_found", StatusCodes.Status404NotFound);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var select = new NpgsqlCommand($"""
            SELECT revision FROM {_schema}.chief_of_staff_priorities
            WHERE priority_id = @id AND tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwner(select, actor, tenantId);
            select.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
            var revision = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (revision is null)
                throw new CoordinationException("priority_not_found", StatusCodes.Status404NotFound);
            if ((long)revision != expectedRevision)
                throw new CoordinationException("priority_revision_conflict", StatusCodes.Status409Conflict);
        }
        await change(connection, transaction).ConfigureAwait(false);
        await using (var bump = new NpgsqlCommand($"""
            UPDATE {_schema}.chief_of_staff_priorities
            SET revision = revision + 1, updated_at = clock_timestamp()
            WHERE priority_id = @id
            """, connection, transaction))
        {
            bump.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, priorityId);
            await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var list = await ReadListAsync(connection, transaction, actor, tenantId, priorityId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list.Items.Single();
    }

    /// <summary>Creates the caller's list row when needed, locks it, and returns its order revision.</summary>
    private async Task<long> LockListAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CoordinationActor actor, string tenantId,
        CancellationToken cancellationToken)
    {
        await using (var ensure = new NpgsqlCommand($"""
            INSERT INTO {_schema}.chief_of_staff_priority_lists (tenant_id, owner_issuer, owner_subject)
            VALUES (@tenant, @issuer, @subject) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddOwner(ensure, actor, tenantId);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var select = new NpgsqlCommand($"""
            SELECT order_revision FROM {_schema}.chief_of_staff_priority_lists
            WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            FOR UPDATE
            """, connection, transaction);
        AddOwner(select, actor, tenantId);
        return (long)(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task BumpOrderRevisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CoordinationActor actor, string tenantId,
        CancellationToken cancellationToken)
    {
        await using var bump = new NpgsqlCommand($"""
            UPDATE {_schema}.chief_of_staff_priority_lists SET order_revision = order_revision + 1
            WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            """, connection, transaction);
        AddOwner(bump, actor, tenantId);
        await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the caller's Priorities, or one Priority, with links resolved against owner tables.</summary>
    private async Task<ChiefOfStaffPriorityList> ReadListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CoordinationActor actor, string tenantId,
        string? priorityId, CancellationToken cancellationToken)
    {
        long orderRevision = 0;
        await using (var revision = new NpgsqlCommand($"""
            SELECT order_revision FROM {_schema}.chief_of_staff_priority_lists
            WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
            """, connection, transaction))
        {
            AddOwner(revision, actor, tenantId);
            if (await revision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value)
                orderRevision = value;
        }

        var priorities = new List<(string Id, string Outcome, string? Notes, string State, string Checklist,
            int Position, long Revision, DateTimeOffset Created, DateTimeOffset Updated)>();
        await using (var select = new NpgsqlCommand($"""
            SELECT priority_id, outcome, notes, state, checklist::text, position, revision, created_at, updated_at
            FROM {_schema}.chief_of_staff_priorities
            WHERE tenant_id = @tenant AND owner_issuer = @issuer AND owner_subject = @subject
                AND (@id::varchar IS NULL OR priority_id = @id)
            ORDER BY position, priority_id
            """, connection, transaction))
        {
            AddOwner(select, actor, tenantId);
            select.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, (object?)priorityId ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                priorities.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        }

        var links = new Dictionary<string, List<ChiefOfStaffPriorityLink>>(StringComparer.Ordinal);
        await using (var select = new NpgsqlCommand($"""
            SELECT l.priority_id, l.link_id, l.kind, l.project_id, l.run_id, l.conversation_id, l.item_number,
                l.artifact_id, l.created_at,
                CASE l.kind
                    WHEN 'run' THEN r.run_id IS NOT NULL
                    WHEN 'conversation' THEN c.conversation_id IS NOT NULL
                    ELSE true
                END AS found,
                CASE l.kind
                    WHEN 'run' THEN {RunStateSql("r", "root")}
                    WHEN 'conversation' THEN coalesce({RunStateSql("cr", "croot")}, cq.state)
                    ELSE 'unavailable'
                END AS state,
                c.run_id
            FROM {_schema}.chief_of_staff_priority_links AS l
            JOIN {_schema}.chief_of_staff_priorities AS p ON p.priority_id = l.priority_id
            LEFT JOIN {_schema}.accepted_runs AS r
                ON l.kind = 'run' AND r.tenant_id = p.tenant_id AND r.project_id = l.project_id AND r.run_id = l.run_id
            LEFT JOIN {_schema}.coordination_sessions AS root
                ON root.project_id = r.project_id AND root.run_id = r.run_id AND root.parent_session_id IS NULL
            LEFT JOIN {_schema}.chief_of_staff_conversations AS c
                ON l.kind = 'conversation' AND c.conversation_id = l.conversation_id AND c.project_id = l.project_id
                AND c.tenant_id = p.tenant_id AND c.owner_issuer = p.owner_issuer AND c.owner_subject = p.owner_subject
            LEFT JOIN {_schema}.project_run_requests AS cq ON cq.project_id = c.project_id AND cq.run_id = c.run_id
            LEFT JOIN {_schema}.accepted_runs AS cr
                ON cr.tenant_id = c.tenant_id AND cr.project_id = c.project_id AND cr.run_id = c.run_id
            LEFT JOIN {_schema}.coordination_sessions AS croot
                ON croot.project_id = cr.project_id AND croot.run_id = cr.run_id AND croot.parent_session_id IS NULL
            WHERE p.tenant_id = @tenant AND p.owner_issuer = @issuer AND p.owner_subject = @subject
                AND (@id::varchar IS NULL OR p.priority_id = @id)
            ORDER BY l.created_at, l.link_id
            """, connection, transaction))
        {
            AddOwner(select, actor, tenantId);
            select.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, (object?)priorityId ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var found = reader.GetBoolean(9);
                var kind = reader.GetString(2);
                var link = new ChiefOfStaffPriorityLink(
                    reader.GetString(1), kind, found ? "available" : "missing",
                    reader.GetString(3),
                    reader.IsDBNull(4) ? (kind == "conversation" && !reader.IsDBNull(11) ? reader.GetString(11) : null)
                        : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    found && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                    reader.GetFieldValue<DateTimeOffset>(8));
                if (!links.TryGetValue(reader.GetString(0), out var bucket))
                    links[reader.GetString(0)] = bucket = [];
                bucket.Add(link);
            }
        }

        return new ChiefOfStaffPriorityList(orderRevision, [.. priorities.Select(p => new ChiefOfStaffPriority(
            p.Id, p.Outcome, p.Notes, p.State,
            JsonSerializer.Deserialize<ImmutableArray<ChiefOfStaffChecklistItem>>(p.Checklist, JsonOptions),
            p.Position, p.Revision,
            links.TryGetValue(p.Id, out var items) ? [.. items] : [],
            p.Created, p.Updated))]);
    }

    /// <summary>Reads conversation entries owned by the caller, with run title and state.</summary>
    private async Task<List<ChiefOfStaffConversation>> ReadConversationsAsync(
        NpgsqlConnection connection, CoordinationActor actor, string tenantId, string? conversationId,
        string? projectId, string? runId, ConversationCursor? after, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT c.conversation_id, c.project_id, c.run_id, q.root_session_id, q.title, q.state,
                {RunStateSql("r", "root")}, c.created_at
            FROM {_schema}.chief_of_staff_conversations AS c
            JOIN {_schema}.project_run_requests AS q ON q.project_id = c.project_id AND q.run_id = c.run_id
            LEFT JOIN {_schema}.accepted_runs AS r
                ON r.tenant_id = c.tenant_id AND r.project_id = c.project_id AND r.run_id = c.run_id
            LEFT JOIN {_schema}.coordination_sessions AS root
                ON root.project_id = r.project_id AND root.run_id = r.run_id AND root.parent_session_id IS NULL
            WHERE c.tenant_id = @tenant AND c.owner_issuer = @issuer AND c.owner_subject = @subject
                AND (@conversation::varchar IS NULL OR c.conversation_id = @conversation)
                AND (@project::varchar IS NULL OR (c.project_id = @project AND c.run_id = @run))
                AND (@afterCreated::timestamptz IS NULL
                    OR (c.created_at, c.conversation_id) < (@afterCreated, @afterId::varchar))
            ORDER BY c.created_at DESC, c.conversation_id DESC
            LIMIT @limit
            """, connection);
        AddOwner(command, actor, tenantId);
        command.Parameters.AddWithValue("conversation", NpgsqlDbType.Varchar, (object?)conversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterCreated", NpgsqlDbType.TimestampTz, (object?)after?.CreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("afterId", NpgsqlDbType.Varchar, (object?)after?.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        var rows = new List<ChiefOfStaffConversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new ChiefOfStaffConversation(
                reader.GetString(0), "available", reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    /// <summary>Checks link shape: identifiers are valid and only the fields for the kind are set.</summary>
    private static void ValidateLink(AddChiefOfStaffPriorityLinkRequest request)
    {
        static bool Id(string? value) => value is not null && IsIdentity(value);
        var valid = request.Kind switch
        {
            "run" => Id(request.RunId) && request.ConversationId is null && request.Number is null && request.ArtifactId is null,
            "conversation" => request.ConversationId is not null && IsOpaqueId(request.ConversationId) &&
                request.RunId is null && request.Number is null && request.ArtifactId is null,
            "issue" or "pull_request" => request.Number is > 0 && request.RunId is null &&
                request.ConversationId is null && request.ArtifactId is null,
            "artifact" => Id(request.RunId) && Id(request.ArtifactId) && request.ConversationId is null &&
                request.Number is null,
            _ => false
        };
        if (!valid || !Id(request.ProjectId))
            throw new CoordinationException("priority_link_invalid", StatusCodes.Status400BadRequest);
    }

    private static string ValidateOutcome(string? outcome)
    {
        var value = outcome?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 200)
            throw new CoordinationException("priority_outcome_invalid", StatusCodes.Status400BadRequest);
        return value;
    }

    private static string? ValidateNotes(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
            return null;
        if (Encoding.UTF8.GetByteCount(notes) > 16384)
            throw new CoordinationException("priority_notes_invalid", StatusCodes.Status400BadRequest);
        return notes;
    }

    /// <summary>Validates checklist items and assigns identifiers to new items.</summary>
    private static ImmutableArray<ChiefOfStaffChecklistItem> NormalizeChecklist(
        ImmutableArray<ChiefOfStaffChecklistItem> items)
    {
        if (items.IsDefault)
            return [];
        if (items.Length > MaximumChecklistItems)
            throw new CoordinationException("priority_checklist_invalid", StatusCodes.Status400BadRequest);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<ChiefOfStaffChecklistItem>(items.Length);
        foreach (var item in items)
        {
            var text = item?.Text?.Trim();
            var id = item?.ItemId ?? NewId();
            if (item is null || string.IsNullOrEmpty(text) || text.Length > 500 || !IsOpaqueId(id) || !seen.Add(id))
                throw new CoordinationException("priority_checklist_invalid", StatusCodes.Status400BadRequest);
            result.Add(new ChiefOfStaffChecklistItem(id, text, item.Done));
        }
        return result.MoveToImmutable();
    }

    private static void AddOwner(NpgsqlCommand command, CoordinationActor actor, string tenantId)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Accepts owner-assigned identifiers: 1–64 ASCII letters, digits, or <c>-</c>.</summary>
    internal static bool IsOpaqueId(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsIdentity(string value) =>
        value.Length is >= 1 and <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static string EncodeCursor(ConversationCursor cursor) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ConversationCursor DecodeCursor(string value, int pageSize)
    {
        if (value.Length is 0 or > 1024)
            throw new CoordinationException("cursor_invalid", StatusCodes.Status400BadRequest);
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch { 0 => "", 2 => "==", 3 => "=", _ => throw new FormatException() };
            var cursor = JsonSerializer.Deserialize<ConversationCursor>(Convert.FromBase64String(base64), JsonOptions);
            if (cursor is null || cursor.Version != CursorVersion || cursor.PageSize != pageSize ||
                !IsOpaqueId(cursor.ConversationId))
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new CoordinationException("cursor_invalid", StatusCodes.Status400BadRequest);
        }
    }

    private sealed record ConversationCursor(int Version, int PageSize, DateTimeOffset CreatedAt, string ConversationId);
}
