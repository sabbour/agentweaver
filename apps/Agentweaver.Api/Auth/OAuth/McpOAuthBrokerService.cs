using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// Helpers for resolving the Authorization Server issuer/audience and validating redirect URIs.
/// The issuer is bound to the real request host so the published RFC 8414 metadata and minted
/// token claims always match the host the client actually reached (e.g. the staging AKS host),
/// unless an explicit <c>Auth:OAuth:Issuer</c> override is configured.
/// </summary>
public static class OAuthServerConfig
{
    public static string ResolveIssuer(HttpContext context, IConfiguration configuration)
    {
        var configured = configuration["Auth:OAuth:Issuer"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var request = context.Request;
        return $"{request.Scheme}://{request.Host.Value}";
    }

    /// <summary>The MCP resource the access token is bound to (RFC 8707). Defaults to {issuer}/mcp.</summary>
    public static string ResolveAudience(string issuer, IConfiguration configuration)
    {
        var configured = configuration["Auth:OAuth:Audience"];
        return !string.IsNullOrWhiteSpace(configured) ? configured : $"{issuer}/mcp";
    }

    /// <summary>
    /// Exact-match redirect-URI policy: only loopback HTTP (RFC 8252) or HTTPS URIs are permitted;
    /// fragments are rejected. Without Dynamic Client Registration (T5) there is no per-client
    /// registered set yet, so HTTPS URIs are accepted structurally here and the EXACT string is
    /// re-checked at token redemption against the value captured at /authorize.
    /// TODO(T5): validate against the per-client registered redirect URIs from /oauth/register.
    /// </summary>
    public static bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri)) return false;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var host = uri.Host;
            return host is "127.0.0.1" or "::1"
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>Outcome of handling the brokered GitHub callback.</summary>
public enum BrokerOutcome { Success, Denied, UnknownState, ExchangeFailed }

/// <summary>Result of the brokered GitHub callback; carries the client redirect target.</summary>
public sealed record BrokerCallbackResult(
    BrokerOutcome Outcome,
    string? RedirectUri,
    string? ClientState,
    string? Code,
    string? Error,
    string? ErrorDescription);

/// <summary>A redeemed authorization code, ready for the token endpoint to mint an access token.</summary>
public sealed record AuthorizationCodeGrant(string Subject, string GithubLogin, string Scope);

/// <summary>Failure reason when an authorization code cannot be redeemed.</summary>
public sealed record CodeRedemptionError(string Error, string ErrorDescription);

/// <summary>
/// In-memory broker for the Agentweaver OAuth 2.1 Authorization Server.
///
/// It bridges a public MCP client's PKCE authorization-code request to a GitHub login, reusing the
/// existing <see cref="GitHubOAuthRedirectService"/> (GitHub authorize-URL build, CSRF state, and
/// code-to-token exchange) and <see cref="IGitHubOrgAuthorizationService"/> (microsoft org check).
/// The GitHub client_secret and the user's GitHub token never leave the server; the client only ever
/// receives Agentweaver-minted artifacts (authorization code, then JWT access token).
///
/// State held in memory (single-instance only):
///  - pending authorizations keyed by the GitHub CSRF state, correlating the client's PKCE request
///    to the brokered GitHub login leg;
///  - issued authorization codes (single-use, short TTL) bound to client_id + redirect_uri + PKCE.
///
/// TODO(T4): persist rotating refresh tokens (hashed) + reuse detection + a jti denylist.
/// TODO(T5): replace the implicit "any client_id" acceptance with Dynamic Client Registration.
/// </summary>
public sealed class McpOAuthBrokerService
{
    // Authorization codes are single-use and must be redeemed quickly (RFC 6749 recommends <= 60s).
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingAuthorizationLifetime = TimeSpan.FromMinutes(10);

    private readonly GitHubOAuthRedirectService _gitHub;
    private readonly IGitHubOrgAuthorizationService _orgAuth;
    private readonly ILogger<McpOAuthBrokerService> _logger;

    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new();
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new();

    public McpOAuthBrokerService(
        GitHubOAuthRedirectService gitHub,
        IGitHubOrgAuthorizationService orgAuth,
        ILogger<McpOAuthBrokerService> logger)
    {
        _gitHub = gitHub;
        _orgAuth = orgAuth;
        _logger = logger;
    }

    /// <summary>
    /// Begins an authorization: builds the GitHub authorize URL (with its CSRF state) and records the
    /// client's PKCE request keyed by that state, so the brokered GitHub callback can correlate back.
    /// Returns the GitHub authorize URL the user agent should be redirected to.
    /// </summary>
    public string BeginAuthorization(
        string clientId, string redirectUri, string codeChallenge, string? clientState, string scope, string? resource)
    {
        PurgeExpired();

        var gitHubAuthorizeUrl = _gitHub.BeginAuthorization();
        var state = ExtractState(gitHubAuthorizeUrl);

        _pending[state] = new PendingAuthorization(
            clientId, redirectUri, codeChallenge, clientState, scope, resource,
            DateTimeOffset.UtcNow.Add(PendingAuthorizationLifetime));

        return gitHubAuthorizeUrl;
    }

    /// <summary>True when the GitHub CSRF state belongs to a pending MCP authorization (broker leg).</summary>
    public bool IsPendingState(string? state) =>
        !string.IsNullOrEmpty(state) && _pending.ContainsKey(state);

    /// <summary>
    /// Handles the brokered GitHub callback: exchanges the GitHub code (via the reused service),
    /// enforces microsoft org membership, then — on success — issues a single-use authorization code
    /// and returns the client's loopback/registered redirect target.
    /// </summary>
    public async Task<BrokerCallbackResult> HandleGitHubCallbackAsync(string code, string state, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending) || pending.IsExpired)
            return new BrokerCallbackResult(BrokerOutcome.UnknownState, null, null, null,
                "invalid_request", "Unknown or expired authorization state.");

        string login;
        string gitHubAccessToken;
        try
        {
            (login, gitHubAccessToken) = await _gitHub.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Brokered GitHub code exchange failed for client {ClientId}.", pending.ClientId);
            return new BrokerCallbackResult(BrokerOutcome.ExchangeFailed, pending.RedirectUri, pending.ClientState, null,
                "access_denied", "GitHub authorization failed.");
        }

        // Enforce org membership at issuance. Fail closed when org auth is configured.
        if (_orgAuth.IsConfigured)
        {
            var membership = await _orgAuth.CheckMembershipAsync(gitHubAccessToken, login, ct).ConfigureAwait(false);
            if (membership != OrgAuthResult.Allowed)
            {
                _logger.LogWarning(
                    "Denied MCP token issuance for GitHub login {Login}: org membership result {Result}.",
                    login, membership);
                return new BrokerCallbackResult(BrokerOutcome.Denied, pending.RedirectUri, pending.ClientState, null,
                    "access_denied", "Not a member of the required GitHub organization.");
            }
        }

        var authorizationCode = GenerateOpaqueToken();
        _codes[authorizationCode] = new IssuedCode(
            login, login, pending.CodeChallenge, pending.RedirectUri, pending.ClientId, pending.Scope,
            DateTimeOffset.UtcNow.Add(AuthorizationCodeLifetime));

        _logger.LogInformation("Issued MCP authorization code for GitHub login {Login}.", login);
        return new BrokerCallbackResult(BrokerOutcome.Success, pending.RedirectUri, pending.ClientState,
            authorizationCode, null, null);
    }

    /// <summary>
    /// Redeems an authorization code at the token endpoint: enforces single-use, expiry, exact
    /// redirect-URI match, client binding, and PKCE S256 verification. Returns the grant on success.
    /// </summary>
    public (AuthorizationCodeGrant? Grant, CodeRedemptionError? Error) RedeemAuthorizationCode(
        string? code, string? codeVerifier, string? redirectUri, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(code) || !_codes.TryRemove(code, out var issued))
            return (null, new CodeRedemptionError("invalid_grant", "Authorization code is invalid or already used."));

        if (issued.IsExpired)
            return (null, new CodeRedemptionError("invalid_grant", "Authorization code has expired."));

        if (!string.Equals(issued.RedirectUri, redirectUri, StringComparison.Ordinal))
            return (null, new CodeRedemptionError("invalid_grant", "redirect_uri does not match the authorization request."));

        if (!string.Equals(issued.ClientId, clientId, StringComparison.Ordinal))
            return (null, new CodeRedemptionError("invalid_grant", "client_id does not match the authorization request."));

        if (string.IsNullOrWhiteSpace(codeVerifier) || !VerifyPkce(issued.CodeChallenge, codeVerifier))
            return (null, new CodeRedemptionError("invalid_grant", "PKCE verification failed."));

        return (new AuthorizationCodeGrant(issued.Subject, issued.GithubLogin, issued.Scope), null);
    }

    /// <summary>Generates a 256-bit random, URL-safe opaque token (authorization codes / refresh placeholder).</summary>
    public static string GenerateOpaqueToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static bool VerifyPkce(string codeChallenge, string codeVerifier)
    {
        // S256 only: code_challenge == BASE64URL(SHA256(ASCII(code_verifier))).
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Base64UrlEncoder.Encode(hash);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(codeChallenge));
    }

    private static string ExtractState(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?');
        var pair = query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _pending)
            if (now > kvp.Value.ExpiresAt)
                _pending.TryRemove(kvp.Key, out _);
        foreach (var kvp in _codes)
            if (now > kvp.Value.ExpiresAt)
                _codes.TryRemove(kvp.Key, out _);
    }

    private sealed record PendingAuthorization(
        string ClientId, string RedirectUri, string CodeChallenge, string? ClientState,
        string Scope, string? Resource, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }

    private sealed record IssuedCode(
        string Subject, string GithubLogin, string CodeChallenge, string RedirectUri,
        string ClientId, string Scope, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}
