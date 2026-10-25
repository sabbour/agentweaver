using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal sealed class MafOpenPullRequestExecutor(
    MafExecutionCheckpointStore execution,
    MafSourceControlOwnerClient sourceControlClient,
    NpgsqlDataSource dataSource,
    Func<CancellationToken, Task<RuntimeActorAuthorization>> createRuntimeActorAsync,
    Func<MafExecutionOpenPullRequestIntent, CancellationToken, Task> requireCurrentAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireDecisionAsync,
    Func<bool> isWallTimeLimitReached,
    Action markWallTimeLimitReached,
    Action<MafExecutionCheckpointSnapshot> updateCheckpoint)
{
    internal async Task<bool> ExecuteAsync(
        MafExecutionOpenPullRequestIntent intent,
        CancellationToken cancellationToken)
    {
        if (isWallTimeLimitReached())
        {
            markWallTimeLimitReached();
            return false;
        }

        var beforeCall = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (beforeCall.State.OpenPullRequestReceipts.TryGetValue(intent.StepId, out var completed))
        {
            _ = MafOpenPullRequestContract.ValidateReceipt(intent, completed);
            updateCheckpoint(beforeCall);
            return false;
        }
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);

        var diffActor = await createRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
        SourceControlWorkspaceDiffView diff;
        try
        {
            diff = await sourceControlClient.ReadWorkspaceDiffAsync(
                diffActor, intent.Identity, intent.Workspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            diffActor.Bearer.Invalidate();
        }
        MafOpenPullRequestContract.RequireWorkspaceRevision(intent, diff);

        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        var pullRequestActor = await createRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
        SourceControlPullRequest pullRequest;
        try
        {
            pullRequest = await sourceControlClient.CreateOrReusePullRequestAsync(
                pullRequestActor, intent.Identity, intent.Request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pullRequestActor.Bearer.Invalidate();
        }
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        var receipt = MafOpenPullRequestContract.CreateReceipt(intent, pullRequest);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await requireDecisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var latest = await execution.ReadLatestInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        var next = CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(latest, intent, receipt);
        var committed = next is null
            ? latest
            : await execution.AppendInTransactionAsync(
                connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
        return true;
    }
}
