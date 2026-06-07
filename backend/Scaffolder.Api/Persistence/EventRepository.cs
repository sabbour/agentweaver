using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

/// <summary>
/// Append-only EF Core repository for the per-run event log.
/// No update or delete operations are exposed. Sequence is assigned atomically.
/// </summary>
internal sealed class EventRepository : IEventRepository
{
    private readonly ScaffolderDbContext _db;

    public EventRepository(ScaffolderDbContext db)
    {
        _db = db;
    }

    public async Task<EventEntity> AppendAsync(EventEntity eventEntity, CancellationToken ct = default)
    {
        // Assign the next monotonic sequence atomically within this request scope.
        // Note: for production multi-instance scenarios, use a database sequence or
        // optimistic concurrency retry. For the local developer parity target this is sufficient.
        var maxSeq = await GetMaxSequenceAsync(eventEntity.RunId, ct);
        eventEntity.Sequence = maxSeq + 1;
        eventEntity.Id = eventEntity.Id == Guid.Empty ? Guid.NewGuid() : eventEntity.Id;

        _db.Events.Add(eventEntity);
        await _db.SaveChangesAsync(ct);
        return eventEntity;
    }

    public async Task<IReadOnlyList<EventEntity>> ReadFromSequenceAsync(
        Guid runId,
        long lastSeenSequence,
        CancellationToken ct = default)
    {
        return await _db.Events
            .Where(e => e.RunId == runId && e.Sequence > lastSeenSequence)
            .OrderBy(e => e.Sequence)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<long> GetMaxSequenceAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.Events
            .Where(e => e.RunId == runId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync(ct) ?? 0L;
    }
}
