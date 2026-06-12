using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// Real GitHub device-flow OAuth service. Initiates device authorization at the configured
/// GitHub base URL, holds the device_code server-side (never returned to clients), polls
/// for completion, and persists the resulting token via IGitHubTokenStore.
/// Minimal scopes: repo + read:user (section 3.8 of the plan).
/// </summary>
public sealed class GitHubDeviceFlowAuthService : IGitHubAuthService
{
    private const string DefaultScopes = "repo read:user";

    private readonly string _baseUrl;
    private readonly string? _clientId;
    private readonly string _scopes;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly HttpClient _http;
    private readonly ILogger<GitHubDeviceFlowAuthService> _logger;

    // Server-side device code storage, keyed by scope key
    private readonly ConcurrentDictionary<string, InFlightFlow> _inFlightFlows = new();

    public GitHubDeviceFlowAuthService(
        IConfiguration configuration,
        IGitHubTokenStore tokenStore,
        HttpClient http,
        ILogger<GitHubDeviceFlowAuthService> logger)
    {
        _baseUrl = configuration["Auth:GitHub:BaseUrl"] ?? "https://github.com";
        _clientId = configuration["Auth:GitHub:ClientId"];
        _scopes = configuration["Auth:GitHub:Scopes"] ?? DefaultScopes;
        _tokenStore = tokenStore;
        _http = http;
        _logger = logger;
    }

    private string RequireClientId() =>
        !string.IsNullOrWhiteSpace(_clientId)
            ? _clientId
            : throw new GitHubNotConfiguredException("Auth:GitHub:ClientId must be configured to use GitHub sign-in.");

    public async Task<GitHubDeviceFlowStart> StartDeviceFlowAsync(
        GitHubTokenScope scope, CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = _scopes
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty device code response from GitHub.");

        if (string.IsNullOrWhiteSpace(body.DeviceCode))
            throw new InvalidOperationException("GitHub did not return a device_code.");

        // Store the device_code server-side; return only what the user needs
        _inFlightFlows[scope.Key] = new InFlightFlow(
            body.DeviceCode!,
            body.Interval > 0 ? body.Interval : 5,
            DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 900));

        return new GitHubDeviceFlowStart(
            body.UserCode ?? string.Empty,
            body.VerificationUri ?? $"{_baseUrl}/login/device",
            body.ExpiresIn,
            body.Interval > 0 ? body.Interval : 5);
    }

    public async Task<GitHubDeviceFlowPollResponse> PollDeviceFlowAsync(
        GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (!_inFlightFlows.TryGetValue(scope.Key, out var flow))
            return new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Expired, null);

        if (DateTimeOffset.UtcNow > flow.ExpiresAt)
        {
            _inFlightFlows.TryRemove(scope.Key, out _);
            return new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Expired, null);
        }

        using var pollRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = RequireClientId(),
                ["device_code"] = flow.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            })
        };
        pollRequest.Headers.Accept.ParseAdd("application/json");
        var response = await _http.SendAsync(pollRequest, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(ct)
            .ConfigureAwait(false);
        if (body is null) return new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Pending, null);

        if (!string.IsNullOrWhiteSpace(body.Error))
        {
            return body.Error switch
            {
                "authorization_pending" or "slow_down" =>
                    new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Pending, null),
                "access_denied" => new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Denied, null),
                _ => new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Expired, null)
            };
        }

        if (string.IsNullOrWhiteSpace(body.AccessToken))
            return new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Pending, null);

        // Fetch identity to store the login
        var login = await FetchLoginAsync(body.AccessToken!, ct).ConfigureAwait(false);

        var token = new GitHubToken(
            body.AccessToken!,
            body.RefreshToken,
            string.IsNullOrWhiteSpace(body.RefreshTokenExpiresIn) ? null
                : DateTimeOffset.UtcNow.AddSeconds(double.Parse(body.RefreshTokenExpiresIn)),
            login,
            (_scopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        await _tokenStore.SetAsync(scope, token, ct).ConfigureAwait(false);
        _inFlightFlows.TryRemove(scope.Key, out _);

        _logger.LogInformation("GitHub device flow completed for scope {Scope}, login {Login}",
            scope.Key, login);

        return new GitHubDeviceFlowPollResponse(GitHubDeviceFlowPollResult.Success, login);
    }

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        _tokenStore.SignOutAsync(scope, ct);

    private async Task<string> FetchLoginAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Scaffolder/1.0");
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "unknown";
        var body = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(ct).ConfigureAwait(false);
        return body?.Login ?? "unknown";
    }

    private sealed record InFlightFlow(string DeviceCode, int Interval, DateTimeOffset ExpiresAt);

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
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
    }
}

/// <summary>Thrown when GitHub OAuth is not configured (missing ClientId).</summary>
public sealed class GitHubNotConfiguredException : InvalidOperationException
{
    public GitHubNotConfiguredException(string message) : base(message) { }
}
