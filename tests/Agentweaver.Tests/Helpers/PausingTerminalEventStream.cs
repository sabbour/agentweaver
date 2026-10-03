using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

internal sealed class PausingTerminalEventStream(IRunEventStream inner) : IRunEventStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EnteredAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _terminalAppends;

    public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default) =>
        inner.AppendAsync(runId, evt, ct);

    public async Task<RunEvent> AppendTerminalOutcomeAsync(
        string runId, TerminalRunOutcome outcome, CancellationToken ct = default)
    {
        if (outcome.EventType == EventTypes.RunFailed)
        {
            Entered.TrySetResult();
            if (Interlocked.Increment(ref _terminalAppends) > 1)
                EnteredAgain.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        return await inner.AppendTerminalOutcomeAsync(runId, outcome, ct);
    }

    public Task<bool> TryLinkTerminalOutcomeAsync(
        string runId, TerminalRunOutcome outcome, RunEvent canonicalEvent, CancellationToken ct = default) =>
        inner.TryLinkTerminalOutcomeAsync(runId, outcome, canonicalEvent, ct);

    public Task<IReadOnlyList<RunEvent>> AppendWhileRunLeaseOwnedAsync(
        string runId, IReadOnlyList<RunEvent> events, IRunStore runStore, RunLeaseFence lease,
        CancellationToken ct = default) =>
        inner.AppendWhileRunLeaseOwnedAsync(runId, events, runStore, lease, ct);

    public IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int fromSequence = 0, CancellationToken ct = default) =>
        inner.SubscribeAsync(runId, fromSequence, ct);

    public ValueTask CompleteAsync(string runId, CancellationToken ct = default) =>
        inner.CompleteAsync(runId, ct);

    public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(
        string runId, int fromSequence = 0, CancellationToken ct = default) =>
        inner.GetPersistedEventsAsync(runId, fromSequence, ct);
}
