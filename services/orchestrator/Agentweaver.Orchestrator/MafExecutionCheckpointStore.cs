using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Adapts MAF execution state to the PostgreSQL checkpoint owner for one bound session and execution fence.
/// It validates state transitions, serializes checkpoint writes, and restores large workspace seeds from durable storage.
/// </summary>
internal sealed class MafExecutionCheckpointStore
{
    private const string StoreName = MafExecutionCheckpointContract.StoreName;
    internal static string CurrentSdkVersion { get; } =
        typeof(WorkflowBuilder).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The MAF SDK version is unavailable.");

    private readonly PostgresMafCheckpointStore _checkpoints;
    private readonly MafCheckpointBinding _binding;

    /// <summary>
    /// Creates a store bound to one session, fence, SDK version, and validated checkpoint store name.
    /// </summary>
    /// <param name="checkpoints">The PostgreSQL checkpoint owner.</param>
    /// <param name="binding">The identity and execution-fence binding for this store instance.</param>
    /// <param name="checkpointStoreName">An optional store name override, restricted to safe identifier characters.</param>
    /// <exception cref="ArgumentException">Thrown when the checkpoint store name is blank or contains unsupported characters.</exception>
    internal MafExecutionCheckpointStore(
        PostgresMafCheckpointStore checkpoints,
        MafCheckpointBinding binding,
        string? checkpointStoreName = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(binding);
        var storeName = checkpointStoreName ?? StoreName;
        if (string.IsNullOrWhiteSpace(storeName) || storeName.Length > 128 ||
            storeName.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid MAF execution checkpoint store name is required.",
                nameof(checkpointStoreName));
        _binding = binding with { CacheReference = null, StoreName = storeName };
        _checkpoints = checkpoints.ForRun(_binding);
    }

    /// <summary>
    /// Reads and deserializes the newest checkpoint for this store's bound session.
    /// </summary>
    /// <returns>The latest validated snapshot, or <see langword="null"/> when no checkpoint exists.</returns>
    internal async Task<MafExecutionCheckpointSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken)
    {
        var latest = await _checkpoints.ReadLatestCheckpointAsync(_binding, cancellationToken)
            .ConfigureAwait(false);
        return latest is null
            ? null
            : new MafExecutionCheckpointSnapshot(
                latest.Value.Info, MafExecutionCheckpointContract.Deserialize(latest.Value.Value))
            {
                ParentCheckpointId = latest.Value.ParentCheckpointId,
                HasParentMetadata = latest.Value.HasParentMetadata
            };
    }

    /// <summary>
    /// Reads a child-session association only while its fixed-work item is running.
    /// </summary>
    internal async Task<MafExecutionFixedWorkAssociation?> ReadFixedWorkAssociationAsync(
        string childSessionId,
        string workPlanId,
        CancellationToken cancellationToken) =>
        await ReadFixedWorkAssociationAsync(
            childSessionId, workPlanId, allowTerminal: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads a fixed-work association for observation while its item is running or terminal.
    /// </summary>
    internal async Task<MafExecutionFixedWorkAssociation?> ReadFixedWorkAssociationForObservationAsync(
        string childSessionId,
        string workPlanId,
        CancellationToken cancellationToken) =>
        await ReadFixedWorkAssociationAsync(
            childSessionId, workPlanId, allowTerminal: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds a child association in the latest plan and applies the requested status eligibility rule.
    /// </summary>
    /// <param name="childSessionId">The child session to find.</param>
    /// <param name="workPlanId">The plan that must own the association.</param>
    /// <param name="allowTerminal">Whether terminal statuses are eligible for observation.</param>
    /// <param name="cancellationToken">Cancels the checkpoint read.</param>
    /// <returns>The matching association when its status is eligible; otherwise <see langword="null"/>.</returns>
    private async Task<MafExecutionFixedWorkAssociation?> ReadFixedWorkAssociationAsync(
        string childSessionId,
        string workPlanId,
        bool allowTerminal,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null ||
            !string.Equals(latest.State.WorkPlanId, workPlanId, StringComparison.Ordinal))
            return null;
        var association = latest.State.FixedWorkAssociations.Values.SingleOrDefault(candidate =>
            string.Equals(candidate.ChildSessionId, childSessionId, StringComparison.Ordinal));
        var status = association is null
            ? MafExecutionTaskStatus.Pending
            : latest.State.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId);
        return association is not null &&
            (allowTerminal
                ? IsObservationFixedWorkStatus(status)
                : status == MafExecutionTaskStatus.Running)
            ? association
            : null;
    }

    /// <summary>
    /// Identifies fixed-work statuses that can still provide an observation for their child.
    /// </summary>
    internal static bool IsObservationFixedWorkStatus(MafExecutionTaskStatus status) =>
        status is MafExecutionTaskStatus.Running or MafExecutionTaskStatus.Succeeded or
            MafExecutionTaskStatus.Failed or MafExecutionTaskStatus.Indeterminate;

    /// <summary>
    /// Reads and deserializes the newest checkpoint using the caller's existing PostgreSQL transaction.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="transaction">The transaction that owns the consistent read.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The latest bound snapshot, or <see langword="null"/> when absent.</returns>
    internal async Task<MafExecutionCheckpointSnapshot?> ReadLatestInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var latest = await _checkpoints.ReadLatestCheckpointInTransactionAsync(
            connection, transaction, _binding, cancellationToken).ConfigureAwait(false);
        return latest is null
            ? null
            : new MafExecutionCheckpointSnapshot(
                latest.Value.Info, MafExecutionCheckpointContract.Deserialize(latest.Value.Value))
            {
                ParentCheckpointId = latest.Value.ParentCheckpointId,
                HasParentMetadata = latest.Value.HasParentMetadata
            };
    }

    /// <summary>
    /// Reports whether the store is bound to the given session and execution fence.
    /// </summary>
    internal bool IsBoundTo(SessionIdentity identity, long executionFence) =>
        MafExecutionCheckpointContract.IsBoundTo(_binding, identity, executionFence);

    /// <summary>
    /// Appends a checkpoint in its own transaction.
    /// </summary>
    /// <param name="checkpointId">The new checkpoint identifier.</param>
    /// <param name="expectedParent">The checkpoint that must still be latest, or <see langword="null"/> for initial state.</param>
    /// <param name="state">The complete state to validate and persist.</param>
    /// <param name="cancellationToken">Cancels opening, writing, or committing the transaction.</param>
    /// <returns>The persisted checkpoint snapshot.</returns>
    /// <exception cref="CoordinationException">Thrown when the expected parent or state transition is stale or invalid.</exception>
    /// <remarks>The write is committed atomically with any extracted workspace seed data.</remarks>
    internal async Task<MafExecutionCheckpointSnapshot> AppendAsync(
        string checkpointId,
        CheckpointInfo? expectedParent,
        MafExecutionCheckpoint state,
        CancellationToken cancellationToken)
    {
        await using var connection = await _bindingConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await AppendInTransactionAsync(
            connection, transaction, checkpointId, expectedParent, state, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Validates and appends a checkpoint using a caller-owned transaction.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="transaction">The transaction that owns the append.</param>
    /// <param name="checkpointId">The new checkpoint identifier.</param>
    /// <param name="expectedParent">The checkpoint that must remain latest.</param>
    /// <param name="state">The state to serialize and persist.</param>
    /// <param name="cancellationToken">Cancels lock acquisition or database operations.</param>
    /// <returns>The persisted snapshot, including parent metadata.</returns>
    /// <exception cref="CoordinationException">Thrown when the parent, revision, or transition check fails.</exception>
    /// <remarks>A transaction-scoped advisory lock serializes updates for this session; an exact current-checkpoint replay is delegated to the checkpoint owner for idempotent handling.</remarks>
    internal async Task<MafExecutionCheckpointSnapshot> AppendInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string checkpointId,
        CheckpointInfo? expectedParent,
        MafExecutionCheckpoint state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        MafExecutionCheckpointContract.ValidateState(state);
        MafExecutionCheckpointContract.ValidateBuildTestBinding(
            state, _binding.Identity, _binding.ExecutionFence, checkpointId);
        MafExecutionCheckpointContract.ValidateReviewBinding(
            state, _binding.Identity, _binding.ExecutionFence, checkpointId);
        MafExecutionCheckpointContract.ValidateRubberduckBinding(
            state, _binding.Identity, _binding.ExecutionFence, checkpointId);
        var originIntents = state.BuildTestIntents.Values.Where(intent =>
            intent.WorkspaceSeedReference?.Origin.CheckpointId == checkpointId).ToArray();
        if (originIntents.Length > 1)
            throw new ArgumentException("One originating checkpoint can store only one BuildTest seed.", nameof(state));
        var workspaceSeed = originIntents.SingleOrDefault()?.WorkspaceSeed?.ToArray();
        var latest = await _checkpoints.ReadLatestCheckpointInTransactionAsync(
            connection, transaction, _binding, cancellationToken).ConfigureAwait(false);
        await AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        // A competing append may have committed while this transaction waited for the lock.
        latest = await _checkpoints.ReadLatestCheckpointInTransactionAsync(
            connection, transaction, _binding, cancellationToken).ConfigureAwait(false);
        if (latest is { } current &&
            string.Equals(current.Info.CheckpointId, checkpointId, StringComparison.Ordinal))
        {
            // Let the durable owner compare payloads; matching checkpoint IDs alone do not prove a replay.
            var replayPayload = MafExecutionCheckpointContract.Serialize(state);
            var replay = await _checkpoints.AppendInTransactionAsync(
                connection,
                transaction,
                _binding,
                _binding.Identity.SessionId,
                checkpointId,
                replayPayload,
                expectedParent,
                cancellationToken, workspaceSeed).ConfigureAwait(false);
            return new MafExecutionCheckpointSnapshot(replay.Info, state)
            {
                ParentCheckpointId = replay.ParentCheckpointId,
                HasParentMetadata = replay.HasParentMetadata
            };
        }

        if (expectedParent is null)
        {
            if (latest is not null || state.Revision != 1)
                throw new CoordinationException("maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
        }
        else
        {
            if (latest is null ||
                latest.Value.Info.SessionId != expectedParent.SessionId ||
                latest.Value.Info.CheckpointId != expectedParent.CheckpointId)
                throw new CoordinationException("maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
            var previous = MafExecutionCheckpointContract.Deserialize(latest.Value.Value);
            if (state.Revision != checked(previous.Revision + 1) ||
                state.DecisionStateVersion < previous.DecisionStateVersion)
                throw new CoordinationException("maf_execution_checkpoint_revision_invalid",
                    StatusCodes.Status409Conflict);
            MafExecutionCheckpointContract.ValidateTransition(previous, state);
        }

        var payload = MafExecutionCheckpointContract.Serialize(state);
        var checkpoint = await _checkpoints.AppendInTransactionAsync(
            connection,
            transaction,
            _binding,
            _binding.Identity.SessionId,
            checkpointId,
            payload,
            expectedParent,
            cancellationToken, workspaceSeed).ConfigureAwait(false);
        return new MafExecutionCheckpointSnapshot(checkpoint.Info, state)
        {
            ParentCheckpointId = checkpoint.ParentCheckpointId,
            HasParentMetadata = checkpoint.HasParentMetadata
        };
    }

    /// <summary>
    /// Loads large workspace seed bytes referenced by an intent from the checkpoint owner.
    /// </summary>
    /// <param name="intent">The intent containing a durable seed reference, when applicable.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The intent with its seed restored, or the original intent when it has no workspace source.</returns>
    internal async Task<MafExecutionBuildTestIntent> HydrateBuildTestIntentAsync(
        MafExecutionBuildTestIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent.EffectiveWorkspaceSource is null)
            return intent;
        await using var connection = await _bindingConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await _checkpoints.ReadBuildTestWorkspaceSeedInTransactionAsync(
            connection, transaction, _binding, intent, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent with { WorkspaceSeed = bytes.ToImmutableArray() };
    }

    /// <summary>
    /// Opens a connection through the checkpoint owner using this store's configuration.
    /// </summary>
    private async Task<NpgsqlConnection> _bindingConnectionAsync(CancellationToken cancellationToken) =>
        await _checkpoints.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Acquires a transaction-scoped PostgreSQL advisory lock for this session's execution stream.
    /// </summary>
    /// <param name="connection">The connection participating in the write transaction.</param>
    /// <param name="transaction">The transaction that holds the lock until commit or rollback.</param>
    /// <param name="cancellationToken">Cancels lock acquisition.</param>
    /// <remarks>The lock key is derived from project, run, and session identity to serialize competing checkpoint appends.</remarks>
    internal async Task AcquireExecutionLockInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.maf.execution'), hashtext(@scope))",
            connection, transaction);
        command.Parameters.AddWithValue("scope", NpgsqlDbType.Text,
            JsonSerializer.Serialize(new[]
            {
                _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId
            }));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

}
