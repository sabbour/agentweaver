namespace Scaffolder.Api.Agent;

public interface IAgentLoopHost
{
    /// <summary>
    /// Executes the agent loop for a run.
    /// Returns when the task is complete, or when ct is cancelled (bounds exceeded).
    /// </summary>
    Task ExecuteAsync(AgentLoopContext context, CancellationToken ct);
}

public sealed class AgentLoopContext
{
    public required Guid RunId { get; init; }
    public required string TaskPrompt { get; init; }
    public required string ArtifactDir { get; init; }
    public required ModelSourceType ModelSource { get; init; }
}

public enum ModelSourceType
{
    CopilotSdk,
    MicrosoftFoundry
}
