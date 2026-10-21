using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// A caller notification from a pending gate or a recent backlog promotion.
/// Gate fields are null for <c>backlogPromoted</c> items; promotion fields are null for <c>pendingGate</c> items.
/// </summary>
public sealed record UserNotification(
    string NotificationId, string Source, string ProjectId, string RunId, string SessionId,
    string? RequestId, CoordinationBlockerKind? Kind, string? Prompt, DateTimeOffset CreatedAt,
    Agentweaver.Orchestrator.Core.CoordinatorGateKind? GateKind, long? StateVersion, DateTimeOffset UpdatedAt,
    Guid? PromotionId, int? TaskCount);

/// <summary>Notification source names returned in <see cref="UserNotification.Source"/>.</summary>
public static class UserNotificationSources
{
    /// <summary>A pending coordinator gate that needs the caller's answer.</summary>
    public const string PendingGate = "pendingGate";

    /// <summary>A confirmed work plan that was promoted into backlog tasks in the last 24 hours.</summary>
    public const string BacklogPromoted = "backlogPromoted";
}

/// <summary>A newest-first attention page and the total pending count after caller dismissals.</summary>
public sealed record UserNotificationPage(
    ImmutableArray<UserNotification> Items, long Count, string? NextCursor);

/// <summary>
/// Reads authoritative pending gates and recent backlog promotions, and stores caller-specific dismissals
/// without resolving gates or changing backlog tasks.
/// </summary>
internal sealed class UserNotificationStore
{
    /// <summary>How long a backlog promotion stays in the feed, matching the 0.x notice window.</summary>
    internal static readonly TimeSpan BacklogPromotedWindow = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public UserNotificationStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (string.IsNullOrEmpty(schema) || schema.Length > 63 ||
            !char.IsAsciiLetterLower(schema[0]) ||
            schema.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_') ||
            schema is "public" or "information_schema" || schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
    }

    /// <summary>Returns project read authority using the same permission set as project run lists.</summary>
    internal static (bool AllProjects, string[] ProjectIds) ReadProjects(ProjectsAuthorizationContext authority)
    {
        var all = authority.EffectiveAuthority.Any(item =>
            item.ResourceType == "tenant" && item.ResourceId == authority.TenantId &&
            item.Permissions.Any(p => p.Permission == "readProjects" && p.RoleRevision > 0));
        var ids = authority.EffectiveAuthority.Where(item =>
            item.ResourceType == "project" && item.Permissions.Any(p =>
                p.Permission is "readProjects" or "readRunSelection" && p.RoleRevision > 0))
            .Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return (all, ids);
    }

    /// <summary>Reads one snapshot so badge count and page agree; defensively narrows any bound authority before paging.</summary>
    internal async Task<UserNotificationPage> ListAsync(
        ProjectsAuthorizationContext authority, int pageSize, string? cursor, CancellationToken ct,
        string? projectId = null, bool includeDismissed = false)
    {
        // Validate before opening a connection; a cursor cannot override these query limits.
        if (pageSize is < 1 or > 100)
            throw new CoordinationException("query_invalid", StatusCodes.Status400BadRequest);
        if (projectId is not null) CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        var binding = QueryBinding(authority, pageSize, projectId, includeDismissed);
        var after = cursor is null ? null : DecodeCursor(cursor, binding);
        var (all, projectIds) = ReadProjects(authority);
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // Count and page must see the same task, gate, and dismissal state.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var sources = SourcesSql(!includeDismissed);
        long count;
        await using (var countCommand = new NpgsqlCommand(
            $"{sources} SELECT count(*) FROM sources", connection, transaction))
        {
            BindAuthority(countCommand, authority, all, projectIds);
            BindFilter(countCommand, projectId);
            count = (long)(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        await using var command = new NpgsqlCommand($"""
            {sources}
            SELECT source, project_id, run_id, session_id, source_key, gate, created_at,
                state_version, updated_at, execution_fence, task_count
            FROM sources
            WHERE @afterAt::timestamptz IS NULL OR
                (created_at, project_id, run_id, source, source_key) <
                (@afterAt, @afterProject::varchar, @afterRun::varchar, @afterSource::text, @afterKey::text)
            ORDER BY created_at DESC, project_id DESC, run_id DESC, source DESC, source_key DESC
            LIMIT @limit
            """, connection, transaction);
        // The cursor follows the full sort key, including source, so tied timestamps do not skip rows.
        BindAuthority(command, authority, all, projectIds);
        BindFilter(command, projectId);
        command.Parameters.AddWithValue("afterAt", NpgsqlDbType.TimestampTz, (object?)after?.CreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("afterProject", NpgsqlDbType.Varchar, (object?)after?.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterRun", NpgsqlDbType.Varchar, (object?)after?.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterSource", NpgsqlDbType.Text, (object?)after?.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("afterKey", NpgsqlDbType.Text, (object?)after?.SourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        var items = ImmutableArray.CreateBuilder<UserNotification>();
        // Keep source keys beside items; gate requests and promotion IDs use different identities.
        var keys = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var source = reader.GetString(0);
                var project = reader.GetString(1);
                var run = reader.GetString(2);
                var session = reader.GetString(3);
                var key = reader.GetString(4);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(6);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(8);
                keys.Add(key);
                if (source == UserNotificationSources.BacklogPromoted)
                {
                    // A promotion is a task-creation notice, not a gate the caller can approve.
                    var promotionId = Guid.Parse(key);
                    items.Add(new UserNotification(PromotionNotificationId(project, run, promotionId),
                        source, project, run, session, null, null, null, createdAt, null, null, updatedAt,
                        promotionId, reader.GetInt32(10)));
                    continue;
                }
                // Reject corrupt gate state through the same parser used by pending-gate notifications.
                var gate = ReadGate(reader.GetString(5), key, reader.GetInt64(9));
                var blocker = CoordinationEndpoints.ToStatusBlocker(gate);
                items.Add(new UserNotification(NotificationId(project, run, key),
                    source, project, run, session, key, blocker.Kind, blocker.Prompt,
                    createdAt, gate.Kind, reader.GetInt64(7), updatedAt, null, null));
            }
        }
        // The extra row detects another page without changing the total badge count.
        var more = items.Count > pageSize;
        if (more) items.RemoveAt(items.Count - 1);
        var last = more ? items[^1] : null;
        // Bind the next page to this caller, authority snapshot, query, and source-aware ordering.
        var next = last is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(2, binding, last.CreatedAt, last.ProjectId, last.RunId, last.Source, keys[items.Count - 1]),
            JsonOptions));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new UserNotificationPage(items.ToImmutable(), count, next);
    }

    /// <summary>Dismisses an authorized pending source idempotently; callers cannot mutate other users' state.</summary>
    internal async Task DismissAsync(
        ProjectsAuthorizationContext authority, string notificationId, CancellationToken ct,
        Func<CancellationToken, Task>? revalidateAuthority = null)
    {
        // IDs are opaque hashes; malformed input must not reach either dismissal query.
        if (notificationId.Length != 64 || notificationId.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CoordinationException("notification_id_invalid", 400);
        var (all, projectIds) = ReadProjects(authority);
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Re-select the source under current read authority instead of trusting an ID from the browser.
        // Do not filter existing dismissals: the unique key makes repeated requests harmless.
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.user_notification_dismissals
                (tenant_id, actor_issuer, actor_subject, project_id, run_id, request_id)
            SELECT @tenant, @issuer, @subject, g.project_id, g.run_id, g.request_id
            {PendingWhere(includeDismissals: false)}
            AND upper(encode(sha256(convert_to(
                g.project_id || chr(31) || g.run_id || chr(31) || g.request_id, 'UTF8')), 'hex')) = @id
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        BindAuthority(command, authority, all, projectIds);
        command.Parameters.AddWithValue("id", notificationId.ToUpperInvariant());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // Promotion hashes have their own source prefix and receipt table; tasks remain unchanged.
        await using var promotionCommand = new NpgsqlCommand($"""
            INSERT INTO {_schema}.user_notification_promotion_dismissals
                (tenant_id, actor_issuer, actor_subject, promotion_id)
            SELECT DISTINCT @tenant, @issuer, @subject, p.promotion_id
            {PromotionWhere(includeDismissals: false)}
            AND upper(encode(sha256(convert_to(
                'backlogPromoted' || chr(31) || p.project_id || chr(31) || p.run_id || chr(31) ||
                p.promotion_id::text, 'UTF8')), 'hex')) = @id
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        BindAuthority(promotionCommand, authority, all, projectIds);
        promotionCommand.Parameters.AddWithValue("id", notificationId.ToUpperInvariant());
        await promotionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // A revoked grant must roll back both writes rather than leave a stale authorized dismissal.
        if (revalidateAuthority is not null)
            await revalidateAuthority(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private string PendingWhere(bool includeDismissals = true) => $"""
        FROM {_schema}.coordinator_gates g
        JOIN {_schema}.accepted_runs r ON r.project_id = g.project_id AND r.run_id = g.run_id
        JOIN {_schema}.coordination_sessions s ON s.project_id = g.project_id
            AND s.run_id = g.run_id AND s.session_id = g.session_id
        JOIN {_schema}.coordination_sessions root ON root.project_id = s.project_id
            AND root.run_id = s.run_id AND root.session_id = s.root_session_id
        WHERE r.tenant_id = @tenant AND r.execution_state IN ('idle', 'active', 'blocked')
            AND g.gate_state = 'pending' AND g.execution_fence = s.execution_fence
            AND s.lifecycle_state = 'active'
            AND root.lifecycle_state = 'active'
            AND (@all OR g.project_id = ANY(@projects))
            AND (@project::varchar IS NULL OR g.project_id = @project)
            AND (@run::varchar IS NULL OR g.run_id = @run)
        """ + (includeDismissals ? $"""

            AND NOT EXISTS (
                SELECT 1 FROM {_schema}.user_notification_dismissals d
                WHERE d.tenant_id = @tenant AND d.actor_issuer = @issuer AND d.actor_subject = @subject
                    AND d.project_id = g.project_id AND d.run_id = g.run_id AND d.request_id = g.request_id)
        """ : "");

    /// <summary>Unions both notification sources with a shared row shape and an optional project filter.</summary>
    private string SourcesSql(bool includeDismissals)
    {
        // Count and paging reuse this exact projection so both sources obey the same visibility rules.
        // Group task origins by promotion: one plan can create many tasks but only one notice.
        return $"""
        WITH sources AS (
            SELECT 'pendingGate'::text AS source, g.project_id, g.run_id, g.session_id,
                g.request_id::text AS source_key, g.gate::text AS gate, g.created_at, g.state_version,
                g.updated_at, g.execution_fence, NULL::integer AS task_count
            {PendingWhere(includeDismissals)}
                AND (@filterProject::varchar IS NULL OR g.project_id = @filterProject)
            UNION ALL
            SELECT 'backlogPromoted'::text, p.project_id, p.run_id, p.root_session_id,
                p.promotion_id::text, NULL::text, p.created_at, NULL::bigint,
                p.created_at, NULL::bigint, count(*)::integer
            {PromotionWhere(includeDismissals)}
                AND (@filterProject::varchar IS NULL OR p.project_id = @filterProject)
            GROUP BY p.promotion_id, p.project_id, p.run_id, p.root_session_id, p.created_at
        )
        """;
    }

    /// <summary>
    /// Selects recent promotions whose tasks are still on the board, as the 0.x
    /// "N subtasks created" notice did; promotions with no unarchived task are skipped.
    /// </summary>
    private string PromotionWhere(bool includeDismissals = true)
    {
        // An origin row proves promotion; ordinary captures have none. Inner joins also exclude
        // promotions with no remaining unarchived tasks before either counting or dismissal.
        // Tenant and project checks apply even when a caller knows another promotion's ID.
        return $"""
        FROM {_schema}.backlog_plan_promotions p
        JOIN {_schema}.accepted_runs r ON r.project_id = p.project_id AND r.run_id = p.run_id
        JOIN {_schema}.backlog_task_origins o ON o.promotion_id = p.promotion_id
        JOIN {_schema}.backlog_tasks t ON t.project_id = o.project_id AND t.task_id = o.task_id
        WHERE r.tenant_id = @tenant AND NOT t.is_archived
            AND p.created_at >= now() - interval '{(int)BacklogPromotedWindow.TotalHours} hours'
            AND (@all OR p.project_id = ANY(@projects))
            AND (@project::varchar IS NULL OR p.project_id = @project)
            AND (@run::varchar IS NULL OR p.run_id = @run)
        """ + (includeDismissals ? $"""

            AND NOT EXISTS (
                SELECT 1 FROM {_schema}.user_notification_promotion_dismissals d
                WHERE d.tenant_id = @tenant AND d.actor_issuer = @issuer AND d.actor_subject = @subject
                    AND d.promotion_id = p.promotion_id)
        """ : "");
    }

    private static void BindFilter(NpgsqlCommand command, string? projectId) =>
        command.Parameters.AddWithValue("filterProject", NpgsqlDbType.Varchar, (object?)projectId ?? DBNull.Value);

    private static void BindAuthority(
        NpgsqlCommand command, ProjectsAuthorizationContext authority, bool all, string[] projectIds)
    {
        command.Parameters.AddWithValue("tenant", authority.TenantId);
        command.Parameters.AddWithValue("issuer", authority.Issuer);
        command.Parameters.AddWithValue("subject", authority.ActorId);
        command.Parameters.AddWithValue("all", all);
        command.Parameters.AddWithValue("projects", NpgsqlDbType.Array | NpgsqlDbType.Varchar, projectIds);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, (object?)authority.BoundProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)authority.BoundRunId ?? DBNull.Value);
    }

    internal static string NotificationId(string project, string run, string request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project}\u001f{run}\u001f{request}")));

    /// <summary>Returns a promotion notice ID; its four parts never match a three-part gate ID.</summary>
    internal static string PromotionNotificationId(string project, string run, Guid promotionId)
    {
        // Match the SQL hash exactly. The source prefix separates these IDs from gate IDs.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"backlogPromoted\u001f{project}\u001f{run}\u001f{promotionId:D}")));
    }

    internal static Agentweaver.Orchestrator.Core.CoordinatorGateRequest ReadGate(
        string json, string requestId, long executionFence)
    {
        try
        {
            var gate = JsonSerializer.Deserialize<Agentweaver.Orchestrator.Core.CoordinatorGateRequest>(
                json, JsonOptions);
            if (gate is null || gate.RequestId != requestId || gate.Fence != executionFence ||
                gate.AllowedChoices.IsDefault)
                throw new CoordinationException("coordinator_gate_state_unavailable", 503);
            return gate;
        }
        catch (JsonException)
        {
            throw new CoordinationException("coordinator_gate_state_unavailable", 503);
        }
    }

    private static string QueryBinding(
        ProjectsAuthorizationContext authority, int pageSize, string? projectId, bool includeDismissed)
    {
        var (all, ids) = ReadProjects(authority);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            authority.Issuer, authority.ActorId, authority.TenantId, authority.MembershipRevision,
            authority.BoundProjectId, authority.BoundRunId, All = all, Projects = ids,
            PageSize = pageSize, ProjectId = projectId, IncludeDismissed = includeDismissed
        })));
    }

    private static Cursor DecodeCursor(string value, string binding)
    {
        // Bound decoding work before base64 conversion or JSON allocation.
        if (value.Length > 4096) throw new CoordinationException("query_invalid", 400);
        try
        {
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value), JsonOptions);
            // Version 2 includes the source and its key. Older cursors cannot order the combined feed.
            // The binding rejects replay after a caller, authority, filter, or page-size change.
            if (cursor is null || cursor.Version != 2 || cursor.Binding != binding ||
                string.IsNullOrEmpty(cursor.ProjectId) || string.IsNullOrEmpty(cursor.RunId) ||
                cursor.Source is not (UserNotificationSources.PendingGate or UserNotificationSources.BacklogPromoted) ||
                string.IsNullOrEmpty(cursor.SourceKey))
                throw new CoordinationException("query_invalid", 400);
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            // Invalid encodings and invalid JSON are query errors, not empty successful pages.
            throw new CoordinationException("query_invalid", 400);
        }
    }

    private sealed record Cursor(
        int Version, string Binding, DateTimeOffset CreatedAt, string ProjectId, string RunId,
        string Source, string SourceKey);
}
