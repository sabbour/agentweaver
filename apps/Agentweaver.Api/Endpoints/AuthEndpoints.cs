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
// GET /auth/github/authorize — begin OAuth redirect flow
app.MapGet("/auth/github/authorize", (GitHubOAuthRedirectService oauthService) =>
{
    try
    {
        var url = oauthService.BeginAuthorization();
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
    string? code,
    string? state,
    string? error,
    GitHubOAuthRedirectService oauthService,
    Agentweaver.Api.Auth.OAuth.McpOAuthBrokerService oauthBroker,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    var frontendUrl = (configuration["Auth:GitHub:FrontendUrl"] ?? "http://localhost:8080").TrimEnd('/');

    // MCP OAuth broker leg: correlate by the GitHub CSRF state.
    if (!string.IsNullOrWhiteSpace(state) && oauthBroker.IsPendingState(state))
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

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(error)}");

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    try
    {
        var (login, accessToken) = await oauthService.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
        return Results.Redirect(
            $"{frontendUrl}/?auth=success&session_token={Uri.EscapeDataString(accessToken)}&login={Uri.EscapeDataString(login)}");
    }
    catch (Exception ex)
    {
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(ex.Message)}");
    }
}).AllowAnonymous();

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
    });
});

// GET /api/github/repos — list authenticated user's GitHub repositories
app.MapGet("/api/github/repos", async (
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
        var repos = new List<GitHubRepoResponse>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/user/repos?sort=pushed&per_page={perPage}&page={page}&affiliation=owner");
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
