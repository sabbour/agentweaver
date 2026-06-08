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
    Task<string> ExecuteAsync(string task, string workingDirectory, ModelSource modelSource, string runId, ChannelWriter<RunEvent>? stream, CancellationToken ct);
}
