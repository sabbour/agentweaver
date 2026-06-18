using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Scaffolder.AgentRuntime;
using Scaffolder.Api.Memory;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Auth;
using Scaffolder.Api.Casting;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Coordinator;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Projects;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Security;
using Scaffolder.Domain;
using Scaffolder.Squad.Catalog;
using Scaffolder.Squad.Model;
using Scaffolder.Squad.Squad;
using Scaffolder.Squad.Analysis;
using Scaffolder.Squad.Sync;

namespace Scaffolder.Api.Endpoints;

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

// GET /auth/github/callback — receive OAuth code from GitHub, exchange for token
app.MapGet("/auth/github/callback", async (
    string? code,
    string? state,
    string? error,
    GitHubOAuthRedirectService oauthService,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    var frontendUrl = configuration["Auth:GitHub:FrontendUrl"] ?? "http://localhost:8080";

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(error)}");

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

    try
    {
        await oauthService.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
        return Results.Redirect($"{frontendUrl}/?auth=success");
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
            request.Headers.UserAgent.ParseAdd("Scaffolder/1.0");
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
