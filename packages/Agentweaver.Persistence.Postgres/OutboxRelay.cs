namespace Agentweaver.Persistence.Postgres;

/// <summary>
/// Coordinates one bounded, caller-driven relay batch over <see cref="PostgresOutbox"/>: claim
/// leased events, publish each through an injected <see cref="IOutboxPublisher"/>, and
/// acknowledge only after a confirmed publish using the still-valid lease token returned by the
/// claim. This type is not a daemon, heartbeat, or background loop; it starts no thread, timer,
/// or own network/database transaction beyond the existing <see cref="PostgresOutbox.ClaimAsync"/>
/// and <see cref="PostgresOutbox.AcknowledgeAsync"/> calls it reuses. Callers invoke
/// <see cref="RelayOnceAsync"/> explicitly, as often as their own process requires.
/// </summary>
public sealed class OutboxRelay
{
    private readonly PostgresOutbox _outbox;
    private readonly IOutboxPublisher _publisher;

    public OutboxRelay(PostgresOutbox outbox, IOutboxPublisher publisher)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> leased events (per the same per-stream,
    /// earliest-undelivered rule as <see cref="PostgresOutbox.ClaimAsync"/>), publishes each in
    /// claim order, and acknowledges each confirmed publish with its lease token. A publish
    /// exception is recorded as <see cref="RelayOutcome.PublishFailed"/> for that event only;
    /// because a claim batch holds at most one delivery per stream, a failure never blocks an
    /// unrelated stream's delivery in the same batch, so the loop continues. An expired,
    /// replaced, or already-settled lease at acknowledgment time is reported as
    /// <see cref="RelayOutcome.AcknowledgmentFenced"/> rather than treated as an error; the
    /// publish already happened and delivery remains at-least-once. Cancellation stops waiting
    /// even if the publisher ignores its token; publication may still complete externally but
    /// this relay will not acknowledge it. Cancellation is checked
    /// before the claim, before each publish, and again before each acknowledgment; once
    /// requested it propagates immediately and the method returns no outcome list. Earlier
    /// items in the same batch may already have been published and/or acknowledged by that
    /// point; unclaimed or unprocessed events remain safely reclaimable once their lease
    /// expires.
    /// </summary>
    public async Task<IReadOnlyList<RelayOutcome>> RelayOnceAsync(
        string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deliveries = await _outbox.ClaimAsync(workerId, batchSize, leaseDuration, cancellationToken);
        var outcomes = new List<RelayOutcome>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _publisher.PublishAsync(delivery.Event, cancellationToken).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The relay's own cancellation fired during publication: propagate it rather
                // than shape it as a per-event outcome. An unrelated cancellation-shaped
                // exception (for example a transport-internal timeout on a token that was
                // never canceled) falls through to the generic handler below instead.
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(new RelayOutcome.PublishFailed(delivery.Event, ex));
                continue;
            }

            // A second, explicit checkpoint: cancellation requested after a confirmed publish
            // must still stop before acknowledging, leaving the event published-but-unacknowledged
            // (safe for at-least-once redelivery) rather than racing acknowledgment.
            cancellationToken.ThrowIfCancellationRequested();
            var acknowledged = await _outbox.AcknowledgeAsync(
                delivery.Event.Message.Id, delivery.LeaseToken, cancellationToken);
            outcomes.Add(acknowledged
                ? new RelayOutcome.Acknowledged(delivery.Event)
                : new RelayOutcome.AcknowledgmentFenced(delivery.Event));
        }
        return outcomes;
    }
}
