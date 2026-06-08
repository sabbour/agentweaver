using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Routes run execution to the correct provider runner based on
/// <see cref="ModelSource"/> (Principle II).
/// </summary>
public sealed class AgentRunnerRouter : IAgentRunner
{
    private readonly AgentRunner _foundryRunner;
    private readonly GitHubCopilotAgentRunner _copilotRunner;

    public AgentRunnerRouter(AgentRunner foundryRunner, GitHubCopilotAgentRunner copilotRunner)
    {
        _foundryRunner = foundryRunner ?? throw new ArgumentNullException(nameof(foundryRunner));
        _copilotRunner = copilotRunner ?? throw new ArgumentNullException(nameof(copilotRunner));
    }

    public Task ExecuteAsync(Run run, IRunEventPublisher publisher, IRunEventStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.ModelSource switch
        {
            ModelSource.MicrosoftFoundry => _foundryRunner.ExecuteAsync(run, publisher, store, ct),
            ModelSource.GitHubCopilot    => _copilotRunner.ExecuteAsync(run, publisher, store, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(run), $"Unsupported model source: {run.ModelSource}"),
        };
    }
}
