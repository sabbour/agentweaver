using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal sealed class MafMergeExecutor(
    MafExecutionCheckpointStore execution,
    MafSourceControlOwnerClient sourceControlClient,
    NpgsqlDataSource dataSource,
    Func<CancellationToken, Task<RuntimeActorAuthorization>> createRuntimeActorAsync,
    Func<MafExecutionMergeIntent, CancellationToken, Task> requireCurrentAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireDecisionAsync,
    Func<bool> isWallTimeLimitReached,
    Action markWallTimeLimitReached,
    Action<MafExecutionCheckpointSnapshot> updateCheckpoint,
    Action<string, string> markApprovalPending)
{
    internal async Task<MafMergeExecutionOutcome> ExecuteAsync(
        MafExecutionMergeIntent intent,
        CancellationToken cancellationToken)
    {
        // A wall-time boundary must leave the persisted operation for exact recovery on the next dispatch.
        if (isWallTimeLimitReached())
        {
            markWallTimeLimitReached();
            return MafMergeExecutionOutcome.NoProgress;
        }

        var beforeCall = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (beforeCall.State.MergeReceipts.TryGetValue(intent.StepId, out var completed))
        {
            _ = MafMergeContract.ValidateReceipt(intent, completed);
            updateCheckpoint(beforeCall);
            return MafMergeExecutionOutcome.NoProgress;
        }
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);

        var prepareActor = await createRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
        MafMergeOwnerIntent prepared;
        try
        {
            prepared = await sourceControlClient.PrepareMergeIntentAsync(
                prepareActor, intent.Identity, new(
                    intent.IdempotencyKey,
                    intent.DecisionStateVersion,
                    intent.WorkflowId,
                    intent.DefinitionRevision,
                    intent.WorkPlanId,
                    intent.StepId,
                    intent.PullRequestNumber,
                    intent.Method)
                {
                    ExpectedHeadSha = intent.ExpectedHeadSha
                }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prepareActor.Bearer.Invalidate();
        }

        if (prepared.State == "approval_pending")
        {
            // The owner has created the approval gate; preserve the running checkpoint and return to the caller.
            if (!string.Equals(
                    prepared.IntentId, intent.OwnerIntentId, StringComparison.Ordinal) ||
                !string.Equals(
                    prepared.ApprovalRequestId, intent.ApprovalRequestId, StringComparison.Ordinal))
                throw new CoordinationException(
                    "source_control_merge_response_invalid", StatusCodes.Status502BadGateway);
            markApprovalPending(intent.OwnerIntentId, intent.ApprovalRequestId);
            updateCheckpoint(beforeCall);
            return MafMergeExecutionOutcome.AwaitingApproval;
        }

        MafMergeContract.ValidatePreparedOwnerIntent(intent, prepared);
        if (prepared.State == "merged")
        {
            // A response lost after a successful remote merge is recovered from the owner's durable result.
            var receipt = MafMergeContract.CreateReceipt(intent, prepared, prepared);
            return await CompleteAsync(intent, receipt, cancellationToken).ConfigureAwait(false)
                ? MafMergeExecutionOutcome.Completed
                : MafMergeExecutionOutcome.NoProgress;
        }

        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        var executeActor = await createRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
        MafMergeOwnerIntent executed;
        try
        {
            executed = await sourceControlClient.ExecuteMergeIntentAsync(
                executeActor, intent.Identity, intent.OwnerIntentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            executeActor.Bearer.Invalidate();
        }
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        if (executed.State != "merged")
            throw FailureForOwnerState(executed.State, executed.FailureCode);

        var terminal = MafMergeContract.CreateReceipt(intent, prepared, executed);
        return await CompleteAsync(intent, terminal, cancellationToken).ConfigureAwait(false)
            ? MafMergeExecutionOutcome.Completed
            : MafMergeExecutionOutcome.NoProgress;
    }

    private async Task<bool> CompleteAsync(
        MafExecutionMergeIntent intent,
        MafMergeTerminalReceipt receipt,
        CancellationToken cancellationToken)
    {
        // Append the receipt under the current decision and checkpoint locks so a stale merge cannot release dependents.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await requireDecisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var latest = await execution.ReadLatestInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        var next = CoordinationEndpoints.PrepareMergeCompletionCheckpoint(latest, intent, receipt);
        var committed = next is null
            ? latest
            : await execution.AppendInTransactionAsync(
                connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
        return next is not null;
    }

    private static CoordinationException FailureForOwnerState(string state, string? failureCode)
    {
        // A durable uncertain or conflict result is terminal evidence against repeating the merge effect.
        if (state == "outcome_uncertain")
            return new("source_control_merge_outcome_uncertain", StatusCodes.Status502BadGateway);
        if (state is "conflict" or "merge_started")
            return new(
                string.IsNullOrWhiteSpace(failureCode) ? "source_control_merge_conflict" : failureCode,
                StatusCodes.Status409Conflict);
        return new("source_control_merge_not_approved", StatusCodes.Status409Conflict);
    }
}
