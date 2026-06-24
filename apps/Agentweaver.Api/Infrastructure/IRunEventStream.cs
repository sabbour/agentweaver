using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Durable, pub/sub event log for a run. Two-layer: synchronous SQLite write-through (Layer 1)
/// for durability and crash safety, plus an in-process Channel&lt;RunEvent&gt; per active run
/// (Layer 2) for low-latency fan-out tailing.
///
/// Wire protocol is frozen: SSE frame layout, Last-Event-ID semantics, and [DONE] terminator
/// are preserved exactly as emitted by RunEndpoints.cs.
/// </summary>
public interface IRunEventStream
{
    /// <summary>
    /// Appends an event to the run's log. Performs a synchronous SQLite write BEFORE returning,
    /// then publishes to the in-process channel. The append is durable before it is acknowledged.
    /// If <paramref name="evt"/> already carries a positive <see cref="RunEvent.Sequence"/> it is
    /// honored (idempotent on the unique <c>(RunId, Sequence)</c> index); otherwise a monotonic
    /// sequence is assigned.
    /// </summary>
    ValueTask AppendAsync(string runId, RunEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to a run's event stream. Replays persisted events from <paramref name="fromSequence"/>,
    /// then tails the live channel for new events. Handoff is gapless and duplicate-free at the
    /// cursor boundary. Returns an <see cref="IAsyncEnumerable{T}"/> that completes when a terminal
    /// event is observed, the live channel is completed, or <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<RunEvent> SubscribeAsync(string runId, int fromSequence = 0, CancellationToken ct = default);

    /// <summary>
    /// Signals that a run has completed. Closes the in-process channel so subscribers drain
    /// remaining buffered events and then complete normally.
    /// </summary>
    ValueTask CompleteAsync(string runId, CancellationToken ct = default);
}
