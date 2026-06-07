namespace Scaffolder.Domain;

/// <summary>
/// Durable event log abstraction. Implemented in Scaffolder.Api.
/// </summary>
public interface IRunEventStore
{
    /// <summary>
    /// Persists an event, allocating its per-run monotonic <see cref="RunEvent.Sequence"/>
    /// inside the write transaction. The caller supplies the event with a
    /// placeholder sequence; the returned event carries the allocated sequence
    /// and is what callers should publish for live streaming.
    /// </summary>
    Task<RunEvent> AppendAsync(RunEvent evt, CancellationToken ct = default);

    IAsyncEnumerable<RunEvent> ReadFromAsync(RunId runId, int afterSequence, CancellationToken ct = default);

    Task<RunEvent?> GetLatestTerminalEventAsync(RunId runId, CancellationToken ct = default);
}
