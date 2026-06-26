using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;
using System.Net;

namespace Agentweaver.AgentRuntime.Providers;

/// <summary>
/// Creates <see cref="CopilotClient"/> instances configured from settings.
/// Each run gets a fresh client and must dispose it.
/// </summary>
public sealed class GitHubCopilotClientFactory : IAsyncDisposable
{
    private readonly string? _configFallbackToken;
    private readonly string? _configFallbackTokenFile;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    private readonly ILogger<GitHubCopilotClientFactory>? _logger;
    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] RateLimitRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public GitHubCopilotClientFactory(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider? accessTokenProvider = null,
        ILogger<GitHubCopilotClientFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(scopeProvider);

        var section = configuration.GetSection("Providers:GitHubCopilot");
        _configFallbackToken = section.GetValue<string>("GitHubToken")
            ?? section.GetValue<string>("ApiKey");
        _configFallbackTokenFile = section.GetValue<string>("GitHubTokenFile")
            ?? section.GetValue<string>("ApiKeyFile");
        _tokenStore = tokenStore;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// Synchronous factory kept for backward compatibility during transition.
    /// Uses only the config fallback token; does not consult the token store.
    /// </summary>
    public CopilotClient CreateClient()
    {
        var options = new CopilotClientOptions();
        var token = ReadConfigFallbackToken();
        if (!string.IsNullOrWhiteSpace(token))
            options.GitHubToken = token;
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
            GitHubTokenStatus.NeverSignedIn => ReadConfigFallbackToken(), // config MAY be used locally
            _ => null
        };
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot is not authorized. Sign in with 'agentweaver github sign-in'.");
        options.GitHubToken = token;
        return new CopilotClient(options);
    }

    /// <summary>
    /// Returns true when the persisted access token is expired or close enough to expiry that a
    /// streaming call should recreate its Copilot client before starting the call.
    /// </summary>
    public async Task<bool> ShouldRefreshBeforeAiCallAsync(GitHubTokenScope scope, CancellationToken ct)
    {
        var token = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
        if (token?.ExpiresAt is null)
            return false;

        return token.ExpiresAt <= DateTimeOffset.UtcNow.Add(TokenExpirySkew);
    }

    public static bool IsUnauthorized(Exception ex) =>
        HasStatusCode(ex, HttpStatusCode.Unauthorized) || ExceptionText(ex).Contains("401", StringComparison.OrdinalIgnoreCase);

    public static bool IsRateLimited(Exception ex) =>
        HasStatusCode(ex, HttpStatusCode.TooManyRequests)
        || ExceptionText(ex).Contains("429", StringComparison.OrdinalIgnoreCase)
        || ExceptionText(ex).Contains("too many requests", StringComparison.OrdinalIgnoreCase)
        || ExceptionText(ex).Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan? GetRateLimitRetryDelay(int retryAttempt)
    {
        if (retryAttempt < 1 || retryAttempt > RateLimitRetryDelays.Length)
            return null;
        return RateLimitRetryDelays[retryAttempt - 1];
    }

    public void LogAiRetry(Exception ex, int retryAttempt, TimeSpan delay, string reason) =>
        _logger?.LogWarning(
            ex,
            "Retrying GitHub Copilot AI call after {DelayMs}ms (attempt {Attempt}/{MaxAttempts}) due to {Reason}",
            (int)delay.TotalMilliseconds,
            retryAttempt,
            RateLimitRetryDelays.Length,
            reason);

    private static bool HasStatusCode(Exception ex, HttpStatusCode statusCode)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http && http.StatusCode == statusCode)
                return true;
        }

        return false;
    }

    private static string ExceptionText(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }

    private string? ReadConfigFallbackToken()
    {
        if (!string.IsNullOrWhiteSpace(_configFallbackTokenFile))
        {
            try
            {
                if (File.Exists(_configFallbackTokenFile))
                {
                    var token = File.ReadAllText(_configFallbackTokenFile).Trim();
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
            catch (IOException)
            {
                // Fall back to direct config below; auth failure handling must not leak paths or token data.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to direct config below; auth failure handling must not leak paths or token data.
            }
        }

        return _configFallbackToken;
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
