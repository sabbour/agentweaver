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
    public async Task ProjectPendingAsync(
        CancellationToken ct = default,
        RunStreamStore? targetStreamStore = null)
    {
        foreach (var pending in await runStore.GetUnprojectedTerminalOutcomesAsync(ct).ConfigureAwait(false))
            await ProjectAsync(pending, ct, targetStreamStore).ConfigureAwait(false);
    }

    /// <summary>Reconciles legacy terminal rows through the generation-fenced terminal projector.</summary>
    public async Task AdoptCompatibleLegacyOutcomesAsync(CancellationToken ct = default)
    {
        foreach (var status in TerminalStatuses)
        {
            foreach (var run in await runStore.GetByStatusAsync(status, ct).ConfigureAwait(false))
            {
                var events = await eventStream.GetPersistedEventsAsync(run.Id.ToString(), 0, ct)
                    .ConfigureAwait(false);
                RunEvent? linkedCandidate = null;
                TerminalRunOutcome? outcome = null;
                foreach (var candidate in events.Where(evt => IsCompatible(status, evt.Type)).Reverse())
                {
                    var candidateOutcome = new TerminalRunOutcome(
                        status,
                        candidate.Type,
                        JsonSerializer.SerializeToElement(candidate.Payload),
                        candidate.TimestampUtc == default ? run.EndedAt ?? run.StartedAt : candidate.TimestampUtc,
                        run.LifecycleGeneration);
                    if (await eventStream.TryLinkTerminalOutcomeAsync(
                            run.Id.ToString(), candidateOutcome, candidate, ct).ConfigureAwait(false))
                    {
                        linkedCandidate = candidate;
                        outcome = candidateOutcome;
                        break;
                    }
                }

                outcome ??= CreateRecoveredLegacyOutcome(run);
                _ = await runStore.TryAdoptLegacyTerminalOutcomeAsync(run.Id, outcome, ct)
                    .ConfigureAwait(false);
                if (linkedCandidate is not null)
                    _ = await TryProjectExistingTerminalAsync(
                        run.Id, run.LifecycleGeneration, linkedCandidate, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task ProjectAsync(
        PendingTerminalRunOutcome pending,
        CancellationToken ct = default,
        RunStreamStore? targetStreamStore = null)
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

        var persisted = await eventStream
            .AppendTerminalOutcomeAsync(pending.RunId.ToString(), pending.Outcome, ct)
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

        var liveStreamStore = targetStreamStore ?? streamStore;
        if (liveStreamStore is not null
            && !liveStreamStore.TryRecordDurableTerminalAndComplete(
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
        if (liveStreamStore is null)
            await eventStream.CompleteAsync(pending.RunId.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles a terminal event that was already durably emitted by another provider. The
    /// generation-to-sequence binding is persisted before the outbox row is acknowledged so a
    /// restarted subscriber can close at that exact canonical event.
    /// </summary>
    public async Task<bool> TryProjectExistingTerminalAsync(
        RunId runId,
        int lifecycleGeneration,
        RunEvent canonicalEvent,
        CancellationToken ct = default,
        RunStreamStore? targetStreamStore = null)
    {
        var pending = (await runStore.GetUnprojectedTerminalOutcomesAsync(ct).ConfigureAwait(false))
            .SingleOrDefault(outcome => outcome.RunId == runId && outcome.LifecycleGeneration == lifecycleGeneration);
        if (pending is null || canonicalEvent.Type != pending.Outcome.EventType)
            return false;

        if (!await eventStream.TryLinkTerminalOutcomeAsync(
                runId.ToString(), pending.Outcome, canonicalEvent, ct).ConfigureAwait(false))
            return false;

        var current = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (current is null || current.LifecycleGeneration != lifecycleGeneration)
        {
            logger.LogInformation(
                "Skipping stale existing terminal projection for {RunId} generation {Generation}",
                runId, lifecycleGeneration);
            await runStore.MarkTerminalOutcomeProjectedAsync(runId, lifecycleGeneration, ct).ConfigureAwait(false);
            return true;
        }

        var liveStreamStore = targetStreamStore ?? streamStore;
        if (liveStreamStore is not null
            && !liveStreamStore.TryRecordDurableTerminalAndComplete(
                runId.ToString(), lifecycleGeneration, canonicalEvent))
        {
            logger.LogInformation(
                "Skipping stale existing terminal live projection for {RunId} generation {Generation}",
                runId, lifecycleGeneration);
            await runStore.MarkTerminalOutcomeProjectedAsync(runId, lifecycleGeneration, ct).ConfigureAwait(false);
            return true;
        }

        await runStore.MarkTerminalOutcomeProjectedAsync(runId, lifecycleGeneration, ct).ConfigureAwait(false);
        if (liveStreamStore is null)
            await eventStream.CompleteAsync(runId.ToString(), ct).ConfigureAwait(false);
        return true;
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
        RunStatus.Failed => eventType is EventTypes.RunFailed or EventTypes.RunCancelled,
        RunStatus.Merged => eventType == EventTypes.MergeCompleted,
        RunStatus.Declined => eventType == EventTypes.ReviewDeclined,
        RunStatus.MergeFailed => eventType == EventTypes.MergeFailed,
        RunStatus.AssembleReady => eventType == EventTypes.RunAssembleReady,
        _ => false,
    };

    private static TerminalRunOutcome CreateRecoveredLegacyOutcome(Run run)
    {
        var (eventType, payload) = run.Status switch
        {
            RunStatus.Completed => (EventTypes.RunCompleted, (object)new { result = run.Result }),
            RunStatus.Failed => (EventTypes.RunFailed, new
            {
                reason = run.Result ?? "recovered_missing_terminal_event",
                retryable = false,
                recovered = true,
            }),
            RunStatus.Merged => (EventTypes.MergeCompleted, new
            {
                result = run.Result,
                mergedCommitHash = run.MergedCommitHash,
            }),
            RunStatus.Declined => (EventTypes.ReviewDeclined, new
            {
                result = run.Result,
                reviewer = run.ReviewedBy,
            }),
            RunStatus.MergeFailed => (EventTypes.MergeFailed, new
            {
                result = run.Result,
                mergeConflicts = run.MergeConflicts,
                mergedCommitHash = run.MergedCommitHash,
            }),
            RunStatus.AssembleReady => (EventTypes.RunAssembleReady, new
            {
                treeHash = run.TreeHash,
                worktreeBranch = run.WorktreeBranch,
                diff = run.Diff,
                stepCount = run.StepCount,
            }),
            _ => throw new InvalidOperationException($"Status {run.Status} is not terminal."),
        };
        return TerminalRunOutcome.Create(
            run.Status,
            eventType,
            payload,
            run.EndedAt ?? run.StartedAt,
            run.LifecycleGeneration);
    }
}

/// <summary>Continuously recovers durable terminal-outbox rows that could not be projected.</summary>
public sealed class TerminalOutcomeRecoveryService(
    TerminalOutcomeProjector projector,
    ILogger<TerminalOutcomeRecoveryService> logger) : BackgroundService
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RetryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await projector.AdoptCompatibleLegacyOutcomesAsync(ct).ConfigureAwait(false);
            await projector.ProjectPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep the committed winner unprojected so the next lease-coordinated scan retries it.
            logger.LogWarning(ex, "Terminal outcome recovery scan failed; pending projections will retry");
        }
    }
}
