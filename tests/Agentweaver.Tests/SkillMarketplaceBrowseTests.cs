using System.Net;
using System.Text;
using Agentweaver.Api.Git;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Regression coverage for the "Browse curated marketplaces freezes when selecting a source" bug.
/// The browse path used to do a full (untimed) LibGit2Sharp clone of large marketplace repos, which
/// took ~100s and made the dialog appear frozen. The fix fetches only the marketplace subtree via
/// <see cref="GitHubSkillTreeClient"/> and bounds the whole operation, so any failure/timeout surfaces
/// as a clean error instead of an infinite spin.
/// </summary>
public sealed class SkillMarketplaceBrowseTests
{
    // ── GitHubSkillTreeClient: subtree-only fetch replacing the full clone ─────────────

    [Fact]
    public async Task ListSubtreeBlobsAsync_returns_only_blobs_under_the_subpath()
    {
        const string treeJson = """
        {"tree":[
          {"path":"README.md","type":"blob","size":10},
          {"path":"skills","type":"tree","size":0},
          {"path":"skills/pr-review/SKILL.md","type":"blob","size":20},
          {"path":"skills/pr-review/reference.md","type":"blob","size":30},
          {"path":"docs/guide.md","type":"blob","size":40}
        ],"truncated":false}
        """;
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(req =>
        {
            req.RequestUri!.Host.Should().Be("api.github.com");
            req.RequestUri.AbsoluteUri.Should().Contain("/git/trees/main?recursive=1");
            return Json(treeJson);
        })));

        var blobs = await client.ListSubtreeBlobsAsync("acme", "repo", "main", "skills", token: null, CancellationToken.None);

        blobs.Select(b => b.Path).Should().BeEquivalentTo(
            "skills/pr-review/SKILL.md", "skills/pr-review/reference.md");
    }

    [Fact]
    public async Task GetRawTextAsync_returns_text_for_a_small_utf8_blob()
    {
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(req =>
        {
            req.RequestUri!.Host.Should().Be("raw.githubusercontent.com");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# skill", Encoding.UTF8) };
        })));

        var text = await client.GetRawTextAsync("acme", "repo", "main", "skills/pr-review/SKILL.md", token: null, maxBytes: 1024, CancellationToken.None);

        text.Should().Be("# skill");
    }

    [Fact]
    public async Task GetRawTextAsync_skips_binary_blobs()
    {
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 0, 3 }) })));

        var text = await client.GetRawTextAsync("acme", "repo", "main", "skills/logo.png", token: null, maxBytes: 1024, CancellationToken.None);

        text.Should().BeNull();
    }

    [Fact]
    public async Task GetRawTextAsync_skips_oversized_blobs()
    {
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("way too large", Encoding.UTF8) })));

        var text = await client.GetRawTextAsync("acme", "repo", "main", "skills/big.md", token: null, maxBytes: 4, CancellationToken.None);

        text.Should().BeNull();
    }

    [Fact]
    public async Task GetRawTextAsync_returns_null_on_non_success()
    {
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound))));

        var text = await client.GetRawTextAsync("acme", "repo", "main", "skills/missing.md", token: null, maxBytes: 1024, CancellationToken.None);

        text.Should().BeNull();
    }

    // ── SkillCatalogService.BrowseMarketplaceAsync: never hangs, always surfaces errors ─

    [Fact]
    public async Task BrowseMarketplaceAsync_surfaces_timeout_instead_of_freezing()
    {
        // A fetch that is cancelled (an HttpClient timeout surfaces as TaskCanceledException) must
        // return a clear timeout error, not spin forever — the core of the browse-freeze fix.
        var svc = CreateService(new ThrowingTreeClient(() => throw new TaskCanceledException()));

        var (outcome, error, candidates) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceTimeoutMessage);
        candidates.Should().BeNull();
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_surfaces_unavailable_on_transport_failure()
    {
        var svc = CreateService(new ThrowingTreeClient(() => throw new HttpRequestException("boom")));

        var (outcome, error, candidates) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceUnavailableMessage);
        candidates.Should().BeNull();
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_reports_unavailable_when_no_tree_client_is_configured()
    {
        var svc = CreateService(treeClient: null);

        var (outcome, error, _) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceUnavailableMessage);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────

    private static readonly Project ProjectRef = new()
    {
        Id = ProjectId.New(),
        Name = "browse-test",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), "aw-browse-test"),
        DefaultBranch = "main",
        Owner = "owner-1",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly CallerContext Caller = new() { User = "owner-1" };

    private static SkillCatalogService CreateService(IGitHubSkillTreeClient? treeClient) => new(
        new UnusedSkillStore(),
        new SingleProjectStore(ProjectRef),
        new ProjectGitInitializer(NullLogger<ProjectGitInitializer>.Instance),
        new SkillParser(),
        new InstallationScopeProvider(),
        new SignedOutTokenStore(),
        NullLogger<SkillCatalogService>.Instance,
        accessTokenProvider: null,
        treeClient: treeClient);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ThrowingTreeClient(Func<IReadOnlyList<GitHubTreeBlob>> onList) : IGitHubSkillTreeClient
    {
        public Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
            string owner, string repo, string branch, string subpath, string? token, CancellationToken ct) =>
            Task.FromResult(onList());

        public Task<string?> GetRawTextAsync(
            string owner, string repo, string branch, string path, string? token, long maxBytes, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class SingleProjectStore(Project project) : IProjectStore
    {
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult<Project?>(id == project.Id ? project : null);

        public Task InsertAsync(Project p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateGenerationModelSettingsAsync(ProjectId id, string? b, string? w, string? o, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InstallationScopeProvider : IGitHubTokenScopeProvider
    {
        public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;
    }

    private sealed class SignedOutTokenStore : IGitHubTokenStore
    {
        public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null));
        public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) => Task.FromResult<GitHubToken?>(null);
        public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) => Task.FromResult<GitHubIdentity?>(null);
        public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedSkillStore : ISkillStore
    {
        public Task InsertAsync(Skill skill, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Skill skill, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Skill?> GetAsync(ProjectId projectId, SkillId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Skill?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Skill>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId projectId, SkillId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignAsync(ProjectId projectId, SkillId skillId, string agentName, DateTimeOffset createdAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UnassignAsync(ProjectId projectId, SkillId skillId, string agentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SkillAssignment>> ListAssignmentsByProjectAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Skill>> ListActiveSkillsForAgentAsync(ProjectId projectId, string agentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SkillDefaultsStoreApplyResult> ApplyDefaultsAsync(SkillDefaultsStorePlan plan, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
