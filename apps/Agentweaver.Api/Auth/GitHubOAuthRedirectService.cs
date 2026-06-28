using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Handles GitHub OAuth 2.0 authorization code flow for web sign-in.
/// Generates authorization URLs with CSRF state tokens, exchanges codes for tokens,
/// and stores them via IGitHubTokenStore.
/// </summary>
public sealed class GitHubOAuthRedirectService
{
    private const string DefaultScopes = "repo read:user read:org";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly string _baseUrl;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string? _callbackUrl;
    private readonly string _scopes;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubOAuthRedirectService> _logger;

    public GitHubOAuthRedirectService(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<GitHubOAuthRedirectService> logger)
    {
        _baseUrl = configuration["Auth:GitHub:BaseUrl"] ?? "https://github.com";
        _clientId = configuration["Auth:GitHub:ClientId"];
        _clientSecret = configuration["Auth:GitHub:ClientSecret"];
        _callbackUrl = configuration["Auth:GitHub:CallbackUrl"];
        _scopes = configuration["Auth:GitHub:Scopes"] ?? DefaultScopes;
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private string RequireClientId() =>
        !string.IsNullOrWhiteSpace(_clientId) ? _clientId
        : throw new GitHubNotConfiguredException("Auth:GitHub:ClientId must be configured.");

    private string RequireClientSecret() =>
        !string.IsNullOrWhiteSpace(_clientSecret) ? _clientSecret
        : throw new GitHubNotConfiguredException("Auth:GitHub:ClientSecret must be configured.");

    private string RequireCallbackUrl() =>
        !string.IsNullOrWhiteSpace(_callbackUrl) ? _callbackUrl
        : throw new GitHubNotConfiguredException("Auth:GitHub:CallbackUrl must be configured.");

    /// <summary>
    /// Returns a GitHub OAuth authorization URL with a fresh CSRF state token. The state is persisted
    /// to <see cref="MemoryDbContext"/> (Postgres in prod, SQLite in dev) so the callback can validate
    /// it on ANY replica, not just the pod that served this request.
    /// </summary>
    public async Task<string> BeginAuthorizationAsync(CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        var callbackUrl = RequireCallbackUrl();

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.OAuthStates.Add(new OAuthState
            {
                State = state,
                ExpiresAt = DateTimeOffset.UtcNow.Add(StateLifetime),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return $"{_baseUrl}/login/oauth/authorize" +
               $"?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(callbackUrl)}" +
               $"&scope={Uri.EscapeDataString(_scopes)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    /// <summary>Exchanges an authorization code for a token. Returns (login, accessToken) on success.</summary>
    public async Task<(string Login, string AccessToken)> ExchangeCodeAsync(string code, string state, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            // Atomic single-use CSRF claim across replicas. Read the row for its expiry snapshot, then
            // conditionally delete by State only: exactly one caller's delete affects the row, so a
            // replay (or a state armed on another pod that was already consumed) sees zero rows
            // affected → reject. Expiry is enforced on the snapshot rather than in the DELETE predicate
            // because the DateTimeOffset comparison is not translatable on SQLite (it is on Postgres);
            // this mirrors the OAuth broker's claim. Guarantees at-most-once redemption across replicas.
            var existing = await db.OAuthStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.State == state, ct)
                .ConfigureAwait(false);

            var claimed = existing is not null
                && await db.OAuthStates
                    .Where(s => s.State == state)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

            if (existing is null || !claimed || now > existing.ExpiresAt)
                throw new InvalidOperationException("Invalid or expired OAuth state.");

            // Best-effort purge of expired states; never let cleanup break the sign-in flow. The
            // DateTimeOffset comparison is only translatable on Postgres (prod, where growth matters),
            // so it is skipped on SQLite/dev — read-time expiry above remains authoritative.
            if (db.Database.IsNpgsql())
            {
                try
                {
                    await db.OAuthStates.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Opportunistic purge of expired OAuth states failed; continuing.");
                }
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = RequireClientId(),
                ["client_secret"] = RequireClientSecret(),
                ["code"] = code,
                ["redirect_uri"] = RequireCallbackUrl()
            })
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var http1 = _httpClientFactory.CreateClient();
        var response = await http1.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            throw new InvalidOperationException("GitHub did not return an access token.");

        if (!string.IsNullOrWhiteSpace(body.Error))
            throw new InvalidOperationException($"GitHub OAuth error: {body.Error}");

        var (login, avatarUrl) = await FetchUserAsync(body.AccessToken!, ct).ConfigureAwait(false);

        var token = new GitHubToken(
            body.AccessToken!,
            body.RefreshToken,
            body.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn.Value) : null,
            login,
            avatarUrl,
            (_scopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        await _tokenStore.SetAsync(GitHubTokenScope.Installation, token, ct).ConfigureAwait(false);

        _logger.LogInformation("GitHub OAuth redirect flow completed for login {Login}", login);
        return (login, body.AccessToken!);
    }

    private async Task<(string Login, string? AvatarUrl)> FetchUserAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        using var http2 = _httpClientFactory.CreateClient();
        var response = await http2.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return ("unknown", null);
        var body = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(ct).ConfigureAwait(false);
        return (body?.Login ?? "unknown", body?.AvatarUrl);
    }

    private sealed class AccessTokenResponse
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

    private sealed class GitHubUserResponse
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    }
}
