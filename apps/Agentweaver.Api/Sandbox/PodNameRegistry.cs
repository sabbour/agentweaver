using System.Collections.Concurrent;
using Agentweaver.AgentRuntime.Workflow;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IPodNameRegistry"/>.
/// </summary>
public sealed class PodNameRegistry : IPodNameRegistry, IAgentHostTurnTokenRegistry
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _agentEndpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _turnTokens = new(StringComparer.Ordinal);

    public void Register(string runId, string podName) =>
        _map[runId] = podName;

    public void Unregister(string runId)
    {
        _map.TryRemove(runId, out _);
        _agentEndpoints.TryRemove(runId, out _);
        _turnTokens.TryRemove(runId, out _);
    }

    public string? TryGet(string runId) =>
        _map.TryGetValue(runId, out var podName) ? podName : null;

    public void RegisterAgentEndpoint(string runId, string endpointUrl) =>
        _agentEndpoints[runId] = endpointUrl;

    public string? TryGetAgentEndpoint(string runId) =>
        _agentEndpoints.TryGetValue(runId, out var url) ? url : null;

    public void RegisterTurnToken(string runId, string token) =>
        _turnTokens[runId] = token;

    public void UnregisterTurnToken(string runId) =>
        _turnTokens.TryRemove(runId, out _);

    public string? TryGetTurnToken(string runId) =>
        _turnTokens.TryGetValue(runId, out var token) ? token : null;
}
