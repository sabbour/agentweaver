namespace Agentweaver.Domain;

/// <summary>
/// A single event in a run's stream.
/// </summary>
/// <param name="TimestampUtc">
/// Server-side wall-clock time the event was appended to the run's stream (Principle: "when it
/// happened", not "when it was read/serialized"). Stamped centrally by
/// <c>RunStreamStore.RecordNext</c>/<c>Record</c> so every event reliably carries one, instead of
/// relying on individual emitters to remember to embed a timestamp in their payload. Defaults to
/// <c>default(DateTimeOffset)</c> for callers that construct a <see cref="RunEvent"/> directly
/// (e.g. tests, non-stream transports) without going through the stream store.
/// </param>
public sealed record RunEvent(int Sequence, string Type, object Payload, DateTimeOffset TimestampUtc = default);
