using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Endpoints for the Agentweaver-hosted OAuth 2.1 Authorization Server (Option C / Seraph design).
///
/// Implemented here:
///  - T1: RFC 8414 AS metadata (/.well-known/oauth-authorization-server) + JWKS (/oauth/jwks).
///  - T3: authorization-code flow with mandatory PKCE S256 (/oauth/authorize, /oauth/token), brokering
///        GitHub login and enforcing microsoft org membership before issuing tokens.
///  - T4: rotating refresh-token grant + /oauth/revoke (RFC 7009) + jti denylist.
///  - T5: /oauth/register (RFC 7591 Dynamic Client Registration); registered redirect URIs are the
///        authoritative per-client allowlist at /oauth/authorize.
///
/// All endpoints are unauthenticated (public discovery + public-client flow). They are exempted from
/// the GitHub token / org-authorization middleware via the /oauth and /.well-known prefixes.
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
                registration_endpoint = $"{issuer}/oauth/register",
                jwks_uri = $"{issuer}/oauth/jwks",
                revocation_endpoint = $"{issuer}/oauth/revoke",
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
        app.MapGet("/oauth/authorize", async (
            HttpContext ctx,
            McpOAuthBrokerService broker,
            McpClientStore clientStore,
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

            // T5: when the client registered via DCR, its registered redirect URIs are authoritative —
            // redirect_uri MUST exactly match one of them. Unregistered clients (e.g. loopback native
            // clients that skipped DCR) still rely on the static allowlist check above.
            var registeredUris = await clientStore
                .GetRedirectUrisAsync(client_id!, ctx.RequestAborted)
                .ConfigureAwait(false);
            if (registeredUris is not null
                && !registeredUris.Contains(redirect_uri!, StringComparer.Ordinal))
            {
                return InvalidRequest("redirect_uri does not match a redirect URI registered for this client_id.");
            }

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
                // or the check is INCONCLUSIVE (the brokered token has expired / GitHub is unreachable)
                // we fall back to the org captured at issuance rather than hard-denying — otherwise a
                // private-org member would be locked out every ~8h when GitHub's user token expires
                // (Seraph T4–T7 review, Fix 2). Only a DEFINITIVE non-membership revokes + denies.
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

                        if (membership is OrgAuthResult.Denied or OrgAuthResult.OrgAccessNotGranted)
                        {
                            await refreshStore.RevokeAsync(form["refresh_token"], ctx.RequestAborted).ConfigureAwait(false);
                            logger.LogWarning(
                                "Refused refresh for {Login}: org membership re-check returned {Result}.",
                                grant.GithubLogin, membership);
                            return Results.Json(
                                new { error = "access_denied", error_description = "Org membership is no longer valid." },
                                statusCode: StatusCodes.Status403Forbidden);
                        }

                        if (membership == OrgAuthResult.Inconclusive)
                        {
                            logger.LogInformation(
                                "Org re-check inconclusive for {Login} on refresh (brokered GitHub token " +
                                "likely expired); falling back to the issuance-time org claim '{Org}'.",
                                grant.GithubLogin, grant.Org);
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

        // ---- T5: Dynamic Client Registration (RFC 7591) -----------------------------------------
        // Public MCP clients self-register their exact redirect URIs and receive an ephemeral,
        // non-secret client_id. Registered redirect URIs become the authoritative per-client allowlist
        // at /oauth/authorize; the static F2 prefix allowlist remains as defense-in-depth. Each
        // redirect URI must independently pass the same loopback/allowlisted-HTTPS policy. Rate-limited.
        app.MapPost("/oauth/register", async (
            HttpContext ctx,
            McpClientStore clientStore,
            IConfiguration config) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";

            DynamicClientRegistrationRequest? body;
            try
            {
                body = await ctx.Request
                    .ReadFromJsonAsync<DynamicClientRegistrationRequest>(ctx.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadOAuthRequest("invalid_client_metadata", "Request body must be valid JSON.");
            }

            var redirectUris = body?.RedirectUris;
            if (redirectUris is null || redirectUris.Count == 0)
                return BadOAuthRequest("invalid_redirect_uri", "redirect_uris is required and must be non-empty.");

            foreach (var uri in redirectUris)
            {
                if (!OAuthServerConfig.IsAllowedRedirectUri(uri, config))
                    return BadOAuthRequest(
                        "invalid_redirect_uri",
                        $"redirect_uri '{uri}' must be loopback or an allowlisted HTTPS URI.");
            }

            // We only support public clients with PKCE; reject any attempt to register a secret-bearing
            // confidential client auth method.
            if (!string.IsNullOrWhiteSpace(body!.TokenEndpointAuthMethod)
                && !string.Equals(body.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
            {
                return BadOAuthRequest(
                    "invalid_client_metadata",
                    "Only public clients are supported (token_endpoint_auth_method must be 'none').");
            }

            var result = await clientStore
                .RegisterAsync(redirectUris, body.ClientName, ctx.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(new
            {
                client_id = result.ClientId,
                client_id_issued_at = result.ClientIdIssuedAt,
                redirect_uris = result.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                client_name = body.ClientName,
            }, statusCode: StatusCodes.Status201Created);
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

/// <summary>Subset of the RFC 7591 client metadata accepted by <c>/oauth/register</c> (T5).</summary>
public sealed record DynamicClientRegistrationRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; init; }
}
