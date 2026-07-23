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

        var (outcome, error, page) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceTimeoutMessage);
        page.Should().BeNull();
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_surfaces_unavailable_on_transport_failure()
    {
        var svc = CreateService(new ThrowingTreeClient(() => throw new HttpRequestException("boom")));

        var (outcome, error, page) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceUnavailableMessage);
        page.Should().BeNull();
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_reports_unavailable_when_no_tree_client_is_configured()
    {
        var svc = CreateService(treeClient: null);

        var (outcome, error, _) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "acme", "repo", "main", "skills", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.SourceUnavailable);
        error.Should().Be(SkillCatalogService.MarketplaceUnavailableMessage);
    }

    // ── GitHubSkillTreeClient: anonymous fallback for public repos on 401/403 ───────────

    [Fact]
    public async Task ListSubtreeBlobsAsync_retries_anonymously_when_the_token_is_rejected()
    {
        // microsoft/skills is public but lives in a SAML-enforced org: an un-SSO'd OAuth token gets
        // 403 on the Trees API even though anonymous reads return 200. The client must fall back.
        var attempts = 0;
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(req =>
        {
            attempts++;
            return req.Headers.Authorization is not null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json("""{"tree":[{"path":"skills/pr-review/SKILL.md","type":"blob","size":20}],"truncated":false}""");
        })));

        var blobs = await client.ListSubtreeBlobsAsync("microsoft", "skills", "main", "skills", token: "un-sso'd-token", CancellationToken.None);

        attempts.Should().Be(2);
        blobs.Select(b => b.Path).Should().ContainSingle().Which.Should().Be("skills/pr-review/SKILL.md");
    }

    [Fact]
    public async Task GetRawTextAsync_retries_anonymously_when_the_token_is_rejected()
    {
        var attempts = 0;
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(req =>
        {
            attempts++;
            return req.Headers.Authorization is not null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# skill", Encoding.UTF8) };
        })));

        var text = await client.GetRawTextAsync("microsoft", "skills", "main", "skills/pr-review/SKILL.md", token: "un-sso'd-token", maxBytes: 1024, CancellationToken.None);

        attempts.Should().Be(2);
        text.Should().Be("# skill");
    }

    [Fact]
    public async Task GetRawTextAsync_does_not_retry_when_already_anonymous()
    {
        var attempts = 0;
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        })));

        var text = await client.GetRawTextAsync("acme", "repo", "main", "skills/x/SKILL.md", token: null, maxBytes: 1024, CancellationToken.None);

        attempts.Should().Be(1);
        text.Should().BeNull();
    }

    [Fact]
    public async Task GetRawTextAsync_retries_anonymously_on_a_non_auth_failure()
    {
        // raw.githubusercontent.com does NOT always answer an un-SSO'd token with 401/403 — for a repo
        // in a SAML-enforced org it can return 404 for the token yet 200 anonymously. The fallback must
        // therefore trigger on ANY non-success status, not only 401/403, or browse descriptions come
        // back empty for microsoft/skills even though the anonymous read would succeed.
        var attempts = 0;
        var client = new GitHubSkillTreeClient(new StubHttpClientFactory(new StubHandler(req =>
        {
            attempts++;
            return req.Headers.Authorization is not null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# skill", Encoding.UTF8) };
        })));

        var text = await client.GetRawTextAsync("microsoft", "skills", "main", "skills/x/SKILL.md", token: "un-sso'd-token", maxBytes: 1024, CancellationToken.None);

        attempts.Should().Be(2);
        text.Should().Be("# skill");
    }

    // ── Browse builds a PAGINATED index (name + short definition) without bulk-downloading ──

    [Fact]
    public async Task BrowseMarketplaceAsync_builds_index_from_skill_manifests_without_downloading_resources()
    {
        // Product contract: browse is a lightweight paginated index — each candidate carries a name +
        // short definition read from SKILL.md frontmatter, but the skill's OTHER resource files are
        // NEVER downloaded at browse time (only at import).
        var blobs = new List<GitHubTreeBlob>
        {
            new("skills/pr-review/SKILL.md", 40),
            new("skills/pr-review/reference.md", 100),
            new("skills/pr-review/diagram.bin", 100),
            new("skills/deploy/SKILL.md", 40),
            new("skills/deploy/runbook.md", 100),
        };
        var tree = new RecordingTreeClient(blobs, path => path.EndsWith("/SKILL.md", StringComparison.Ordinal)
            ? $"---\nname: {Path.GetFileName(Path.GetDirectoryName(path))}\ndescription: A short definition for {Path.GetFileName(Path.GetDirectoryName(path))}.\n---\nDo the thing thoroughly and safely."
            : throw new InvalidOperationException($"browse must not download resource blob {path}"));
        var svc = CreateService(tree);

        var (outcome, error, page) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.Ok);
        error.Should().BeNull();
        page!.Total.Should().Be(2);
        page.HasMore.Should().BeFalse();
        page.Candidates.Select(c => c.Location).Should().BeEquivalentTo("skills/pr-review", "skills/deploy");
        // Each candidate carries a short definition parsed from its SKILL.md frontmatter.
        page.Candidates.Should().Contain(c => c.Location == "skills/pr-review"
            && c.Name == "pr-review"
            && c.Description == "A short definition for pr-review.");
        page.Candidates.Should().OnlyContain(c => c.Description != null && c.Description.Length > 0);
        // ONLY the two SKILL.md manifests were fetched — never the reference/runbook/binary resources.
        tree.RawRequests.Should().BeEquivalentTo("skills/pr-review/SKILL.md", "skills/deploy/SKILL.md");
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_page_1_returns_only_pageSize_items_and_fetches_only_their_descriptions()
    {
        // Pagination is what keeps browse fast for a huge marketplace: page 1 must return exactly
        // pageSize candidates (each fully hydrated) and download SKILL.md for ONLY those candidates —
        // never for the off-page ones. This is the 386-skill / 30-47s regression guard.
        var svc = CreateService(new RecordingTreeClient(SkillBlobs(6), SkillFrontmatter));

        var (outcome, _, page) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: null, page: 1, pageSize: 2, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.Ok);
        page!.Total.Should().Be(6);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(2);
        page.HasMore.Should().BeTrue();
        page.Candidates.Select(c => c.Location).Should().Equal("skills/skill-00", "skills/skill-01");
        page.Candidates.Should().OnlyContain(c => c.Description != null && c.Description.Length > 0);
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_does_not_fetch_descriptions_for_off_page_candidates()
    {
        var tree = new RecordingTreeClient(SkillBlobs(6), SkillFrontmatter);
        var svc = CreateService(tree);

        _ = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: null, page: 1, pageSize: 2, Caller, CancellationToken.None);

        // Only the two on-page SKILL.md manifests were downloaded, not the remaining four.
        tree.RawRequests.Should().BeEquivalentTo("skills/skill-00/SKILL.md", "skills/skill-01/SKILL.md");
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_page_2_returns_the_next_distinct_offset()
    {
        var svc = CreateService(new RecordingTreeClient(SkillBlobs(5), SkillFrontmatter));

        var (_, _, page2) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: null, page: 2, pageSize: 2, Caller, CancellationToken.None);
        var (_, _, page3) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: null, page: 3, pageSize: 2, Caller, CancellationToken.None);

        page2!.Candidates.Select(c => c.Location).Should().Equal("skills/skill-02", "skills/skill-03");
        page2.HasMore.Should().BeTrue();
        // Last page: one item, no more.
        page3!.Candidates.Select(c => c.Location).Should().Equal("skills/skill-04");
        page3.Total.Should().Be(5);
        page3.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task BrowseMarketplaceAsync_filters_by_query_before_paginating()
    {
        var blobs = new List<GitHubTreeBlob>
        {
            new("skills/azure-openai/SKILL.md", 40),
            new("skills/azure-storage/SKILL.md", 40),
            new("skills/github-actions/SKILL.md", 40),
        };
        var svc = CreateService(new RecordingTreeClient(blobs, SkillFrontmatter));

        var (_, _, page) = await svc.BrowseMarketplaceAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", "skills", query: "azure", page: 1, pageSize: 25, Caller, CancellationToken.None);

        page!.Total.Should().Be(2);
        page.Candidates.Select(c => c.Location).Should().BeEquivalentTo("skills/azure-openai", "skills/azure-storage");
    }

    private static IReadOnlyList<GitHubTreeBlob> SkillBlobs(int count) =>
        Enumerable.Range(0, count).Select(i => new GitHubTreeBlob($"skills/skill-{i:D2}/SKILL.md", 40)).ToList();

    private static string SkillFrontmatter(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path));
        return $"---\nname: {name}\ndescription: A short definition for {name}.\n---\nInstructions.";
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
    {        public Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
            string owner, string repo, string branch, string subpath, string? token, CancellationToken ct) =>
            Task.FromResult(onList());

        public Task<string?> GetRawTextAsync(
            string owner, string repo, string branch, string path, string? token, long maxBytes, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Tree client backed by a fixed blob list that records every raw-blob path actually downloaded,
    /// so tests can assert browse pulls only SKILL.md manifests. Mirrors the real client's subpath
    /// filtering on the tree listing.
    /// </summary>
    private sealed class RecordingTreeClient(IReadOnlyList<GitHubTreeBlob> blobs, Func<string, string?> content) : IGitHubSkillTreeClient
    {
        private readonly List<string> _rawRequests = new();
        public IReadOnlyList<string> RawRequests => _rawRequests;

        public Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
            string owner, string repo, string branch, string subpath, string? token, CancellationToken ct)
        {
            var normalized = subpath.Trim('/');
            var prefix = normalized.Length == 0 ? string.Empty : normalized + "/";
            IReadOnlyList<GitHubTreeBlob> scoped = blobs
                .Where(b => normalized.Length == 0 || b.Path == normalized || b.Path.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(scoped);
        }

        public Task<string?> GetRawTextAsync(
            string owner, string repo, string branch, string path, string? token, long maxBytes, CancellationToken ct)
        {
            lock (_rawRequests) _rawRequests.Add(path);
            return Task.FromResult(content(path));
        }
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
