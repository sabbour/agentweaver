using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Knowledge;

public sealed class NativePostgresMemoryProvider : IMemoryProvider
{
    public const string ProviderId = "postgres.native-memory";
    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = NativePostgresMemoryOptions.CurrentOptionsSchemaVersion;
    private const int MaximumDecisionChainLength = 64;

    private const string RecordColumns = """
        record_id, project_id, agent_id, kind, record_type, title, content, rationale,
        importance, tags, state, trust_state, revision, current_revision_id,
        previous_revision_id, source_run_id, source_session_id, promoted_decision_id,
        created_at, updated_at, superseded_by_record_id
        """;
    private const string RevisionColumns = """
        record_id, revision, revision_id, previous_revision_id, kind, record_type,
        title, content, rationale, importance, tags, state, trust_state, reason,
        superseded_by_record_id, source_run_id, source_session_id, actor_fingerprint,
        change_kind, created_at
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly NativePostgresMemoryOptions _options;
    private readonly PostgresOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly string _schema;

    public NativePostgresMemoryProvider(
        NpgsqlDataSource dataSource,
        NativePostgresMemoryOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _dataSource = dataSource;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _schema = $"\"{options.Schema}\"";
        _outbox = new PostgresOutbox(dataSource, options.Schema);
        Descriptor = new ProviderDescriptor(
            ProviderSeam.Memory,
            ProviderId,
            AdapterVersion,
            OptionsSchemaVersion,
            ProviderHostingPattern.RemoteService,
            MemoryProviderCapabilities.All);
    }

    public ProviderDescriptor Descriptor { get; }

    public async Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Seam != ProviderSeam.Memory ||
            !string.Equals(candidate.ProviderId, ProviderId, StringComparison.Ordinal) ||
            candidate.AdapterVersion != AdapterVersion ||
            candidate.OptionsSchemaVersion != _options.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, _options.OptionsRevision, StringComparison.Ordinal) ||
            !MemoryProviderCapabilities.All.IsSubsetOf(candidate.AdvertisedCapabilities))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider candidate does not match native PostgreSQL Memory options.");

        try
        {
            await KnowledgeMigrator.VerifyAsync(_dataSource, _options.Schema, cancellationToken)
                .ConfigureAwait(false);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT current_database()", connection);
            var databaseName = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(databaseName, _options.DatabaseName, StringComparison.Ordinal))
                throw new KnowledgeProviderUnavailableException(
                    "The live Memory resource does not match its configured database identity.");
            return new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Memory,
                    ProviderId,
                    _options.ResourceId,
                    _options.ResourceGeneration),
                MemoryProviderCapabilities.All);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KnowledgeProviderUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw new KnowledgeProviderUnavailableException(
                "The configured native PostgreSQL Memory resource could not be negotiated.");
        }
    }

    public Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateIdentifier(query.ProjectId, nameof(query.ProjectId));
            ValidateIdentifier(query.AgentId, nameof(query.AgentId));
            if (query.Page < 1 || query.PageSize is < 1 or > 100)
                throw new KnowledgeApiException(
                    "invalid_page",
                    "Knowledge search page must be positive and page size must be between 1 and 100.",
                    StatusCodes.Status400BadRequest);
            var offset = checked((long)(query.Page - 1) * query.PageSize);
            var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
            if (text?.Length > 256)
                throw new KnowledgeApiException(
                    "invalid_query",
                    "Knowledge search text cannot exceed 256 characters.",
                    StatusCodes.Status400BadRequest);
            var kind = query.Kind?.ToString();
            var filter = """
                project_id = @project AND agent_id = @agent
                AND (@kind IS NULL OR kind = @kind)
                AND (@include_inactive OR state = 'Active')
                AND (@query IS NULL OR strpos(
                    lower(concat_ws(' ', record_type, title, content, array_to_string(tags, ' '))),
                    lower(@query)) > 0)
                """;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            int totalCount;
            await using (var count = new NpgsqlCommand(
                $"SELECT count(*) FROM {_schema}.knowledge_records WHERE {filter}", connection))
            {
                AddSearchParameters(count, query, kind, text);
                var total = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new KnowledgeStorageUnavailableException());
                totalCount = (int)Math.Min(total, int.MaxValue);
            }

            var items = ImmutableArray.CreateBuilder<KnowledgeRecord>();
            await using (var command = new NpgsqlCommand($"""
                SELECT {RecordColumns}
                FROM {_schema}.knowledge_records
                WHERE {filter}
                ORDER BY updated_at DESC, record_id
                LIMIT @limit OFFSET @offset
                """, connection))
            {
                AddSearchParameters(command, query, kind, text);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.PageSize);
                command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    items.Add(ReadRecord(reader));
            }
            return new KnowledgeRecordPage(items.ToImmutable(), totalCount, query.Page, query.PageSize);
        }, cancellationToken);

    public Task<KnowledgeRecord?> ReadAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            if (recordId == Guid.Empty)
                throw new KnowledgeApiException(
                    "invalid_record_id", "A Knowledge record ID is required.", StatusCodes.Status400BadRequest);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                SELECT {RecordColumns}
                FROM {_schema}.knowledge_records
                WHERE project_id = @project AND record_id = @record
                """, connection);
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRecord(reader)
                : null;
        }, cancellationToken);

    public Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidatePage(page, pageSize);
            var offset = checked((long)(page - 1) * pageSize);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            int totalCount;
            await using (var count = new NpgsqlCommand($"""
                SELECT count(*) FROM {_schema}.knowledge_revisions
                WHERE project_id = @project AND record_id = @record
                """, connection))
            {
                count.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
                count.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
                var total = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new KnowledgeStorageUnavailableException());
                totalCount = (int)Math.Min(total, int.MaxValue);
            }

            var items = ImmutableArray.CreateBuilder<KnowledgeRecordRevision>();
            await using (var command = new NpgsqlCommand($"""
                SELECT {RevisionColumns}
                FROM {_schema}.knowledge_revisions
                WHERE project_id = @project AND record_id = @record
                ORDER BY revision DESC
                LIMIT @limit OFFSET @offset
                """, connection))
            {
                command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
                command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize);
                command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    items.Add(ReadRevision(reader));
            }
            return new KnowledgeRecordRevisionPage(items.ToImmutable(), totalCount, page, pageSize);
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> CreateAsync(
        KnowledgeRecordCreate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateCreate(input, idempotencyKey);
            var normalized = input with { Tags = NormalizeTags(input.Tags) };
            var fingerprint = Fingerprint(normalized);
            var recordId = Guid.NewGuid();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                fingerprint, "record", recordId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordWriteResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var now = _timeProvider.GetUtcNow();
            var revisionId = Guid.NewGuid();
            var state = normalized.Kind == KnowledgeRecordKind.Proposal
                ? KnowledgeRecordState.Pending
                : KnowledgeRecordState.Active;
            var trustState = normalized.Kind == KnowledgeRecordKind.SessionContext
                ? KnowledgeTrustState.Approved
                : KnowledgeTrustState.Pending;
            var record = new KnowledgeRecord(
                recordId,
                normalized.ProjectId,
                normalized.AgentId,
                normalized.Kind,
                normalized.Type,
                normalized.Title,
                normalized.Content,
                normalized.Rationale,
                normalized.Importance,
                normalized.Tags,
                state,
                trustState,
                1,
                revisionId,
                null,
                normalized.SourceRunId,
                normalized.SourceSessionId,
                null,
                now,
                now);
            await InsertRecordAsync(connection, transaction, record, normalized.ActorFingerprint, cancellationToken)
                .ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, record, normalized.ActorFingerprint,
                normalized.Kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created",
                normalized.Kind == KnowledgeRecordKind.Proposal ? "proposal created" : "created",
                cancellationToken).ConfigureAwait(false);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Created, record);
            await CompleteWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                result.Record!.RecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> UpdateAsync(
        KnowledgeRecordUpdate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateUpdate(input, idempotencyKey);
            var normalized = input with { Tags = NormalizeTags(input.Tags) };
            var fingerprint = Fingerprint(normalized);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var candidate = await ReadRecordAsync(
                connection, transaction, normalized.ProjectId, normalized.RecordId, cancellationToken)
                .ConfigureAwait(false);
            if (candidate?.Kind == KnowledgeRecordKind.Decision)
                await AcquireDecisionGraphLockAsync(
                    connection, transaction, normalized.ProjectId, cancellationToken).ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey,
                fingerprint, "record", normalized.RecordId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordWriteResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var current = await ReadLockedRecordAsync(
                connection, transaction, normalized.ProjectId, normalized.RecordId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != normalized.ExpectedRevision)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            if (current.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.SessionContext or
                    KnowledgeRecordKind.Decision) ||
                (current.Kind == KnowledgeRecordKind.Decision
                    ? normalized.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived or
                        KnowledgeRecordState.Superseded)
                    : normalized.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived)) ||
                (current.Kind != KnowledgeRecordKind.Decision && normalized.SupersededByRecordId is not null) ||
                (current.Kind == KnowledgeRecordKind.Decision &&
                    (current.State == KnowledgeRecordState.Superseded ||
                     (current.State == KnowledgeRecordState.Archived &&
                      normalized.State == KnowledgeRecordState.Active))))
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

            if (current.Kind == KnowledgeRecordKind.Decision)
            {
                KnowledgeTransferValidator.ValidateSupersessionState(
                    current.Kind, normalized.State, normalized.SupersededByRecordId,
                    "invalid_replacement");
                if (normalized.State == KnowledgeRecordState.Superseded &&
                    current.State != KnowledgeRecordState.Active)
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);
                if (normalized.SupersededByRecordId is { } replacementId)
                {
                    var replacementStatus = await ValidateDecisionReplacementAsync(
                        connection, transaction, normalized.ProjectId, current.AgentId, normalized.RecordId,
                        replacementId, cancellationToken).ConfigureAwait(false);
                    if (replacementStatus is not null)
                        return new KnowledgeRecordWriteResult(replacementStatus.Value, null,
                            CurrentRevision: current.Revision);
                }
            }

            var decisionContentChanged = current.Content != normalized.Content ||
                current.Rationale != normalized.Rationale;
            var contentChanged = current.Type != normalized.Type ||
                current.Title != normalized.Title ||
                current.Content != normalized.Content ||
                current.Rationale != normalized.Rationale ||
                current.Importance != normalized.Importance ||
                !current.Tags.SequenceEqual(normalized.Tags, StringComparer.Ordinal);
            var trustState = current.Kind == KnowledgeRecordKind.SessionContext
                ? current.TrustState
                : current.Kind == KnowledgeRecordKind.Decision
                    ? decisionContentChanged ? KnowledgeTrustState.Pending : current.TrustState
                    : contentChanged ? KnowledgeTrustState.Pending : current.TrustState;
            var changeKind = current.Kind == KnowledgeRecordKind.Decision
                ? normalized.State switch
                {
                    KnowledgeRecordState.Archived when current.State != KnowledgeRecordState.Archived =>
                        "decision_archived",
                    KnowledgeRecordState.Superseded => "decision_superseded",
                    _ => "updated"
                }
                : "updated";
            var now = _timeProvider.GetUtcNow();
            var updated = current with
            {
                Type = normalized.Type,
                Title = normalized.Title,
                Content = normalized.Content,
                Rationale = normalized.Rationale,
                Importance = normalized.Importance,
                Tags = normalized.Tags,
                State = normalized.State,
                TrustState = trustState,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = now,
                SupersededByRecordId = normalized.SupersededByRecordId
            };
            await UpdateCurrentRecordAsync(
                connection, transaction, current.Revision, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, updated, normalized.ActorFingerprint, changeKind,
                normalized.Reason ?? changeKind.Replace('_', ' '), cancellationToken)
                .ConfigureAwait(false);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
            await CompleteWriteAsync(
                connection, transaction, normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey,
                updated.RecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> RestoreAsync(
        KnowledgeRecordRestore input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateActorFingerprint(input.ActorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1 || input.Revision < 1)
                throw new KnowledgeApiException(
                    "invalid_restore_revision",
                    "A record ID and positive current and historical revisions are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(input);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var candidate = await ReadRecordAsync(
                connection, transaction, input.ProjectId, input.RecordId, cancellationToken)
                .ConfigureAwait(false);
            if (candidate?.Kind == KnowledgeRecordKind.Decision)
                await AcquireDecisionGraphLockAsync(
                    connection, transaction, input.ProjectId, cancellationToken).ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                fingerprint, "record", input.RecordId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordWriteResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var current = await ReadLockedRecordAsync(
                connection, transaction, input.ProjectId, input.RecordId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != input.ExpectedRevision)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            if (current.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.Decision))
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);
            var historical = await ReadRevisionSnapshotAsync(
                connection, transaction, input.ProjectId, input.RecordId, input.Revision, cancellationToken)
                .ConfigureAwait(false);
            if (historical is null)
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);

            var updated = current with
            {
                Type = historical.Type,
                Title = historical.Title,
                Content = historical.Content,
                Rationale = historical.Rationale,
                Importance = historical.Importance,
                Tags = historical.Tags,
                State = KnowledgeRecordState.Active,
                TrustState = KnowledgeTrustState.Pending,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = _timeProvider.GetUtcNow(),
                SupersededByRecordId = null
            };
            var changeKind = current.Kind == KnowledgeRecordKind.Decision
                ? "decision_restored"
                : "updated";
            var reason = input.Reason ?? $"restored revision {input.Revision}";
            await UpdateCurrentRecordAsync(
                connection, transaction, current.Revision, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, updated, input.ActorFingerprint, changeKind, reason,
                cancellationToken).ConfigureAwait(false);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
            await CompleteWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                updated.RecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> ApproveDecisionAsync(
        KnowledgeDecisionApproval input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateActorFingerprint(input.ActorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_decision_revision",
                    "A Decision ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(input);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                fingerprint, "record", input.RecordId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordWriteResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var current = await ReadLockedRecordAsync(
                connection, transaction, input.ProjectId, input.RecordId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || current.Kind != KnowledgeRecordKind.Decision)
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != input.ExpectedRevision)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            if (current.State != KnowledgeRecordState.Active ||
                current.TrustState is not (KnowledgeTrustState.Pending or KnowledgeTrustState.Legacy))
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

            var approved = current with
            {
                TrustState = KnowledgeTrustState.Approved,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            await UpdateCurrentRecordAsync(
                connection, transaction, current.Revision, approved, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, approved, input.ActorFingerprint, "decision_approved",
                input.Reason ?? "approved", cancellationToken).ConfigureAwait(false);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, approved);
            await CompleteWriteAsync(
                connection, transaction, input.ProjectId, input.ActorFingerprint, idempotencyKey,
                approved.RecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeRecordTransferBundle> ExportAsync(
        string projectId,
        string agentId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(agentId, nameof(agentId));
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            var records = new List<KnowledgeRecord>();
            await using (var command = new NpgsqlCommand($"""
                SELECT {RecordColumns}
                FROM {_schema}.knowledge_records
                WHERE project_id = @project
                  AND agent_id = @agent
                  AND kind IN ('Decision', 'Memory')
                ORDER BY kind, agent_id, record_id
                LIMIT @limit
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
                command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, agentId);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer,
                    KnowledgeRecordTransferContract.MaximumRecords + 1);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    records.Add(ReadRecord(reader));
            }
            if (records.Count > KnowledgeRecordTransferContract.MaximumRecords)
                throw KnowledgeTransferValidator.TooLarge(
                    "The Knowledge export record count exceeds the supported bound.");

            var histories = records.ToDictionary(
                record => record.RecordId,
                _ => new List<KnowledgeRecordRevision>());
            if (records.Count > 0)
            {
                await using var command = new NpgsqlCommand($"""
                    SELECT {RevisionColumns}
                    FROM {_schema}.knowledge_revisions
                    WHERE project_id = @project AND record_id = ANY(@records)
                    ORDER BY record_id, revision
                    """, connection, transaction);
                command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
                command.Parameters.AddWithValue(
                    "records",
                    NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                    records.Select(record => record.RecordId).ToArray());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var revisionCount = 0;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (++revisionCount > KnowledgeRecordTransferContract.MaximumRevisions)
                        throw KnowledgeTransferValidator.TooLarge(
                            "The Knowledge export revision count exceeds the supported bound.");
                    var revision = ReadRevision(reader);
                    if (!histories.TryGetValue(revision.RecordId, out var history))
                        throw IncompleteExport();
                    history.Add(revision);
                }
            }

            var bundle = new KnowledgeRecordTransferBundle(
                KnowledgeRecordTransferContract.Format,
                KnowledgeRecordTransferContract.SchemaVersion,
                projectId,
                agentId,
                records.Select(record => new KnowledgeRecordTransferEntry(
                    record, histories[record.RecordId].ToImmutableArray())).ToImmutableArray());
            KnowledgeTransferValidator.Validate(
                bundle, projectId, agentId, "knowledge_transfer_incomplete",
                StatusCodes.Status409Conflict, allowEmpty: true);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return bundle;
        }, cancellationToken);

    public Task<KnowledgeRecordImportResult> ImportAsync(
        KnowledgeRecordTransferBundle input,
        string runId,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateIdentifier(input.AgentId, nameof(input.AgentId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            KnowledgeTransferValidator.Validate(input, input.ProjectId, input.AgentId);
            var fingerprint = Fingerprint(new { input, runId });
            var projectId = input.ProjectId;
            var resultRecordId = input.Records[0].Record.RecordId;
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await AcquireDecisionGraphLockAsync(
                connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                fingerprint, "transfer", resultRecordId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordImportResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var recordIds = input.Records.Select(entry => entry.Record.RecordId).ToArray();
            await using (var collision = new NpgsqlCommand($"""
                SELECT record_id FROM {_schema}.knowledge_records
                WHERE record_id = ANY(@records)
                LIMIT 1
                """, connection, transaction))
            {
                collision.Parameters.AddWithValue(
                    "records", NpgsqlDbType.Array | NpgsqlDbType.Uuid, recordIds);
                if (await collision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw TransferConflict("A transferred record ID already exists; import does not merge record heads.");
            }

            var revisionIds = input.Records
                .SelectMany(entry => entry.Revisions)
                .Select(revision => revision.RevisionId)
                .ToArray();
            await using (var collision = new NpgsqlCommand($"""
                SELECT revision_id FROM {_schema}.knowledge_revisions
                WHERE revision_id = ANY(@revisions)
                LIMIT 1
                """, connection, transaction))
            {
                collision.Parameters.AddWithValue(
                    "revisions", NpgsqlDbType.Array | NpgsqlDbType.Uuid, revisionIds);
                if (await collision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw TransferConflict("A transferred revision ID already exists; import will not overwrite history.");
            }

            await ValidateTransferReplacementGraphAsync(
                connection, transaction, input, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var importedRecords = ImmutableArray.CreateBuilder<KnowledgeRecord>(input.Records.Length);
            foreach (var entry in input.Records)
            {
                var source = entry.Record;
                var imported = source with
                {
                    State = KnowledgeRecordState.Active,
                    Revision = checked(source.Revision + 1),
                    PreviousRevisionId = source.RevisionId,
                    RevisionId = Guid.NewGuid(),
                    TrustState = KnowledgeTrustState.Pending,
                    SourceRunId = runId,
                    SourceSessionId = null,
                    UpdatedAt = now,
                    SupersededByRecordId = null
                };
                var creatorFingerprint = entry.Revisions[0].ActorFingerprint ?? actorFingerprint;
                await InsertRecordAsync(
                    connection, transaction, imported, creatorFingerprint, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var revision in entry.Revisions)
                    await InsertRevisionSnapshotAsync(
                        connection, transaction, projectId, revision, cancellationToken).ConfigureAwait(false);
                await InsertRevisionAsync(
                    connection, transaction, imported, actorFingerprint, "imported",
                    "imported from an authorized Knowledge transfer", cancellationToken)
                    .ConfigureAwait(false);
                importedRecords.Add(imported);
            }

            var result = new KnowledgeRecordImportResult(importedRecords.ToImmutable());
            await CompleteWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                resultRecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> RejectProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (proposalId == Guid.Empty || expectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_proposal_revision",
                    "A proposal ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(new { projectId, runId, proposalId, expectedRevision });
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                fingerprint, "record", proposalId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeRecordWriteResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var current = await ReadLockedRecordAsync(
                connection, transaction, projectId, proposalId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Kind != KnowledgeRecordKind.Proposal ||
                !string.Equals(current.SourceRunId, runId, StringComparison.Ordinal))
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != expectedRevision)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            if (current.State != KnowledgeRecordState.Pending ||
                current.TrustState != KnowledgeTrustState.Pending)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

            var updated = current with
            {
                State = KnowledgeRecordState.Rejected,
                TrustState = KnowledgeTrustState.Rejected,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            await UpdateCurrentRecordAsync(
                connection, transaction, current.Revision, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, updated, actorFingerprint, "proposal_rejected",
                "proposal rejected", cancellationToken)
                .ConfigureAwait(false);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
            await CompleteWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                updated.RecordId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<KnowledgeProposalPromotionResult> PromoteProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        AcceptedEffectAuthorizationBounds authorization,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateAuthorizationBounds(projectId, runId, authorization);
            ValidateIdempotencyKey(idempotencyKey);
            if (proposalId == Guid.Empty || expectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_proposal_revision",
                    "A proposal ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(new { projectId, runId, proposalId, expectedRevision });
            var decisionId = Guid.NewGuid();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var reservation = await ReserveWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                fingerprint, "promotion", decisionId, cancellationToken).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                var duplicate = Deserialize<KnowledgeProposalPromotionResult>(reservation.ResultJson);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate with { IsDuplicate = true };
            }

            var proposal = await ReadLockedRecordAsync(
                connection, transaction, projectId, proposalId, cancellationToken).ConfigureAwait(false);
            if (proposal is null || proposal.Kind != KnowledgeRecordKind.Proposal ||
                !string.Equals(proposal.SourceRunId, runId, StringComparison.Ordinal))
                return new KnowledgeProposalPromotionResult(KnowledgeWriteStatus.NotFound, null, null, null);
            if (proposal.Revision != expectedRevision)
                return new KnowledgeProposalPromotionResult(
                    KnowledgeWriteStatus.Stale, null, null, null, CurrentRevision: proposal.Revision);
            if (proposal.State != KnowledgeRecordState.Pending ||
                proposal.TrustState != KnowledgeTrustState.Pending)
                return new KnowledgeProposalPromotionResult(
                    KnowledgeWriteStatus.InvalidState, null, null, null, CurrentRevision: proposal.Revision);

            var now = _timeProvider.GetUtcNow();
            var decision = new KnowledgeRecord(
                decisionId,
                proposal.ProjectId,
                proposal.AgentId,
                KnowledgeRecordKind.Decision,
                proposal.Type,
                proposal.Title,
                proposal.Content,
                proposal.Rationale,
                proposal.Importance,
                proposal.Tags,
                KnowledgeRecordState.Active,
                KnowledgeTrustState.Approved,
                1,
                Guid.NewGuid(),
                null,
                proposal.SourceRunId,
                proposal.SourceSessionId,
                null,
                now,
                now);
            var promotedProposal = proposal with
            {
                State = KnowledgeRecordState.Promoted,
                Revision = checked(proposal.Revision + 1),
                PreviousRevisionId = proposal.RevisionId,
                RevisionId = Guid.NewGuid(),
                PromotedDecisionId = decision.RecordId,
                UpdatedAt = now
            };

            await UpdateCurrentRecordAsync(
                connection, transaction, proposal.Revision, promotedProposal, cancellationToken)
                .ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, promotedProposal, actorFingerprint,
                "proposal_promoted", "proposal promoted", cancellationToken).ConfigureAwait(false);
            await InsertRecordAsync(
                connection, transaction, decision, actorFingerprint, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection, transaction, decision, actorFingerprint, "created", "created", cancellationToken)
                .ConfigureAwait(false);

            var eventId = Guid.NewGuid();
            var eventKey = $"decision-accepted:{proposalId:D}:{expectedRevision}";
            var receipt = new AcceptedEffectReceipt(
                eventId,
                AcceptedEffectContractVersions.CurrentSchemaVersion,
                AcceptedEffectContractVersions.CurrentEventVersion,
                projectId,
                runId,
                decisionId,
                decisionId,
                decision.Revision,
                authorization.Issuer,
                authorization.Subject,
                authorization.TenantId,
                authorization.BoundProjectId,
                authorization.BoundRunId,
                authorization.AuthorizationResourceType,
                authorization.AuthorizationResourceId,
                authorization.AuthorizationRevision,
                authorization.MembershipRevision,
                authorization.ProjectRevision,
                authorization.ProjectConfigurationRevision,
                authorization.ContextRevision,
                now);
            var eventPayload = JsonSerializer.SerializeToElement(receipt, JsonOptions);
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                eventId,
                $"knowledge/{projectId}/{runId}",
                eventKey,
                "knowledge.accepted-effect",
                AcceptedEffectContractVersions.CurrentEventVersion,
                eventPayload,
                now), cancellationToken).ConfigureAwait(false);

            var result = new KnowledgeProposalPromotionResult(
                KnowledgeWriteStatus.Updated,
                promotedProposal,
                decision,
                eventId);
            await CompleteWriteAsync(
                connection, transaction, projectId, actorFingerprint, idempotencyKey,
                decisionId, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            if (receiptId == Guid.Empty)
                throw new KnowledgeApiException(
                    "invalid_receipt_id",
                    "An accepted-effect receipt ID is required.",
                    StatusCodes.Status400BadRequest);
            var stored = await _outbox.ReadAsync(receiptId, cancellationToken).ConfigureAwait(false);
            return stored is null
                ? null
                : ReadReceiptPayload(stored.Value.Event, receiptId);
        }, cancellationToken);

    public Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            var state = await ReadAcceptedEffectDeliveryCoreAsync(
                projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
            return state?.Receipt;
        }, cancellationToken);

    public Task<AcceptedEffectDeliveryState?> ReadAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(() => ReadAcceptedEffectDeliveryCoreAsync(
            projectId, runId, receiptId, cancellationToken), cancellationToken);

    public Task<AcceptedEffectDeliveryLease?> ClaimAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateAcceptedEffectScope(projectId, runId, receiptId);
            ValidateIdentifier(workerId, nameof(workerId));
            var delivery = await _outbox.ClaimAsync(
                workerId, receiptId, leaseDuration, cancellationToken).ConfigureAwait(false);
            if (delivery is null)
                return null;

            var receipt = ReadReceiptPayload(delivery.Event, receiptId);
            if (receipt is null ||
                !string.Equals(receipt.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(receipt.RunId, runId, StringComparison.Ordinal))
            {
                await _outbox.ReleaseAsync(receiptId, delivery.LeaseToken, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }
            return new AcceptedEffectDeliveryLease(receipt, delivery.LeaseToken);
        }, cancellationToken);

    public Task<bool> ReleaseAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            var state = await ReadAcceptedEffectDeliveryCoreAsync(
                projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
            return state is not null &&
                await _outbox.ReleaseAsync(receiptId, leaseToken, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> AcknowledgeAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            var state = await ReadAcceptedEffectDeliveryCoreAsync(
                projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
            return state is not null &&
                await _outbox.AcknowledgeAsync(receiptId, leaseToken, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<AcceptedEffectDeliveryState?> ReadAcceptedEffectDeliveryCoreAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        ValidateAcceptedEffectScope(projectId, runId, receiptId);
        var stored = await _outbox.ReadAsync(receiptId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        var receipt = ReadReceiptPayload(stored.Value.Event, receiptId);
        return receipt is null ||
            !string.Equals(receipt.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RunId, runId, StringComparison.Ordinal)
                ? null
                : new AcceptedEffectDeliveryState(receipt, stored.Value.IsDelivered);
    }

    private static AcceptedEffectReceipt? ReadReceiptPayload(StoredOutboxEvent stored, Guid receiptId)
    {
        if (stored.Message.EventType != "knowledge.accepted-effect")
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<AcceptedEffectReceipt>(
                stored.Message.Payload.GetRawText(), JsonOptions);
            if (receipt is null ||
                receipt.ReceiptId != receiptId ||
                receipt.SchemaVersion != AcceptedEffectContractVersions.CurrentSchemaVersion ||
                receipt.EventVersion != stored.Message.EventVersion ||
                receipt.EventVersion != AcceptedEffectContractVersions.CurrentEventVersion ||
                receipt.EffectId == Guid.Empty ||
                receipt.RecordId == Guid.Empty ||
                receipt.RecordVersion < 1)
                throw new JsonException("Invalid receipt contract.");
            return receipt;
        }
        catch (JsonException)
        {
            throw new KnowledgeStorageUnavailableException();
        }
    }

    private static void ValidateAcceptedEffectScope(string projectId, string runId, Guid receiptId)
    {
        ValidateIdentifier(projectId, nameof(projectId));
        ValidateIdentifier(runId, nameof(runId));
        if (receiptId == Guid.Empty)
            throw new KnowledgeApiException(
                "invalid_receipt_id",
                "An accepted-effect receipt ID is required.",
                StatusCodes.Status400BadRequest);
    }

    public Task<KnowledgeContextCandidates> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(agentId, nameof(agentId));
            ValidateIdentifier(runId, nameof(runId));
            if (maximumRecords is < 1 or > 10_000)
                throw new KnowledgeApiException(
                    "invalid_context_limit",
                    "The Knowledge context candidate limit is invalid.",
                    StatusCodes.Status500InternalServerError);

            var records = ImmutableArray.CreateBuilder<KnowledgeRecord>();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                SELECT {RecordColumns}
                FROM {_schema}.knowledge_records
                WHERE project_id = @project
                  AND (
                    (kind = 'Decision' AND state = 'Active' AND trust_state = 'Approved')
                    OR (kind = 'Memory' AND state = 'Active' AND trust_state IN ('Pending', 'Approved')
                        AND (agent_id = @agent OR (
                            EXISTS (
                                SELECT 1
                                FROM unnest(tags) AS tag(tag_value)
                                WHERE lower(tag.tag_value) = 'cross-team'
                            ) AND trust_state = 'Approved')))
                    OR (kind = 'SessionContext' AND state = 'Active' AND trust_state = 'Approved'
                        AND agent_id = @agent AND source_run_id = @run)
                  )
                ORDER BY CASE kind WHEN 'Decision' THEN 0 WHEN 'Memory' THEN 1 ELSE 2 END,
                         importance DESC, updated_at DESC, record_id
                LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, agentId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, maximumRecords + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                records.Add(ReadRecord(reader));
            if (records.Count > maximumRecords)
                throw new KnowledgeContextCandidateLimitException(maximumRecords);
            return new KnowledgeContextCandidates(records.ToImmutable());
        }, cancellationToken);

    private async Task<T> WithStorageAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            throw new KnowledgeStorageUnavailableException();
        }
        catch (OutboxConflictException)
        {
            throw new KnowledgeApiException(
                "accepted_event_conflict",
                "The accepted-decision event identity conflicts with a different immutable event.",
                StatusCodes.Status409Conflict);
        }
    }

    private async Task<KnowledgeRecord?> ReadLockedRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {RecordColumns}
            FROM {_schema}.knowledge_records
            WHERE project_id = @project AND record_id = @record
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private async Task<KnowledgeRecord?> ReadRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {RecordColumns}
            FROM {_schema}.knowledge_records
            WHERE project_id = @project AND record_id = @record
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private async Task<KnowledgeRecordRevision?> ReadRevisionSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        Guid recordId,
        int revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {RevisionColumns}
            FROM {_schema}.knowledge_revisions
            WHERE project_id = @project AND record_id = @record AND revision = @revision
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, recordId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Integer, revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRevision(reader)
            : null;
    }

    private static async Task AcquireDecisionGraphLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.knowledge.decision-graph'), hashtext(@project))",
            connection,
            transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Text, projectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<KnowledgeWriteStatus?> ValidateDecisionReplacementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string sourceAgentId,
        Guid sourceRecordId,
        Guid replacementRecordId,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { sourceRecordId };
        var currentId = replacementRecordId;
        for (var depth = 0; depth < MaximumDecisionChainLength; depth++)
        {
            if (!visited.Add(currentId))
                return KnowledgeWriteStatus.ReplacementCycle;
            var current = await ReadRecordAsync(
                connection, transaction, projectId, currentId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Kind != KnowledgeRecordKind.Decision ||
                !string.Equals(current.AgentId, sourceAgentId, StringComparison.Ordinal))
                return KnowledgeWriteStatus.InvalidReplacement;
            if (current.State == KnowledgeRecordState.Superseded)
            {
                if (current.SupersededByRecordId is not { } next)
                    return KnowledgeWriteStatus.InvalidReplacement;
                currentId = next;
                continue;
            }
            return current.State is KnowledgeRecordState.Active or KnowledgeRecordState.Archived
                ? null
                : KnowledgeWriteStatus.InvalidReplacement;
        }
        throw new KnowledgeApiException(
            "supersession_chain_too_long",
            "The Decision supersession chain exceeds the supported traversal bound.",
            StatusCodes.Status409Conflict);
    }

    private async Task ValidateTransferReplacementGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        KnowledgeRecordTransferBundle bundle,
        CancellationToken cancellationToken)
    {
        var included = bundle.Records.ToDictionary(entry => entry.Record.RecordId);
        var referencedIds = bundle.Records
            .SelectMany(entry => entry.Revisions
                .Where(revision => revision.SupersededByRecordId is not null)
                .Select(revision => revision.SupersededByRecordId!.Value))
            .Distinct();
        foreach (var targetId in referencedIds)
        {
            if (included.TryGetValue(targetId, out var includedTarget))
            {
                if (includedTarget.Record.Kind != KnowledgeRecordKind.Decision ||
                    !string.Equals(includedTarget.Record.AgentId, bundle.AgentId, StringComparison.Ordinal))
                    throw InvalidReplacement(
                        "A supersession link must target a Decision in the transferred agent scope.");
                continue;
            }
            var target = await ReadRecordAsync(
                connection, transaction, bundle.ProjectId, targetId, cancellationToken).ConfigureAwait(false);
            if (target?.Kind != KnowledgeRecordKind.Decision ||
                !string.Equals(target.AgentId, bundle.AgentId, StringComparison.Ordinal))
                throw InvalidReplacement(
                    "A supersession target is missing or outside the transferred project and agent scope.");
        }

        foreach (var source in bundle.Records
                     .Where(entry => entry.Record.State == KnowledgeRecordState.Superseded))
        {
            var visited = new HashSet<Guid> { source.Record.RecordId };
            var currentId = source.Record.SupersededByRecordId!.Value;
            var reachedTerminal = false;
            for (var depth = 0; depth < MaximumDecisionChainLength; depth++)
            {
                if (!visited.Add(currentId))
                    throw ReplacementCycle();
                var current = included.TryGetValue(currentId, out var entry)
                    ? entry.Record
                    : await ReadRecordAsync(
                        connection, transaction, bundle.ProjectId, currentId, cancellationToken)
                        .ConfigureAwait(false);
                if (current is null || current.Kind != KnowledgeRecordKind.Decision ||
                    !string.Equals(current.AgentId, bundle.AgentId, StringComparison.Ordinal))
                    throw InvalidReplacement(
                        "A supersession target is missing or outside the transferred project and agent scope.");
                if (current.State == KnowledgeRecordState.Superseded)
                {
                    if (current.SupersededByRecordId is not { } next)
                        throw InvalidReplacement("A superseded Decision has no replacement link.");
                    currentId = next;
                    continue;
                }
                if (current.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived))
                    throw InvalidReplacement("A supersession target has an unsupported state.");
                reachedTerminal = true;
                break;
            }
            if (!reachedTerminal)
                throw new KnowledgeApiException(
                    "supersession_chain_too_long",
                    "The Decision supersession chain exceeds the supported traversal bound.",
                    StatusCodes.Status409Conflict);
        }
    }

    private async Task InsertRevisionSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        KnowledgeRecordRevision revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.knowledge_revisions
                (project_id, record_id, revision, revision_id, previous_revision_id,
                 kind, record_type, title, content, rationale, importance, tags,
                 state, trust_state, source_run_id, source_session_id, actor_fingerprint,
                 change_kind, reason, superseded_by_record_id, created_at)
            VALUES
                (@project, @record, @revision, @revision_id, @previous_revision_id,
                 @kind, @type, @title, @content, @rationale, @importance, @tags,
                 @state, @trust, @source_run, @source_session, @actor,
                 @change_kind, @reason, @superseded_by, @created)
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, revision.RecordId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Integer, revision.Revision);
        command.Parameters.AddWithValue("revision_id", NpgsqlDbType.Uuid, revision.RevisionId);
        AddNullable(command, "previous_revision_id", NpgsqlDbType.Uuid, revision.PreviousRevisionId);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, revision.Kind.ToString());
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, revision.Type);
        AddNullable(command, "title", NpgsqlDbType.Varchar, revision.Title);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Text, revision.Content);
        AddNullable(command, "rationale", NpgsqlDbType.Text, revision.Rationale);
        command.Parameters.AddWithValue("importance", NpgsqlDbType.Varchar, revision.Importance);
        command.Parameters.AddWithValue("tags", NpgsqlDbType.Array | NpgsqlDbType.Text, revision.Tags.ToArray());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, revision.State.ToString());
        command.Parameters.AddWithValue("trust", NpgsqlDbType.Varchar, revision.TrustState.ToString());
        AddNullable(command, "source_run", NpgsqlDbType.Varchar, revision.SourceRunId);
        AddNullable(command, "source_session", NpgsqlDbType.Varchar, revision.SourceSessionId);
        AddNullable(command, "actor", NpgsqlDbType.Char, revision.ActorFingerprint);
        AddNullable(command, "change_kind", NpgsqlDbType.Varchar, revision.ChangeKind);
        AddNullable(command, "reason", NpgsqlDbType.Text, revision.Reason);
        AddNullable(command, "superseded_by", NpgsqlDbType.Uuid, revision.SupersededByRecordId);
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, revision.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        KnowledgeRecord record,
        string actorFingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.knowledge_records
                (record_id, project_id, agent_id, kind, record_type, title, content, rationale,
                 importance, tags, state, trust_state, revision, current_revision_id,
                 previous_revision_id, source_run_id, source_session_id, promoted_decision_id,
                 creator_fingerprint, created_at, updated_at, superseded_by_record_id)
            VALUES
                (@record, @project, @agent, @kind, @type, @title, @content, @rationale,
                 @importance, @tags, @state, @trust, @revision, @revision_id,
                 @previous_revision_id, @source_run, @source_session, @promoted_decision,
                 @actor, @created, @updated, @superseded_by)
            """, connection, transaction);
        AddRecordParameters(command, record, actorFingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        KnowledgeRecord record,
        string? actorFingerprint,
        string? changeKind,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.knowledge_revisions
                (project_id, record_id, revision, revision_id, previous_revision_id,
                 kind, record_type, title, content, rationale, importance, tags,
                 state, trust_state, source_run_id, source_session_id, actor_fingerprint,
                 change_kind, reason, superseded_by_record_id, created_at)
            VALUES
                (@project, @record, @revision, @revision_id, @previous_revision_id,
                 @kind, @type, @title, @content, @rationale, @importance, @tags,
                 @state, @trust, @source_run, @source_session, @actor, @change_kind,
                 @reason, @superseded_by, @created)
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, record.ProjectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, record.RecordId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Integer, record.Revision);
        command.Parameters.AddWithValue("revision_id", NpgsqlDbType.Uuid, record.RevisionId);
        AddNullable(command, "previous_revision_id", NpgsqlDbType.Uuid, record.PreviousRevisionId);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, record.Kind.ToString());
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, record.Type);
        AddNullable(command, "title", NpgsqlDbType.Varchar, record.Title);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Text, record.Content);
        AddNullable(command, "rationale", NpgsqlDbType.Text, record.Rationale);
        command.Parameters.AddWithValue("importance", NpgsqlDbType.Varchar, record.Importance);
        command.Parameters.AddWithValue("tags", NpgsqlDbType.Array | NpgsqlDbType.Text, record.Tags.ToArray());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, record.State.ToString());
        command.Parameters.AddWithValue("trust", NpgsqlDbType.Varchar, record.TrustState.ToString());
        AddNullable(command, "source_run", NpgsqlDbType.Varchar, record.SourceRunId);
        AddNullable(command, "source_session", NpgsqlDbType.Varchar, record.SourceSessionId);
        AddNullable(command, "actor", NpgsqlDbType.Char, actorFingerprint);
        AddNullable(command, "change_kind", NpgsqlDbType.Varchar, changeKind);
        AddNullable(command, "reason", NpgsqlDbType.Text, reason);
        AddNullable(command, "superseded_by", NpgsqlDbType.Uuid, record.SupersededByRecordId);
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, record.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateCurrentRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int expectedRevision,
        KnowledgeRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.knowledge_records
            SET kind = @kind, record_type = @type, title = @title, content = @content,
                rationale = @rationale, importance = @importance, tags = @tags,
                state = @state, trust_state = @trust, revision = @revision,
                current_revision_id = @revision_id, previous_revision_id = @previous_revision_id,
                promoted_decision_id = @promoted_decision, updated_at = @updated,
                superseded_by_record_id = @superseded_by
            WHERE project_id = @project AND record_id = @record AND revision = @expected
            """, connection, transaction);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, record.Kind.ToString());
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, record.Type);
        AddNullable(command, "title", NpgsqlDbType.Varchar, record.Title);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Text, record.Content);
        AddNullable(command, "rationale", NpgsqlDbType.Text, record.Rationale);
        command.Parameters.AddWithValue("importance", NpgsqlDbType.Varchar, record.Importance);
        command.Parameters.AddWithValue("tags", NpgsqlDbType.Array | NpgsqlDbType.Text, record.Tags.ToArray());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, record.State.ToString());
        command.Parameters.AddWithValue("trust", NpgsqlDbType.Varchar, record.TrustState.ToString());
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Integer, record.Revision);
        command.Parameters.AddWithValue("revision_id", NpgsqlDbType.Uuid, record.RevisionId);
        AddNullable(command, "previous_revision_id", NpgsqlDbType.Uuid, record.PreviousRevisionId);
        AddNullable(command, "promoted_decision", NpgsqlDbType.Uuid, record.PromotedDecisionId);
        AddNullable(command, "superseded_by", NpgsqlDbType.Uuid, record.SupersededByRecordId);
        command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, record.UpdatedAt);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, record.ProjectId);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, record.RecordId);
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Integer, expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KnowledgeApiException(
                "stale_revision",
                "The Knowledge record changed during the update.",
                StatusCodes.Status409Conflict);
    }

    private async Task<IdempotencyReservation> ReserveWriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        string requestFingerprint,
        string resultKind,
        Guid resultRecordId,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.knowledge_write_idempotency
                (project_id, actor_fingerprint, idempotency_key, request_fingerprint,
                 result_kind, result_record_id)
            VALUES (@project, @actor, @key, @fingerprint, @kind, @result_record)
            ON CONFLICT (project_id, actor_fingerprint, idempotency_key) DO NOTHING
            RETURNING 1
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            insert.Parameters.AddWithValue("actor", NpgsqlDbType.Char, actorFingerprint);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Char, requestFingerprint);
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, resultKind);
            insert.Parameters.AddWithValue("result_record", NpgsqlDbType.Uuid, resultRecordId);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                return new IdempotencyReservation(true, null);
        }

        await using var read = new NpgsqlCommand($"""
            SELECT request_fingerprint, result_kind, result_json
            FROM {_schema}.knowledge_write_idempotency
            WHERE project_id = @project AND actor_fingerprint = @actor AND idempotency_key = @key
            """, connection, transaction);
        read.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        read.Parameters.AddWithValue("actor", NpgsqlDbType.Char, actorFingerprint);
        read.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KnowledgeStorageUnavailableException();
        if (!string.Equals(reader.GetString(0), requestFingerprint, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), resultKind, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "idempotency_conflict",
                "The idempotency key was already used for a different Knowledge write.",
                StatusCodes.Status409Conflict);
        if (reader.IsDBNull(2))
            throw new InvalidOperationException("A committed Knowledge idempotency receipt has no result.");
        return new IdempotencyReservation(false, reader.GetString(2));
    }

    private async Task CompleteWriteAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        Guid resultRecordId,
        T result,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.knowledge_write_idempotency
            SET result_record_id = @record, result_json = @result::jsonb
            WHERE project_id = @project AND actor_fingerprint = @actor AND idempotency_key = @key
            """, connection, transaction);
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, resultRecordId);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, json);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Char, actorFingerprint);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KnowledgeStorageUnavailableException();
    }

    private static KnowledgeRecord ReadRecord(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<KnowledgeRecordKind>(reader.GetString(3), ignoreCase: false),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<string[]>(9).ToImmutableArray(),
            Enum.Parse<KnowledgeRecordState>(reader.GetString(10), ignoreCase: false),
            Enum.Parse<KnowledgeTrustState>(reader.GetString(11), ignoreCase: false),
            reader.GetInt32(12),
            reader.GetGuid(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetGuid(17),
            reader.GetFieldValue<DateTimeOffset>(18),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.IsDBNull(20) ? null : reader.GetGuid(20));

    private static KnowledgeRecordRevision ReadRevision(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            Enum.Parse<KnowledgeRecordKind>(reader.GetString(4), ignoreCase: false),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<string[]>(10).ToImmutableArray(),
            Enum.Parse<KnowledgeRecordState>(reader.GetString(11), ignoreCase: false),
            Enum.Parse<KnowledgeTrustState>(reader.GetString(12), ignoreCase: false),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.IsDBNull(14) ? null : reader.GetGuid(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));

    private static void AddRecordParameters(
        NpgsqlCommand command,
        KnowledgeRecord record,
        string actorFingerprint)
    {
        command.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, record.RecordId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, record.ProjectId);
        command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, record.AgentId);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, record.Kind.ToString());
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, record.Type);
        AddNullable(command, "title", NpgsqlDbType.Varchar, record.Title);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Text, record.Content);
        AddNullable(command, "rationale", NpgsqlDbType.Text, record.Rationale);
        command.Parameters.AddWithValue("importance", NpgsqlDbType.Varchar, record.Importance);
        command.Parameters.AddWithValue("tags", NpgsqlDbType.Array | NpgsqlDbType.Text, record.Tags.ToArray());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, record.State.ToString());
        command.Parameters.AddWithValue("trust", NpgsqlDbType.Varchar, record.TrustState.ToString());
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Integer, record.Revision);
        command.Parameters.AddWithValue("revision_id", NpgsqlDbType.Uuid, record.RevisionId);
        AddNullable(command, "previous_revision_id", NpgsqlDbType.Uuid, record.PreviousRevisionId);
        AddNullable(command, "source_run", NpgsqlDbType.Varchar, record.SourceRunId);
        AddNullable(command, "source_session", NpgsqlDbType.Varchar, record.SourceSessionId);
        AddNullable(command, "promoted_decision", NpgsqlDbType.Uuid, record.PromotedDecisionId);
        AddNullable(command, "superseded_by", NpgsqlDbType.Uuid, record.SupersededByRecordId);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Char, actorFingerprint);
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, record.CreatedAt);
        command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, record.UpdatedAt);
    }

    private static void AddSearchParameters(
        NpgsqlCommand command,
        KnowledgeRecordQuery query,
        string? kind,
        string? text)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, query.ProjectId);
        command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, query.AgentId);
        AddNullable(command, "kind", NpgsqlDbType.Varchar, kind);
        command.Parameters.AddWithValue("include_inactive", NpgsqlDbType.Boolean, query.IncludeInactive);
        AddNullable(command, "query", NpgsqlDbType.Text, text);
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private static T Deserialize<T>(string? json) =>
        json is null
            ? throw new InvalidOperationException("A Knowledge idempotency receipt contains no result.")
            : JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidOperationException(
                    "A Knowledge idempotency receipt contains an invalid result.");

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)))
            .ToLowerInvariant();

    private static void ValidateCreate(KnowledgeRecordCreate input, string idempotencyKey)
    {
        ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
        ValidateIdentifier(input.AgentId, nameof(input.AgentId));
        ValidateActorFingerprint(input.ActorFingerprint);
        ValidateIdempotencyKey(idempotencyKey);
        if (!Enum.IsDefined(input.Kind) || input.Kind == KnowledgeRecordKind.Decision ||
            string.IsNullOrWhiteSpace(input.Type) || input.Type.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 40_000 ||
            input.Title?.Length > 512 || input.Rationale?.Length > 8_000 ||
            input.Reason?.Length > 1_024 || input.Reason?.Any(char.IsControl) == true ||
            input.Importance is not ("low" or "medium" or "high") ||
            input.Tags.IsDefault || input.Tags.Length > 32)
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Knowledge record kind, type, content, importance, tags, or length is invalid.",
                StatusCodes.Status400BadRequest);
        foreach (var tag in input.Tags)
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl))
                throw new KnowledgeApiException(
                    "invalid_knowledge_record",
                    "Knowledge tags must be nonempty values of at most 64 characters without control characters.",
                    StatusCodes.Status400BadRequest);
    }

    private static void ValidateUpdate(KnowledgeRecordUpdate input, string idempotencyKey)
    {
        ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
        ValidateActorFingerprint(input.ActorFingerprint);
        ValidateIdempotencyKey(idempotencyKey);
        if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1 ||
            string.IsNullOrWhiteSpace(input.Type) || input.Type.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 40_000 ||
            input.Title?.Length > 512 || input.Rationale?.Length > 8_000 ||
            input.Reason?.Length > 1_024 || input.Reason?.Any(char.IsControl) == true ||
            input.SupersededByRecordId == Guid.Empty ||
            input.Importance is not ("low" or "medium" or "high") ||
            input.Tags.IsDefault || input.Tags.Length > 32)
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Knowledge record update fields or expected revision are invalid.",
                StatusCodes.Status400BadRequest);
        foreach (var tag in input.Tags)
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl))
                throw new KnowledgeApiException(
                    "invalid_knowledge_record",
                    "Knowledge tags must be nonempty values of at most 64 characters without control characters.",
                    StatusCodes.Status400BadRequest);
    }

    private static ImmutableArray<string> NormalizeTags(ImmutableArray<string> tags) =>
        tags.Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new KnowledgeApiException(
                "invalid_page",
                "Knowledge history page must be positive and page size must be between 1 and 100.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateActorFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new KnowledgeApiException(
                "invalid_actor_fingerprint",
                "A validated actor fingerprint is required.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateAuthorizationBounds(
        string projectId,
        string runId,
        AcceptedEffectAuthorizationBounds authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!Uri.TryCreate(authorization.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps ||
            !IsReceiptIdentifier(authorization.Subject) ||
            !IsReceiptIdentifier(authorization.TenantId) ||
            (authorization.BoundProjectId is not null &&
                !string.Equals(authorization.BoundProjectId, projectId, StringComparison.Ordinal)) ||
            (authorization.BoundRunId is not null &&
                !string.Equals(authorization.BoundRunId, runId, StringComparison.Ordinal)) ||
            (authorization.BoundRunId is not null && authorization.BoundProjectId is null) ||
            !Enum.IsDefined(authorization.AuthorizationResourceType) ||
            (authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Project &&
                !string.Equals(authorization.AuthorizationResourceId, projectId, StringComparison.Ordinal)) ||
            (authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Tenant &&
                !string.Equals(authorization.AuthorizationResourceId, authorization.TenantId, StringComparison.Ordinal)) ||
            authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Platform ||
            !IsReceiptIdentifier(authorization.AuthorizationResourceId) ||
            authorization.AuthorizationRevision < 1 ||
            authorization.MembershipRevision < 1 ||
            authorization.ProjectRevision < 1 ||
            authorization.ProjectConfigurationRevision < 1 ||
            !IsReceiptIdentifier(authorization.ContextRevision))
            throw new KnowledgeApiException(
                "invalid_accepted_effect_authority",
                "Validated project and run authorization bounds are required for accepted effects.",
                StatusCodes.Status400BadRequest);
    }

    private static bool IsReceiptIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static KnowledgeApiException IncompleteExport() =>
        new(
            "knowledge_transfer_incomplete",
            "A complete, consistent Knowledge revision chain could not be exported.",
            StatusCodes.Status409Conflict);

    private static KnowledgeApiException TransferConflict(string detail) =>
        new("knowledge_transfer_conflict", detail, StatusCodes.Status409Conflict);

    private static KnowledgeApiException InvalidReplacement(string detail) =>
        new("invalid_replacement", detail, StatusCodes.Status409Conflict);

    private static KnowledgeApiException ReplacementCycle() =>
        new(
            "replacement_cycle",
            "A Decision cannot be superseded by itself or by a Decision that leads back to it.",
            StatusCodes.Status409Conflict);

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new KnowledgeApiException(
                "idempotency_key_required",
                "A valid Idempotency-Key header is required for Knowledge writes.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new KnowledgeApiException(
                "invalid_identifier",
                $"The {name} value is invalid.",
                StatusCodes.Status400BadRequest);
    }

    private sealed record IdempotencyReservation(bool IsNew, string? ResultJson);
}
