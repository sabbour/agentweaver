namespace Scaffolder.Domain;

/// <summary>
/// In-memory fan-out for live run-step streaming (Principle V). Implemented in
/// Scaffolder.Api.
/// </summary>
public interface IRunEventPublisher
{
    void Publish(RunEvent evt);

    IAsyncEnumerable<RunEvent> SubscribeAsync(RunId runId, int afterSequence, CancellationToken ct = default);

    void Complete(RunId runId);
}
