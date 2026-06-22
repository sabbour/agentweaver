using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for the 403 ownership bug that the CapturedBy=GitHub-login change introduced:
/// a backlog-pickup coordinator run stores its accountable human (the captured GitHub login, e.g.
/// "sabbour") as <c>Run.SubmittingUser</c>, but ownership was checked against the API-key principal
/// (Auth:User, e.g. "projects-test-user"). The two never matched, so every owner-scoped endpoint
/// (GET /api/runs/{id}, /outcome-spec, /work-plan, /graph, /children, assembly/*) 403'd and the
/// Orchestration page showed "API error 403".
///
/// The fix makes ownership GitHub-identity aware: the per-request caller is enriched in
/// <c>ApiKeyAuthMiddleware</c> with the signed-in GitHub login (resolved from the LOCAL token store,
/// never the network), and <c>CallerContext.Owns</c> matches a run whose SubmittingUser equals EITHER
/// the API-key principal OR that GitHub login. Interactive runs (SubmittingUser = the API principal)
/// keep resolving via the principal branch.
///
/// These tests run against the real in-process host (<see cref="ProjectsWebApplicationFactory"/>) with
/// the sanctioned in-memory <see cref="Agentweaver.Api.Auth.InMemoryGitHubTokenStore"/> (a real
/// component, not a mock — Principle VII). A pickup-shaped run is inserted directly via the real
/// <see cref="SqliteRunStore"/> so the test stays fully hermetic (no orchestration, no live model, no
/// network) while still exercising the exact ownership path the bug lived on.
/// </summary>
public sealed class PickupRunOwnershipTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private const string GitHubLogin = "sabbour";

    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PickupRunOwnershipTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task SignedInGitHubUser_CanView_PickupRun_AttributedToThatLogin()
    {
        // Sign the caller's installation scope in as the GitHub login that captured the task.
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        // A pickup coordinator run is accountable to the captured GitHub login, NOT the API principal.
        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the signed-in GitHub user owns a pickup run attributed to their login; it must not 403");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("failed");
    }

    [Fact]
    public async Task SignedInGitHubUser_StreamEndpoint_IsViewable_NotHidden()
    {
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        // The /stream owner check returns 404 (not 403) to hide run existence on mismatch. With the
        // identity-aware fix the signed-in owner is recognized, so the stream must NOT 404 for them.
        var resp = await _client.GetAsync($"/api/runs/{runId}/stream",
            HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the identity-aware owner check must let the signed-in GitHub user stream their pickup run");
    }

    [Fact]
    public async Task SignedInGitHubUser_CannotView_RunOwnedByDifferentUser()
    {
        // Signed in as "sabbour", but the run is attributed to someone else entirely — neither the
        // API principal nor the signed-in GitHub login owns it.
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        var runId = await InsertPickupRunAsync(submittingUser: "a-different-github-login");

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a run owned by neither the API principal nor the signed-in GitHub login must stay 403");
    }

    [Fact]
    public async Task InteractiveRun_OwnedByApiPrincipal_StillResolves_WhenSignedOut()
    {
        // No GitHub identity: ownership must still work via the API-key principal (interactive parity).
        await _factory.TokenStore.SignOutAsync(GitHubTokenScope.Installation);

        var runId = await InsertPickupRunAsync(
            submittingUser: ProjectsWebApplicationFactory.TestUser, origin: RunOrigin.Interactive);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "interactive runs stored under the API principal must keep resolving regardless of GitHub state");
    }

    [Fact]
    public async Task SignedOut_PickupRunAttributedToGitHubLogin_Stays403()
    {
        // With no signed-in identity the caller's GitHubLogin is null, so a run attributed to a GitHub
        // login matches neither identity — the pre-fix-correct deny is preserved (no over-broad access).
        await _factory.TokenStore.SignOutAsync(GitHubTokenScope.Installation);

        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "without a signed-in GitHub identity a run attributed to a GitHub login is not owned");
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        using var anon = _factory.CreateClient();   // no bearer token
        var resp = await anon.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the API-key gate rejects unauthenticated callers before any ownership logic");
    }

    /// <summary>
    /// Inserts a coordinator pickup-shaped run directly via the real run store and returns its run_id.
    /// Status is Failed (terminal) so no background service or orchestration runs — the test stays
    /// hermetic while faithfully reproducing a picked-up run's identity shape (Origin=BacklogPickup,
    /// SubmittingUser = the captured GitHub login, AgentName="Coordinator", WorkflowRunId=null).
    /// </summary>
    private async Task<string> InsertPickupRunAsync(string submittingUser, RunOrigin origin = RunOrigin.BacklogPickup)
    {
        var projectId = await CreateProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();

        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "pickup ownership regression",
            SubmittingUser    = submittingUser,
            Status            = RunStatus.Failed,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = ProjectId.Parse(projectId),
            ModelId           = "gpt-4o",
            AgentName         = "Coordinator",
            WorkflowRunId     = null,
            Origin            = origin,
        };
        await runStore.InsertAsync(run);
        return run.Id.ToString();
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Pickup Owner Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }
}
