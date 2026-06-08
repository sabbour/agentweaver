using GitHub.Copilot;
using Microsoft.Extensions.Configuration;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Creates and owns the configuration for the GitHub Copilot
/// <see cref="CopilotClient"/> lifecycle (Principle II). Configuration is read
/// from the <c>Providers:GitHubCopilot</c> section at startup.
/// </summary>
/// <remarks>
/// The Copilot path drives the GitHub Copilot CLI subprocess through the
/// official <c>GitHub.Copilot.SDK</c> rather than an OpenAI-compatible chat
/// endpoint, so it does not implement <see cref="IChatClientFactory"/>. Each run
/// gets a fresh <see cref="CopilotClient"/> from <see cref="CreateClient"/> and
/// disposes it when the run completes.
/// </remarks>
public sealed class GitHubCopilotClientFactory : IAsyncDisposable
{
    private const string DefaultModel = "gpt-4.1";

    private readonly string _gitHubToken;
    private readonly string _model;

    public GitHubCopilotClientFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Providers:GitHubCopilot");

        // Prefer the dedicated 'GitHubToken' key; fall back to the legacy
        // 'ApiKey' key so existing deployments keep working.
        _gitHubToken = section.GetValue<string>("GitHubToken")
            ?? section.GetValue<string>("ApiKey")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:GitHubCopilot:GitHubToken' for the GitHub Copilot model source.");

        _model = section.GetValue<string>("Model") ?? DefaultModel;
    }

    /// <summary>The model identifier the Copilot session should use.</summary>
    public string Model => _model;

    /// <summary>
    /// Creates a new <see cref="CopilotClient"/> configured to spawn the Copilot
    /// CLI subprocess and authenticate with the configured token. The caller owns
    /// the returned client and must dispose it.
    /// </summary>
    public CopilotClient CreateClient()
    {
        return new CopilotClient(new CopilotClientOptions
        {
            GitHubToken = _gitHubToken,
            Mode = CopilotClientMode.CopilotCli,
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
