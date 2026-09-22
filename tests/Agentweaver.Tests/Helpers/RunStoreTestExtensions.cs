using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

internal static class RunStoreTestExtensions
{
    public static async Task<bool> TerminalizeForTestAsync(
        this IRunStore store,
        RunId runId,
        RunStatus status,
        string? result = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default)
    {
        var run = await store.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run {runId} was not found.");
        var at = occurredAt ?? DateTimeOffset.UtcNow;
        var outcome = status switch
        {
            RunStatus.Completed => TerminalRunOutcome.Create(
                status, EventTypes.RunCompleted, new { result }, at, run.LifecycleGeneration),
            RunStatus.Failed => TerminalRunOutcome.Create(
                status, EventTypes.RunFailed, new { reason = result }, at, run.LifecycleGeneration),
            RunStatus.Merged => TerminalRunOutcome.Create(
                status, EventTypes.MergeCompleted, new { result }, at, run.LifecycleGeneration),
            RunStatus.Declined => TerminalRunOutcome.Create(
                status, EventTypes.ReviewDeclined, new { result }, at, run.LifecycleGeneration),
            RunStatus.MergeFailed => TerminalRunOutcome.Create(
                status, EventTypes.MergeFailed, new { result }, at, run.LifecycleGeneration),
            RunStatus.AssembleReady => TerminalRunOutcome.Create(
                status, EventTypes.RunAssembleReady, new { result }, at, run.LifecycleGeneration),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A typed terminal outcome is required."),
        };
        return await store.TrySetTerminalOutcomeAsync(runId, outcome, result, ct).ConfigureAwait(false);
    }
}
