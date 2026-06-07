using System.Collections.Concurrent;
using System.Threading.Channels;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Streaming;

/// <summary>
/// T033: In-process fan-out broadcaster for run events to live SSE subscribers.
///
/// Architecture:
///   - Registered as a Singleton so it lives across all HTTP request scopes.
///   - EventLogService holds an IEventBroadcaster? reference and calls BroadcastAsync
///     after each successful event append (wired in T036).
///   - Each SSE connection calls Subscribe() to get a dedicated Channel reader;
///     the endpoint reads from that channel and formats SSE frames until the run
///     reaches a terminal state or the client disconnects.
///   - BroadcastAsync is non-blocking: events are written with TryWrite so slow
///     subscribers never stall the event-log write path.
/// </summary>
public sealed class EventBroadcaster : IEventBroadcaster
{
    private readonly ILogger<EventBroadcaster> _logger;

    // Per-run subscriber channels. ConcurrentDictionary for lock-free lookup;
    // a plain lock guards the inner list mutations.
    private readonly ConcurrentDictionary<Guid, List<Channel<EventEntity>>> _subscribers = new();
    private readonly object _lock = new();

    public EventBroadcaster(ILogger<EventBroadcaster> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to live events for a run. Returns an unbounded channel.
    /// The caller MUST call <see cref="Unsubscribe"/> when the SSE connection closes.
    /// </summary>
    public Channel<EventEntity> Subscribe(Guid runId)
    {
        var channel = Channel.CreateUnbounded<EventEntity>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
            {
                list = new List<Channel<EventEntity>>();
                _subscribers[runId] = list;
            }

            list.Add(channel);
        }

        _logger.LogDebug(
            "SSE subscriber added for run {RunId}. Active subscribers: {Count}",
            runId, CountSubscribers(runId));

        return channel;
    }

    /// <summary>
    /// Removes a subscriber channel and completes its writer so the reader terminates.
    /// Safe to call multiple times.
    /// </summary>
    public void Unsubscribe(Guid runId, Channel<EventEntity> channel)
    {
        lock (_lock)
        {
            if (_subscribers.TryGetValue(runId, out var list))
            {
                list.Remove(channel);
                if (list.Count == 0)
                {
                    _subscribers.TryRemove(runId, out _);
                }
            }
        }

        // Complete the writer so ReadAllAsync on the reader returns.
        channel.Writer.TryComplete();

        _logger.LogDebug(
            "SSE subscriber removed for run {RunId}. Remaining: {Count}",
            runId, CountSubscribers(runId));
    }

    /// <summary>
    /// Broadcasts an event entity to all active SSE subscribers for the run.
    /// Uses TryWrite (non-blocking) — a closed or full channel is silently removed.
    /// </summary>
    public Task BroadcastAsync(EventEntity eventEntity, CancellationToken ct = default)
    {
        List<Channel<EventEntity>>? snapshot = null;

        lock (_lock)
        {
            if (_subscribers.TryGetValue(eventEntity.RunId, out var list) && list.Count > 0)
            {
                snapshot = new List<Channel<EventEntity>>(list);
            }
        }

        if (snapshot is null)
        {
            return Task.CompletedTask;
        }

        List<Channel<EventEntity>>? dead = null;

        foreach (var channel in snapshot)
        {
            if (!channel.Writer.TryWrite(eventEntity))
            {
                _logger.LogWarning(
                    "SSE channel for run {RunId} rejected event seq={Seq}; removing.",
                    eventEntity.RunId, eventEntity.Sequence);
                dead ??= new List<Channel<EventEntity>>();
                dead.Add(channel);
            }
        }

        if (dead is not null)
        {
            foreach (var ch in dead)
            {
                Unsubscribe(eventEntity.RunId, ch);
            }
        }

        return Task.CompletedTask;
    }

    private int CountSubscribers(Guid runId) =>
        _subscribers.TryGetValue(runId, out var list) ? list.Count : 0;
}
