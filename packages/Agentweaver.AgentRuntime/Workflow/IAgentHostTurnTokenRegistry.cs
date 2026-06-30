namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Run-scoped registry for the bearer token required by AgentHost A2A turn calls.
/// </summary>
public interface IAgentHostTurnTokenRegistry
{
    /// <summary>Registers the turn bearer token for the given run. Overwrites any prior entry.</summary>
    void RegisterTurnToken(string runId, string token);

    /// <summary>Removes the turn token when the AgentHost pod is released.</summary>
    void UnregisterTurnToken(string runId);

    /// <summary>Returns the turn token for the given run, or <see langword="null"/> if not registered.</summary>
    string? TryGetTurnToken(string runId);
}
