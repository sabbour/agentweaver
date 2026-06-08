using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Scaffolder.Api.Infrastructure;

public sealed class RunStreamStore
{
    private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();

    public Channel<string> CreateChannel(string runId)
    {
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _channels[runId] = ch;
        return ch;
    }

    public Channel<string>? Get(string runId) =>
        _channels.TryGetValue(runId, out var ch) ? ch : null;

    public void Remove(string runId) => _channels.TryRemove(runId, out _);
}
