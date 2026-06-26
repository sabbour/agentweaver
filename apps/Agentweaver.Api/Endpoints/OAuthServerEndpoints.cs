using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Endpoints for the Agentweaver-hosted OAuth 2.1 Authorization Server (Option C / Seraph design).
///
/// Implemented here (T1-T3):
///  - T1: RFC 8414 AS metadata (/.well-known/oauth-authorization-server) + JWKS (/oauth/jwks).
///  - T3: authorization-code flow with mandatory PKCE S256 (/oauth/authorize, /oauth/token), brokering
///        GitHub login and enforcing microsoft org membership before issuing tokens.
///
/// All endpoints are unauthenticated (public discovery + public-client flow). They are exempted from
/// the GitHub token / org-authorization middleware via the /oauth and /.well-known prefixes.
///
/// NOT implemented here (post-checkpoint):
///  - TODO(T4): /oauth/revoke (RFC 7009) + rotating refresh-token store + jti denylist.
///  - TODO(T5): /oauth/register (RFC 7591 Dynamic Client Registration).
///  - TODO(T6): MCP Resource-Server changes (WWW-Authenticate 401, oauth-protected-resource, JWT mw).
///  - TODO(T7): per-user downstream identity mapping for MCP -> API calls.
/// </summary>
public static class OAuthServerEndpoints
{
    /// <summary>F3: named fixed-window rate-limit policy applied to the OAuth flow endpoints.</summary>
    public const string RateLimitPolicy = "oauth";

    public static void MapOAuthServerEndpoints(this WebApplication app)
    {
        // ---- T1: Authorization Server Metadata (RFC 8414) ----------------------------------------
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx, IConfiguration config) =>
        {
            var issuer = OAuthServerConfig.ResolveIssuer(ctx, config);
            return Results.Json(new
            {
                issuer,
                authorization_endpoint = $"{issuer}/oauth/authorize",
                token_endpoint = $"{issuer}/oauth/token",
                registration_endpoint = $"{issuer}/oauth/register", // TODO(T5): DCR
                jwks_uri = $"{issuer}/oauth/jwks",
                revocation_endpoint = $"{issuer}/oauth/revoke",      // TODO(T4): revocation
                scopes_supported = new[] { "mcp:invoke", "offline_access" },
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" }, // S256 only — plain is rejected
                token_endpoint_auth_methods_supported = new[] { "none" },
            });
        }).AllowAnonymous();

        // ---- T1: JWKS — public signing key ------------------------------------------------------
        app.MapGet("/oauth/jwks", (McpTokenService tokenService) =>
        {
            var jwk = tokenService.GetPublicJsonWebKey();
            return Results.Json(new
            {
                keys = new[]
                {
                    new { kty = jwk.Kty, use = jwk.Use, alg = jwk.Alg, kid = jwk.Kid, n = jwk.N, e = jwk.E },
                },
            });
        }).AllowAnonymous();

        // ---- T3: Authorization endpoint (PKCE S256 mandatory) -----------------------------------
        app.MapGet("/oauth/authorize", (
            HttpContext ctx,
            McpOAuthBrokerService broker,
            IConfiguration config,
            ILogger<Program> logger,
            string? response_type,
            string? client_id,
            string? redirect_uri,
            string? code_challenge,
            string? code_challenge_method,
            string? scope,
            string? state,
            string? resource) =>
        {
            // Validate redirect_uri and client_id FIRST. If either is invalid we MUST NOT redirect
            // (prevents open redirects / leaking errors to an unverified target) — return 400.
            if (string.IsNullOrWhiteSpace(client_id))
                return InvalidRequest("client_id is required.");

            if (!OAuthServerConfig.IsAllowedRedirectUri(redirect_uri, config))
                return InvalidRequest("redirect_uri is missing or not an allowed loopback/allowlisted HTTPS URI.");

            if (!string.Equals(response_type, "code", StringComparison.Ordinal))
                return BadOAuthRequest("unsupported_response_type", "Only response_type=code is supported.");

            // Mandatory PKCE — reject missing challenge and anything other than S256 (no 'plain').
            if (string.IsNullOrWhiteSpace(code_challenge))
                return BadOAuthRequest("invalid_request", "code_challenge is required (PKCE is mandatory).");

            if (!string.Equals(code_challenge_method, "S256", StringComparison.Ordinal))
                return BadOAuthRequest("invalid_request", "code_challenge_method must be S256.");

            var effectiveScope = string.IsNullOrWhiteSpace(scope) ? McpTokenService.AccessTokenScope : scope!;

            try
            {
                var gitHubAuthorizeUrl = broker.BeginAuthorization(
                    client_id!, redirect_uri!, code_challenge!, state, effectiveScope, resource);
                return Results.Redirect(gitHubAuthorizeUrl);
            }
            catch (GitHubNotConfiguredException ex)
            {
                logger.LogWarning("MCP OAuth authorize attempted but GitHub OAuth is not configured: {Message}", ex.Message);
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

        // ---- T3/T4: Token endpoint (authorization_code + rotating refresh_token grants) ----------
        app.MapPost("/oauth/token", async (
            HttpContext ctx,
            McpOAuthBrokerService broker,
            McpTokenService tokenService,
            McpRefreshTokenStore refreshStore,
            IGitHubOrgAuthorizationService orgAuth,
            IGitHubAccessTokenProvider gitHubTokens,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";

            if (!ctx.Request.HasFormContentType)
                return BadOAuthRequest("invalid_request", "Expected application/x-www-form-urlencoded body.");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
            var grantType = form["grant_type"].ToString();
            var issuer = OAuthServerConfig.ResolveIssuer(ctx, config);
            var audience = OAuthServerConfig.ResolveAudience(issuer, config);

            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                var (grant, error) = broker.RedeemAuthorizationCode(
                    form["code"], form["code_verifier"], form["redirect_uri"], form["client_id"]);

                if (error is not null)
                    return BadOAuthRequest(error.Error, error.ErrorDescription);

                var org = config["Auth:GitHub:AllowedOrg"]?.Trim();
                var clientId = form["client_id"].ToString();

                var accessToken = tokenService.CreateAccessToken(
                    issuer, audience, grant!.Subject, grant.GithubLogin, org);

                // T4: issue a rotating refresh token bound to sub + client_id (stored hashed).
                var refreshToken = await refreshStore.IssueAsync(
                    new McpRefreshGrant(grant.Subject, grant.GithubLogin, clientId, grant.Scope, org),
                    ctx.RequestAborted).ConfigureAwait(false);

                return Results.Json(new
                {
                    access_token = accessToken,
                    token_type = "Bearer",
                    expires_in = (int)McpTokenService.AccessTokenLifetime.TotalSeconds,
                    scope = grant.Scope,
                    refresh_token = refreshToken,
                });
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                var rotation = await refreshStore.RotateAsync(
                    form["refresh_token"], form["client_id"], ctx.RequestAborted).ConfigureAwait(false);

                if (rotation.Error is not null)
                    return BadOAuthRequest(rotation.Error, rotation.ErrorDescription ?? "Refresh failed.");

                var grant = rotation.Grant!;

                // T4: re-validate org membership on refresh so revoked org access propagates within
                // one access-token lifetime. Best-effort: if the brokered GitHub token is unavailable
                // we fall back to the org captured at issuance (documented in the design).
                if (orgAuth.IsConfigured)
                {
                    var gitHubToken = await gitHubTokens
                        .GetValidAccessTokenAsync(GitHubTokenScope.ForUser(grant.GithubLogin), ctx.RequestAborted)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(gitHubToken))
                    {
                        var membership = await orgAuth
                            .CheckMembershipAsync(gitHubToken, grant.GithubLogin, ctx.RequestAborted)
                            .ConfigureAwait(false);
                        if (membership != OrgAuthResult.Allowed)
                        {
                            await refreshStore.RevokeAsync(form["refresh_token"], ctx.RequestAborted).ConfigureAwait(false);
                            logger.LogWarning(
                                "Refused refresh for {Login}: org membership re-check returned {Result}.",
                                grant.GithubLogin, membership);
                            return Results.Json(
                                new { error = "access_denied", error_description = "Org membership is no longer valid." },
                                statusCode: StatusCodes.Status403Forbidden);
                        }
                    }
                }

                var accessToken = tokenService.CreateAccessToken(
                    issuer, audience, grant.Subject, grant.GithubLogin, grant.Org);

                return Results.Json(new
                {
                    access_token = accessToken,
                    token_type = "Bearer",
                    expires_in = (int)McpTokenService.AccessTokenLifetime.TotalSeconds,
                    scope = grant.Scope,
                    refresh_token = rotation.NewRefreshToken,
                });
            }

            return BadOAuthRequest("unsupported_grant_type", "grant_type must be authorization_code or refresh_token.");
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

        // ---- T4: Revocation endpoint (RFC 7009) -------------------------------------------------
        // Always returns 200, even for an unknown/invalid token, per RFC 7009 §2.2. Revokes the
        // refresh-token chain and, when the value is an Agentweaver access token, denylists its jti
        // until natural expiry so it cannot be replayed before it lapses.
        app.MapPost("/oauth/revoke", async (
            HttpContext ctx,
            McpTokenService tokenService,
            McpRefreshTokenStore refreshStore,
            IConfiguration config) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";

            if (!ctx.Request.HasFormContentType)
                return BadOAuthRequest("invalid_request", "Expected application/x-www-form-urlencoded body.");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
            var token = form["token"].ToString();

            if (!string.IsNullOrWhiteSpace(token))
            {
                await refreshStore.RevokeAsync(token, ctx.RequestAborted).ConfigureAwait(false);

                var issuer = OAuthServerConfig.ResolveIssuer(ctx, config);
                var audience = OAuthServerConfig.ResolveAudience(issuer, config);
                if (tokenService.TryReadJtiAndExpiry(token, issuer, audience, out var jti, out var expiresAt))
                    await refreshStore.DenyJtiAsync(jti!, expiresAt, ctx.RequestAborted).ConfigureAwait(false);
            }

            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
    }

    private static IResult InvalidRequest(string description) =>
        Results.Json(new { error = "invalid_request", error_description = description },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadOAuthRequest(string error, string description) =>
        Results.Json(new { error, error_description = description },
            statusCode: StatusCodes.Status400BadRequest);
}
