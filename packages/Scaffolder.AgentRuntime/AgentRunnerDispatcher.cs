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
        ModelSource modelSource,
        string runId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct) =>
        modelSource switch
        {
            ModelSource.GitHubCopilot => _copilot.ExecuteAsync(task, workingDirectory, modelSource, runId, stream, ct),
            ModelSource.MicrosoftFoundry => _foundry.ExecuteAsync(task, workingDirectory, modelSource, runId, stream, ct),
            _ => throw new NotSupportedException($"Model source '{modelSource}' is not configured."),
        };
}
