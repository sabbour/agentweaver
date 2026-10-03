using System.Text.Json;

namespace Agentweaver.Persistence.Postgres;

public record OutboxEvent(
    Guid Id,
    string StreamId,
    string IdempotencyKey,
    string EventType,
    int EventVersion,
    JsonElement Payload,
    DateTimeOffset OccurredAt);

public record StoredOutboxEvent(OutboxEvent Message, long Sequence);

public record OutboxDelivery(
    StoredOutboxEvent Event,
    Guid LeaseToken,
    string WorkerId,
    DateTimeOffset LeasedUntil);

public sealed class OutboxConflictException : Exception
{
    public OutboxConflictException(string message) : base(message) { }
}

/// <summary>
/// Minimal injected transport boundary for <see cref="OutboxRelay"/>. Implementations publish
/// one claimed event and either complete or throw; the relay never infers success from a
/// transport receipt it did not observe and never acknowledges an event whose publish call
/// threw or whose cancellation token fired.
/// </summary>
public interface IOutboxPublisher
{
    Task PublishAsync(StoredOutboxEvent storedEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// The disposition of one claimed event after a relay batch attempted to publish and
/// acknowledge it. This is a closed hierarchy so callers cannot observe a contradictory
/// combination such as a fenced acknowledgment carrying a publish exception.
/// </summary>
public abstract record RelayOutcome(StoredOutboxEvent Event)
{
    /// <summary>The event was published and acknowledged under the still-valid lease.</summary>
    public sealed record Acknowledged(StoredOutboxEvent Event) : RelayOutcome(Event);

    /// <summary>
    /// The event was published, but the lease token was no longer current by the time
    /// acknowledgment ran: it expired, was replaced by a competing claim, or was already
    /// acknowledged. The relay already published; delivery remains at-least-once and the
    /// event will be redelivered once its new lease (if any) is reclaimed.
    /// </summary>
    public sealed record AcknowledgmentFenced(StoredOutboxEvent Event) : RelayOutcome(Event);

    /// <summary>
    /// The injected publisher threw while attempting to publish this event. The event was
    /// never acknowledged and its lease is untouched; it becomes reclaimable once the lease
    /// expires.
    /// </summary>
    public sealed record PublishFailed(StoredOutboxEvent Event, Exception Failure) : RelayOutcome(Event);
}
