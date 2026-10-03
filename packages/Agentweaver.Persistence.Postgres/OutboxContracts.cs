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
