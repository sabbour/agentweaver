using System.Data;
using System.Runtime.ExceptionServices;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal sealed record ProjectGovernanceCheckpointCreateRequest(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    string IdempotencyKey);

internal sealed record ProjectGovernanceCheckpointExport(
    Guid CheckpointId,
    string CheckpointHash,
    byte[] Content,
    bool Retained);

internal enum ProjectGovernanceCheckpointVerification
{
    Unknown,
    Matches,
    Mismatch,
}

internal sealed class ProjectGovernanceCheckpointStore(
    IDbContextFactory<ProjectsConfigDbContext> contextFactory,
    IObjectStore? retainedStore,
    TimeProvider timeProvider)
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    internal async Task<ProjectGovernanceCheckpointV1> CreateAsync(
        ProjectGovernanceCheckpointCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        var replay = await FindByIdempotencyKeyAsync(
            connection,
            npgsqlTransaction,
            request,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ValidateStoredCheckpoint(replay);
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
        string headHash;
        await using (var readHead = new NpgsqlCommand("""
            SELECT sequence, head_hash
            FROM projects_config.project_governance_decision_stream_heads
            WHERE scope_type = @scope_type AND scope_id = @scope_id
              AND resource_type = @resource_type AND resource_id = @resource_id
            FOR SHARE
            """, connection, npgsqlTransaction))
        {
            AddStreamParameters(readHead, request);
            await using var reader = await readHead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The governance decision stream head was not created.");
            sequence = reader.GetInt64(0);
            headHash = reader.GetString(1);
        }

        var checkpoint = ProjectGovernanceCheckpointCanonicalJson.Create(
            Guid.NewGuid(),
            request.ScopeType.ToString(),
            request.ScopeId,
            request.ResourceType,
            request.ResourceId,
            sequence,
            headHash,
            timeProvider.GetUtcNow());
        var canonicalCheckpoint = ProjectGovernanceCheckpointCanonicalJson.Serialize(checkpoint);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO projects_config.project_governance_decision_checkpoints
                (checkpoint_id, scope_type, scope_id, resource_type, resource_id, sequence,
                 idempotency_key, head_hash, checkpoint_hash, canonical_checkpoint)
            VALUES
                (@checkpoint_id, @scope_type, @scope_id, @resource_type, @resource_id, @sequence,
                 @idempotency_key, @head_hash, @checkpoint_hash, @canonical_checkpoint)
            """, connection, npgsqlTransaction))
        {
            AddStreamParameters(insert, request);
            insert.Parameters.AddWithValue("checkpoint_id", checkpoint.CheckpointId);
            insert.Parameters.AddWithValue("sequence", checkpoint.Sequence);
            insert.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
            insert.Parameters.AddWithValue("head_hash", checkpoint.HeadHash);
            insert.Parameters.AddWithValue("checkpoint_hash", checkpoint.CheckpointHash);
            insert.Parameters.AddWithValue("canonical_checkpoint", NpgsqlDbType.Bytea, canonicalCheckpoint);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    internal async Task<ProjectGovernanceCheckpointExport> ExportAsync(
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        var row = await ReadCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Governance checkpoint '{checkpointId}' was not found.");
        var checkpoint = ValidateStoredCheckpoint(row);
        if (retainedStore is null)
            return new(checkpoint.CheckpointId, checkpoint.CheckpointHash, row.CanonicalCheckpoint, Retained: false);

        var key = CheckpointObjectKey(checkpoint.CheckpointHash);
        await WriteOrConfirmAsync(key, row.CanonicalCheckpoint, cancellationToken).ConfigureAwait(false);
        return new(checkpoint.CheckpointId, checkpoint.CheckpointHash, row.CanonicalCheckpoint, Retained: true);
    }

    internal async Task<ProjectGovernanceCheckpointVerification> VerifyRetainedAsync(
        string checkpointHash,
        CancellationToken cancellationToken)
    {
        if (retainedStore is null || !ProjectGovernanceDecisionCanonicalJson.IsSha256(checkpointHash))
            return ProjectGovernanceCheckpointVerification.Unknown;

        using var retained = await retainedStore.ReadAsync(
            CheckpointObjectKey(checkpointHash),
            cancellationToken).ConfigureAwait(false);
        if (retained is null)
            return ProjectGovernanceCheckpointVerification.Unknown;
        if (retained.Length > ProjectGovernanceCheckpointCanonicalJson.MaximumCanonicalBytes)
            return ProjectGovernanceCheckpointVerification.Mismatch;

        var bytes = new byte[checked((int)retained.Length)];
        await retained.Content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await retained.Content.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 ||
            !ProjectGovernanceCheckpointCanonicalJson.TryDeserialize(bytes, out var checkpoint) ||
            checkpoint is null ||
            !string.Equals(checkpoint.CheckpointHash, checkpointHash, StringComparison.Ordinal))
            return ProjectGovernanceCheckpointVerification.Mismatch;

        return await VerifyChainAsync(checkpoint, cancellationToken).ConfigureAwait(false)
            ? ProjectGovernanceCheckpointVerification.Matches
            : ProjectGovernanceCheckpointVerification.Mismatch;
    }

    private async Task<bool> VerifyChainAsync(
        ProjectGovernanceCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var readHead = new NpgsqlCommand("""
                SELECT sequence, head_hash
                FROM projects_config.project_governance_decision_stream_heads
                WHERE scope_type = @scope_type AND scope_id = @scope_id
                  AND resource_type = @resource_type AND resource_id = @resource_id
                """, connection))
            {
                AddCheckpointStreamParameters(readHead, checkpoint);
                await using var headReader = await readHead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await headReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return checkpoint.Sequence == 0 && checkpoint.HeadHash == GenesisHash;
                var sequence = headReader.GetInt64(0);
                var headHash = headReader.GetString(1);
                if (sequence < checkpoint.Sequence ||
                    sequence == checkpoint.Sequence && headHash != checkpoint.HeadHash)
                    return false;
            }

            await using var command = new NpgsqlCommand("""
                SELECT sequence, decision_id, previous_hash, record_hash, canonical_record
                FROM projects_config.project_governance_decisions
                WHERE scope_type = @scope_type AND scope_id = @scope_id
                  AND resource_type = @resource_type AND resource_id = @resource_id
                  AND sequence <= @sequence
                ORDER BY sequence
                """, connection);
            AddCheckpointStreamParameters(command, checkpoint);
            command.Parameters.AddWithValue("sequence", checkpoint.Sequence);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            long lastSequence = 0;
            var previousHash = GenesisHash;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sequence = reader.GetInt64(0);
                var decisionId = reader.GetGuid(1);
                var rowPreviousHash = reader.GetString(2);
                var storedHash = reader.GetString(3);
                var canonicalRecord = reader.GetFieldValue<byte[]>(4);
                if (sequence != lastSequence + 1 ||
                    !string.Equals(rowPreviousHash, previousHash, StringComparison.Ordinal) ||
                    !ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(
                        canonicalRecord,
                        out var record) ||
                    record is null ||
                    record.SchemaVersion != 1 ||
                    record.ScopeType != checkpoint.ScopeType ||
                    record.ScopeId != checkpoint.ScopeId ||
                    record.ResourceType != checkpoint.ResourceType ||
                    record.ResourceId != checkpoint.ResourceId ||
                    record.Sequence != sequence ||
                    record.DecisionId != decisionId ||
                    record.PreviousHash != previousHash ||
                    !string.Equals(
                        ProjectGovernanceDecisionCanonicalJson.ChainHash(previousHash, canonicalRecord),
                        storedHash,
                        StringComparison.Ordinal))
                    return false;

                lastSequence = sequence;
                previousHash = storedHash;
            }

            return lastSequence == checkpoint.Sequence &&
                string.Equals(previousHash, checkpoint.HeadHash, StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteOrConfirmAsync(
        ObjectKey key,
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new MemoryStream(content, writable: false);
            await retainedStore!.WriteAsync(key, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception writeException) when (!cancellationToken.IsCancellationRequested)
        {
            ObjectRead? existing;
            try
            {
                existing = await retainedStore!.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ExceptionDispatchInfo.Capture(writeException).Throw();
                throw;
            }

            using (existing)
            {
                if (existing is not null && await ContentMatchesAsync(existing, content, cancellationToken).ConfigureAwait(false))
                    return;
                if (existing is not null)
                    throw new InvalidDataException("A retained governance checkpoint has different content.");
            }

            ExceptionDispatchInfo.Capture(writeException).Throw();
        }
    }

    private static async Task<bool> ContentMatchesAsync(
        ObjectRead existing,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        if (existing.Length != expected.Length)
            return false;
        var actual = new byte[expected.Length];
        await existing.Content.ReadExactlyAsync(actual, cancellationToken).ConfigureAwait(false);
        return await existing.Content.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) == 0 &&
            actual.AsSpan().SequenceEqual(expected);
    }

    private async Task<CheckpointRow?> FindByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectGovernanceCheckpointCreateRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT checkpoint_id, scope_type, scope_id, resource_type, resource_id,
                   sequence, head_hash, checkpoint_hash, canonical_checkpoint
            FROM projects_config.project_governance_decision_checkpoints
            WHERE scope_type = @scope_type AND scope_id = @scope_id
              AND resource_type = @resource_type AND resource_id = @resource_id
              AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddStreamParameters(command, request);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var row = ReadRow(reader);
        var checkpoint = ValidateStoredCheckpoint(row);
        if (checkpoint.ScopeType != request.ScopeType.ToString() ||
            checkpoint.ScopeId != request.ScopeId ||
            checkpoint.ResourceType != request.ResourceType ||
            checkpoint.ResourceId != request.ResourceId)
            throw new InvalidDataException("The stored governance checkpoint does not match its stream.");
        return row;
    }

    private async Task<CheckpointRow?> ReadCheckpointAsync(
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand("""
            SELECT checkpoint_id, scope_type, scope_id, resource_type, resource_id,
                   sequence, head_hash, checkpoint_hash, canonical_checkpoint
            FROM projects_config.project_governance_decision_checkpoints
            WHERE checkpoint_id = @checkpoint_id
            """, connection);
            command.Parameters.AddWithValue("checkpoint_id", checkpointId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRow(reader) : null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static CheckpointRow ReadRow(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<byte[]>(8));

    private static ProjectGovernanceCheckpointV1 ValidateStoredCheckpoint(CheckpointRow row)
    {
        if (!ProjectGovernanceCheckpointCanonicalJson.TryDeserialize(row.CanonicalCheckpoint, out var checkpoint) ||
            checkpoint is null ||
            checkpoint.CheckpointId != row.CheckpointId ||
            checkpoint.ScopeType != row.ScopeType ||
            checkpoint.ScopeId != row.ScopeId ||
            checkpoint.ResourceType != row.ResourceType ||
            checkpoint.ResourceId != row.ResourceId ||
            checkpoint.Sequence != row.Sequence ||
            checkpoint.HeadHash != row.HeadHash ||
            checkpoint.CheckpointHash != row.CheckpointHash)
            throw new InvalidDataException("The stored governance checkpoint is invalid.");
        return checkpoint;
    }

    private static ObjectKey CheckpointObjectKey(string checkpointHash) =>
        new($"governance-checkpoints/v1/{checkpointHash}.json");

    private static void AddStreamParameters(
        NpgsqlCommand command,
        ProjectGovernanceCheckpointCreateRequest request)
    {
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        command.Parameters.AddWithValue("resource_type", request.ResourceType);
        command.Parameters.AddWithValue("resource_id", request.ResourceId);
    }

    private static void AddCheckpointStreamParameters(
        NpgsqlCommand command,
        ProjectGovernanceCheckpointV1 checkpoint)
    {
        command.Parameters.AddWithValue("scope_type", checkpoint.ScopeType);
        command.Parameters.AddWithValue("scope_id", checkpoint.ScopeId);
        command.Parameters.AddWithValue("resource_type", checkpoint.ResourceType);
        command.Parameters.AddWithValue("resource_id", checkpoint.ResourceId);
    }

    private static void ValidateRequest(ProjectGovernanceCheckpointCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ScopeType) ||
            string.IsNullOrWhiteSpace(request.ScopeId) || request.ScopeId.Length > 256 ||
            (request.ScopeType == NativeAcsPolicyScopeType.Platform &&
             request.ScopeId != ProjectAuthorizationOwner.PlatformResourceId) ||
            string.IsNullOrWhiteSpace(request.ResourceType) || request.ResourceType.Length > 64 ||
            string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 128 ||
            request.IdempotencyKey != request.IdempotencyKey.Trim())
            throw new ArgumentException("Governance checkpoint stream identity or idempotency key is invalid.", nameof(request));
    }

    private sealed record CheckpointRow(
        Guid CheckpointId,
        string ScopeType,
        string ScopeId,
        string ResourceType,
        string ResourceId,
        long Sequence,
        string HeadHash,
        string CheckpointHash,
        byte[] CanonicalCheckpoint);
}
