using Microsoft.Extensions.AI;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Routes a run to the correct provider factory based on its
/// <see cref="ModelSource"/>. Only the two permitted sources are accepted; any
/// other value is rejected at the runtime layer (Principle II).
/// </summary>
public sealed class ChatClientFactoryRouter
{
    private readonly GitHubCopilotChatClientFactory _copilot;
    private readonly MicrosoftFoundryChatClientFactory _foundry;

    public ChatClientFactoryRouter(
        GitHubCopilotChatClientFactory copilot,
        MicrosoftFoundryChatClientFactory foundry)
    {
        _copilot = copilot ?? throw new ArgumentNullException(nameof(copilot));
        _foundry = foundry ?? throw new ArgumentNullException(nameof(foundry));
    }

    public IChatClient CreateForRun(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.ModelSource switch
        {
            ModelSource.GitHubCopilot => _copilot.CreateForRun(run),
            ModelSource.MicrosoftFoundry => _foundry.CreateForRun(run),
            _ => throw new ArgumentOutOfRangeException(
                nameof(run), $"Unsupported model source: {run.ModelSource}")
        };
    }
}
