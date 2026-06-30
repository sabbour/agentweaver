using System.Threading.Channels;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

public sealed class AgentRunnerDispatcher : IAgentRunner
{
    private readonly GitHubCopilotAgentRunner _copilot;
    private readonly FoundryAgentRunner _foundry;

    public AgentRunnerDispatcher(GitHubCopilotAgentRunner copilot, FoundryAgentRunner foundry)
    {
        _copilot = copilot;
        _foundry = foundry;
    }

    public Task<string> ExecuteAsync(
        string task,
        string workingDirectory,
        string repositoryPath,
        ModelSource modelSource,
        string runId,
        string? modelId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct,
        string? systemPromptContext = null,
        string? userId = null) =>
        modelSource switch
        {
            ModelSource.GitHubCopilot => _copilot.ExecuteAsync(task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct, systemPromptContext, userId),
            ModelSource.MicrosoftFoundry => _foundry.ExecuteAsync(task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct, systemPromptContext, userId),
            _ => throw new NotSupportedException($"Model source '{modelSource}' is not configured."),
        };
}
