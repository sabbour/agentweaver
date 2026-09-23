using Agentweaver.Domain;
using Agentweaver.Api.Contracts;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Closes the memory flywheel after each successful project run:
/// 1. Auto-merges low-risk inbox entries (learning / pattern / update) into decisions.
/// 2. Appends the run outcome to the current open session summary.
/// 3. Exports the updated memory state to .squad/ and .agentweaver/context/.
///
/// Non-blocking — all exceptions are caught and logged. The run terminal state is
/// never affected by failures in this service.
/// </summary>
public sealed class PostRunScribeService(
    ScribeHousekeepingService housekeeping,
    ILogger<PostRunScribeService> logger)
{
    public async Task RunAsync(Run run, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(run.AgentName) || !run.ProjectId.HasValue) return;

        try
        {
            await housekeeping.RunAsync(
                new ScribeHousekeepingRequest(
                    run,
                    string.Equals(run.AgentName, "coordinator", StringComparison.OrdinalIgnoreCase)
                        ? ScribeAuthority.CoordinatorFinalization
                        : ScribeAuthority.Worker,
                    run.Status.ToApiString()),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diagnostic = ScribeFailureClassifier.Classify(ex);
            logger.LogWarning(
                "PostRunScribe failed for run {RunId}; code={FailureCode}; retryable={Retryable}",
                run.Id, diagnostic.Code, diagnostic.Retryable);
        }
    }
}
