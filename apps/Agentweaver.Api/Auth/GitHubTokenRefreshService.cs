using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// The single place where GitHub user access tokens are validated and transparently refreshed.
/// Both OAuth-redirect and device-flow issued tokens are persisted via <see cref="IGitHubTokenStore"/>
/// keyed by scope, so routing every consumer through <see cref="GetValidAccessTokenAsync"/> means
/// tokens from either flow are refreshed identically.
///
/// Refresh policy:
///  - No stored token (signed-out / never-signed-in)        -> return null.
///  - Non-expiring token (ExpiresAt is null, classic token) -> return access token unchanged.
///  - Token comfortably in the future (outside skew window) -> return access token unchanged.
///  - Token expired/near-expiry WITH a refresh token        -> POST grant_type=refresh_token,
///       persist the rotated token, return the fresh access token.
///  - Refresh not possible (no refresh token, GitHub error, revoked/expired refresh token)
///       -> sign out the scope (clean re-auth-required state) and return null. Never loops.
///
/// A per-scope refresh lease serializes refreshes so concurrent callers on an expired token
/// trigger exactly one network refresh across replicas when the backing token store supports
/// distributed leases; later callers re-read the freshly stored token instead of refreshing again.
/// Raw token values are never logged.
/// </summary>
public sealed class GitHubTokenRefreshService : IGitHubAccessTokenProvider
{
    // Refresh when the access token is within this window of expiry (clock-skew safety margin).
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshLeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshLeasePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _baseUrl;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IDistributedGitHubTokenRefreshLeaseStore? _leaseStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubTokenRefreshService> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localGates = new(StringComparer.Ordinal);

    public GitHubTokenRefreshService(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubTokenRefreshService> logger)
    {
        _baseUrl = configuration["Auth:GitHub:BaseUrl"] ?? "https://github.com";
        _clientId = configuration["Auth:GitHub:ClientId"];
        _clientSecret = configuration["Auth:GitHub:ClientSecret"];
        _tokenStore = tokenStore;
        _leaseStore = tokenStore as IDistributedGitHubTokenRefreshLeaseStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var token = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
        if (token is null)
            return null; // signed-out or never-signed-in

        if (!NeedsRefresh(token))
            return token.AccessToken;

        return await RefreshWithLeaseAsync(scope, token, ct).ConfigureAwait(false);
    }

    private async Task<string?> RefreshWithLeaseAsync(
        GitHubTokenScope scope,
        GitHubToken observedToken,
        CancellationToken ct)
    {
        while (true)
        {
            await using var lease = _leaseStore is not null
                ? await _leaseStore.TryAcquireRefreshLeaseAsync(scope, _leaseOwner, RefreshLeaseTtl, ct).ConfigureAwait(false)
                : await AcquireLocalRefreshLeaseAsync(scope, ct).ConfigureAwait(false);

            if (lease is not null)
                return await RefreshAsLeaseOwnerAsync(scope, observedToken, ct).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + RefreshLeaseTtl;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RefreshLeasePollInterval, ct).ConfigureAwait(false);
                var token = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
                if (token is null)
                    return null;
                if (!SameRefreshMaterial(token, observedToken) || !NeedsRefresh(token))
                    return token.AccessToken;
            }

            observedToken = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false) ?? observedToken;
        }
    }

    private async Task<string?> RefreshAsLeaseOwnerAsync(
        GitHubTokenScope scope,
        GitHubToken observedToken,
        CancellationToken ct)
    {
        // Re-read after acquiring the lease: another replica may have already rotated the token.
        var token = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
        if (token is null)
            return null;
        if (!SameRefreshMaterial(token, observedToken) || !NeedsRefresh(token))
            return token.AccessToken;

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            // Expired and nothing to refresh with -> re-authentication required.
            _logger.LogWarning(
                "GitHub access token for scope {Scope} is expired and has no refresh token; sign-in required.",
                scope.Key);
            await _tokenStore.SignOutAsync(scope, ct).ConfigureAwait(false);
            return null;
        }

        var refreshed = await RequestRefreshAsync(token, ct).ConfigureAwait(false);
        if (refreshed.Token is null)
        {
            // For transient failures (network, 5xx, etc.), do NOT sign out — retry on next cycle.
            if (refreshed.FailureKind == RefreshFailureKind.Transient)
            {
                // Re-read: another replica may have already succeeded.
                var latest = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
                if (latest is not null && (!SameRefreshMaterial(latest, token) || !NeedsRefresh(latest)))
                    return latest.AccessToken;

                _logger.LogWarning(
                    "GitHub token refresh encountered a transient failure for scope {Scope}; will retry next cycle.",
                    scope.Key);
                return null; // keep signed-in state, no sign-out
            }

            // Permanent failure: the refresh token is definitively invalid. Sign out so the user sees
            // a clear "re-link required" state instead of silently failing.
            _logger.LogWarning(
                "GitHub token refresh failed permanently for scope {Scope}; token marked as signed-out. " +
                "User must re-link their GitHub account to restore Copilot access.",
                scope.Key);
            await _tokenStore.SignOutAsync(scope, ct).ConfigureAwait(false);
            return null;
        }

        await _tokenStore.SetAsync(scope, refreshed.Token, ct).ConfigureAwait(false);
        _logger.LogInformation("Refreshed GitHub access token for scope {Scope}.", scope.Key);
        return refreshed.Token.AccessToken;
    }

    private static bool SameRefreshMaterial(GitHubToken left, GitHubToken right) =>
        string.Equals(left.AccessToken, right.AccessToken, StringComparison.Ordinal)
        && string.Equals(left.RefreshToken, right.RefreshToken, StringComparison.Ordinal)
        && left.ExpiresAt == right.ExpiresAt;

    private async Task<IDistributedGitHubTokenRefreshLease> AcquireLocalRefreshLeaseAsync(
        GitHubTokenScope scope,
        CancellationToken ct)
    {
        var gate = _localGates.GetOrAdd(scope.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new LocalGitHubTokenRefreshLease(gate);
    }

    private static bool NeedsRefresh(GitHubToken token)
    {
        // Non-expiring (classic) tokens never need refresh.
        if (token.ExpiresAt is null)
            return false;
        return DateTimeOffset.UtcNow >= token.ExpiresAt.Value - ExpirySkew;
    }

    private enum RefreshFailureKind { Transient, Permanent }
    private sealed record RefreshResult(GitHubToken? Token, RefreshFailureKind? FailureKind)
    {
        public static RefreshResult Success(GitHubToken token) => new(token, null);
        public static RefreshResult Transient() => new(null, RefreshFailureKind.Transient);
        public static RefreshResult Permanent() => new(null, RefreshFailureKind.Permanent);
    }

    // Permanent GitHub error codes that mean the refresh token is definitively revoked/invalid.
    private static readonly HashSet<string> PermanentErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bad_verification_code",
        "expired_token",
        "token_not_yet_valid",
        "incorrect_client_credentials",
        "bad_oauth_token",
        "bad_refresh_token",
    };

    private async Task<RefreshResult> RequestRefreshAsync(GitHubToken current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
        {
            _logger.LogWarning("GitHub token refresh skipped: ClientId/ClientSecret not configured.");
            return RefreshResult.Transient();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId!,
                    ["client_secret"] = _clientSecret!,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = current.RefreshToken!
                })
            };
            request.Headers.Accept.ParseAdd("application/json");

            using var http = _httpClientFactory.CreateClient();
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            // 401/403 from GitHub means the app credentials or token are definitively rejected.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "GitHub refresh endpoint returned {StatusCode}; treating as permanent failure for scope.",
                    response.StatusCode);
                return RefreshResult.Permanent();
            }

            // 5xx and other non-success codes are transient — GitHub may be down.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub refresh endpoint returned {StatusCode}; treating as transient failure.",
                    response.StatusCode);
                return RefreshResult.Transient();
            }

            RefreshTokenResponse? body;
            try { body = await response.Content.ReadFromJsonAsync<RefreshTokenResponse>(ct).ConfigureAwait(false); }
            catch (Exception) { body = null; }

            if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            {
                _logger.LogWarning("GitHub refresh response body was empty or missing access_token; treating as transient.");
                return RefreshResult.Transient();
            }

            if (!string.IsNullOrWhiteSpace(body.Error))
            {
                var isPermanent = PermanentErrorCodes.Contains(body.Error);
                _logger.LogWarning(
                    "GitHub refresh token rejected (error={Error}); treating as {Kind} failure.",
                    body.Error, isPermanent ? "permanent" : "transient");
                return isPermanent ? RefreshResult.Permanent() : RefreshResult.Transient();
            }

            var expiresAt = body.ExpiresIn is > 0
                ? DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn.Value)
                : (DateTimeOffset?)null;

            // GitHub rotates the refresh token; fall back to the old one if a new one is not returned.
            var refreshToken = string.IsNullOrWhiteSpace(body.RefreshToken)
                ? current.RefreshToken
                : body.RefreshToken;

            return RefreshResult.Success(new GitHubToken(
                body.AccessToken!,
                refreshToken,
                expiresAt,
                current.Login,
                current.AvatarUrl,
                current.Scopes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub token refresh request threw an exception (transient).");
            return RefreshResult.Transient();
        }
    }

    private sealed class RefreshTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? ExpiresIn { get; set; }

        [JsonPropertyName("refresh_token_expires_in")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? RefreshTokenExpiresIn { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class LocalGitHubTokenRefreshLease : IDistributedGitHubTokenRefreshLease
    {
        private readonly SemaphoreSlim _gate;

        public LocalGitHubTokenRefreshLease(SemaphoreSlim gate) => _gate = gate;

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
