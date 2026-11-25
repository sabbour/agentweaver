using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal sealed class ProjectGovernanceDecisionStore(
    IDbContextFactory<ProjectsConfigDbContext> contextFactory,
    TimeProvider timeProvider)
{
    private const string OutboxEventType = "project.governance_decision_appended.v1";
    private static readonly string GenesisHash = new('0', 64);
    private static readonly JsonSerializerOptions OutboxJsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<ProjectGovernanceDecisionReceipt> AppendWithCurrentHeadAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        string resourceType,
        string resourceId,
        string idempotencyKey,
        ProjectGovernanceDecisionInput decision,
        CancellationToken cancellationToken)
    {
        ProjectConfigException? lastConflict = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var head = await ReadHeadAsync(
                scopeType, scopeId, resourceType, resourceId, cancellationToken).ConfigureAwait(false);
            try
            {
                return await AppendAsync(
                    new ProjectGovernanceDecisionAppendRequest(
                        scopeType,
                        scopeId,
                        resourceType,
                        resourceId,
                        head.Sequence,
                        head.FenceGeneration,
                        head.HeadHash,
                        idempotencyKey,
                        decision),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProjectConfigException exception) when (exception.StatusCode == StatusCodes.Status409Conflict)
            {
                lastConflict = exception;
            }
        }

        throw lastConflict ?? ProjectConfigException.Conflict("The governance decision stream head changed.");
    }

    private async Task<ProjectGovernanceDecisionStreamHeadSnapshot> ReadHeadAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var head = await db.GovernanceDecisionStreamHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.ResourceType == resourceType &&
                item.ResourceId == resourceId,
                cancellationToken)
            .ConfigureAwait(false);
        return head is null
            ? new ProjectGovernanceDecisionStreamHeadSnapshot(0, 0, GenesisHash)
            : new ProjectGovernanceDecisionStreamHeadSnapshot(
                head.Sequence, head.FenceGeneration, head.HeadHash);
    }

    internal async Task<ProjectGovernanceDecisionReceipt> AppendAsync(
        ProjectGovernanceDecisionAppendRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var decision = ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(request.Decision);
        var requestDigest = ProjectGovernanceDecisionCanonicalJson.Digest(
            ProjectGovernanceDecisionCanonicalJson.Serialize(decision));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        try
        {
            var replay = await FindReplayAsync(connection, npgsqlTransaction, request, requestDigest, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            await using (var initialize = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decision_stream_heads
                    (scope_type, scope_id, resource_type, resource_id, sequence, fence_generation, head_hash, updated_at)
                VALUES (@scope_type, @scope_id, @resource_type, @resource_id, 0, 0, @genesis_hash, @now)
                ON CONFLICT (scope_type, scope_id, resource_type, resource_id) DO NOTHING
                """, connection, npgsqlTransaction))
            {
                AddStreamParameters(initialize, request);
                initialize.Parameters.AddWithValue("genesis_hash", GenesisHash);
                initialize.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
                await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            long sequence;
            long fence;
            string headHash;
            await using (var readHead = new NpgsqlCommand("""
                SELECT sequence, fence_generation, head_hash
                FROM projects_config.project_governance_decision_stream_heads
                WHERE scope_type = @scope_type AND scope_id = @scope_id
                  AND resource_type = @resource_type AND resource_id = @resource_id
                FOR UPDATE
                """, connection, npgsqlTransaction))
            {
                AddStreamParameters(readHead, request);
                await using var reader = await readHead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The governance decision stream head was not created.");
                sequence = reader.GetInt64(0);
                fence = reader.GetInt64(1);
                headHash = reader.GetString(2);
            }

            if (sequence != request.ExpectedSequence ||
                fence != request.ExpectedFenceGeneration ||
                !string.Equals(headHash, request.ExpectedHeadHash, StringComparison.Ordinal))
                throw ProjectConfigException.Conflict("The governance decision stream head changed.");

            var nextSequence = checked(sequence + 1);
            var nextFence = checked(fence + 1);
            var decisionId = Guid.NewGuid();
            var now = timeProvider.GetUtcNow();
            var record = new ProjectGovernanceDecisionV1(
                SchemaVersion: 1,
                DecisionId: decisionId,
                ScopeType: request.ScopeType.ToString(),
                ScopeId: request.ScopeId,
                ResourceType: request.ResourceType,
                ResourceId: request.ResourceId,
                Sequence: nextSequence,
                PreviousHash: headHash,
                RecordedAt: now,
                Decision: decision);
            var canonicalRecord = ProjectGovernanceDecisionCanonicalJson.Serialize(record);
            if (canonicalRecord.Length > ProjectGovernanceDecisionCanonicalJson.MaximumCanonicalBytes)
                throw new ArgumentException("Canonical decision record exceeds 65536 bytes.", nameof(request));
            var recordHash = ProjectGovernanceDecisionCanonicalJson.ChainHash(headHash, canonicalRecord);

            await using (var insertRecord = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decisions
                    (scope_type, scope_id, resource_type, resource_id, sequence, decision_id,
                     idempotency_key, request_digest, previous_hash, record_hash, canonical_record, recorded_at)
                VALUES
                    (@scope_type, @scope_id, @resource_type, @resource_id, @sequence, @decision_id,
                     @idempotency_key, @request_digest, @previous_hash, @record_hash, @canonical_record, @recorded_at)
                """, connection, npgsqlTransaction))
            {
                AddStreamParameters(insertRecord, request);
                insertRecord.Parameters.AddWithValue("sequence", nextSequence);
                insertRecord.Parameters.AddWithValue("decision_id", decisionId);
                insertRecord.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
                insertRecord.Parameters.AddWithValue("request_digest", requestDigest);
                insertRecord.Parameters.AddWithValue("previous_hash", headHash);
                insertRecord.Parameters.AddWithValue("record_hash", recordHash);
                insertRecord.Parameters.AddWithValue("canonical_record", NpgsqlDbType.Bytea, canonicalRecord);
                insertRecord.Parameters.AddWithValue("recorded_at", now);
                await insertRecord.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var outboxPayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                decisionId,
                scopeType = request.ScopeType.ToString(),
                scopeId = request.ScopeId,
                resourceType = request.ResourceType,
                resourceId = request.ResourceId,
                sequence = nextSequence,
                recordHash,
            }, OutboxJsonOptions);
            await using (var insertOutbox = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decision_outbox
                    (outbox_id, decision_id, event_type, sequence, record_hash, payload, created_at)
                VALUES (@outbox_id, @decision_id, @event_type, @sequence, @record_hash, @payload, @created_at)
                """, connection, npgsqlTransaction))
            {
                insertOutbox.Parameters.AddWithValue("outbox_id", decisionId);
                insertOutbox.Parameters.AddWithValue("decision_id", decisionId);
                insertOutbox.Parameters.AddWithValue("event_type", OutboxEventType);
                insertOutbox.Parameters.AddWithValue("sequence", nextSequence);
                insertOutbox.Parameters.AddWithValue("record_hash", recordHash);
                insertOutbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, outboxPayload);
                insertOutbox.Parameters.AddWithValue("created_at", now);
                await insertOutbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var advanceHead = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decision_stream_heads
                SET sequence = @next_sequence, fence_generation = @next_fence,
                    head_hash = @record_hash, updated_at = @now
                WHERE scope_type = @scope_type AND scope_id = @scope_id
                  AND resource_type = @resource_type AND resource_id = @resource_id
                  AND sequence = @expected_sequence AND fence_generation = @expected_fence
                  AND head_hash = @expected_hash
                """, connection, npgsqlTransaction))
            {
                AddStreamParameters(advanceHead, request);
                advanceHead.Parameters.AddWithValue("next_sequence", nextSequence);
                advanceHead.Parameters.AddWithValue("next_fence", nextFence);
                advanceHead.Parameters.AddWithValue("record_hash", recordHash);
                advanceHead.Parameters.AddWithValue("now", now);
                advanceHead.Parameters.AddWithValue("expected_sequence", sequence);
                advanceHead.Parameters.AddWithValue("expected_fence", fence);
                advanceHead.Parameters.AddWithValue("expected_hash", headHash);
                if (await advanceHead.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw ProjectConfigException.Conflict("The governance decision stream head changed.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(decisionId, decisionId, nextSequence, nextFence, headHash, recordHash, Replayed: false);
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation)
        {
            throw ProjectConfigException.Conflict("The governance decision append conflicted with another request.");
        }
    }

    private static async Task<ProjectGovernanceDecisionReceipt?> FindReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectGovernanceDecisionAppendRequest request,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT decision_id, sequence, previous_hash, record_hash, request_digest
            FROM projects_config.project_governance_decisions
            WHERE scope_type = @scope_type AND scope_id = @scope_id
              AND resource_type = @resource_type AND resource_id = @resource_id
              AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddStreamParameters(command, request);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (!string.Equals(reader.GetString(4), requestDigest, StringComparison.Ordinal))
            throw ProjectConfigException.Conflict("The governance decision idempotency key was reused for changed content.");
        var id = reader.GetGuid(0);
        var sequence = reader.GetInt64(1);
        return new(id, id, sequence, sequence, reader.GetString(2), reader.GetString(3), Replayed: true);
    }

    private static void AddStreamParameters(
        NpgsqlCommand command,
        ProjectGovernanceDecisionAppendRequest request)
    {
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        command.Parameters.AddWithValue("resource_type", request.ResourceType);
        command.Parameters.AddWithValue("resource_id", request.ResourceId);
    }

    private static void ValidateRequest(ProjectGovernanceDecisionAppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ScopeType) ||
            string.IsNullOrWhiteSpace(request.ScopeId) || request.ScopeId.Length > 256 ||
            (request.ScopeType == NativeAcsPolicyScopeType.Platform &&
             request.ScopeId != ProjectAuthorizationOwner.PlatformResourceId) ||
            string.IsNullOrWhiteSpace(request.ResourceType) || request.ResourceType.Length > 64 ||
            string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 256 ||
            request.ExpectedSequence < 0 || request.ExpectedFenceGeneration < 0 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            !ProjectGovernanceDecisionCanonicalJson.IsSha256(request.ExpectedHeadHash))
            throw new ArgumentException("Governance decision stream identity, fence, or idempotency key is invalid.", nameof(request));
    }

    internal static ProjectConfigException HistoryRequestInvalid() =>
        ProjectConfigException.Invalid(
            "governance_decision_history_request_invalid",
            "The governance decision history request is invalid.");

    internal static string ParseRequiredResourceFilter(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maxLength ||
            value.Trim().Length != value.Length)
            throw HistoryRequestInvalid();
        return value;
    }

    internal static int ParseHistoryPageSize(string? value)
    {
        if (value is null)
            return 20;
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0') ||
            value.Any(character => character is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize is < 1 or > 100)
            throw HistoryRequestInvalid();
        return pageSize;
    }

    internal static long? ParseBeforeSequence(string? value)
    {
        if (value is null)
            return null;
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0') ||
            value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence <= 0)
            throw HistoryRequestInvalid();
        return sequence;
    }

    internal async Task<ProjectGovernanceDecisionHistoryPage> ReadHistoryAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        string resourceType,
        string resourceId,
        int pageSize,
        long? beforeSequence,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scopeType) ||
            string.IsNullOrWhiteSpace(scopeId) || scopeId.Length > 256 ||
            (scopeType == NativeAcsPolicyScopeType.Platform &&
             scopeId != ProjectAuthorizationOwner.PlatformResourceId))
            throw HistoryRequestInvalid();
        resourceType = ParseRequiredResourceFilter(resourceType, 64);
        resourceId = ParseRequiredResourceFilter(resourceId, 256);
        if (pageSize is < 1 or > 100 || beforeSequence is <= 0)
            throw HistoryRequestInvalid();

        if (beforeSequence == 1)
            return new ProjectGovernanceDecisionHistoryPage(
                scopeType, scopeId, resourceType, resourceId, [], null);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var sql = beforeSequence.HasValue
                ? """
                  SELECT sequence, decision_id, previous_hash, record_hash, recorded_at, canonical_record
                  FROM projects_config.project_governance_decisions
                  WHERE scope_type = @scope_type
                    AND scope_id = @scope_id
                    AND resource_type = @resource_type
                    AND resource_id = @resource_id
                    AND sequence < @before_sequence
                  ORDER BY sequence DESC
                  LIMIT @limit
                  """
                : """
                  SELECT sequence, decision_id, previous_hash, record_hash, recorded_at, canonical_record
                  FROM projects_config.project_governance_decisions
                  WHERE scope_type = @scope_type
                    AND scope_id = @scope_id
                    AND resource_type = @resource_type
                    AND resource_id = @resource_id
                  ORDER BY sequence DESC
                  LIMIT @limit
                  """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("scope_type", scopeType.ToString());
            command.Parameters.AddWithValue("scope_id", scopeId);
            command.Parameters.AddWithValue("resource_type", resourceType);
            command.Parameters.AddWithValue("resource_id", resourceId);
            if (beforeSequence.HasValue)
                command.Parameters.Add("before_sequence", NpgsqlDbType.Bigint).Value = beforeSequence.Value;
            command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = checked(pageSize + 1);

            var items = ImmutableArray.CreateBuilder<ProjectGovernanceDecisionHistoryItem>(pageSize);
            var hasMore = false;
            ProjectGovernanceDecisionHistoryItem? previousInPage = null;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count == pageSize)
                {
                    hasMore = true;
                    break;
                }

                var sequence = reader.GetInt64(0);
                var decisionId = reader.GetGuid(1);
                var previousHash = reader.GetString(2);
                var recordHash = reader.GetString(3);
                var recordedAt = reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime();
                var canonicalRecord = reader.GetFieldValue<byte[]>(5);

                if (sequence <= 0 ||
                    !ProjectGovernanceDecisionCanonicalJson.IsSha256(previousHash) ||
                    !ProjectGovernanceDecisionCanonicalJson.IsSha256(recordHash) ||
                    (sequence == 1 && !string.Equals(previousHash, GenesisHash, StringComparison.Ordinal)) ||
                    !ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(canonicalRecord, out var record) ||
                    record is null ||
                    record.SchemaVersion != 1 ||
                    !string.Equals(record.ScopeType, scopeType.ToString(), StringComparison.Ordinal) ||
                    !string.Equals(record.ScopeId, scopeId, StringComparison.Ordinal) ||
                    !string.Equals(record.ResourceType, resourceType, StringComparison.Ordinal) ||
                    !string.Equals(record.ResourceId, resourceId, StringComparison.Ordinal) ||
                    record.Sequence != sequence ||
                    record.DecisionId != decisionId ||
                    !string.Equals(record.PreviousHash, previousHash, StringComparison.Ordinal) ||
                    Math.Abs((record.RecordedAt.ToUniversalTime() - recordedAt).Ticks) > TimeSpan.TicksPerMicrosecond ||
                    !string.Equals(
                        ProjectGovernanceDecisionCanonicalJson.ChainHash(previousHash, canonicalRecord),
                        recordHash,
                        StringComparison.Ordinal))
                    throw CorruptRecord();

                if (previousInPage is not null &&
                    (previousInPage.Sequence != sequence + 1 ||
                     !string.Equals(previousInPage.PreviousHash, recordHash, StringComparison.Ordinal)))
                    throw CorruptRecord();

                var item = new ProjectGovernanceDecisionHistoryItem(
                    decisionId,
                    sequence,
                    previousHash,
                    recordHash,
                    recordedAt,
                    new ProjectGovernanceDecisionDetailView(
                        record.Decision.DecisionType,
                        record.Decision.Outcome,
                        record.Decision.ApprovalState,
                        record.Decision.PolicyRevision,
                        record.Decision.PolicyArtifactSha256,
                        record.Decision.SelectedRule,
                        record.Decision.InterventionPoint,
                        record.Decision.RequestId,
                        record.Decision.ActionId,
                        record.Decision.RunId,
                        record.Decision.AgentId,
                        record.Decision.RoleId,
                        record.Decision.HumanEntraOid,
                        record.Decision.TenantId,
                        record.Decision.BrokerIssuer,
                        record.Decision.BrokerSubject,
                        record.Decision.EffectiveRoleRevisions,
                        record.Decision.OwnerReceiptId,
                        record.Decision.OwnerReceiptPosition,
                        record.Decision.CompilerGeneration,
                        record.Decision.ApplicationGeneration,
                        record.Decision.CorrelationId,
                        record.Decision.DecidedAt));
                items.Add(item);
                previousInPage = item;
            }

            return new ProjectGovernanceDecisionHistoryPage(
                scopeType,
                scopeId,
                resourceType,
                resourceId,
                items.ToImmutable(),
                hasMore ? items[^1].Sequence : null);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static ProjectConfigException CorruptRecord() =>
        new(
            "governance_decision_corrupt",
            "A stored governance decision record failed its integrity check.",
            StatusCodes.Status500InternalServerError);
}
