namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Shared source of truth for the AgentHost pod bound to a run. The in-process
/// <see cref="IPodNameRegistry"/> remains a cache; this store is readable by any API replica.
/// </summary>
public interface IExecutionPodNameStore
{
    void Register(string runId, string podName);
    string? TryGet(string runId);
}
