using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
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
    /// Redirect-URI policy. Permitted targets are:
    ///  - loopback HTTP (RFC 8252): 127.0.0.1 / ::1 / localhost on any port (native clients), and
    ///  - HTTPS URIs whose absolute string starts with one of the configured
    ///    <c>Auth:OAuth:AllowedRedirectUriPrefixes</c> entries.
    /// Fragments and <c>userinfo</c> (user@host) are always rejected.
    ///
    /// F2 (interim, pre-DCR): without Dynamic Client Registration (T5) there is no per-client
    /// registered set, so the static prefix allowlist is the authoritative gate for non-loopback
    /// redirects. When T5 lands, registered redirect URIs become authoritative and this allowlist
    /// remains as defense-in-depth. If no prefixes are configured, ONLY loopback redirects are allowed.
    /// The exact string is additionally re-checked at token redemption against the value captured at
    /// /authorize.
    /// </summary>
    public static bool IsAllowedRedirectUri(string? redirectUri, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(redirectUri)) return false;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;
        // Reject embedded credentials (https://user@evil.com) — these can mislead URL parsers/users.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        if (uri.Scheme == Uri.UriSchemeHttp)
            return IsLoopbackHost(uri.Host);

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            var allowedPrefixes = configuration
                .GetSection("Auth:OAuth:AllowedRedirectUriPrefixes")
                .Get<string[]>() ?? [];

            foreach (var prefix in allowedPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(prefix)
                    && redirectUri.StartsWith(prefix.Trim(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        return false;
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "::1"
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches a requested <paramref name="redirectUri"/> against a client's DCR-registered set.
    /// Non-loopback URIs require an exact ordinal match. For loopback URIs (127.0.0.1 / ::1 /
    /// localhost) the port is IGNORED per RFC 8252 §7.3: native clients bind a fresh ephemeral
    /// loopback port on every run, so a registered loopback URI matches any port sharing the same
    /// scheme, host and path. Returns true when the requested URI matches any registered entry.
    /// </summary>
    public static bool RedirectUriMatchesRegistered(string redirectUri, IReadOnlyList<string> registeredUris)
    {
        if (registeredUris.Contains(redirectUri, StringComparer.Ordinal))
            return true;

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requested)
            || requested.Scheme != Uri.UriSchemeHttp
            || !IsLoopbackHost(requested.Host))
            return false;

        foreach (var registered in registeredUris)
        {
            if (Uri.TryCreate(registered, UriKind.Absolute, out var reg)
                && reg.Scheme == Uri.UriSchemeHttp
                && IsLoopbackHost(reg.Host)
                && string.Equals(reg.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reg.AbsolutePath, requested.AbsolutePath, StringComparison.Ordinal))
                return true;
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
/// EF-backed, replica-safe broker for the Agentweaver OAuth 2.1 Authorization Server.
///
/// It bridges a public MCP client's PKCE authorization-code request to a GitHub login, reusing the
/// existing <see cref="GitHubOAuthRedirectService"/> (GitHub authorize-URL build, CSRF state, and
/// code-to-token exchange) and <see cref="IGitHubOrgAuthorizationService"/> (microsoft org check).
/// The GitHub client_secret and the user's GitHub token never leave the server; the client only ever
/// receives Agentweaver-minted artifacts (authorization code, then JWT access token).
///
/// State is persisted in <see cref="MemoryDbContext"/> (Postgres in prod, SQLite in dev) so the flow
/// survives load-balancing across replicas:
///  - pending authorizations keyed by the GitHub CSRF state, correlating the client's PKCE request
///    to the brokered GitHub login leg;
///  - issued authorization codes (single-use, short TTL) bound to client_id + redirect_uri + PKCE.
///
/// Scoped (per-request) because it depends on the scoped <see cref="MemoryDbContext"/>; resolved
/// directly by the minimal-API OAuth handlers (all call sites are in HTTP request scope).
///
/// TODO(T5): replace the implicit "any client_id" acceptance with Dynamic Client Registration.
/// </summary>
public sealed class McpOAuthBrokerService
{
    // Authorization codes are single-use and must be redeemed quickly (RFC 6749 recommends <= 60s).
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingAuthorizationLifetime = TimeSpan.FromMinutes(10);

    private readonly GitHubOAuthRedirectService _gitHub;
    private readonly IGitHubOrgAuthorizationService _orgAuth;
    private readonly IGitHubTokenStore _gitHubTokens;
    private readonly MemoryDbContext _db;
    private readonly ILogger<McpOAuthBrokerService> _logger;

    public McpOAuthBrokerService(
        GitHubOAuthRedirectService gitHub,
        IGitHubOrgAuthorizationService orgAuth,
        IGitHubTokenStore gitHubTokens,
        MemoryDbContext db,
        ILogger<McpOAuthBrokerService> logger)
    {
        _gitHub = gitHub;
        _orgAuth = orgAuth;
        _gitHubTokens = gitHubTokens;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Begins an authorization: builds the GitHub authorize URL (with its CSRF state) and persists the
    /// client's PKCE request keyed by that state, so the brokered GitHub callback can correlate back.
    /// Returns the GitHub authorize URL the user agent should be redirected to.
    /// </summary>
    public async Task<string> BeginAuthorization(
        string clientId, string redirectUri, string codeChallenge, string? clientState, string scope, string? resource,
        CancellationToken ct = default)
    {
        await PurgeExpiredAsync(ct).ConfigureAwait(false);

        var gitHubAuthorizeUrl = await _gitHub.BeginAuthorizationAsync(ct).ConfigureAwait(false);
        var state = ExtractState(gitHubAuthorizeUrl);

        _db.McpPendingAuthorizations.Add(new McpPendingAuthorization
        {
            State = state,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            ClientState = clientState,
            Scope = scope,
            Resource = resource,
            ExpiresAt = DateTimeOffset.UtcNow.Add(PendingAuthorizationLifetime),
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return gitHubAuthorizeUrl;
    }

    /// <summary>True when the GitHub CSRF state belongs to a pending MCP authorization (broker leg).</summary>
    public async Task<bool> IsPendingState(string? state, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(state)
        && await _db.McpPendingAuthorizations.AnyAsync(p => p.State == state, ct).ConfigureAwait(false);

    /// <summary>
    /// Handles the brokered GitHub callback: exchanges the GitHub code (via the reused service),
    /// enforces microsoft org membership, then — on success — issues a single-use authorization code
    /// and returns the client's loopback/registered redirect target.
    /// </summary>
    public async Task<BrokerCallbackResult> HandleGitHubCallbackAsync(string code, string state, CancellationToken ct)
    {
        // Atomically claim the pending authorization: only the caller whose delete affected the row
        // proceeds, so a replayed/duplicate callback for the same state is rejected as unknown.
        var pending = await _db.McpPendingAuthorizations
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.State == state, ct)
            .ConfigureAwait(false);

        var claimed = pending is not null
            && await _db.McpPendingAuthorizations
                .Where(p => p.State == state)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

        if (pending is null || !claimed || DateTimeOffset.UtcNow > pending.ExpiresAt)
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
        _db.McpAuthorizationCodes.Add(new McpAuthorizationCode
        {
            Code = authorizationCode,
            Subject = login,
            GithubLogin = login,
            CodeChallenge = pending.CodeChallenge,
            RedirectUri = pending.RedirectUri,
            ClientId = pending.ClientId,
            Scope = pending.Scope,
            ExpiresAt = DateTimeOffset.UtcNow.Add(AuthorizationCodeLifetime),
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Persist the brokered GitHub token under the per-user scope so the AS can re-check org
        // membership when this user's access token is refreshed (T4). Stored only if no richer token
        // already exists for the user (avoid clobbering a web-sign-in token that carries refresh material).
        // The GitHub token is never returned to the MCP client.
        try
        {
            var userScope = GitHubTokenScope.ForUser(login);
            var existing = await _gitHubTokens.GetAsync(userScope, ct).ConfigureAwait(false);
            if (existing.Status != GitHubTokenStatus.SignedIn)
            {
                await _gitHubTokens.SetAsync(
                    userScope,
                    new GitHubToken(gitHubAccessToken, null, null, login, null, []),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: refresh-time org re-check degrades to the issuance-time org value.
            _logger.LogWarning(ex, "Could not persist brokered GitHub token for {Login}; refresh org re-check will be best-effort.", login);
        }

        _logger.LogInformation("Issued MCP authorization code for GitHub login {Login}.", login);
        return new BrokerCallbackResult(BrokerOutcome.Success, pending.RedirectUri, pending.ClientState,
            authorizationCode, null, null);
    }

    /// <summary>
    /// Redeems an authorization code at the token endpoint: enforces single-use, expiry, exact
    /// redirect-URI match, client binding, and PKCE S256 verification. Returns the grant on success.
    ///
    /// Single-use is atomic across replicas: the code row is conditionally deleted by its value and a
    /// zero-rows-affected result (the row was already consumed by a concurrent redemption on this or
    /// another replica, or never existed) yields <c>invalid_grant</c>. Two concurrent redemptions of
    /// the same code therefore resolve to exactly one success.
    /// </summary>
    public async Task<(AuthorizationCodeGrant? Grant, CodeRedemptionError? Error)> RedeemAuthorizationCode(
        string? code, string? codeVerifier, string? redirectUri, string? clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (null, new CodeRedemptionError("invalid_grant", "Authorization code is invalid or already used."));

        var issued = await _db.McpAuthorizationCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == code, ct)
            .ConfigureAwait(false);

        // Atomic single-use: only the caller whose conditional delete affected the row may proceed.
        // A concurrent redemption (here or on another replica) sees zero rows affected → invalid_grant.
        var consumed = issued is not null
            && await _db.McpAuthorizationCodes
                .Where(c => c.Code == code)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

        if (issued is null || !consumed)
            return (null, new CodeRedemptionError("invalid_grant", "Authorization code is invalid or already used."));

        if (DateTimeOffset.UtcNow > issued.ExpiresAt)
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

    /// <summary>
    /// Best-effort cleanup of expired pending authorizations and codes. Read-time expiry checks remain
    /// authoritative for correctness; this just keeps the tables bounded. The DateTimeOffset comparison
    /// is only translatable on Postgres (prod, where growth matters), so on SQLite/dev it is skipped.
    /// </summary>
    private async Task PurgeExpiredAsync(CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql())
            return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            await _db.McpPendingAuthorizations.Where(p => p.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await _db.McpAuthorizationCodes.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: expired rows are also rejected at read time.
            _logger.LogDebug(ex, "Opportunistic purge of expired OAuth broker state failed; continuing.");
        }
    }
}
