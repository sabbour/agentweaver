using System.Collections.Concurrent;
using System.Threading.Channels;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

public sealed class RunStreamEntry
{
    public Channel<RunEvent> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true });
    public List<RunEvent> History { get; } = [];
    private readonly Lock _lock = new();

    public void Record(RunEvent evt)
    {
        lock (_lock) History.Add(evt);
    }

    public IReadOnlyList<RunEvent> GetSince(int lastSeen)
    {
        lock (_lock) return History.Where(e => e.Sequence > lastSeen).ToList();
    }
}

public sealed class RunStreamStore
{
    private readonly ConcurrentDictionary<string, RunStreamEntry> _entries = new();

    public RunStreamEntry Create(string runId)
    {
        var entry = new RunStreamEntry();
        _entries[runId] = entry;
        return entry;
    }

    public RunStreamEntry? Get(string runId) =>
        _entries.TryGetValue(runId, out var e) ? e : null;

    public void Remove(string runId) => _entries.TryRemove(runId, out _);
}
