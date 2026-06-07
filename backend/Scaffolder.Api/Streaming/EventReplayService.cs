using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Streaming;

/// <summary>
/// T034: Reads historical events from the durable event log for SSE replay.
///
/// When a client connects with a Last-Event-ID or lastSeenSequence cursor,
/// this service hydrates the backlog of events that were missed — even after
/// a process restart — because the event log is durable and append-only.
/// </summary>
public sealed class EventReplayService
{
    private readonly IEventRepository _eventRepository;
    private readonly ILogger<EventReplayService> _logger;

    public EventReplayService(
        IEventRepository eventRepository,
        ILogger<EventReplayService> logger)
    {
        _eventRepository = eventRepository;
        _logger = logger;
    }

    /// <summary>
    /// Returns all events for a run with sequence strictly greater than
    /// <paramref name="lastSeenSequence"/>, ordered ascending.
    /// Pass 0 to get all events from the beginning.
    /// </summary>
    public async Task<IReadOnlyList<EventEntity>> GetEventsAfterAsync(
        Guid runId,
        long lastSeenSequence,
        CancellationToken ct = default)
    {
        var events = await _eventRepository.ReadFromSequenceAsync(runId, lastSeenSequence, ct);

        _logger.LogDebug(
            "EventReplayService: replaying {Count} events for run {RunId} after seq={Seq}",
            events.Count, runId, lastSeenSequence);

        return events;
    }
}
