using System.Threading.Channels;

namespace Agentweaver.Domain;

/// <summary>
/// A pre-issued, purpose-bound, non-run GitHub Copilot capability for exactly one
/// <see cref="IAgentRunner"/> call. Non-run AI generation features (blueprint/workflow/skill/casting
/// generation) have no <c>Run</c> entity and therefore no run-bound capability snapshot to redeem; they
/// must not fabricate a synthetic run id and expect a GitHub-Copilot-sourced call to succeed. When this
/// is supplied, the runner redeems it via the project-operation capability mechanism instead of the
/// run-snapshot mechanism.
/// </summary>
public sealed record CopilotOperationCapability(
    string CapabilityReference,
    string ProjectId,
    string EntraObjectId,
    ProjectModelProviderCapabilityPurpose Purpose);

/// <summary>
/// Integration seam to the agent loop implemented in Agentweaver.AgentRuntime.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Executes one agent turn for the given task and returns the agent's full response.
    /// Chunks are written to <paramref name="stream"/> as they arrive when provided.
    /// </summary>
    /// <param name="repositoryPath">
    /// The original repository path. Used to read project-scoped configuration
    /// (e.g. .agentweaver/settings.yml) from the live repo rather than the worktree checkout.
    /// </param>
    Task<string> ExecuteAsync(string task, string workingDirectory, string repositoryPath, ModelSource modelSource, string runId, string? modelId, ChannelWriter<RunEvent>? stream, CancellationToken ct, string? systemPromptContext = null, string? userId = null);

    Task<string> ExecuteForProjectAsync(
        string task,
        string workingDirectory,
        string repositoryPath,
        ModelSource modelSource,
        string runId,
        string? modelId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct,
        string? systemPromptContext = null,
        string? userId = null,
        string? projectId = null,
        CopilotOperationCapability? copilotCapability = null) =>
        ExecuteAsync(
            task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct,
            systemPromptContext, userId);
}
