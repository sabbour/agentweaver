using GitHub.Copilot;
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
    private readonly string? _runtimeCliPath;
    private readonly IGitHubTokenStore? _tokenStore;
    private readonly IGitHubTokenScopeProvider? _scopeProvider;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    private readonly ICopilotCredentialProvider? _runBoundCredentialProvider;
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
        IGitHubTokenStore? tokenStore = null,
        IGitHubTokenScopeProvider? scopeProvider = null,
        IGitHubAccessTokenProvider? accessTokenProvider = null,
        ILogger<GitHubCopilotClientFactory>? logger = null,
        ICopilotCredentialProvider? runBoundCredentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (tokenStore is null && runBoundCredentialProvider is null)
            throw new ArgumentException(
                "A token store or a run-bound Copilot credential provider is required.");

        var section = configuration.GetSection("Providers:GitHubCopilot");
        _configFallbackToken = section.GetValue<string>("GitHubToken")
            ?? section.GetValue<string>("ApiKey");
        _configFallbackTokenFile = section.GetValue<string>("GitHubTokenFile")
            ?? section.GetValue<string>("ApiKeyFile");
        // Optional explicit path to the native Copilot CLI runtime. When the SDK's automatic
        // resolution (bin/.../runtimes/<rid>/native/copilot) is unavailable — e.g. a dev host
        // whose RID was never provisioned into the build output — this lets an operator point the
        // runtime at a locally installed CLI instead. Precedence: config > env var.
        _runtimeCliPath = FirstNonWhiteSpace(
            section.GetValue<string>("RuntimeCliPath"),
            Environment.GetEnvironmentVariable("AGENTWEAVER_COPILOT_CLI_PATH"),
            Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"));
        _tokenStore = tokenStore;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
        _runBoundCredentialProvider = runBoundCredentialProvider;
    }

    /// <summary>
    /// Synchronous factory kept for backward compatibility during transition.
    /// Uses only the config fallback token; does not consult the token store.
    /// </summary>
    public CopilotClient CreateClient()
    {
        var options = new CopilotClientOptions();
        ApplyRuntimeConnection(options);
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
        ApplyRuntimeConnection(options);
        if (_runBoundCredentialProvider is not null)
        {
            var credential = await _runBoundCredentialProvider.GetAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential?.AccessToken))
                throw new GitHubCopilotUnauthorizedException(
                    "GitHub Copilot is not authorized for this run.");
            options.GitHubToken = credential.AccessToken;
            return new CopilotClient(options);
        }

        var entry = await _tokenStore!.GetAsync(scope, ct).ConfigureAwait(false);
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
    /// Applies an explicit Copilot runtime CLI path when one is configured, overriding the SDK's
    /// default resolution (which probes <c>bin/.../runtimes/&lt;rid&gt;/native/copilot</c> relative to
    /// the output directory). This is the escape hatch for hosts whose RID was never provisioned
    /// into the build output. When no path is configured the SDK's bundled/auto-resolved runtime is
    /// used unchanged.
    /// </summary>
    private void ApplyRuntimeConnection(CopilotClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(_runtimeCliPath))
            return;

        if (!File.Exists(_runtimeCliPath))
        {
            _logger?.LogWarning(
                "Configured Copilot runtime CLI path does not exist; falling back to SDK auto-resolution. " +
                "Set Providers:GitHubCopilot:RuntimeCliPath (or AGENTWEAVER_COPILOT_CLI_PATH) to a valid CLI binary.");
            return;
        }

        options.Connection = RuntimeConnection.ForStdio(_runtimeCliPath, Array.Empty<string>());
        _logger?.LogInformation("Using explicit Copilot runtime CLI via stdio connection override.");
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }


    public async Task<bool> ShouldRefreshBeforeAiCallAsync(GitHubTokenScope scope, CancellationToken ct)
    {
        if (_runBoundCredentialProvider is not null)
        {
            var credential = await _runBoundCredentialProvider.GetAsync(ct).ConfigureAwait(false);
            return credential?.ExpiresAt <= DateTimeOffset.UtcNow.Add(TokenExpirySkew);
        }

        var token = await _tokenStore!.GetTokenAsync(scope, ct).ConfigureAwait(false);
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

/// <summary>Trusted provider for ephemeral, inference-only Copilot sign-in material.</summary>
public interface ICopilotCredentialProvider
{
    Task<CopilotCredential?> GetAsync(CancellationToken ct = default);
}

/// <summary>Non-serializable Copilot credential held only by a trusted runtime process.</summary>
public sealed record CopilotCredential(string AccessToken, DateTimeOffset? ExpiresAt);

/// <summary>
/// Thrown when no valid GitHub token is available for Copilot.
/// Does not include token content or credential details in the message.
/// </summary>
public sealed class GitHubCopilotUnauthorizedException : AgentProviderException
{
    /// <summary>
    /// Canonical provider error code for "Copilot needs a (re-)sign-in". Must stay in sync with the
    /// <c>github_copilot_auth_required</c> literal used by the other Copilot runtime call sites.
    /// Named distinctly from the inherited <see cref="AgentProviderException.ErrorCode"/> instance
    /// property so it does not hide it (CS0108, warning-as-error).
    /// </summary>
    public const string AuthRequiredErrorCode = "github_copilot_auth_required";

    public GitHubCopilotUnauthorizedException(string message)
        : base(
            ModelSource.GitHubCopilot,
            AgentProviderFailureKind.Authorization,
            AuthRequiredErrorCode,
            message,
            isRetryable: false)
    {
    }
}
