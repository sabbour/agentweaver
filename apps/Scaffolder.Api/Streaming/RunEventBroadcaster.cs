using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Streaming;

/// <summary>
/// In-memory fan-out for live run-step streaming (Principle V, FR-021). Each run
/// has a stream that delivers every published event to all subscribers. A new
/// subscriber first attaches to the live feed, then backfills from the durable
/// log for any sequence after its cursor, then continues live. Overlapping
/// events are de-duplicated by sequence, so reconnect with a Last-Event-ID cursor
/// replays only the missing tail and then continues without gaps.
/// </summary>
public sealed class RunEventBroadcaster : IRunEventPublisher
{
    private readonly ConcurrentDictionary<RunId, RunStream> _streams = new();
    private readonly SqliteEventStore _store;

    public RunEventBroadcaster(SqliteEventStore store) => _store = store;

    public void Publish(RunEvent evt)
    {
        var stream = _streams.GetOrAdd(evt.RunId, _ => new RunStream());
        stream.Publish(evt);
    }

    public void Complete(RunId runId)
    {
        if (_streams.TryGetValue(runId, out var stream))
        {
            stream.Complete();
        }
    }

    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        RunId runId,
        int afterSequence,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(runId, _ => new RunStream());
        var live = stream.AddSubscriber();
        var lastYielded = afterSequence;

        try
        {
            // Replay the durable tail after the caller's cursor. Track whether the replay
            // itself delivered a terminal event — this handles the case where a run has
            // already finished and its RunStream was evicted (all prior subscribers left),
            // causing GetOrAdd above to create a fresh, uncompleted stream. Without this
            // guard the live loop would block on WaitToReadAsync indefinitely.
            var replayedTerminal = false;
            await foreach (var evt in _store.ReadFromAsync(runId, afterSequence, ct).ConfigureAwait(false))
            {
                if (evt.Sequence > lastYielded)
                {
                    lastYielded = evt.Sequence;
                    yield return evt;
                    if (IsTerminalEvent(evt.Type))
                        replayedTerminal = true;
                }
            }

            // Only enter the live loop if the replay did not already cover a terminal event.
            // When AddSubscriber() sees _completed=true (first-viewer path), the channel is
            // immediately completed and WaitToReadAsync returns false on the first call anyway;
            // the replayedTerminal guard covers the evicted-stream re-subscription path.
            if (!replayedTerminal)
            {
                while (await live.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (live.Reader.TryRead(out var evt))
                    {
                        if (evt.Sequence > lastYielded)
                        {
                            lastYielded = evt.Sequence;
                            yield return evt;
                        }
                    }
                }
            }
        }
        finally
        {
            if (stream.RemoveSubscriber(live))
            {
                _streams.TryRemove(new KeyValuePair<RunId, RunStream>(runId, stream));
            }
        }
    }

    private static bool IsTerminalEvent(string type) =>
        type is EventType.RunCompleted or EventType.RunFailed or EventType.RunBounded;

    private sealed class RunStream
    {
        private readonly object _gate = new();
        private readonly List<Channel<RunEvent>> _subscribers = new();
        private bool _completed;

        public void Publish(RunEvent evt)
        {
            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(evt);
                }
            }
        }

        public Channel<RunEvent> AddSubscriber()
        {
            var channel = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            lock (_gate)
            {
                _subscribers.Add(channel);
                if (_completed)
                {
                    channel.Writer.TryComplete();
                }
            }

            return channel;
        }

        /// <summary>
        /// Removes a subscriber and returns true when the stream is finished and
        /// has no remaining subscribers, signalling the caller to discard it.
        /// </summary>
        public bool RemoveSubscriber(Channel<RunEvent> channel)
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
                return _completed && _subscribers.Count == 0;
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
    }
}
