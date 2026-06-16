using System.Threading.Channels;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

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
        string? systemPromptContext = null) =>
        modelSource switch
        {
            ModelSource.GitHubCopilot => _copilot.ExecuteAsync(task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct, systemPromptContext),
            ModelSource.MicrosoftFoundry => _foundry.ExecuteAsync(task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct, systemPromptContext),
            _ => throw new NotSupportedException($"Model source '{modelSource}' is not configured."),
        };
}
