using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

internal static class RunStoreTerminalOutcomeExtensions
{
    public static async Task<bool> TrySetTerminalOutcomeForCurrentGenerationAsync(
        this IRunStore runStore,
        RunId runId,
        RunStatus status,
        string eventType,
        object payload,
        DateTimeOffset occurredAt,
        string? result,
        CancellationToken ct = default)
    {
        var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
        return run is not null && await runStore.TrySetTerminalOutcomeAsync(
            runId,
            TerminalRunOutcome.Create(
                status,
                eventType,
                payload,
                occurredAt,
                run.LifecycleGeneration),
            result,
            ct).ConfigureAwait(false);
    }
}
