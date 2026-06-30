using System.Collections.Concurrent;
using Agentweaver.AgentRuntime.Workflow;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thread-safe in-memory cache for <see cref="IPodNameRegistry"/>. Pod names are also mirrored to a
/// shared store so API replicas that did not launch the AgentHost can still render graph pod badges.
/// </summary>
public sealed class PodNameRegistry : IPodNameRegistry, IAgentHostTurnTokenRegistry
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _agentEndpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _turnTokens = new(StringComparer.Ordinal);
    private readonly IExecutionPodNameStore? _executionPods;

    public PodNameRegistry(IExecutionPodNameStore? executionPods = null)
    {
        _executionPods = executionPods;
    }

    public void Register(string runId, string podName)
    {
        _map[runId] = podName;
        _executionPods?.Register(runId, podName);
    }

    public void Unregister(string runId)
    {
        _map.TryRemove(runId, out _);
        _agentEndpoints.TryRemove(runId, out _);
        _turnTokens.TryRemove(runId, out _);
    }

    public string? TryGet(string runId)
    {
        var podName = _executionPods?.TryGet(runId);
        if (!string.IsNullOrWhiteSpace(podName))
        {
            _map[runId] = podName;
            return podName;
        }

        if (_map.TryGetValue(runId, out podName))
            return podName;
        return podName;
    }

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
