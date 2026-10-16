using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Binds a MAF checkpoint store to an authenticated actor, accepted run, execution fence, model, and optional cache object.
/// </summary>
internal sealed record MafCheckpointBinding(
    SessionIdentity Identity,
    CoordinationActor Actor,
    long ExecutionFence,
    string SdkVersion,
    string PinnedModelReference,
    ObjectKey? CacheReference,
    string StoreName = "coordinator");

/// <summary>
/// Describes whether an SDK checkpoint cache can be used or must be rebuilt from the durable run journal.
/// </summary>
internal enum CheckpointCacheRecovery
{
    /// <summary>The checkpoint and matching SDK cache are available.</summary>
    UseCheckpoint,
    /// <summary>The checkpoint is present, but its cache cannot be used and context must be rebuilt.</summary>
    RebuildFromJournal,
    /// <summary>No checkpoint with the requested ID exists in this bound run.</summary>
    CheckpointMissing
}

/// <summary>
/// Reports a checkpoint recovery decision and an optional diagnostic reason.
/// </summary>
internal sealed record CheckpointRecoveryDecision(
    CheckpointCacheRecovery Recovery,
    string? Reason);

/// <summary>
/// Signals that the SDK checkpoint cannot be retrieved and the caller must rebuild from its durable journal.
/// </summary>
internal sealed class CheckpointJournalRebuildRequiredException(string reason)
    : InvalidOperationException(reason);

/// <summary>
/// Persists bound MAF workflow checkpoints in PostgreSQL and validates their current run authority on access.
/// </summary>
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

    /// <summary>
    /// Creates an unbound checkpoint store, which must be bound to a run before checkpoint operations are used.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the schema is invalid or reserved.</exception>
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

    /// <summary>
    /// Creates a run-bound view of this store after validating the owner and checkpoint binding.
    /// </summary>
    internal PostgresMafCheckpointStore ForRun(MafCheckpointBinding binding) =>
        new(_dataSource, _schema.Trim('"'), _objectStore, _timeProvider, ValidateBinding(binding));

    /// <summary>
    /// Opens a PostgreSQL connection from the configured data source.
    /// </summary>
    internal ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>
    /// Appends an idempotent checkpoint using the caller's transaction after locking and validating current run state.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the owner binding, selection, parent, or idempotent checkpoint differs.</exception>
    internal async Task<(CheckpointInfo Info, string? ParentCheckpointId, bool HasParentMetadata)> AppendInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        string sessionId,
        string checkpointId,
        JsonElement value,
        CheckpointInfo? parent,
        CancellationToken cancellationToken,
        byte[]? workspaceSeed = null)
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
        ValidateOriginWorkspaceSeed(
            value, validatedBinding, checkpointId, selection.SelectionHash, workspaceSeed);
        if (parent is not null && !await CheckpointExistsAsync(
                connection, transaction, validatedBinding, parent.CheckpointId, cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException("checkpoint_parent_not_found", StatusCodes.Status409Conflict);

        var payload = value.GetRawText();
        if (await FindExistingCheckpointAsync(
                connection, transaction, validatedBinding, selection.SelectionHash, checkpointId,
                parent?.CheckpointId, payload, workspaceSeed, cancellationToken).ConfigureAwait(false) is { } existing)
            return existing;

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.maf_workflow_checkpoints
                (project_id, run_id, session_id, store_name, checkpoint_id, parent_checkpoint_id,
                 has_parent_metadata,
                 payload, cache_object_key, cache_sdk_version, pinned_model_reference,
                 accepted_selection_hash, execution_fence, created_at, workspace_seed)
            VALUES
                (@project, @run, @session, @store, @checkpoint, @parent, @hasParentMetadata, @payload, @cache,
                 @sdk, @model, @selectionHash, @fence, @createdAt, @workspaceSeed)
            """, connection, transaction);
        AddBinding(insert, validatedBinding);
        insert.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, validatedBinding.StoreName);
        insert.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        insert.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar,
            (object?)parent?.CheckpointId ?? DBNull.Value);
        insert.Parameters.AddWithValue("hasParentMetadata", NpgsqlDbType.Boolean, true);
        insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        insert.Parameters.AddWithValue("cache", NpgsqlDbType.Varchar,
            (object?)validatedBinding.CacheReference?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("sdk", NpgsqlDbType.Varchar, validatedBinding.SdkVersion);
        insert.Parameters.AddWithValue("model", NpgsqlDbType.Varchar, validatedBinding.PinnedModelReference);
        insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selection.SelectionHash);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, validatedBinding.ExecutionFence);
        insert.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow());
        insert.Parameters.AddWithValue("workspaceSeed", NpgsqlDbType.Bytea, (object?)workspaceSeed ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (new CheckpointInfo(sessionId, checkpointId), parent?.CheckpointId, true);
    }

    /// <summary>
    /// Creates a bound store view using the supplied object-store cache reference.
    /// </summary>
    internal PostgresMafCheckpointStore ForCacheReference(ObjectKey? cacheReference)
    {
        var binding = RequireBinding();
        return ForRun(binding with { CacheReference = cacheReference });
    }

    /// <summary>
    /// Creates and commits a checkpoint for the store's bound run.
    /// </summary>
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
        return checkpoint.Info;
    }

    /// <summary>
    /// Retrieves a checkpoint payload after confirming that its SDK cache and current run binding are usable.
    /// </summary>
    /// <exception cref="CheckpointJournalRebuildRequiredException">Thrown when the cache must be rebuilt from the journal.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the checkpoint ID is not stored for this run.</exception>
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

    /// <summary>
    /// Lists checkpoints for the bound run, optionally filtering by their parent checkpoint.
    /// </summary>
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

    /// <summary>
    /// Reads the latest checkpoint for a run using a dedicated transaction.
    /// </summary>
    internal async Task<(
        CheckpointInfo Info,
        JsonElement Value,
        string? ParentCheckpointId,
        bool HasParentMetadata)?> ReadLatestCheckpointAsync(
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

    /// <summary>
    /// Reads the latest checkpoint within the caller's transaction.
    /// </summary>
    internal async Task<(
        CheckpointInfo Info,
        JsonElement Value,
        string? ParentCheckpointId,
        bool HasParentMetadata)?> ReadLatestCheckpointInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        CancellationToken cancellationToken) =>
        await ReadLatestCheckpointInTransactionCoreAsync(
            connection, transaction, binding, requireCompletedRootEvidence: false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Reads the latest root checkpoint only when completed-root evidence is allowed by current owner state.
    /// </summary>
    internal Task<(
        CheckpointInfo Info,
        JsonElement Value,
        string? ParentCheckpointId,
        bool HasParentMetadata)?> ReadLatestCheckpointForCompletedRootEvidenceInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        CancellationToken cancellationToken) =>
        ReadLatestCheckpointInTransactionCoreAsync(
            connection, transaction, binding, requireCompletedRootEvidence: true, cancellationToken);

    /// <summary>
    /// Reads the unique leaf checkpoint and validates its run binding and optional completed-root evidence.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when owner state is unavailable, stale, or has forked checkpoints.</exception>
    private async Task<(
        CheckpointInfo Info,
        JsonElement Value,
        string? ParentCheckpointId,
        bool HasParentMetadata)?> ReadLatestCheckpointInTransactionCoreAsync(
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
            SELECT checkpoint.checkpoint_id, checkpoint.parent_checkpoint_id,
                   checkpoint.has_parent_metadata, checkpoint.payload
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
        var parentCheckpointId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var hasParentMetadata = reader.GetBoolean(2);
        using var document = JsonDocument.Parse(reader.GetString(3));
        var value = document.RootElement.Clone();
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "maf_execution_checkpoint_forked", StatusCodes.Status409Conflict);
        return (
            new CheckpointInfo(validatedBinding.Identity.SessionId, checkpointId),
            value,
            parentCheckpointId,
            hasParentMetadata);
    }

    /// <summary>
    /// Determines whether a checkpoint and its SDK cache match the current SDK, model, and accepted run binding.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the checkpoint reference or run binding is invalid or stale.</exception>
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

    /// <summary>
    /// Requires the referenced SDK cache object to exist in the configured object store.
    /// </summary>
    /// <exception cref="CheckpointJournalRebuildRequiredException">Thrown when the cache store or object is unavailable.</exception>
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

    /// <summary>
    /// Reads the current binding using the standard active-session validation policy.
    /// </summary>
    private async Task<CurrentBinding> ReadCurrentBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        bool lockRows,
        CancellationToken cancellationToken) =>
        await ReadCurrentBindingAsync(
            connection, transaction, binding, lockRows,
            requireCompletedRootEvidence: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads accepted selection and session state, optionally locking owner rows and requiring completed-root evidence.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when actor, fence, lifecycle, selection, or model binding is invalid.</exception>
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

    /// <summary>
    /// Checks whether the named checkpoint exists within the specified bound run and fence.
    /// </summary>
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

    /// <summary>
    /// Checks whether an existing checkpoint ID is an exact replay of the requested payload and metadata.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the checkpoint ID is already bound to different data.</exception>
    private async Task<(CheckpointInfo Info, string? ParentCheckpointId, bool HasParentMetadata)?>
        FindExistingCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        string selectionHash,
        string checkpointId,
        string? parentCheckpointId,
        string payload,
        byte[]? workspaceSeed,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload = @payload, parent_checkpoint_id, has_parent_metadata, cache_object_key,
                   cache_sdk_version, pinned_model_reference, accepted_selection_hash, execution_fence,
                   workspace_seed IS NOT DISTINCT FROM @workspaceSeed
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND checkpoint_id = @checkpoint
            FOR UPDATE
            """, connection, transaction);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, binding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("workspaceSeed", NpgsqlDbType.Bytea, (object?)workspaceSeed ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var samePayload = reader.GetBoolean(0);
        var storedParent = reader.IsDBNull(1) ? null : reader.GetString(1);
        var hasParentMetadata = reader.GetBoolean(2);
        var storedCache = reader.IsDBNull(3) ? null : reader.GetString(3);
        var storedSelectionHash = reader.GetString(6).TrimEnd();
        if (!samePayload ||
            !string.Equals(storedParent, parentCheckpointId, StringComparison.Ordinal) ||
            !string.Equals(storedCache, binding.CacheReference?.Value, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), binding.SdkVersion, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(5), binding.PinnedModelReference, StringComparison.Ordinal) ||
            !string.Equals(storedSelectionHash, selectionHash, StringComparison.Ordinal) ||
            reader.GetInt64(7) != binding.ExecutionFence || !reader.GetBoolean(8))
            throw new CoordinationException("checkpoint_idempotency_conflict", StatusCodes.Status409Conflict);

        return (new CheckpointInfo(binding.Identity.SessionId, checkpointId), storedParent, hasParentMetadata);
    }

    /// <summary>
    /// Validates any BuildTest workspace seed stored with its originating execution checkpoint.
    /// </summary>
    private static void ValidateOriginWorkspaceSeed(
        JsonElement payload,
        MafCheckpointBinding binding,
        string checkpointId,
        string selectionHash,
        byte[]? bytes)
    {
        if (binding.StoreName != MafExecutionCheckpointContract.StoreName)
        {
            if (bytes is not null)
                throw new ArgumentException("Only an execution checkpoint can store a BuildTest seed.");
            return;
        }
        var state = MafExecutionCheckpointContract.Deserialize(payload);
        var origins = state.BuildTestIntents.Values.Where(intent =>
            intent.WorkspaceSeedReference?.Origin.CheckpointId == checkpointId).ToArray();
        if (origins.Length > 1 || origins.Length == 0 && bytes is not null ||
            origins.Length == 1 && bytes is null)
            throw new ArgumentException("The originating BuildTest checkpoint requires exactly one bound seed.");
        if (origins.Length == 0)
            return;
        var intent = origins[0];
        var source = intent.EffectiveWorkspaceSource!;
        if (intent.Identity != binding.Identity || intent.ExecutionFence != binding.ExecutionFence ||
            intent.AcceptedSelectionHash != selectionHash ||
            source.ActorIssuer != binding.Actor.Issuer || source.ActorSubject != binding.Actor.Subject ||
            source.TenantId != intent.ExpectedBinding.Fence.Owner.TenantId)
            throw new CoordinationException("checkpoint_workspace_seed_binding_invalid", StatusCodes.Status403Forbidden);
        _ = GitWorkspaceBuildTestSeedPackage.Parse(bytes!, source);
    }

    /// <summary>
    /// Reads the seed bytes for an accepted BuildTest intent from its originating checkpoint under the caller's transaction.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the intent, selection, checkpoint, seed length, or seed integrity differs.</exception>
    internal async Task<byte[]> ReadBuildTestWorkspaceSeedInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafCheckpointBinding binding,
        MafExecutionBuildTestIntent intent,
        CancellationToken cancellationToken)
    {
        var validatedBinding = ValidateBinding(binding);
        if (RequireBinding() != validatedBinding ||
            validatedBinding.StoreName != MafExecutionCheckpointContract.StoreName ||
            intent.Identity != validatedBinding.Identity ||
            intent.ExecutionFence != validatedBinding.ExecutionFence ||
            intent.EffectiveWorkspaceSource is null || intent.WorkspaceSeedReference is null)
            throw new CoordinationException("checkpoint_workspace_seed_binding_invalid", StatusCodes.Status403Forbidden);
        _ = intent.ToAcceptedCommand(requireSeed: false);
        var current = await ReadCurrentBindingAsync(
            connection, transaction, validatedBinding, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (current.SelectionHash != intent.AcceptedSelectionHash)
            throw new CoordinationException("checkpoint_run_selection_changed", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, payload, octet_length(workspace_seed), workspace_seed
            FROM {_schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND store_name = @store AND checkpoint_id = @checkpoint AND execution_fence = @fence
            FOR SHARE
            """, connection, transaction);
        AddBinding(command, validatedBinding);
        command.Parameters.AddWithValue("store", NpgsqlDbType.Varchar, validatedBinding.StoreName);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, intent.CheckpointId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, validatedBinding.ExecutionFence);
        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("checkpoint_workspace_seed_missing", StatusCodes.Status409Conflict);
        if (reader.GetString(0).TrimEnd() != current.SelectionHash)
            throw new CoordinationException("checkpoint_run_selection_changed", StatusCodes.Status409Conflict);
        using var document = JsonDocument.Parse(reader.GetString(1));
        var origin = MafExecutionCheckpointContract.Deserialize(document.RootElement);
        if (!origin.BuildTestIntents.TryGetValue(intent.StepId, out var storedIntent) ||
            !MafExecutionCheckpointContract.BuildTestIntentsMatch(storedIntent, intent) ||
            storedIntent.CheckpointRevision != origin.Revision ||
            reader.IsDBNull(2))
            throw new CoordinationException("checkpoint_workspace_seed_missing", StatusCodes.Status409Conflict);
        var length = reader.GetInt32(2);
        if (length < 1 || length > ProducedRunCaptureLimits.MaximumPackageBytes ||
            length != intent.WorkspaceSeedReference.SeedByteLength)
            throw new CoordinationException("checkpoint_workspace_seed_integrity_invalid", StatusCodes.Status409Conflict);
        var bytes = new byte[length];
        await using (var stream = reader.GetStream(3))
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOriginWorkspaceSeed(
                document.RootElement, validatedBinding, intent.CheckpointId, current.SelectionHash, bytes);
        }
        catch (ArgumentException)
        {
            throw new CoordinationException("checkpoint_workspace_seed_integrity_invalid", StatusCodes.Status409Conflict);
        }
        return bytes;
    }

    /// <summary>
    /// Validates checkpoint session identity, payload shape and size, and optional parent reference.
    /// </summary>
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

    /// <summary>
    /// Reads the pinned model reference from an accepted run selection.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the selection has no valid pinned model reference.</exception>
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

    /// <summary>
    /// Requires this store to be bound to the requested accepted run and session.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this store has no run binding.</exception>
    /// <exception cref="CoordinationException">Thrown when the requested session differs from the bound session.</exception>
    private MafCheckpointBinding RequireBinding(string? sessionId = null)
    {
        var binding = _binding
            ?? throw new InvalidOperationException("A MAF checkpoint store must be bound to an accepted run.");
        if (sessionId is not null && !string.Equals(sessionId, binding.Identity.SessionId, StringComparison.Ordinal))
            throw new CoordinationException("checkpoint_session_mismatch", StatusCodes.Status403Forbidden);
        return binding;
    }

    /// <summary>
    /// Validates all identifiers and authority fields in a checkpoint binding.
    /// </summary>
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

    /// <summary>
    /// Checks that metadata is non-empty, bounded, and free of control characters.
    /// </summary>
    private static bool IsMetadataValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    /// <summary>
    /// Checks that a checkpoint identifier contains only supported ASCII identifier characters.
    /// </summary>
    private static bool IsIdentifier(string? value) =>
        IsMetadataValue(value, 128) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    /// <summary>
    /// Adds the project, run, and session parameters shared by checkpoint queries.
    /// </summary>
    private static void AddBinding(NpgsqlCommand command, MafCheckpointBinding binding)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.Identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.Identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.Identity.SessionId);
    }

    /// <summary>
    /// Rewrites checkpoint JSON with type metadata properties first for SDK deserialization.
    /// </summary>
    internal static JsonElement RestoreMetadataOrder(JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteWithMetadataFirst(payload, writer);
        using var restored = JsonDocument.Parse(stream.ToArray());
        return restored.RootElement.Clone();
    }

    /// <summary>
    /// Writes a JSON tree while placing serialization metadata before other object properties.
    /// </summary>
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

    /// <summary>
    /// Carries the accepted selection hash read with the current checkpoint owner binding.
    /// </summary>
    private sealed record CurrentBinding(string SelectionHash);
}
