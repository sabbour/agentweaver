namespace Scaffolder.Domain;

/// <summary>
/// Integration seam to the agent loop implemented in Scaffolder.AgentRuntime.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Executes one agent turn for the given task and returns the agent's response.
    /// </summary>
    Task<string> ExecuteAsync(string task, string workingDirectory, CancellationToken ct);
}
