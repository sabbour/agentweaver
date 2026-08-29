using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Handles the Microsoft identity platform v2.0 authorization-code + PKCE flow for interactive
/// browser sign-in. It generates the
/// Microsoft <c>/oauth2/v2.0/authorize</c> URL (with a CSRF <c>state</c> and PKCE
/// <c>code_challenge</c>), persists the PKCE <c>code_verifier</c> server-side bound to the state
/// (<see cref="EntraOAuthState"/>) so the callback can redeem the code on ANY replica, then — at the
/// callback — redeems the code + verifier at Microsoft's <c>/oauth2/v2.0/token</c> endpoint for an
/// access token. The access token (audience = this app's <c>ClientId</c>) is validated and its
/// identity claims are extracted by reusing <see cref="EntraAccessTokenValidator"/>, the exact same
/// validator every API request already runs, so the browser-sign-in token and the request-time token
/// are validated identically. When a client secret is configured the flow redeems as a confidential
/// client; otherwise it falls back to PKCE-only redemption without any client credential.
/// </summary>
public sealed class EntraOAuthRedirectService
{
    private const string DefaultScopes = "openid profile email";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly IConfiguration _configuration;
    private readonly EntraAccessTokenValidator _tokenValidator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EntraOAuthRedirectService> _logger;

    private enum ClientAuthenticationMode
    {
        None,
        Secret,
    }

    public EntraOAuthRedirectService(
        IConfiguration configuration,
        EntraAccessTokenValidator tokenValidator,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<EntraOAuthRedirectService> logger)
    {
        _configuration = configuration;
        _tokenValidator = tokenValidator;
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private string? ClientId => _configuration["Auth:Entra:ClientId"];
    private string? ClientSecret => _configuration["Auth:Entra:ClientSecret"];

    /// <summary>The redirect URI registered on the Entra app.</summary>
    private string? RedirectUri =>
        _configuration["Auth:Entra:RedirectUri"];

    /// <summary>
    /// Scopes requested at /authorize. Defaults to the OIDC scopes plus this app's own resource via
    /// <c>{ClientId}/.default</c>, so the issued access token's audience is this app's ClientId (what
    /// <see cref="EntraAccessTokenValidator"/> validates) and carries the platform App Roles claim.
    /// </summary>
    private string Scopes
    {
        get
        {
            var configured = _configuration["Auth:Entra:Scopes"];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            var clientId = ClientId;
            return string.IsNullOrWhiteSpace(clientId)
                ? DefaultScopes
                : $"{DefaultScopes} {clientId}/.default";
        }
    }

    /// <summary>
    /// True when everything required for the Entra browser sign-in redirect flow is present:
    /// ClientId, a resolvable Authority (TenantId or explicit Authority), and a redirect URI.
    /// ClientSecret is optional because PKCE-only redemption is supported when the Entra app allows
    /// public client flows.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(_tokenValidator.Authority)
        && !string.IsNullOrWhiteSpace(RedirectUri);

    private string RequireClientId() =>
        !string.IsNullOrWhiteSpace(ClientId) ? ClientId!
        : throw new EntraNotConfiguredException("Auth:Entra:ClientId must be configured.");

    private ClientAuthenticationMode TokenClientAuthenticationMode =>
        string.IsNullOrWhiteSpace(ClientSecret)
            ? ClientAuthenticationMode.None
            : ClientAuthenticationMode.Secret;

    private string RequireRedirectUri() =>
        !string.IsNullOrWhiteSpace(RedirectUri) ? RedirectUri!
        : throw new EntraNotConfiguredException("Auth:Entra:RedirectUri must be configured.");

    private string RequireAuthorityBase()
    {
        var authority = _tokenValidator.Authority
            ?? throw new EntraNotConfiguredException("Auth:Entra:TenantId or Auth:Entra:Authority must be configured.");

        // Microsoft's v2.0 authorize/token endpoints hang off the tenant base, not the /v2.0 issuer
        // suffix that the discovery Authority carries. Strip a trailing /v2.0 so we can append the
        // /oauth2/v2.0/{authorize,token} paths deterministically (no network round trip at /authorize).
        var trimmed = authority.TrimEnd('/');
        if (trimmed.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/v2.0".Length];
        return trimmed;
    }

    private string AuthorizeEndpoint => $"{RequireAuthorityBase()}/oauth2/v2.0/authorize";
    private string TokenEndpoint => $"{RequireAuthorityBase()}/oauth2/v2.0/token";

    /// <summary>
    /// Begins a sign-in: mints a fresh CSRF <c>state</c> + PKCE pair, persists the code_verifier
    /// (bound to the state) to <see cref="MemoryDbContext"/> so the callback can complete on any
    /// replica, and returns the Microsoft authorization URL the browser should be redirected to.
    /// </summary>
    public async Task<string> BeginAuthorizationAsync(CancellationToken ct = default)
    {
        ValidateAuthorizationConfiguration();

        var state = GenerateUrlSafeToken();
        var codeVerifier = GenerateUrlSafeToken();
        var codeChallenge = ComputeS256Challenge(codeVerifier);

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.EntraOAuthStates.Add(new EntraOAuthState
            {
                State = state,
                CodeVerifier = codeVerifier,
                ExpiresAt = DateTimeOffset.UtcNow.Add(StateLifetime),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return CreateAuthorizationUrl(state, codeChallenge);
    }

    private void ValidateAuthorizationConfiguration()
    {
        _ = RequireAuthorityBase();
        _ = RequireClientId();
        _ = RequireRedirectUri();
    }

    public string CreateAuthorizationUrl(string state, string codeChallenge) =>
        $"{AuthorizeEndpoint}" +
        $"?client_id={Uri.EscapeDataString(RequireClientId())}" +
        $"&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(RequireRedirectUri())}" +
        $"&response_mode=query" +
        $"&scope={Uri.EscapeDataString(Scopes)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        $"&code_challenge_method=S256";

    /// <summary>
    /// Completes a sign-in: atomically claims the pending <c>state</c> (single-use across replicas),
    /// redeems the authorization <paramref name="code"/> + stored PKCE verifier at Microsoft's token
    /// endpoint, then validates the resulting access token with <see cref="EntraAccessTokenValidator"/>
    /// and returns its identity claims (oid/tid/roles/display name) alongside the raw access token.
    /// </summary>
    public async Task<(EntraAccessTokenClaims Claims, string AccessToken)> ExchangeCodeAsync(
        string code, string state, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string codeVerifier;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            // Atomic single-use CSRF/PKCE claim across replicas: read the row for its verifier + expiry
            // snapshot, then conditionally delete by State only. Exactly one caller's delete affects the
            // row, so a replay (or a state armed on another pod that was already consumed) sees zero rows
            // affected → reject. Expiry is enforced on the snapshot rather than in the DELETE predicate
            // because the DateTimeOffset comparison is not translatable on SQLite (it is on Postgres);
            // this mirrors the GitHub OAuthState claim. Guarantees at-most-once redemption across replicas.
            var existing = await db.EntraOAuthStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.State == state, ct)
                .ConfigureAwait(false);

            var claimed = existing is not null
                && await db.EntraOAuthStates
                    .Where(s => s.State == state)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

            if (existing is null || !claimed || now > existing.ExpiresAt)
                throw new InvalidOperationException("Invalid or expired Entra OAuth state.");

            codeVerifier = existing.CodeVerifier;

            // Best-effort purge of expired states; never let cleanup break sign-in. Only translatable
            // on Postgres (prod, where growth matters); skipped on SQLite/dev.
            if (db.Database.IsNpgsql())
            {
                try
                {
                    await db.EntraOAuthStates.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Opportunistic purge of expired Entra OAuth states failed; continuing.");
                }
            }
        }

        var accessToken = await RedeemCodeForAccessTokenAsync(code, codeVerifier, ct).ConfigureAwait(false);

        // Validate the access token exactly as every API request does (issuer, audience=ClientId,
        // signature, tenant, lifetime) and extract oid/tid/roles/display name. Fail closed on any
        // validation failure rather than trusting the token endpoint response blindly.
        var claims = await _tokenValidator.ValidateAsync(accessToken, ct).ConfigureAwait(false);
        if (claims is null)
            throw new InvalidOperationException("Microsoft Entra returned a token that failed validation.");

        _logger.LogInformation("Entra OAuth redirect flow completed for object id {ObjectId}.", claims.ObjectId);
        return (claims, accessToken);
    }

    private async Task<string> RedeemCodeForAccessTokenAsync(string code, string codeVerifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = RequireClientId(),
            ["code"] = code,
            ["redirect_uri"] = RequireRedirectUri(),
            ["code_verifier"] = codeVerifier,
            ["scope"] = Scopes,
        };

        if (TokenClientAuthenticationMode == ClientAuthenticationMode.Secret)
            form["client_secret"] = ClientSecret!;

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var http = _httpClientFactory.CreateClient("entra-oidc");
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
        if (body is not null && !string.IsNullOrWhiteSpace(body.Error))
            throw new InvalidOperationException($"Microsoft Entra token error: {body.Error} {body.ErrorDescription}".Trim());

        response.EnsureSuccessStatusCode();

        if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            throw new InvalidOperationException("Microsoft Entra did not return an access token.");

        return body.AccessToken!;
    }

    /// <summary>Generates a 256-bit random, URL-safe token (used for both CSRF state and PKCE verifier).</summary>
    private static string GenerateUrlSafeToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE S256: code_challenge = BASE64URL(SHA256(ASCII(code_verifier))).</summary>
    private static string ComputeS256Challenge(string codeVerifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? ExpiresIn { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}

/// <summary>Thrown when the Entra sign-in redirect flow is invoked without the required configuration.</summary>
public sealed class EntraNotConfiguredException : InvalidOperationException
{
    public EntraNotConfiguredException(string message) : base(message) { }
}
