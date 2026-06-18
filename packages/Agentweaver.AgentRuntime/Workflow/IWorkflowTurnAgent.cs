using System.Threading.Channels;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// The minimal contract a workflow turn executor needs from an agent: provision per-run
/// state, then run a single turn (creating or resuming the underlying session internally)
/// and return the accumulated assistant text.
///
/// <para>
/// Extracting this interface restores a test seam: production resolves
/// <see cref="CopilotAIAgent"/> (and its <c>RaiAIAgent</c>/<c>ScribeAIAgent</c> subclasses)
/// via <see cref="IWorkflowAgentFactory"/>, while tests can supply a fake that performs
/// deterministic file/event operations without touching the GitHub Copilot SDK.
/// </para>
/// </summary>
public interface IWorkflowTurnAgent : IAsyncDisposable
{
    /// <summary>
    /// Provisions the agent for a single run. Must be called before <see cref="RunTurnAsync"/>.
    /// </summary>
    Task SetupAsync(
        string workingDirectory,
        string repositoryPath,
        string runId,
        string? modelId,
        string? systemPromptContext,
        ChannelWriter<RunEvent>? streamWriter,
        string? projectId,
        string? agentName,
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken ct);

    /// <summary>
    /// Runs a single agent turn. When <paramref name="isRevision"/> is true the agent resumes
    /// its existing session (retaining conversation history); otherwise it creates a fresh one.
    /// Returns the accumulated assistant text for the turn.
    /// </summary>
    Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct);
}
