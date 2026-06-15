using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// Handles GitHub OAuth 2.0 authorization code flow for web sign-in.
/// Generates authorization URLs with CSRF state tokens, exchanges codes for tokens,
/// and stores them via IGitHubTokenStore.
/// </summary>
public sealed class GitHubOAuthRedirectService
{
    private const string DefaultScopes = "repo read:user";

    private readonly string _baseUrl;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string? _callbackUrl;
    private readonly string _scopes;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubOAuthRedirectService> _logger;

    // In-memory CSRF state store: state token -> expiry
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingStates = new();

    public GitHubOAuthRedirectService(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubOAuthRedirectService> logger)
    {
        _baseUrl = configuration["Auth:GitHub:BaseUrl"] ?? "https://github.com";
        _clientId = configuration["Auth:GitHub:ClientId"];
        _clientSecret = configuration["Auth:GitHub:ClientSecret"];
        _callbackUrl = configuration["Auth:GitHub:CallbackUrl"];
        _scopes = configuration["Auth:GitHub:Scopes"] ?? DefaultScopes;
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
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

    /// <summary>Returns a GitHub OAuth authorization URL with a fresh CSRF state token.</summary>
    public string BeginAuthorization()
    {
        var clientId = RequireClientId();
        var callbackUrl = RequireCallbackUrl();

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        _pendingStates[state] = DateTimeOffset.UtcNow.AddMinutes(10);

        return $"{_baseUrl}/login/oauth/authorize" +
               $"?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(callbackUrl)}" +
               $"&scope={Uri.EscapeDataString(_scopes)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    /// <summary>Exchanges an authorization code for a token. Returns login on success.</summary>
    public async Task<string> ExchangeCodeAsync(string code, string state, CancellationToken ct = default)
    {
        // Validate CSRF state
        if (!_pendingStates.TryRemove(state, out var expiry) || DateTimeOffset.UtcNow > expiry)
            throw new InvalidOperationException("Invalid or expired OAuth state.");

        // Purge stale states
        foreach (var key in _pendingStates.Keys.ToArray())
            if (DateTimeOffset.UtcNow > _pendingStates.GetValueOrDefault(key))
                _pendingStates.TryRemove(key, out _);

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
            string.IsNullOrWhiteSpace(body.RefreshTokenExpiresIn) ? null
                : DateTimeOffset.UtcNow.AddSeconds(double.Parse(body.RefreshTokenExpiresIn)),
            login,
            avatarUrl,
            (_scopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        await _tokenStore.SetAsync(GitHubTokenScope.Installation, token, ct).ConfigureAwait(false);

        _logger.LogInformation("GitHub OAuth redirect flow completed for login {Login}", login);
        return login;
    }

    private async Task<(string Login, string? AvatarUrl)> FetchUserAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Scaffolder/1.0");
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
        [JsonPropertyName("refresh_token_expires_in")] public string? RefreshTokenExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class GitHubUserResponse
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    }
}
