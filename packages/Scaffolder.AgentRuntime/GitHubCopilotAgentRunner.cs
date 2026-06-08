using GitHub.Copilot;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Runs a single agent turn via the Microsoft Agent Framework with the GitHub
/// Copilot SDK backend.
/// </summary>
public sealed class GitHubCopilotAgentRunner : IAgentRunner
{
    private readonly GitHubCopilotClientFactory _factory;

    public GitHubCopilotAgentRunner(GitHubCopilotClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<string> ExecuteAsync(string task, CancellationToken ct)
    {
        await using var client = _factory.CreateClient();
        await client.StartAsync(ct);

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false,
            id: null, name: null, description: null);

        var session = await agent.CreateSessionAsync(ct);
        var result = await agent.RunAsync(task, session, options: null, ct);
        return result.ToString();
    }
}
