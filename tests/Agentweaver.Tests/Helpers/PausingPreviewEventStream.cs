using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using System.Text.Json;

namespace Agentweaver.Tests.Helpers;

// Pause at the persistence boundary, not at an earlier run-status or HTTP check. The ordinary
// AppendAsync seam also reproduces the old RecordNext race if conditional persistence is removed.
internal sealed class PausingPreviewEventStream(IRunEventStream inner) : IRunEventStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? ConditionalFailure { get; set; }
    public Exception? WorkflowStepFailure { get; set; }
    public IReadOnlyList<RunEvent>? EventsAtWorkflowStepFailure { get; private set; }

    public async ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
        if (WorkflowStepFailure is { } failure && evt.Type == EventTypes.WorkflowStep)
        {
            var payload = JsonSerializer.SerializeToNode(evt.Payload);
            if (payload?["step"]?.GetValue<string>() == "preview"
                && payload["status"]?.GetValue<string>() is "completed" or "failed")
            {
                EventsAtWorkflowStepFailure = await inner.GetPersistedEventsAsync(runId, ct: ct).ConfigureAwait(false);
                throw failure;
            }
        }
        if (evt.Type is EventTypes.SandboxPreviewReady or EventTypes.CoordinatorPreviewReady)
            await PauseAsync();
        return await inner.AppendAsync(runId, evt, ct);
    }

    public async Task<IReadOnlyList<RunEvent>> AppendWhileRunActiveAsync(
        string runId, IReadOnlyList<RunEvent> events, IRunStore runStore, CancellationToken ct = default)
    {
        await PauseAsync();
        if (ConditionalFailure is not null)
            throw ConditionalFailure;
        return await inner.AppendWhileRunActiveAsync(runId, events, runStore, ct);
    }

    private async Task PauseAsync()
    {
        Entered.TrySetResult();
        await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    public IAsyncEnumerable<RunEvent> SubscribeAsync(string runId, int fromSequence = 0, CancellationToken ct = default) =>
        inner.SubscribeAsync(runId, fromSequence, ct);
    public ValueTask CompleteAsync(string runId, CancellationToken ct = default) => inner.CompleteAsync(runId, ct);
    public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(
        string runId, int fromSequence = 0, CancellationToken ct = default) =>
        inner.GetPersistedEventsAsync(runId, fromSequence, ct);
}
