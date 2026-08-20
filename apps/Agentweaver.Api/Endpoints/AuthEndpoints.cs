using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;

namespace Agentweaver.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
app.MapGet("/api/auth/config", (IConfiguration configuration) =>
{
    var authMode = AuthModeResolver.Resolve(configuration);
    var tenantId = configuration["Auth:Entra:TenantId"];
    var authority = configuration["Auth:Entra:Authority"];
    if (string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(tenantId))
        authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

    return Results.Ok(new
    {
        mode = authMode.ToString(),
        entra = authMode == AuthMode.Entra
            ? new
            {
                client_id = configuration["Auth:Entra:ClientId"],
                tenant_id = tenantId,
                authority,
            }
            : null,
    });
}).AllowAnonymous();

app.MapGet("/api/auth/context", (HttpContext httpContext, IConfiguration configuration) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    return Results.Ok(new
    {
        mode = AuthModeResolver.Resolve(configuration).ToString(),
        user_id = caller.User,
        github_login = caller.GitHubLogin,
        entra_object_id = caller.EntraObjectId,
        entra_tenant_id = caller.EntraTenantId,
        platform_roles = caller.PlatformRoles,
        primary_platform_role = caller.PrimaryPlatformRole,
    });
});

// GET /api/auth/session — the web app's post-sign-in identity/session check (AuthGate in
// apps/web/src/App.tsx). Reaching this handler at all means the caller already presented a
// valid bearer token (GitHubTokenAuthMiddleware ran first), so `authenticated` is always true
// here; an absent/invalid token 401s before this route is ever matched, which the web app's
// apiClient already treats as "signed out". This route is exempt from
// PlatformRoleAuthorizationMiddleware so a signed-in caller with zero platform roles can still
// see their own identity/roles (empty) instead of a 403 dead end.
app.MapGet("/api/auth/session", (HttpContext httpContext, IConfiguration configuration) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    return Results.Ok(new
    {
        authenticated = true,
        auth_mode = AuthModeResolver.ToWireValue(AuthModeResolver.Resolve(configuration)),
        display_name = caller.DisplayName,
        email = caller.Email,
        login = caller.GitHubLogin,
        avatar_url = (string?)null,
        entra_object_id = caller.EntraObjectId,
        platform_roles = caller.PlatformRoles,
    });
});

// GET /auth/github/authorize — begin OAuth redirect flow
app.MapGet("/auth/github/authorize", async (HttpContext httpContext, GitHubOAuthRedirectService oauthService, CancellationToken ct) =>
{
    try
    {
        var url = await oauthService.BeginAuthorizationAsync(ct);
        // Login-CSRF protection (Seraph findings-auth Alert 6): bind the OAuth `state` to THIS browser
        // by echoing it into a Secure, HttpOnly, SameSite=Lax cookie (double-submit pattern). The
        // callback requires the cookie to match the `state` returned by GitHub, so an attacker cannot
        // graft their own pre-authorized state/code onto a victim's browser (the victim's browser never
        // holds a cookie for the attacker's state).
        var state = OAuthStateCookie.ExtractState(url);
        if (state is not null)
            OAuthStateCookie.Set(httpContext, state);
        return Results.Redirect(url);
    }
    catch (GitHubNotConfiguredException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
}).AllowAnonymous();

// GET /auth/github/callback — receive OAuth code from GitHub, exchange for token.
// This single callback (the GitHub OAuth app's registered redirect URI) serves BOTH the web
// sign-in flow and the MCP OAuth Authorization-Server broker leg (Option C). When the CSRF state
// belongs to a pending MCP authorization, the brokered path issues an authorization code back to
// the MCP client's loopback/registered redirect URI; otherwise the existing web path runs.
app.MapGet("/auth/github/callback", async (
    HttpContext httpContext,
    string? code,
    string? state,
    string? error,
    GitHubOAuthRedirectService oauthService,
    LinkedGitHubAccountService linkedAccountService,
    Agentweaver.Api.Auth.OAuth.McpOAuthBrokerService oauthBroker,
    WebSessionExchangeService webSessionExchange,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    var frontendUrl = (configuration["Auth:GitHub:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');

    // MCP OAuth broker leg: correlate by the GitHub CSRF state.
    if (!string.IsNullOrWhiteSpace(state) && await oauthBroker.IsPendingState(state, ct).ConfigureAwait(false))
    {
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new { error = "access_denied", error_description = error ?? "missing_code" });

        var result = await oauthBroker.HandleGitHubCallbackAsync(code!, state!, ct).ConfigureAwait(false);
        if (result.RedirectUri is null)
            return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });

        var separator = result.RedirectUri.Contains('?') ? '&' : '?';
        var clientStateSuffix = string.IsNullOrEmpty(result.ClientState)
            ? string.Empty
            : $"&state={Uri.EscapeDataString(result.ClientState)}";

        var query = result.Outcome == Agentweaver.Api.Auth.OAuth.BrokerOutcome.Success
            ? $"code={Uri.EscapeDataString(result.Code!)}{clientStateSuffix}"
            : $"error={Uri.EscapeDataString(result.Error ?? "access_denied")}" +
              $"&error_description={Uri.EscapeDataString(result.ErrorDescription ?? string.Empty)}{clientStateSuffix}";

        return Results.Redirect($"{result.RedirectUri}{separator}{query}");
    }

    if (string.IsNullOrWhiteSpace(state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    // Login-CSRF protection (Seraph findings-auth Alert 6): the `state` returned by GitHub MUST match
    // the session-bound cookie armed at /auth/github/authorize. A missing or mismatched cookie means
    // this callback was not initiated by this browser (e.g. an attacker's pre-authorized state grafted
    // onto the victim), so reject it before redeeming the code. The cookie is always cleared afterwards.
    var boundState = OAuthStateCookie.Read(httpContext);
    OAuthStateCookie.Clear(httpContext);
    if (string.IsNullOrEmpty(boundState) || !OAuthStateCookie.ConstantTimeEquals(boundState, state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=state_mismatch");

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(error)}");

    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    if (await linkedAccountService.IsPendingStateAsync(state, ct).ConfigureAwait(false))
    {
        try
        {
            var linked = await linkedAccountService.CompleteLinkAsync(code, state, ct).ConfigureAwait(false);
            // Linked-account management lives on the Settings page regardless of which UI
            // surface (sidebar switcher or Settings itself) started the link flow, so always
            // land there rather than the app root after a successful link.
            return Results.Redirect(
                $"{frontendUrl}/settings?auth=github_linked&login={Uri.EscapeDataString(linked.GitHubLogin)}");
        }
        catch (Exception ex)
        {
            return Results.Redirect($"{frontendUrl}/settings?auth=error&reason={Uri.EscapeDataString(ex.Message)}");
        }
    }

    try
    {
        var (login, accessToken) = await oauthService.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
        // F5: do not place the access token (or login) in the redirect URL — it would leak to
        // browser history, server access logs, and Referer headers. Issue a short-lived, single-use
        // one-time code instead; the frontend exchanges it server-side via POST /api/auth/session/exchange.
        var oneTimeCode = await webSessionExchange.IssueAsync(accessToken, login, ct).ConfigureAwait(false);
        return Results.Redirect(
            $"{frontendUrl}/?auth=success&code={Uri.EscapeDataString(oneTimeCode)}");
    }
    catch (Exception ex)
    {
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(ex.Message)}");
    }
}).AllowAnonymous();

// GET /auth/entra/authorize — begin the Microsoft Entra ID browser sign-in redirect flow
// (Microsoft identity platform v2.0 authorization code + PKCE). The Entra counterpart to
// /auth/github/authorize. Only meaningful when Auth:Mode=Entra; otherwise 503 (mirrors the
// GitHubNotConfiguredException → 503 pattern for /auth/github/authorize).
app.MapGet("/auth/entra/authorize", async (
    HttpContext httpContext,
    EntraOAuthRedirectService entraOauthService,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    if (AuthModeResolver.Resolve(configuration) != AuthMode.Entra)
        return Results.Problem("Microsoft Entra sign-in is disabled (Auth:Mode is not Entra).", statusCode: 503);

    try
    {
        var url = await entraOauthService.BeginAuthorizationAsync(ct);
        // Login-CSRF protection (Seraph findings-auth Alert 6): bind the OAuth `state` to THIS browser
        // by echoing it into a Secure, HttpOnly, SameSite=Lax cookie (double-submit pattern), scoped to
        // /auth/entra. The callback requires the cookie to match the `state` returned by Microsoft, so
        // an attacker cannot graft their own pre-authorized state/code onto a victim's browser (the
        // victim's browser never holds a cookie for the attacker's state).
        var state = OAuthStateCookie.ExtractState(url);
        if (state is not null)
            EntraOAuthStateCookie.Set(httpContext, state);
        return Results.Redirect(url);
    }
    catch (EntraNotConfiguredException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
}).AllowAnonymous();

// GET /auth/entra/callback — receive the authorization code from Microsoft Entra, validate the CSRF
// state, redeem the code + PKCE verifier for a validated access token, and establish the platform
// web session via the one-time-code exchange (F5). The Entra counterpart to /auth/github/callback.
app.MapGet("/auth/entra/callback", async (
    HttpContext httpContext,
    string? code,
    string? state,
    string? error,
    string? error_description,
    EntraOAuthRedirectService entraOauthService,
    WebSessionExchangeService webSessionExchange,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    if (AuthModeResolver.Resolve(configuration) != AuthMode.Entra)
        return Results.Problem("Microsoft Entra sign-in is disabled (Auth:Mode is not Entra).", statusCode: 503);

    var frontendUrl = (configuration["Auth:Entra:FrontendUrl"]
                       ?? configuration["Auth:GitHub:FrontendUrl"]
                       ?? "http://localhost:5173").TrimEnd('/');

    if (string.IsNullOrWhiteSpace(state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    // Login-CSRF protection (Seraph findings-auth Alert 6): the `state` returned by Microsoft MUST
    // match the session-bound cookie armed at /auth/entra/authorize. A missing or mismatched cookie
    // means this callback was not initiated by this browser (e.g. an attacker's pre-authorized state
    // grafted onto the victim), so reject it before redeeming the code. The cookie is always cleared.
    var boundState = EntraOAuthStateCookie.Read(httpContext);
    EntraOAuthStateCookie.Clear(httpContext);
    if (string.IsNullOrEmpty(boundState) || !OAuthStateCookie.ConstantTimeEquals(boundState, state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=state_mismatch");

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(error)}");

    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    try
    {
        var (claims, accessToken) = await entraOauthService.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
        // F5: do not place the access token (or identity) in the redirect URL — it would leak to
        // browser history, server access logs, and Referer headers. Issue a short-lived, single-use
        // one-time code instead; the frontend exchanges it server-side via POST
        // /api/auth/session/exchange. The token the browser then sends on API requests IS this Entra
        // access token, which the auth middleware re-validates (issuer/audience/signature/tenant) on
        // every request.
        var oneTimeCode = await webSessionExchange.IssueAsync(accessToken, claims.DisplayName, ct).ConfigureAwait(false);
        return Results.Redirect($"{frontendUrl}/?auth=success&code={Uri.EscapeDataString(oneTimeCode)}");
    }
    catch (Exception ex)
    {
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(ex.Message)}");
    }
}).AllowAnonymous();

// POST /api/auth/session/exchange — redeem a web sign-in one-time code for the session token (F5).
// AllowAnonymous: the opaque, single-use code is itself the credential. The GitHub access token is
// never placed in a URL; it is returned only here, in the response body, over the server-side POST.
app.MapPost("/api/auth/session/exchange", async (
    SessionExchangeRequest request,
    WebSessionExchangeService webSessionExchange,
    CancellationToken ct) =>
{
    if (request is null) return Results.BadRequest(new { error = "invalid_code" });

    var (success, accessToken, login) = await webSessionExchange.TryRedeemAsync(request.Code, ct).ConfigureAwait(false);
    if (!success)
        return Results.BadRequest(new { error = "invalid_code" });

    return Results.Ok(new SessionExchangeResponse(accessToken, login));
}).AllowAnonymous();

app.MapGet("/api/auth/github-accounts", async (
    HttpContext httpContext,
    LinkedGitHubAccountService linkedAccountService,
    IGitHubTokenStore tokenStore,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
        return Results.Conflict(new { error = "Linked GitHub accounts require Entra sign-in." });

    var links = await linkedAccountService.ListLinkedAccountsAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
    var responses = new List<LinkedGitHubAccountResponse>(links.Count);
    foreach (var link in links)
    {
        var tokenScope = GitHubTokenScope.ForLinkedIdentity(caller.EntraObjectId!, link.GitHubLogin);
        var tokenEntry = await tokenStore.GetAsync(tokenScope, ct).ConfigureAwait(false);
        responses.Add(new LinkedGitHubAccountResponse
        {
            Login = link.GitHubLogin,
            AvatarUrl = link.AvatarUrl,
            IsDefault = link.IsDefault,
            CopilotEntitled = link.CopilotEntitled,
            LinkedAt = link.LinkedAt,
            TokenValid = tokenEntry.Status == GitHubTokenStatus.SignedIn,
        });
    }
    return Results.Ok(responses);
});

app.MapPost("/api/auth/github-accounts/link", async (
    HttpContext httpContext,
    LinkedGitHubAccountService linkedAccountService,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
        return Results.Conflict(new { error = "Linked GitHub accounts require Entra sign-in." });

    var authorizeUrl = await linkedAccountService.BeginLinkAuthorizationAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
    var state = OAuthStateCookie.ExtractState(authorizeUrl);
    if (state is not null)
        OAuthStateCookie.Set(httpContext, state);

    return Results.Ok(new BeginGitHubAccountLinkResponse(authorizeUrl));
});

app.MapDelete("/api/auth/github-accounts/{login}", async (
    HttpContext httpContext,
    string login,
    LinkedGitHubAccountService linkedAccountService,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
        return Results.Conflict(new { error = "Linked GitHub accounts require Entra sign-in." });

    var result = await linkedAccountService.UnlinkAsync(caller.EntraObjectId!, login, ct).ConfigureAwait(false);
    if (!result.Removed)
        return Results.NotFound();

    return Results.Ok(new UnlinkGitHubAccountResponse(result.NewDefaultLogin));
});

app.MapPut("/api/auth/github-accounts/{login}/default", async (
    HttpContext httpContext,
    string login,
    LinkedGitHubAccountService linkedAccountService,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
        return Results.Conflict(new { error = "Linked GitHub accounts require Entra sign-in." });

    var changed = await linkedAccountService.SetDefaultAsync(caller.EntraObjectId!, login, ct).ConfigureAwait(false);
    return changed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/auth/github-accounts/accessible-repos", async (
    HttpContext httpContext,
    LinkedGitHubAccountService linkedAccountService,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
        return Results.Conflict(new { error = "Linked GitHub accounts require Entra sign-in." });

    var repos = await linkedAccountService.ListAccessibleRepositoriesAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
    return Results.Ok(repos.Select(repo => new AccessibleGitHubRepositoryResponse
    {
        FullName = repo.FullName,
        Description = repo.Description,
        Private = repo.Private,
        DefaultBranch = repo.DefaultBranch,
        HtmlUrl = repo.HtmlUrl,
        AccessibleViaLogin = repo.AccessibleViaLogin,
        AccessibleViaAvatarUrl = repo.AccessibleViaAvatarUrl,
        AccessibleViaIsDefault = repo.AccessibleViaIsDefault,
        Permission = repo.Permission,
    }));
});

// POST /api/auth/github/device — start device flow
app.MapPost("/api/auth/github/device", async (
    HttpContext httpContext,
    IGitHubAuthService authService,
    IGitHubTokenScopeProvider scopeProvider,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    try
    {
        var result = await authService.StartDeviceFlowAsync(scope, ct);
        return Results.Ok(new GitHubDeviceFlowResponse
        {
            UserCode = result.UserCode,
            VerificationUri = result.VerificationUri,
            ExpiresIn = result.ExpiresIn,
            Interval = result.Interval,
        });
    }
    catch (GitHubNotConfiguredException ex)
    {
        logger.LogWarning("GitHub sign-in attempted but OAuth is not configured: {Message}", ex.Message);
        return Results.Problem(ex.Message, statusCode: 503);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to start GitHub device flow for {User}", caller.User);
        return Results.Problem("Failed to start GitHub device flow.", statusCode: 500);
    }
});

// POST /api/auth/github/poll — poll device flow
app.MapPost("/api/auth/github/poll", async (
    HttpContext httpContext,
    IGitHubAuthService authService,
    IGitHubTokenScopeProvider scopeProvider,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    var result = await authService.PollDeviceFlowAsync(scope, ct);
    return Results.Ok(new GitHubPollResponse
    {
        Status = result.Result switch
        {
            GitHubDeviceFlowPollResult.Pending => "pending",
            GitHubDeviceFlowPollResult.Success => "success",
            GitHubDeviceFlowPollResult.Expired => "expired",
            GitHubDeviceFlowPollResult.Denied  => "denied",
            _ => "unknown"
        },
        Login = result.Login,
    });
});

// GET /api/auth/github — current auth status
app.MapGet("/api/auth/github", async (
    HttpContext httpContext,
    IGitHubTokenStore tokenStore,
    IGitHubTokenScopeProvider scopeProvider,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    var entry = await tokenStore.GetAsync(scope, ct);
    var identity = entry.Status == GitHubTokenStatus.SignedIn
        ? await tokenStore.GetIdentityAsync(scope, ct)
        : null;
    return Results.Ok(new GitHubAuthStatusResponse
    {
        Status = entry.Status switch
        {
            GitHubTokenStatus.SignedIn      => "signed_in",
            GitHubTokenStatus.SignedOut     => "signed_out",
            GitHubTokenStatus.NeverSignedIn => "never_signed_in",
            _ => "unknown"
        },
        Login = identity?.Login,
        AvatarUrl = identity?.AvatarUrl,
        TokenActionRequired = entry.Status != GitHubTokenStatus.SignedIn,
    });
});

// GET /api/github/accounts — authenticated user and their orgs, user first
app.MapGet("/api/github/accounts", async (
    HttpContext httpContext,
    IGitHubTokenScopeProvider scopeProvider,
    IGitHubAccessTokenProvider accessTokenProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    var accessToken = await accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(accessToken))
        return Results.Unauthorized();

    try
    {
        using var http = httpClientFactory.CreateClient("github");

        // 1. Fetch the authenticated user's profile (login, name, avatar_url).
        GitHubApiUser? apiUser;
        using (var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"))
        {
            userReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            userReq.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
            userReq.Headers.Accept.ParseAdd("application/vnd.github+json");

            var userResp = await http.SendAsync(userReq, ct).ConfigureAwait(false);
            if (!userResp.IsSuccessStatusCode)
                return Results.Problem("Failed to fetch GitHub user profile.", statusCode: 500);

            apiUser = await userResp.Content
                .ReadFromJsonAsync<GitHubApiUser>(ct)
                .ConfigureAwait(false);
        }

        if (apiUser is null)
            return Results.Problem("GitHub user profile response was empty.", statusCode: 500);

        var accounts = new List<GitHubAccountResponse>
        {
            new GitHubAccountResponse(
                apiUser.Login ?? string.Empty,
                apiUser.Name,
                apiUser.AvatarUrl ?? string.Empty,
                "user")
        };

        // 2. Fetch the user's orgs with pagination.
        var page = 1;
        const int perPage = 100;
        while (true)
        {
            using var orgsReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/user/orgs?per_page={perPage}&page={page}");
            orgsReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            orgsReq.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
            orgsReq.Headers.Accept.ParseAdd("application/vnd.github+json");

            var orgsResp = await http.SendAsync(orgsReq, ct).ConfigureAwait(false);
            if (!orgsResp.IsSuccessStatusCode) break;

            var batch = await orgsResp.Content
                .ReadFromJsonAsync<GitHubApiOrg[]>(ct)
                .ConfigureAwait(false);

            if (batch is null || batch.Length == 0) break;

            accounts.AddRange(batch.Select(o => new GitHubAccountResponse(
                o.Login ?? string.Empty,
                o.Login, // GitHub org objects don't always return a display name; fall back to login
                o.AvatarUrl ?? string.Empty,
                "org")));

            if (batch.Length < perPage) break;
            page++;
        }

        return Results.Ok(accounts);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to list GitHub accounts for {User}", caller.User);
        return Results.Problem("Failed to fetch GitHub accounts.", statusCode: 500);
    }
});

// GET /api/github/repos — list GitHub repositories.
// Optional ?account= query param selects whose repos to list:
//   absent / own login → GET /user/repos?affiliation=owner (backward-compatible default)
//   org login          → GET /orgs/{org}/repos?type=all
app.MapGet("/api/github/repos", async (
    HttpContext httpContext,
    string? account,
    IGitHubTokenScopeProvider scopeProvider,
    IGitHubAccessTokenProvider accessTokenProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    var accessToken = await accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(accessToken))
        return Results.Unauthorized();

    // Determine which API path to use. caller.User is the GitHub login (set by
    // GitHubTokenAuthMiddleware from the GitHub /user endpoint) so comparing against it
    // avoids an extra round-trip to determine "is this account the authenticated user?"
    var isOwnAccount = string.IsNullOrWhiteSpace(account)
        || string.Equals(account, caller.User, StringComparison.OrdinalIgnoreCase);

    try
    {
        using var http = httpClientFactory.CreateClient("github");
        var repos = new List<GitHubRepoResponse>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            string url;
            if (isOwnAccount)
            {
                url = $"https://api.github.com/user/repos?sort=pushed&per_page={perPage}&page={page}&affiliation=owner";
            }
            else
            {
                var encodedAccount = Uri.EscapeDataString(account!);
                url = $"https://api.github.com/orgs/{encodedAccount}/repos?sort=pushed&per_page={perPage}&page={page}&type=all";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) break;

            var batch = await response.Content
                .ReadFromJsonAsync<GitHubApiRepo[]>(ct)
                .ConfigureAwait(false);

            if (batch is null || batch.Length == 0) break;

            repos.AddRange(batch.Select(r => new GitHubRepoResponse(
                r.FullName ?? string.Empty,
                r.Description,
                r.Private,
                r.DefaultBranch ?? "main"
            )));

            if (batch.Length < perPage) break;
            page++;
        }

        return Results.Ok(repos);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to list GitHub repos for {User}", caller.User);
        return Results.Problem("Failed to fetch GitHub repositories.", statusCode: 500);
    }
});

// POST /api/auth/github/sign-out
app.MapPost("/api/auth/github/sign-out", async (
    HttpContext httpContext,
    IGitHubAuthService authService,
    IGitHubTokenScopeProvider scopeProvider,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var scope = scopeProvider.Resolve(caller.User);
    await authService.SignOutAsync(scope, ct);
    return Results.NoContent();
});
    }
}

/// <summary>
/// Helpers for the browser-session binding of the web sign-in OAuth <c>state</c> (Seraph findings-auth
/// Alert 6, login-CSRF). The <c>state</c> issued at <c>/auth/github/authorize</c> is echoed into a
/// Secure, HttpOnly, SameSite=Lax cookie; <c>/auth/github/callback</c> requires the cookie to match the
/// <c>state</c> GitHub returns (double-submit-cookie pattern), proving the callback was initiated by
/// this same browser. Only the web sign-in leg uses this; the MCP broker leg is a native-client flow.
/// </summary>
internal static class OAuthStateCookie
{
    public const string Name = "aw_oauth_state";
    private const string Path = "/auth/github";

    /// <summary>Extracts the <c>state</c> query value from a GitHub authorize URL, or null if absent.</summary>
    public static string? ExtractState(string authorizeUrl)
    {
        if (!Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("state=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair["state=".Length..]);
        }
        return null;
    }

    public static void Set(HttpContext ctx, string state) =>
        ctx.Response.Cookies.Append(Name, state, new CookieOptions
        {
            HttpOnly = true,
            // Secure whenever the request is HTTPS (always true in prod); relaxed on plain-HTTP localhost
            // dev so the cookie is still delivered there.
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = Path,
            MaxAge = TimeSpan.FromMinutes(10),
        });

    public static string? Read(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(Name, out var value) ? value : null;

    public static void Clear(HttpContext ctx) =>
        ctx.Response.Cookies.Append(Name, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = Path,
            Expires = DateTimeOffset.UnixEpoch,
        });

    public static bool ConstantTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// Browser-session binding for the Microsoft Entra web sign-in OAuth <c>state</c> (login-CSRF
/// mitigation, mirroring <see cref="OAuthStateCookie"/>). Distinct cookie name and <c>Path</c>
/// (<c>/auth/entra</c>) so it is delivered only to the Entra callback and never collides with the
/// GitHub state cookie. State parsing and constant-time comparison are shared with
/// <see cref="OAuthStateCookie"/> (they are path-independent).
/// </summary>
internal static class EntraOAuthStateCookie
{
    public const string Name = "aw_entra_oauth_state";
    private const string Path = "/auth/entra";

    public static void Set(HttpContext ctx, string state) =>
        ctx.Response.Cookies.Append(Name, state, new CookieOptions
        {
            HttpOnly = true,
            // Secure whenever the request is HTTPS (always true in prod); relaxed on plain-HTTP localhost
            // dev so the cookie is still delivered there.
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = Path,
            MaxAge = TimeSpan.FromMinutes(10),
        });

    public static string? Read(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(Name, out var value) ? value : null;

    public static void Clear(HttpContext ctx) =>
        ctx.Response.Cookies.Append(Name, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = Path,
            Expires = DateTimeOffset.UnixEpoch,
        });
}

/// <summary>Minimal GitHub API repo shape for GET /api/github/repos deserialization.</summary>
file sealed class GitHubApiRepo
{
    [System.Text.Json.Serialization.JsonPropertyName("full_name")]
    public string? FullName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("private")]
    public bool Private { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }
}

/// <summary>Minimal GitHub API user shape for GET /api/github/accounts deserialization.</summary>
file sealed class GitHubApiUser
{
    [System.Text.Json.Serialization.JsonPropertyName("login")]
    public string? Login { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

/// <summary>Minimal GitHub API org shape for GET /api/github/accounts deserialization.</summary>
file sealed class GitHubApiOrg
{
    [System.Text.Json.Serialization.JsonPropertyName("login")]
    public string? Login { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

/// <summary>Account entry returned by GET /api/github/accounts.</summary>
file sealed record GitHubAccountResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("login")]   string Login,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")]    string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("avatar_url")] string AvatarUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("type")]    string Type
);

/// <summary>Request body for POST /api/auth/session/exchange (F5 one-time code redemption).</summary>
file sealed record SessionExchangeRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string? Code
);

/// <summary>Success response for POST /api/auth/session/exchange.</summary>
file sealed record SessionExchangeResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_token")] string SessionToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("login")]         string Login
);
