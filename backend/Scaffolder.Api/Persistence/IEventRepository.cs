using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

/// <summary>
/// Append-only repository for the per-run event log.
/// INVARIANT: Events are never updated or deleted.
/// </summary>
public interface IEventRepository
{
    /// <summary>
    /// Appends a new event to the log. Assigns the next monotonic sequence number.
    /// </summary>
    Task<EventEntity> AppendAsync(EventEntity eventEntity, CancellationToken ct = default);

    /// <summary>
    /// Reads events for a run with sequence greater than lastSeenSequence,
    /// ordered by sequence ascending. Used for SSE replay and reconnect.
    /// </summary>
    Task<IReadOnlyList<EventEntity>> ReadFromSequenceAsync(
        Guid runId,
        long lastSeenSequence,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current maximum sequence number for a run. Returns 0 if no events yet.
    /// </summary>
    Task<long> GetMaxSequenceAsync(Guid runId, CancellationToken ct = default);
}
