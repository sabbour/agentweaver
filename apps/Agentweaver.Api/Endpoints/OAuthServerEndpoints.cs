using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;

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

            if (!OAuthServerConfig.IsAllowedRedirectUri(redirect_uri))
                return InvalidRequest("redirect_uri is missing or not an allowed loopback/HTTPS URI.");

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
        }).AllowAnonymous();

        // ---- T3: Token endpoint (authorization_code; refresh_token placeholder -> T4) ------------
        app.MapPost("/oauth/token", async (
            HttpContext ctx,
            McpOAuthBrokerService broker,
            McpTokenService tokenService,
            IConfiguration config) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";

            if (!ctx.Request.HasFormContentType)
                return BadOAuthRequest("invalid_request", "Expected application/x-www-form-urlencoded body.");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
            var grantType = form["grant_type"].ToString();

            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                var (grant, error) = broker.RedeemAuthorizationCode(
                    form["code"], form["code_verifier"], form["redirect_uri"], form["client_id"]);

                if (error is not null)
                    return BadOAuthRequest(error.Error, error.ErrorDescription);

                var issuer = OAuthServerConfig.ResolveIssuer(ctx, config);
                var audience = OAuthServerConfig.ResolveAudience(issuer, config);
                var org = config["Auth:GitHub:AllowedOrg"]?.Trim();

                var accessToken = tokenService.CreateAccessToken(
                    issuer, audience, grant!.Subject, grant.GithubLogin, org);

                // TODO(T4): persist a rotating, hashed refresh token bound to sub+client_id. The opaque
                // value is returned now so the response shape is final and T4 slots in without changing it.
                var refreshTokenPlaceholder = McpOAuthBrokerService.GenerateOpaqueToken();

                return Results.Json(new
                {
                    access_token = accessToken,
                    token_type = "Bearer",
                    expires_in = (int)McpTokenService.AccessTokenLifetime.TotalSeconds,
                    scope = grant.Scope,
                    refresh_token = refreshTokenPlaceholder,
                });
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                // TODO(T4): implement rotating refresh-token grant (validate hash, rotate, reuse-detect).
                return BadOAuthRequest("invalid_request", "refresh_token grant is not yet implemented (T4).");
            }

            return BadOAuthRequest("unsupported_grant_type", "grant_type must be authorization_code.");
        }).AllowAnonymous();
    }

    private static IResult InvalidRequest(string description) =>
        Results.Json(new { error = "invalid_request", error_description = description },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadOAuthRequest(string error, string description) =>
        Results.Json(new { error, error_description = description },
            statusCode: StatusCodes.Status400BadRequest);
}
