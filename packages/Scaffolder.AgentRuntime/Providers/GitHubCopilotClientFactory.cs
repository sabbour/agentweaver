using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Creates <see cref="CopilotClient"/> instances configured from settings.
/// Each run gets a fresh client and must dispose it.
/// </summary>
public sealed class GitHubCopilotClientFactory : IAsyncDisposable
{
    private readonly string? _gitHubToken;

    public GitHubCopilotClientFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Providers:GitHubCopilot");

        _gitHubToken = section.GetValue<string>("GitHubToken")
            ?? section.GetValue<string>("ApiKey");
    }

    public CopilotClient CreateClient()
    {
        var options = new CopilotClientOptions();
        if (!string.IsNullOrWhiteSpace(_gitHubToken))
            options.GitHubToken = _gitHubToken;

        return new CopilotClient(options);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
