using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Providers;

/// <summary>
/// Creates <see cref="CopilotClient"/> instances configured from settings.
/// Each run gets a fresh client and must dispose it.
/// </summary>
public sealed class GitHubCopilotClientFactory : IAsyncDisposable
{
    private readonly string? _configFallbackToken;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;

    public GitHubCopilotClientFactory(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider? accessTokenProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(scopeProvider);

        var section = configuration.GetSection("Providers:GitHubCopilot");
        _configFallbackToken = section.GetValue<string>("GitHubToken")
            ?? section.GetValue<string>("ApiKey");
        _tokenStore = tokenStore;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
    }

    /// <summary>
    /// Synchronous factory kept for backward compatibility during transition.
    /// Uses only the config fallback token; does not consult the token store.
    /// </summary>
    public CopilotClient CreateClient()
    {
        var options = new CopilotClientOptions();
        if (!string.IsNullOrWhiteSpace(_configFallbackToken))
            options.GitHubToken = _configFallbackToken;
        return new CopilotClient(options);
    }

    /// <summary>
    /// Resolves the token for the given scope and returns a configured client.
    /// Throws <see cref="GitHubCopilotUnauthorizedException"/> when no valid token is available.
    /// The model ID is applied to the session later via <see cref="GitHub.Copilot.SDK.SessionConfig.Model"/>;
    /// it is accepted here to keep the factory signature aligned with the runner call site.
    /// </summary>
    public async Task<CopilotClient> CreateClientAsync(
        GitHubTokenScope scope, string? modelId, CancellationToken ct)
    {
        var options = new CopilotClientOptions();
        var entry = await _tokenStore.GetAsync(scope, ct).ConfigureAwait(false);
        var token = entry.Status switch
        {
            // Route signed-in tokens through the refresh-aware provider so an expired access
            // token is transparently rotated; fall back to the raw token when no provider is wired.
            GitHubTokenStatus.SignedIn      => _accessTokenProvider is not null
                                                   ? await _accessTokenProvider
                                                       .GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false)
                                                   : entry.AccessToken,
            GitHubTokenStatus.SignedOut     => null,                   // fail closed after explicit sign-out
            GitHubTokenStatus.NeverSignedIn => _configFallbackToken,   // config MAY be used locally
            _ => null
        };
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot is not authorized. Sign in with 'agentweaver github sign-in'.");
        options.GitHubToken = token;
        return new CopilotClient(options);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Thrown when no valid GitHub token is available for Copilot.
/// Does not include token content or credential details in the message.
/// </summary>
public sealed class GitHubCopilotUnauthorizedException : Exception
{
    public GitHubCopilotUnauthorizedException(string message) : base(message) { }
}
