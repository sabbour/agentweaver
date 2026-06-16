using System.Threading.Channels;

namespace Scaffolder.Domain;

/// <summary>
/// Integration seam to the agent loop implemented in Scaffolder.AgentRuntime.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Executes one agent turn for the given task and returns the agent's full response.
    /// Chunks are written to <paramref name="stream"/> as they arrive when provided.
    /// </summary>
    /// <param name="repositoryPath">
    /// The original repository path. Used to read project-scoped configuration
    /// (e.g. .scaffolder/settings.yml) from the live repo rather than the worktree checkout.
    /// </param>
    Task<string> ExecuteAsync(string task, string workingDirectory, string repositoryPath, ModelSource modelSource, string runId, string? modelId, ChannelWriter<RunEvent>? stream, CancellationToken ct, string? systemPromptContext = null);
}
