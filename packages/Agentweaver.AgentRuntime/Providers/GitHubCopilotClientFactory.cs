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
    private readonly string? _runtimeCliPath;
    private readonly IGitHubCopilotCapabilityCredentialProvider _credentialProvider;
    private readonly ILogger<GitHubCopilotClientFactory>? _logger;
    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] RateLimitRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public GitHubCopilotClientFactory(
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        IGitHubCopilotCapabilityCredentialProvider credentialProvider,
        ILogger<GitHubCopilotClientFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialProvider);

        var section = configuration.GetSection("Providers:GitHubCopilot");
        // Optional explicit path to the native Copilot CLI runtime. When the SDK's automatic
        // resolution (bin/.../runtimes/<rid>/native/copilot) is unavailable — e.g. a dev host
        // whose RID was never provisioned into the build output — this lets an operator point the
        // runtime at a locally installed CLI instead. Precedence: config > env var.
        _runtimeCliPath = FirstNonWhiteSpace(
            section.GetValue<string>("RuntimeCliPath"),
            Environment.GetEnvironmentVariable("AGENTWEAVER_COPILOT_CLI_PATH"),
            Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"));
        _credentialProvider = credentialProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the immutable credential for the given run and returns a configured client.
    /// Throws <see cref="GitHubCopilotUnauthorizedException"/> when its snapshot cannot be redeemed.
    /// The model ID is applied to the session later via <see cref="GitHub.Copilot.SDK.SessionConfig.Model"/>;
    /// it is accepted here to keep the factory signature aligned with the runner call site.
    /// </summary>
    public async Task<CopilotClient> CreateClientAsync(
        string runId, string? modelId, CancellationToken ct)
    {
        var options = new CopilotClientOptions();
        ApplyRuntimeConnection(options);
        var credential = await _credentialProvider.GetCredentialAsync(runId, ct).ConfigureAwait(false);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken) ||
            credential.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires a live run-bound capability snapshot.");
        options.GitHubToken = credential.AccessToken;
        return new CopilotClient(options);
    }

    public CopilotClient CreateByokClient()
    {
        var options = new CopilotClientOptions();
        ApplyRuntimeConnection(options);
        return new CopilotClient(options);
    }

    /// <summary>
    /// Resolves a caller- and project-bound marketplace-classification capability. This path is
    /// intentionally separate from run snapshot redemption so a non-run request cannot fabricate
    /// a run identifier or borrow an ambient credential scope.
    /// </summary>
    public async Task<CopilotClient> CreateMarketplaceClientAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        string? modelId,
        CancellationToken ct) =>
        await CreateProjectOperationClientAsync(
            capabilityReference,
            projectId,
            entraObjectId,
            ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification,
            modelId,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Resolves one explicit, purpose-bound non-run capability. This deliberately does not accept
    /// a run id and cannot fall back to an ambient or installation-scoped credential.
    /// </summary>
    public async Task<CopilotClient> CreateProjectOperationClientAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        ProjectModelProviderCapabilityPurpose purpose,
        string? modelId,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(purpose) ||
            string.IsNullOrWhiteSpace(capabilityReference) ||
            string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(entraObjectId))
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires a live project-bound capability.");

        var options = new CopilotClientOptions();
        ApplyRuntimeConnection(options);
        var credential = await _credentialProvider
            .GetProjectOperationCredentialAsync(
                capabilityReference, projectId, entraObjectId, purpose, ct)
            .ConfigureAwait(false);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken) ||
            credential.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires a live project-bound capability.");
        options.GitHubToken = credential.AccessToken;
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


    public async Task<bool> ShouldRefreshBeforeAiCallAsync(string runId, CancellationToken ct)
    {
        var credential = await _credentialProvider.GetCredentialAsync(runId, ct).ConfigureAwait(false);
        return credential is null || credential.ExpiresAt <= DateTimeOffset.UtcNow.Add(TokenExpirySkew);
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

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
