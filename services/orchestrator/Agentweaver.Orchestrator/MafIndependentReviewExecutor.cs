using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>Replays a checkpointed review through the durable coordinator decision owner.</summary>
internal sealed class MafIndependentReviewExecutor(
    MafExecutionCheckpointStore execution,
    NpgsqlDataSource dataSource,
    Func<CancellationToken, Task<CoordinatorDecisionCurrentState>> readCurrentAsync,
    Func<MafExecutionReviewIntent, CancellationToken, Task> openApprovalAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task> requireDecisionAsync,
    Func<MafExecutionReviewIntent, MafExecutionCheckpoint, CancellationToken, Task> requireEvidenceAsync,
    string authorizedActorId,
    Action<MafExecutionCheckpointSnapshot> updateCheckpoint,
    Action<MafExecutionReviewIntent, long> markApprovalPending)
{
    internal async Task<MafExecutionTaskStatus?> ExecuteAsync(
        MafExecutionReviewIntent intent, CancellationToken cancellationToken)
    {
        // Read the persisted intent again; a crash after preparation cannot create a new review.
        var latest = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (!latest.State.ReviewIntents.TryGetValue(intent.StepId, out var retained) ||
            retained != intent)
            throw new CoordinationException(
                "maf_execution_review_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.ReviewReceipts.TryGetValue(intent.StepId, out var completed))
        {
            // A previously approved checkpoint is not an authorization cache across a revoked run.
            await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
            var current = await readCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current.State.Fence != intent.ExecutionFence ||
                current.SelectionHash != intent.AcceptedSelectionHash ||
                current.State.ConfirmedWorkPlan?.Plan.Id != intent.WorkPlanId ||
                completed.ActorId != authorizedActorId)
                throw new CoordinationException(
                    "maf_execution_review_binding_changed", StatusCodes.Status409Conflict);
            updateCheckpoint(latest);
            return MafIndependentReviewContract.ValidateReceipt(intent, completed);
        }

        await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        var decision = await readCurrentAsync(cancellationToken).ConfigureAwait(false);
        var receipt = MafIndependentReviewContract.ReadDecision(intent, decision, authorizedActorId);
        if (receipt is null)
        {
            if (decision.StateVersion == intent.DecisionStateVersion)
            {
                // PersistTransitionAsync uses the operation ID as its idempotency key, so restart
                // between checkpoint append and gate creation always opens the same request.
                await openApprovalAsync(intent, cancellationToken).ConfigureAwait(false);
            }
            // A concurrent dispatcher may have opened the same durable gate before this request.
            markApprovalPending(intent, checked(intent.DecisionStateVersion + 1));
            updateCheckpoint(latest);
            return null;
        }

        // Revalidate the evidence and decision under owner locks before releasing any dependent step.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await requireDecisionAsync(
            connection, transaction, decision.StateVersion, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        latest = await execution.ReadLatestInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        var next = CoordinationEndpoints.PrepareReviewCompletionCheckpoint(latest, intent, receipt);
        var committed = next is null ? latest :
            await execution.AppendInTransactionAsync(
                connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
        return MafIndependentReviewContract.ValidateReceipt(intent, receipt);
    }
}
