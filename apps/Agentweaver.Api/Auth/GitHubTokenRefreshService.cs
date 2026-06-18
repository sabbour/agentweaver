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
/// A per-scope <see cref="SemaphoreSlim"/> serializes refreshes so concurrent callers on an
/// expired token trigger exactly one network refresh; later callers re-read the freshly stored
/// token instead of refreshing again. Raw token values are never logged.
/// </summary>
public sealed class GitHubTokenRefreshService : IGitHubAccessTokenProvider
{
    // Refresh when the access token is within this window of expiry (clock-skew safety margin).
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly string _baseUrl;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubTokenRefreshService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

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

        // Refresh required — serialize per scope so parallel callers don't double-refresh.
        var gate = _gates.GetOrAdd(scope.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read inside the lock: another caller may have already rotated the token.
            token = await _tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
            if (token is null)
                return null;
            if (!NeedsRefresh(token))
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
            if (refreshed is null)
            {
                _logger.LogWarning(
                    "GitHub token refresh failed for scope {Scope}; sign-in required.", scope.Key);
                await _tokenStore.SignOutAsync(scope, ct).ConfigureAwait(false);
                return null;
            }

            await _tokenStore.SetAsync(scope, refreshed, ct).ConfigureAwait(false);
            _logger.LogInformation("Refreshed GitHub access token for scope {Scope}.", scope.Key);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsRefresh(GitHubToken token)
    {
        // Non-expiring (classic) tokens never need refresh.
        if (token.ExpiresAt is null)
            return false;
        return DateTimeOffset.UtcNow >= token.ExpiresAt.Value - ExpirySkew;
    }

    /// <summary>
    /// Calls GitHub's refresh endpoint and returns the rotated token, or null on any failure.
    /// Identity (login/avatar/scopes) is carried over from the current token.
    /// </summary>
    private async Task<GitHubToken?> RequestRefreshAsync(GitHubToken current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
        {
            _logger.LogWarning("GitHub token refresh skipped: ClientId/ClientSecret not configured.");
            return null;
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
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadFromJsonAsync<RefreshTokenResponse>(ct).ConfigureAwait(false);
            if (body is null
                || !string.IsNullOrWhiteSpace(body.Error)
                || string.IsNullOrWhiteSpace(body.AccessToken))
            {
                return null;
            }

            var expiresAt = body.ExpiresIn is > 0
                ? DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn.Value)
                : (DateTimeOffset?)null;

            // GitHub rotates the refresh token; fall back to the old one if a new one is not returned.
            var refreshToken = string.IsNullOrWhiteSpace(body.RefreshToken)
                ? current.RefreshToken
                : body.RefreshToken;

            return new GitHubToken(
                body.AccessToken!,
                refreshToken,
                expiresAt,
                current.Login,
                current.AvatarUrl,
                current.Scopes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub token refresh request threw an exception.");
            return null;
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
}
