using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed record MafCheckpointBinding(
    SessionIdentity Identity,
    CoordinationActor Actor,
    long ExecutionFence,
    string SdkVersion,
    string PinnedModelReference,
    ObjectKey? CacheReference,
    string StoreName = "coordinator");

internal enum CheckpointCacheRecovery
{
    UseCheckpoint,
    RebuildFromJournal,
    CheckpointMissing
}

internal sealed record CheckpointRecoveryDecision(
    CheckpointCacheRecovery Recovery,
    string? Reason);

internal sealed class CheckpointJournalRebuildRequiredException(string reason)
    : InvalidOperationException(reason);

internal sealed class PostgresMafCheckpointStore : JsonCheckpointStore
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumCheckpointPayloadBytes = 4 * 1024 * 1024;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly IObjectStore? _objectStore;
    private readonly TimeProvider _timeProvider;
    private readonly MafCheckpointBinding? _binding;

    public PostgresMafCheckpointStore(
        NpgsqlDataSource dataSource,
        string schema,
        IObjectStore? objectStore,
        TimeProvider? timeProvider = null)
        : this(dataSource, schema, objectStore, timeProvider, null)
    {
    }

    private PostgresMafCheckpointStore(
        NpgsqlDataSource dataSource,
        string schema,
        IObjectStore? objectStore,
        TimeProvider? timeProvider,
        MafCheckpointBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
        _objectStore = objectStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _binding = binding;
    }

    internal PostgresMafCheckpointStore ForRun(MafCheckpointBinding binding) =>
        new(_dataSource, _schema.Trim('"'), _objectStore, _timeProvider, ValidateBinding(binding));

    internal ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    internal async Task<CheckpointInfo> AppendInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        string sessionId,
        string checkpointId,
        JsonElement value,
        CheckpointInfo? parent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var validatedBinding = ValidateBinding(binding);
        if (RequireBinding(sessionId) != validatedBinding)
            throw new InvalidOperationException("A checkpoint can only be appended through its bound run store.");
        ValidateCheckpoint(value, sessionId, parent);
        if (!IsIdentifier(checkpointId))
            throw new ArgumentException("A stable checkpoint identifier is required.", nameof(checkpointId));

        if (validatedBinding.CacheReference is { } cacheReference)
            await RequireCacheObjectAsync(cacheReference, cancellationToken).ConfigureAwait(false);

        var selection = await ReadCurrentBindingAsync(
            connection, transaction, validatedBinding, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (parent is not null && !await CheckpointExistsAsync(
                connection, transaction, validatedBinding, parent.CheckpointId, cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException("checkpoint_parent_not_found", StatusCodes.Status409Conflict);

        var payload = value.GetRawText();
        if (await FindExistingCheckpointAsync(
                connection, transaction, validatedBinding, selection.SelectionHash, checkpointId,
                parent?.CheckpointId, payload, cancellationToken).ConfigureAwait(false) is { } existing)
            return existing;

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.maf_workflow_checkpoints
                (project_id, run_id, session_id, store_name, checkpoint_id, parent_checkpoint_id,
                 payload, cache_object_key, cache_sdk_version, pinned_model_reference,
                 accepted_selection_hash, execution_fence, created_at)
            VALUES
                (@project, @run, @session, @store, @checkpoint, @parent, @payload, @cache,
                 @sdk, @model, @selectionHash, @fence, @createdAt)
            """, connection, transaction);
        AddBinding(insert, validatedBinding);
        insert.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, validatedBinding.StoreName);
        insert.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        insert.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar,
            (object?)parent?.CheckpointId ?? DBNull.Value);
        insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        insert.Parameters.AddWithValue("cache", NpgsqlDbType.Varchar,
            (object?)validatedBinding.CacheReference?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("sdk", NpgsqlDbType.Varchar, validatedBinding.SdkVersion);
        insert.Parameters.AddWithValue("model", NpgsqlDbType.Varchar, validatedBinding.PinnedModelReference);
        insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selection.SelectionHash);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, validatedBinding.ExecutionFence);
        insert.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new CheckpointInfo(sessionId, checkpointId);
    }

    internal PostgresMafCheckpointStore ForCacheReference(ObjectKey? cacheReference)
    {
        var binding = RequireBinding();
        return ForRun(binding with { CacheReference = cacheReference });
    }

    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        var binding = RequireBinding(sessionId);
        ValidateCheckpoint(value, sessionId, parent);
        var checkpointId = Guid.NewGuid().ToString("N");
        if (binding.CacheReference is { } cacheReference)
            await RequireCacheObjectAsync(cacheReference, CancellationToken.None).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var checkpoint = await AppendInTransactionAsync(
            connection,
            transaction,
            binding,
            sessionId,
            checkpointId,
            value,
            parent,
            CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return checkpoint;
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var binding = RequireBinding(sessionId);
        var decision = await GetRecoveryDecisionAsync(
            binding, key.CheckpointId, binding.SdkVersion, binding.PinnedModelReference, CancellationToken.None)
            .ConfigureAwait(false);
        if (decision.Recovery != CheckpointCacheRecovery.UseCheckpoint)
            throw new CheckpointJournalRebuildRequiredException(
                decision.Reason ?? "The SDK cache is unavailable; rebuild context from the run journal.");

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        _ = await ReadCurrentBindingAsync(
            connection, transaction, binding, lockRows: true, CancellationToken.None).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT payload
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND checkpoint_id = @checkpoint AND execution_fence = @fence
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, key.CheckpointId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        var payload = (string?)await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"MAF checkpoint '{key.CheckpointId}' was not found for the bound run session.");
        await transaction.CommitAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return RestoreMetadataOrder(document.RootElement);
    }

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null)
    {
        var binding = RequireBinding(sessionId);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        _ = await ReadCurrentBindingAsync(
            connection, transaction, binding, lockRows: true, CancellationToken.None).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT checkpoint_id, parent_checkpoint_id, has_parent_metadata
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND execution_fence = @fence
            ORDER BY created_at, checkpoint_id
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        await using var reader = await command.ExecuteReaderAsync();
        var checkpoints = new List<CheckpointInfo>();
        while (await reader.ReadAsync())
        {
            var parentId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var hasParentMetadata = reader.GetBoolean(2);
            if (withParent is null || !hasParentMetadata ||
                string.Equals(parentId, withParent.CheckpointId, StringComparison.Ordinal))
                checkpoints.Add(new CheckpointInfo(sessionId, reader.GetString(0)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return checkpoints;
    }

    internal async Task<(CheckpointInfo Info, JsonElement Value)?> ReadLatestCheckpointAsync(
        MafCheckpointBinding binding,
        CancellationToken cancellationToken)
    {
        var validatedBinding = ValidateBinding(binding);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var latest = await ReadLatestCheckpointInTransactionAsync(
            connection, transaction, validatedBinding, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return latest;
    }

    internal async Task<(CheckpointInfo Info, JsonElement Value)?> ReadLatestCheckpointInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        CancellationToken cancellationToken) =>
        await ReadLatestCheckpointInTransactionCoreAsync(
            connection, transaction, binding, requireCompletedRootEvidence: false, cancellationToken)
            .ConfigureAwait(false);

    internal Task<(CheckpointInfo Info, JsonElement Value)?> ReadLatestCheckpointForCompletedRootEvidenceInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        CancellationToken cancellationToken) =>
        ReadLatestCheckpointInTransactionCoreAsync(
            connection, transaction, binding, requireCompletedRootEvidence: true, cancellationToken);

    private async Task<(CheckpointInfo Info, JsonElement Value)?> ReadLatestCheckpointInTransactionCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        bool requireCompletedRootEvidence,
        CancellationToken cancellationToken)
    {
        var validatedBinding = ValidateBinding(binding);
        if (requireCompletedRootEvidence &&
            !string.Equals(
                validatedBinding.StoreName, MafExecutionCheckpointContract.StoreName, StringComparison.Ordinal))
            throw new CoordinationException(
                "checkpoint_session_unavailable", StatusCodes.Status409Conflict);
        var currentBinding = await ReadCurrentBindingAsync(
            connection, transaction, validatedBinding, lockRows: true,
            requireCompletedRootEvidence, cancellationToken).ConfigureAwait(false);
        var selectionHashPredicate = requireCompletedRootEvidence
            ? "AND checkpoint.accepted_selection_hash = @selectionHash"
            : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT checkpoint.checkpoint_id, checkpoint.payload
            FROM {_schema}.maf_workflow_checkpoints checkpoint
            WHERE checkpoint.project_id = @project AND checkpoint.run_id = @run
                AND checkpoint.session_id = @session AND checkpoint.store_name = @store
                AND checkpoint.execution_fence = @fence
                {selectionHashPredicate}
                AND NOT EXISTS (
                    SELECT 1 FROM {_schema}.maf_workflow_checkpoints child
                    WHERE child.project_id = checkpoint.project_id
                        AND child.run_id = checkpoint.run_id
                        AND child.session_id = checkpoint.session_id
                        AND child.store_name = checkpoint.store_name
                        AND child.execution_fence = checkpoint.execution_fence
                        AND child.parent_checkpoint_id = checkpoint.checkpoint_id)
            ORDER BY checkpoint.created_at DESC, checkpoint.checkpoint_id
            LIMIT 2
            """, connection, transaction);
        AddBinding(command, validatedBinding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, validatedBinding.StoreName);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, validatedBinding.ExecutionFence);
        if (requireCompletedRootEvidence)
            command.Parameters.AddWithValue(
                "selectionHash", NpgsqlDbType.Char, currentBinding.SelectionHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var checkpointId = reader.GetString(0);
        using var document = JsonDocument.Parse(reader.GetString(1));
        var value = document.RootElement.Clone();
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "maf_execution_checkpoint_forked", StatusCodes.Status409Conflict);
        return (new CheckpointInfo(validatedBinding.Identity.SessionId, checkpointId), value);
    }

    internal async Task<CheckpointRecoveryDecision> GetRecoveryDecisionAsync(
        MafCheckpointBinding binding,
        string checkpointId,
        string currentSdkVersion,
        string currentPinnedModelReference,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding);
        if (!IsIdentifier(checkpointId) || !IsMetadataValue(currentSdkVersion, 128) ||
            !IsMetadataValue(currentPinnedModelReference, 256))
            throw new CoordinationException("checkpoint_reference_invalid", StatusCodes.Status400BadRequest);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentBinding = await ReadCurrentBindingAsync(
            connection, transaction, binding, lockRows: true, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT cache_object_key, cache_sdk_version, pinned_model_reference
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND checkpoint_id = @checkpoint
                AND execution_fence = @fence
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new CheckpointRecoveryDecision(CheckpointCacheRecovery.CheckpointMissing, "checkpoint_missing");
        ObjectKey? cacheKey = reader.IsDBNull(0) ? null : new ObjectKey(reader.GetString(0));
        var sdkVersion = reader.GetString(1);
        var pinnedModelReference = reader.GetString(2);
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (cacheKey is null)
            return new CheckpointRecoveryDecision(
                CheckpointCacheRecovery.RebuildFromJournal, "sdk_cache_reference_missing");
        if (!string.Equals(sdkVersion, currentSdkVersion, StringComparison.Ordinal) ||
            !string.Equals(pinnedModelReference, currentPinnedModelReference, StringComparison.Ordinal))
            return new CheckpointRecoveryDecision(
                CheckpointCacheRecovery.RebuildFromJournal, "sdk_cache_binding_incompatible");
        if (_objectStore is null)
            return new CheckpointRecoveryDecision(
                CheckpointCacheRecovery.RebuildFromJournal, "object_store_unavailable");
        using var cachedObject = await _objectStore.ReadAsync(cacheKey.Value, cancellationToken)
            .ConfigureAwait(false);
        await using var revalidationConnection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var revalidationTransaction = await revalidationConnection.BeginTransactionAsync(cancellationToken);
        var refreshedBinding = await ReadCurrentBindingAsync(
            revalidationConnection,
            revalidationTransaction,
            binding,
            lockRows: true,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentBinding.SelectionHash, refreshedBinding.SelectionHash, StringComparison.Ordinal))
            throw new CoordinationException("checkpoint_run_selection_changed", StatusCodes.Status409Conflict);
        await revalidationTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return cachedObject is null
            ? new CheckpointRecoveryDecision(
                CheckpointCacheRecovery.RebuildFromJournal, "sdk_cache_object_missing")
            : new CheckpointRecoveryDecision(CheckpointCacheRecovery.UseCheckpoint, null);
    }

    private async Task RequireCacheObjectAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        if (_objectStore is null)
            throw new CheckpointJournalRebuildRequiredException(
                "The Object Store is unavailable; the SDK cache cannot be checkpointed.");
        using var cache = await _objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (cache is null)
            throw new CheckpointJournalRebuildRequiredException(
                "The SDK cache object is missing; rebuild context from the run journal.");
    }

    private async Task<CurrentBinding> ReadCurrentBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        bool lockRows,
        CancellationToken cancellationToken) =>
        await ReadCurrentBindingAsync(
            connection, transaction, binding, lockRows,
            requireCompletedRootEvidence: false, cancellationToken).ConfigureAwait(false);

    private async Task<CurrentBinding> ReadCurrentBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        bool lockRows,
        bool requireCompletedRootEvidence,
        CancellationToken cancellationToken)
    {
        var lockClause = lockRows ? "FOR SHARE OF r, s" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_selection, r.accepted_selection_hash, r.execution_fence,
                s.writer_issuer, s.writer_subject, s.execution_fence, s.lifecycle_state,
                s.parent_session_id, s.turn_state
            FROM {_schema}.accepted_runs r
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @session
            {lockClause}
            """, connection, transaction);
        AddBinding(command, binding);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("checkpoint_owner_binding_missing", StatusCodes.Status404NotFound);

        var selection = JsonDocument.Parse(reader.GetString(0));
        var selectionHash = reader.GetString(1).Trim();
        var runFence = reader.GetInt64(2);
        var writerIssuer = reader.GetString(3);
        var writerSubject = reader.GetString(4);
        var sessionFence = reader.GetInt64(5);
        var lifecycle = reader.GetString(6);
        var parentSessionId = reader.IsDBNull(7) ? null : reader.GetString(7);
        var turnState = reader.GetString(8);
        var modelReference = ReadSelectedModelReference(selection.RootElement);
        selection.Dispose();

        if (writerIssuer != binding.Actor.Issuer || writerSubject != binding.Actor.Subject)
            throw new CoordinationException("checkpoint_owner_actor_mismatch", StatusCodes.Status403Forbidden);
        if (runFence != binding.ExecutionFence || sessionFence != binding.ExecutionFence)
            throw new CoordinationException("checkpoint_execution_fence_stale", StatusCodes.Status409Conflict);
        if (requireCompletedRootEvidence
            ? parentSessionId is not null || lifecycle != "completed" || turnState != "completed"
            : lifecycle != "active")
            throw new CoordinationException("checkpoint_session_unavailable", StatusCodes.Status409Conflict);
        if (modelReference != binding.PinnedModelReference)
            throw new CoordinationException("checkpoint_model_binding_mismatch", StatusCodes.Status409Conflict);
        await reader.DisposeAsync().ConfigureAwait(false);
        return new CurrentBinding(selectionHash);
    }

    private async Task<bool> CheckpointExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        string checkpointId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_schema}.maf_workflow_checkpoints
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                    AND store_name = @store AND checkpoint_id = @checkpoint
                    AND execution_fence = @fence
            )
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? false);
    }

    private async Task<CheckpointInfo?> FindExistingCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        string selectionHash,
        string checkpointId,
        string? parentCheckpointId,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload = @payload, parent_checkpoint_id, cache_object_key,
                   cache_sdk_version, pinned_model_reference, accepted_selection_hash, execution_fence
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND checkpoint_id = @checkpoint
            FOR UPDATE
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var samePayload = reader.GetBoolean(0);
        var storedParent = reader.IsDBNull(1) ? null : reader.GetString(1);
        var storedCache = reader.IsDBNull(2) ? null : reader.GetString(2);
        var storedSelectionHash = reader.GetString(5).TrimEnd();
        if (!samePayload ||
            !string.Equals(storedParent, parentCheckpointId, StringComparison.Ordinal) ||
            !string.Equals(storedCache, binding.CacheReference?.Value, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), binding.SdkVersion, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), binding.PinnedModelReference, StringComparison.Ordinal) ||
            !string.Equals(storedSelectionHash, selectionHash, StringComparison.Ordinal) ||
            reader.GetInt64(6) != binding.ExecutionFence)
            throw new CoordinationException("checkpoint_idempotency_conflict", StatusCodes.Status409Conflict);

        return new CheckpointInfo(binding.Identity.SessionId, checkpointId);
    }

    private static void ValidateCheckpoint(
        JsonElement value,
        string sessionId,
        CheckpointInfo? parent)
    {
        if (!IsMetadataValue(sessionId, 256))
            throw new ArgumentException("A checkpoint session identifier is required.", nameof(sessionId));
        if (value.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumCheckpointPayloadBytes)
            throw new InvalidOperationException("A bounded MAF checkpoint object is required.");
        if (parent is not null &&
            (!string.Equals(parent.SessionId, sessionId, StringComparison.Ordinal) ||
             !IsIdentifier(parent.CheckpointId)))
            throw new CoordinationException("checkpoint_parent_not_found", StatusCodes.Status409Conflict);
    }

    internal static string ReadSelectedModelReference(JsonElement selection)
    {
        if (selection.ValueKind == JsonValueKind.Object &&
            selection.TryGetProperty("modelSelection", out var model) &&
            model.ValueKind == JsonValueKind.Object &&
            model.TryGetProperty("reference", out var reference) &&
            reference.ValueKind == JsonValueKind.String &&
            IsMetadataValue(reference.GetString(), 256))
            return reference.GetString()!;
        throw new InvalidOperationException("The accepted run selection has no valid pinned model reference.");
    }

    private MafCheckpointBinding RequireBinding(string? sessionId = null)
    {
        var binding = _binding
            ?? throw new InvalidOperationException("A MAF checkpoint store must be bound to an accepted run.");
        if (sessionId is not null && !string.Equals(sessionId, binding.Identity.SessionId, StringComparison.Ordinal))
            throw new CoordinationException("checkpoint_session_mismatch", StatusCodes.Status403Forbidden);
        return binding;
    }

    private static MafCheckpointBinding ValidateBinding(MafCheckpointBinding binding)
    {
        if (!IsMetadataValue(binding.Identity.ProjectId, 256) ||
            !IsMetadataValue(binding.Identity.RunId, 256) ||
            !IsMetadataValue(binding.Identity.SessionId, 256) ||
            !IsMetadataValue(binding.Actor.Issuer, 512) ||
            !IsMetadataValue(binding.Actor.Subject, 256) ||
            binding.ExecutionFence < 1 ||
            !IsMetadataValue(binding.SdkVersion, 128) ||
            !IsMetadataValue(binding.PinnedModelReference, 256) ||
            !IsIdentifier(binding.StoreName))
            throw new ArgumentException("A complete accepted run checkpoint binding is required.", nameof(binding));
        return binding;
    }

    private static bool IsMetadataValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsIdentifier(string? value) =>
        IsMetadataValue(value, 128) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static void AddBinding(NpgsqlCommand command, MafCheckpointBinding binding)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.Identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.Identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.Identity.SessionId);
    }

    internal static JsonElement RestoreMetadataOrder(JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteWithMetadataFirst(payload, writer);
        using var restored = JsonDocument.Parse(stream.ToArray());
        return restored.RootElement.Clone();
    }

    private static void WriteWithMetadataFirst(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var name in new[] { "$id", "$type" })
                    if (element.TryGetProperty(name, out var metadata))
                    {
                        writer.WritePropertyName(name);
                        WriteWithMetadataFirst(metadata, writer);
                    }
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "$id" or "$type")
                        continue;
                    writer.WritePropertyName(property.Name);
                    WriteWithMetadataFirst(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteWithMetadataFirst(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed record CurrentBinding(string SelectionHash);
}
