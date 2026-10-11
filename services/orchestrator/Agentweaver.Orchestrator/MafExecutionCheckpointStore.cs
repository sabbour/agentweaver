using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class MafExecutionCheckpointStore
{
    private const string StoreName = MafExecutionCheckpointContract.StoreName;
    internal static string CurrentSdkVersion { get; } =
        typeof(WorkflowBuilder).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The MAF SDK version is unavailable.");

    private readonly PostgresMafCheckpointStore _checkpoints;
    private readonly MafCheckpointBinding _binding;

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

    internal async Task<MafExecutionCheckpointSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken)
    {
        var latest = await _checkpoints.ReadLatestCheckpointAsync(_binding, cancellationToken)
            .ConfigureAwait(false);
        return latest is null
            ? null
            : new MafExecutionCheckpointSnapshot(
                latest.Value.Info, MafExecutionCheckpointContract.Deserialize(latest.Value.Value));
    }

    internal async Task<MafExecutionFixedWorkAssociation?> ReadFixedWorkAssociationAsync(
        string childSessionId,
        string workPlanId,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null ||
            !string.Equals(latest.State.WorkPlanId, workPlanId, StringComparison.Ordinal))
            return null;
        var association = latest.State.FixedWorkAssociations.Values.SingleOrDefault(candidate =>
            string.Equals(candidate.ChildSessionId, childSessionId, StringComparison.Ordinal));
        return association is not null &&
            latest.State.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId) ==
            MafExecutionTaskStatus.Running
                ? association
                : null;
    }

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
                latest.Value.Info, MafExecutionCheckpointContract.Deserialize(latest.Value.Value));
    }

    internal bool IsBoundTo(SessionIdentity identity, long executionFence) =>
        MafExecutionCheckpointContract.IsBoundTo(_binding, identity, executionFence);

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
        var latest = await _checkpoints.ReadLatestCheckpointInTransactionAsync(
            connection, transaction, _binding, cancellationToken).ConfigureAwait(false);
        await AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        latest = await _checkpoints.ReadLatestCheckpointInTransactionAsync(
            connection, transaction, _binding, cancellationToken).ConfigureAwait(false);
        if (latest is { } current &&
            string.Equals(current.Info.CheckpointId, checkpointId, StringComparison.Ordinal))
        {
            var replayPayload = MafExecutionCheckpointContract.Serialize(state);
            var replayInfo = await _checkpoints.AppendInTransactionAsync(
                connection,
                transaction,
                _binding,
                _binding.Identity.SessionId,
                checkpointId,
                replayPayload,
                expectedParent,
                cancellationToken).ConfigureAwait(false);
            return new MafExecutionCheckpointSnapshot(replayInfo, state);
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
        }

        var payload = MafExecutionCheckpointContract.Serialize(state);
        var info = await _checkpoints.AppendInTransactionAsync(
            connection,
            transaction,
            _binding,
            _binding.Identity.SessionId,
            checkpointId,
            payload,
            expectedParent,
            cancellationToken).ConfigureAwait(false);
        return new MafExecutionCheckpointSnapshot(info, state);
    }

    private async Task<NpgsqlConnection> _bindingConnectionAsync(CancellationToken cancellationToken) =>
        await _checkpoints.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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
