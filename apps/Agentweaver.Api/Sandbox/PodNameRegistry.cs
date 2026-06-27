using System.Collections.Concurrent;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IPodNameRegistry"/>.
/// </summary>
public sealed class PodNameRegistry : IPodNameRegistry
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _agentEndpoints = new(StringComparer.Ordinal);

    public void Register(string runId, string podName) =>
        _map[runId] = podName;

    public void Unregister(string runId)
    {
        _map.TryRemove(runId, out _);
        _agentEndpoints.TryRemove(runId, out _);
    }

    public string? TryGet(string runId) =>
        _map.TryGetValue(runId, out var podName) ? podName : null;

    public void RegisterAgentEndpoint(string runId, string endpointUrl) =>
        _agentEndpoints[runId] = endpointUrl;

    public string? TryGetAgentEndpoint(string runId) =>
        _agentEndpoints.TryGetValue(runId, out var url) ? url : null;
}
