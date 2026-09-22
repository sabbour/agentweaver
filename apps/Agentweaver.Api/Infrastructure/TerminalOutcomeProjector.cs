using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Projects committed terminal winners into the separately durable RunEvents timeline.
/// The mark is intentionally last, making crash recovery a safe idempotent replay.
/// </summary>
public sealed class TerminalOutcomeProjector(
    IRunStore runStore,
    IRunEventStream eventStream,
    ILogger<TerminalOutcomeProjector> logger,
    RunStreamStore? streamStore = null)
{
    public async Task ProjectPendingAsync(CancellationToken ct = default)
    {
        foreach (var pending in await runStore.GetUnprojectedTerminalOutcomesAsync(ct).ConfigureAwait(false))
            await ProjectAsync(pending, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts only legacy terminal rows whose existing durable event has the terminal type
    /// compatible with the stored status. Missing or contradictory history remains untouched.
    /// </summary>
    public async Task AdoptCompatibleLegacyOutcomesAsync(CancellationToken ct = default)
    {
        foreach (var status in TerminalStatuses)
        {
            foreach (var run in await runStore.GetByStatusAsync(status, ct).ConfigureAwait(false))
            {
                var events = await eventStream.GetPersistedEventsAsync(run.Id.ToString(), 0, ct)
                    .ConfigureAwait(false);
                var candidate = events.LastOrDefault(evt => IsCompatible(status, evt.Type));
                if (candidate is null)
                    continue;
                var outcome = new TerminalRunOutcome(
                    status,
                    candidate.Type,
                    JsonSerializer.SerializeToElement(candidate.Payload),
                    candidate.TimestampUtc == default ? run.EndedAt ?? run.StartedAt : candidate.TimestampUtc,
                    run.LifecycleGeneration);
                _ = await runStore.TryAdoptLegacyTerminalOutcomeAsync(run.Id, outcome, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task ProjectAsync(PendingTerminalRunOutcome pending, CancellationToken ct = default)
    {
        var current = await runStore.GetAsync(pending.RunId, ct).ConfigureAwait(false);
        if (current is null || current.LifecycleGeneration != pending.LifecycleGeneration)
        {
            // A reopened lifecycle must never be closed by a delayed projection from an older
            // generation. The durable row remains evidence, but it is not a winner for today.
            logger.LogInformation(
                "Skipping stale terminal-outcome projection for {RunId} generation {Generation}",
                pending.RunId, pending.LifecycleGeneration);
            await runStore.MarkTerminalOutcomeProjectedAsync(
                pending.RunId, pending.LifecycleGeneration, ct).ConfigureAwait(false);
            return;
        }

        var persisted = await eventStream.AppendTerminalOutcomeAsync(pending.RunId.ToString(), pending.Outcome, ct)
            .ConfigureAwait(false);

        current = await runStore.GetAsync(pending.RunId, ct).ConfigureAwait(false);
        if (current is null || current.LifecycleGeneration != pending.LifecycleGeneration)
        {
            logger.LogInformation(
                "Skipping stale terminal-outcome stream completion for {RunId} generation {Generation}",
                pending.RunId, pending.LifecycleGeneration);
            await runStore.MarkTerminalOutcomeProjectedAsync(
                pending.RunId, pending.LifecycleGeneration, ct).ConfigureAwait(false);
            return;
        }

        if (streamStore is not null
            && !streamStore.TryRecordDurableTerminalAndComplete(
                pending.RunId.ToString(), pending.LifecycleGeneration, persisted))
        {
            logger.LogInformation(
                "Skipping stale terminal-outcome live projection for {RunId} generation {Generation}",
                pending.RunId, pending.LifecycleGeneration);
            await runStore.MarkTerminalOutcomeProjectedAsync(
                pending.RunId, pending.LifecycleGeneration, ct).ConfigureAwait(false);
            return;
        }

        await runStore.MarkTerminalOutcomeProjectedAsync(
            pending.RunId, pending.LifecycleGeneration, ct).ConfigureAwait(false);
        if (streamStore is null)
            await eventStream.CompleteAsync(pending.RunId.ToString(), ct).ConfigureAwait(false);
    }

    private static readonly RunStatus[] TerminalStatuses =
    [
        RunStatus.Completed,
        RunStatus.Failed,
        RunStatus.Merged,
        RunStatus.Declined,
        RunStatus.MergeFailed,
        RunStatus.AssembleReady,
    ];

    private static bool IsCompatible(RunStatus status, string eventType) => status switch
    {
        RunStatus.Completed => eventType == EventTypes.RunCompleted,
        RunStatus.Failed => eventType == EventTypes.RunFailed,
        RunStatus.Merged => eventType == EventTypes.MergeCompleted,
        RunStatus.Declined => eventType == EventTypes.ReviewDeclined,
        RunStatus.MergeFailed => eventType == EventTypes.MergeFailed,
        RunStatus.AssembleReady => eventType == EventTypes.RunAssembleReady,
        _ => false,
    };
}

/// <summary>Runs legacy adoption and unprojected-outbox recovery during application startup.</summary>
public sealed class TerminalOutcomeRecoveryService(TerminalOutcomeProjector projector) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await projector.AdoptCompatibleLegacyOutcomesAsync(cancellationToken).ConfigureAwait(false);
        await projector.ProjectPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
