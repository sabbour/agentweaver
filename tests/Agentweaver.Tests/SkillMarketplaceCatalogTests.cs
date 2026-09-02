using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Git;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Coverage for step-1b LLM-driven marketplace catalog parsing: the SKILL.md heuristic indexer, its
/// bounded fail-closed LLM classifier fallback, the auto-detected (URL-source) browse path, project
/// marketplace-source persistence, and URL parsing. These complement <see cref="SkillMarketplaceBrowseTests"/>
/// (config-source browse) and must not regress the anonymous-first / page-lazy speed guarantees.
/// </summary>
public sealed class SkillMarketplaceCatalogTests
{
    // ── Indexer: heuristic SKILL.md derivation (flat + nested, zero blob downloads) ─────

    [Fact]
    public async Task Indexer_heuristic_derives_entries_from_flat_and_nested_skillmd_layouts()
    {
        var blobs = new List<GitHubTreeBlob>
        {
            new("README.md", 10),
            new("skills/pr-review/SKILL.md", 40),
            new("skills/pr-review/reference.md", 100),
            new(".github/plugins/azure/skills/openai/SKILL.md", 40),
            new(".github/plugins/azure/skills/storage/SKILL.md", 40),
        };
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache());

        var index = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, capabilityRunId: null, parseStrategy: null, CancellationToken.None);

        index.Strategy.Should().Be("skillmd");
        index.Entries.Select(e => e.Location).Should().BeEquivalentTo(
            "skills/pr-review",
            ".github/plugins/azure/skills/openai",
            ".github/plugins/azure/skills/storage");
        index.Entries.Should().Contain(e => e.Location == ".github/plugins/azure/skills/openai" && e.Name == "openai");
        // Heuristic never fills descriptions (browse hydrates per page); all null here.
        index.Entries.Should().OnlyContain(e => e.Description == null);
    }

    [Fact]
    public async Task Indexer_caches_by_tree_fingerprint_and_reuses_the_same_index()
    {
        var blobs = new List<GitHubTreeBlob> { new("skills/a/SKILL.md", 40) };
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache());

        var first = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, null, null, CancellationToken.None);
        var second = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, null, null, CancellationToken.None);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Fingerprint_changes_when_tree_content_changes()
    {
        var a = new List<GitHubTreeBlob> { new("skills/a/SKILL.md", 40) };
        var b = new List<GitHubTreeBlob> { new("skills/a/SKILL.md", 41) };
        var c = new List<GitHubTreeBlob> { new("skills/a/SKILL.md", 40), new("skills/b/SKILL.md", 40) };

        var fa = MarketplaceCatalogIndexer.ComputeFingerprint(a);
        MarketplaceCatalogIndexer.ComputeFingerprint(a).Should().Be(fa, "fingerprint is stable for identical trees");
        MarketplaceCatalogIndexer.ComputeFingerprint(b).Should().NotBe(fa, "a size change invalidates the cache");
        MarketplaceCatalogIndexer.ComputeFingerprint(c).Should().NotBe(fa, "an added blob invalidates the cache");
    }

    [Fact]
    public async Task Indexer_without_explicit_capability_skips_llm_and_requires_a_github_connection()
    {
        // No SKILL.md and no explicit capability means the classifier cannot run. A caller identity
        // is not a Copilot capability, so the indexer must return a connect-GitHub requirement instead
        // of silently treating the source as an empty catalog.
        var blobs = new List<GitHubTreeBlob>
        {
            new("catalog/plans/plan-a/plan.md", 40),
            new("catalog/plans/plan-a/notes.md", 100),
        };
        var classifier = new FakeClassifier(new[]
        {
            new MarketplaceCatalogEntry("catalog/plans/plan-a", "plan-a", "A planning skill."),
            new MarketplaceCatalogEntry("catalog/plans/ghost", "ghost", "Does not exist."),
        });
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        var index = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, capabilityRunId: null, parseStrategy: null, CancellationToken.None);

        classifier.Invocations.Should().Be(0);
        index.Strategy.Should().Be("capability-required");
        index.RequiresGitHubConnection.Should().BeTrue();
        index.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Indexer_uses_llm_only_when_an_explicit_capability_is_supplied()
    {
        // When a proposed location DOES contain a SKILL.md in the tree it is importable and kept (with
        // the classifier's description); a hallucinated location without a SKILL.md is dropped. This
        // exercises the validated "keep" branch. (Forced via parseStrategy=llm to bypass the heuristic.)
        var blobs = new List<GitHubTreeBlob>
        {
            new("catalog/plan-a/SKILL.md", 40),
            new("catalog/plan-a/notes.md", 100),
        };
        var classifier = new FakeClassifier(new[]
        {
            new MarketplaceCatalogEntry("catalog/plan-a", "plan-a", "A planning skill."),
            new MarketplaceCatalogEntry("catalog/ghost", "ghost", "Does not exist."),
        });
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        var index = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, capabilityRunId: "run-marketplace", parseStrategy: "llm", CancellationToken.None);

        classifier.Invocations.Should().Be(1);
        classifier.CapabilityRunIds.Should().Equal("run-marketplace");
        index.Strategy.Should().Be("llm");
        index.Entries.Select(e => e.Location).Should().Equal("catalog/plan-a");
        index.Entries[0].Description.Should().Be("A planning skill.");
    }

    [Fact]
    public async Task Indexer_requires_a_github_connection_when_the_explicit_capability_cannot_be_redeemed()
    {
        var blobs = new List<GitHubTreeBlob> { new("catalog/plan-a/SKILL.md", 40) };
        var indexer = new MarketplaceCatalogIndexer(
            new MarketplaceCatalogCache(),
            new UnavailableCapabilityClassifier());

        var index = await indexer.GetOrBuildAsync(
            "acme", "repo", "main", blobs, capabilityRunId: "expired-run", parseStrategy: "llm", CancellationToken.None);

        index.Strategy.Should().Be("capability-required");
        index.RequiresGitHubConnection.Should().BeTrue();
        index.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Indexer_does_not_call_llm_when_heuristic_finds_skillmd()
    {
        var blobs = new List<GitHubTreeBlob> { new("skills/a/SKILL.md", 40) };
        var classifier = new FakeClassifier(Array.Empty<MarketplaceCatalogEntry>());
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        var index = await indexer.GetOrBuildAsync("acme", "repo", "main", blobs, "run-marketplace", null, CancellationToken.None);

        classifier.Invocations.Should().Be(0);
        index.Strategy.Should().Be("skillmd");
    }

    [Fact]
    public async Task Indexer_issues_a_capability_only_for_an_uncached_llm_classification()
    {
        var classifier = new FakeClassifier(
            [new MarketplaceCatalogEntry("skills/a", "a", "A skill.")]);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);
        var issues = 0;
        Task<string?> IssueAsync(CancellationToken _)
        {
            issues++;
            return Task.FromResult<string?>("capability-reference");
        }

        var heuristic = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "auto", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller, issueCapabilityAsync: IssueAsync,
            hasCapabilityAsync: _ => Task.FromResult(true));

        heuristic.Strategy.Should().Be("skillmd");
        issues.Should().Be(0, "deterministic browsing must not create a durable capability");

        var classified = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "classified-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller, issueCapabilityAsync: IssueAsync,
            hasCapabilityAsync: _ => Task.FromResult(true));
        var cached = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "classified-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller, issueCapabilityAsync: IssueAsync,
            hasCapabilityAsync: _ => Task.FromResult(true));

        classified.Strategy.Should().Be("llm");
        cached.Should().BeSameAs(classified);
        classifier.Invocations.Should().Be(1);
        issues.Should().Be(1, "the one model call receives the only issued capability; cache hits issue none");
    }

    [Fact]
    public async Task Indexer_does_not_issue_a_capability_for_an_empty_tree()
    {
        var indexer = new MarketplaceCatalogIndexer(
            new MarketplaceCatalogCache(), new FakeClassifier(Array.Empty<MarketplaceCatalogEntry>()));

        var index = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "empty-repo", "main", Array.Empty<GitHubTreeBlob>(),
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller,
            issueCapabilityAsync: _ => throw new InvalidOperationException("an empty tree has no model call"));

        index.RequiresGitHubConnection.Should().BeFalse();
        index.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Indexer_reuses_an_llm_cache_for_its_legacy_explicit_capability_path()
    {
        var classifier = new FakeClassifier(
            [new MarketplaceCatalogEntry("skills/a", "a", "A skill.")]);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);
        var blobs = new[] { new GitHubTreeBlob("skills/a/SKILL.md", 40) };

        var first = await indexer.GetOrBuildForProjectAsync(
            "acme", "legacy-repo", "main", blobs, "legacy-capability", "llm", CancellationToken.None,
            ProjectRef.Id, Caller);
        var cached = await indexer.GetOrBuildForProjectAsync(
            "acme", "legacy-repo", "main", blobs, "legacy-capability", "llm", CancellationToken.None,
            ProjectRef.Id, Caller);

        cached.Should().BeSameAs(first);
        classifier.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Indexer_requires_connection_for_an_llm_cache_hit_without_an_active_binding()
    {
        var classifier = new FakeClassifier(
            [new MarketplaceCatalogEntry("skills/a", "a", "A skill.")]);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        _ = await indexer.GetOrBuildForProjectAsync(
            "acme", "repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: "capability-reference", parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller);
        var automaticBrowse = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "auto", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller);
        var cachedForDisconnectedCaller = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: "unvalidated-capability-reference", parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller,
            issueCapabilityAsync: _ => throw new InvalidOperationException("cache hits must not issue"),
            hasCapabilityAsync: _ => Task.FromResult(false));

        automaticBrowse.Strategy.Should().Be("skillmd");
        automaticBrowse.RequiresGitHubConnection.Should().BeFalse();
        cachedForDisconnectedCaller.RequiresGitHubConnection.Should().BeTrue();
        cachedForDisconnectedCaller.Entries.Should().BeEmpty();
        classifier.Invocations.Should().Be(1, "the cached catalog must not dispatch another model turn");
    }

    [Fact]
    public async Task BrowseAuto_requires_a_github_connection_when_classification_has_no_explicit_capability()
    {
        var classifier = new FakeClassifier(Array.Empty<MarketplaceCatalogEntry>());
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);
        var svc = CreateService(
            new RecordingTreeClient([new GitHubTreeBlob("catalog/plan.md", 40)], _ => null),
            indexer);

        var (outcome, error, page) = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "marketplace", "main", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.GitHubConnectionRequired);
        error.Should().Be(ModelProviderConnectionRequirement.RequirementMessage);
        page.Should().BeNull();
        classifier.Invocations.Should().Be(0, "no capability must never dispatch a model turn");
    }

    // ── Classifier: JSON parsing + fail-closed ─────────────────────────────────────────

    [Fact]
    public void Classifier_parses_skills_json_object()
    {
        var raw = "here you go:\n{\"skills\":[{\"location\":\"skills/a\",\"name\":\"a\",\"description\":\"Alpha skill.\"}]}\nthanks";
        var parsed = CopilotMarketplaceCatalogClassifier.ParseResult(raw);

        parsed.Should().NotBeNull();
        parsed!.Should().HaveCount(1);
        parsed[0].Location.Should().Be("skills/a");
        parsed[0].Description.Should().Be("Alpha skill.");
    }

    [Fact]
    public void Classifier_empty_skills_array_parses_to_empty_list_not_null()
    {
        CopilotMarketplaceCatalogClassifier.ParseResult("{\"skills\":[]}").Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"other\":1}")]
    public void Classifier_fails_closed_on_unparseable_or_missing_skills(string raw)
    {
        // Missing/garbage responses must yield null (fail-closed) so the indexer falls back to empty,
        // never a hard error.
        CopilotMarketplaceCatalogClassifier.ParseResult(raw).Should().BeNull();
    }

    [Fact]
    public void Classifier_caps_long_descriptions()
    {
        var longDesc = new string('x', 500);
        var raw = "{\"skills\":[{\"location\":\"skills/a\",\"name\":\"a\",\"description\":\"" + longDesc + "\"}]}";
        var parsed = CopilotMarketplaceCatalogClassifier.ParseResult(raw);
        parsed![0].Description!.Length.Should().Be(CopilotMarketplaceCatalogClassifier.MaxDescriptionLength);
    }

    // ── Auto-detected browse (URL source): pagination + no bulk download ────────────────

    [Fact]
    public async Task BrowseAuto_paginates_and_fetches_descriptions_only_for_the_page()
    {
        var tree = new RecordingTreeClient(SkillBlobs(6), SkillFrontmatter);
        var svc = CreateService(tree);

        var (outcome, _, page) = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", query: null, page: 1, pageSize: 2, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.Ok);
        page!.Total.Should().Be(6);
        page.HasMore.Should().BeTrue();
        page.Candidates.Select(c => c.Location).Should().Equal("skills/skill-00", "skills/skill-01");
        page.Candidates.Should().OnlyContain(c => c.Description != null && c.Description!.Length > 0);
        // Only the two on-page SKILL.md manifests were downloaded — never resources, never off-page items.
        tree.RawRequests.Should().BeEquivalentTo("skills/skill-00/SKILL.md", "skills/skill-01/SKILL.md");
    }

    [Fact]
    public async Task BrowseAuto_page_2_returns_the_next_distinct_offset()
    {
        var svc = CreateService(new RecordingTreeClient(SkillBlobs(5), SkillFrontmatter));

        var (_, _, page2) = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", query: null, page: 2, pageSize: 2, Caller, CancellationToken.None);

        page2!.Candidates.Select(c => c.Location).Should().Equal("skills/skill-02", "skills/skill-03");
        page2.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task BrowseAuto_never_downloads_resource_blobs()
    {
        var blobs = new List<GitHubTreeBlob>
        {
            new("skills/pr-review/SKILL.md", 40),
            new("skills/pr-review/reference.md", 100),
            new("skills/pr-review/diagram.bin", 100),
        };
        var tree = new RecordingTreeClient(blobs, path => path.EndsWith("/SKILL.md", StringComparison.Ordinal)
            ? SkillFrontmatter(path)
            : throw new InvalidOperationException($"auto-browse must not download resource blob {path}"));
        var svc = CreateService(tree);

        var (outcome, _, page) = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        outcome.Should().Be(SkillOutcome.Ok);
        page!.Candidates.Select(c => c.Location).Should().Equal("skills/pr-review");
        tree.RawRequests.Should().Equal("skills/pr-review/SKILL.md");
    }

    [Fact]
    public async Task BrowseAuto_uses_anonymous_requests_for_tree_and_descriptions()
    {
        var tree = new RecordingTreeClient(SkillBlobs(3), SkillFrontmatter);
        var svc = CreateService(tree);

        _ = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", query: null, page: 1, pageSize: 25, Caller, CancellationToken.None);

        tree.TokensSeen.Should().NotBeEmpty();
        tree.TokensSeen.Should().OnlyContain(t => t == null);
    }

    [Fact]
    public async Task BrowseAuto_filters_by_query_before_paginating()
    {
        var blobs = new List<GitHubTreeBlob>
        {
            new("skills/azure-openai/SKILL.md", 40),
            new("skills/azure-storage/SKILL.md", 40),
            new("skills/github-actions/SKILL.md", 40),
        };
        var svc = CreateService(new RecordingTreeClient(blobs, SkillFrontmatter));

        var (_, _, page) = await svc.BrowseMarketplaceAutoAsync(
            ProjectRef.Id, "github", "awesome-copilot", "main", query: "azure", page: 1, pageSize: 25, Caller, CancellationToken.None);

        page!.Total.Should().Be(2);
        page.Candidates.Select(c => c.Location).Should().BeEquivalentTo("skills/azure-openai", "skills/azure-storage");
    }

    // ── URL parsing ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("owner/repo", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/repo", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/repo/tree/dev", "owner", "repo", "dev", null)]
    [InlineData("https://github.com/owner/repo/tree/main/.github/plugins/x/skills", "owner", "repo", "main", ".github/plugins/x/skills")]
    public void ParseRepositoryUrl_accepts_slug_and_urls(string input, string owner, string repo, string? branch, string? subpath)
    {
        MarketplaceSourceService.TryParseRepositoryUrl(input, out var o, out var r, out var b, out var s).Should().BeTrue();
        o.Should().Be(owner);
        r.Should().Be(repo);
        b.Should().Be(branch);
        s.Should().Be(subpath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/owner/repo")]
    public void ParseRepositoryUrl_rejects_bad_input(string input)
    {
        MarketplaceSourceService.TryParseRepositoryUrl(input, out _, out _, out _, out _).Should().BeFalse();
    }

    // ── SQLite project source store CRUD ─────────────────────────────────────────────────

    [Fact]
    public async Task SqliteSourceStore_insert_list_get_delete_roundtrip()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectId = await SeedProjectAsync(testDb);
        var store = new SqliteProjectMarketplaceSourceStore(testDb.Db);
        var source = NewSource(projectId, "My Source", "owner/repo");

        (await store.InsertAsync(source)).Should().BeTrue();
        (await store.ListByProjectAsync(projectId)).Should().ContainSingle(s => s.Name == "My Source");
        (await store.GetByNameAsync(projectId, "my source"))!.Repository.Should().Be("owner/repo");
        (await store.DeleteByNameAsync(projectId, "My Source")).Should().BeTrue();
        (await store.ListByProjectAsync(projectId)).Should().BeEmpty();
    }

    [Fact]
    public async Task SqliteSourceStore_rejects_duplicate_name_case_insensitively()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectId = await SeedProjectAsync(testDb);
        var store = new SqliteProjectMarketplaceSourceStore(testDb.Db);

        (await store.InsertAsync(NewSource(projectId, "Dup", "owner/a"))).Should().BeTrue();
        (await store.InsertAsync(NewSource(projectId, "dup", "owner/b"))).Should().BeFalse();
    }

    private static async Task<ProjectId> SeedProjectAsync(TestSqliteDb testDb)
    {
        var projectStore = new SqliteProjectStore(testDb.Db);
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "src-test",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "aw-src-test"),
            DefaultBranch = "main",
            Owner = "owner-1",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        await projectStore.InsertAsync(project);
        return project.Id;
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────

    private static ProjectMarketplaceSource NewSource(ProjectId projectId, string name, string repo) => new()
    {
        ProjectId = projectId,
        SourceId = Guid.NewGuid().ToString("N"),
        Name = name,
        Repository = repo,
        Branch = "main",
        Subpath = null,
        ParseStrategy = "auto",
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static IReadOnlyList<GitHubTreeBlob> SkillBlobs(int count) =>
        Enumerable.Range(0, count).Select(i => new GitHubTreeBlob($"skills/skill-{i:D2}/SKILL.md", 40)).ToList();

    private static string SkillFrontmatter(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path));
        return $"---\nname: {name}\ndescription: A short definition for {name}.\n---\nInstructions.";
    }

    private static readonly Project ProjectRef = new()
    {
        Id = ProjectId.New(),
        Name = "catalog-test",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), "aw-catalog-test"),
        DefaultBranch = "main",
        Owner = "owner-1",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly CallerContext Caller = new() { User = "owner-1" };

    private static SkillCatalogService CreateService(
        IGitHubSkillTreeClient treeClient,
        IMarketplaceCatalogIndexer? catalogIndexer = null) => new(
        new UnusedSkillStore(),
        new SingleProjectStore(ProjectRef),
        new ProjectGitInitializer(NullLogger<ProjectGitInitializer>.Instance),
        new SkillParser(),
        new InstallationScopeProvider(),
        new SignedOutTokenStore(),
        NullLogger<SkillCatalogService>.Instance,
        accessTokenProvider: null,
        treeClient: treeClient,
        catalogIndexer: catalogIndexer ?? new MarketplaceCatalogIndexer(new MarketplaceCatalogCache()),
        projectRoles: new AllowAllProjectRoles(),
        configuration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    [Fact]
    public async Task Indexer_classifies_with_byok_bypassing_capability_issuance()
    {
        var classifier = new FakeByokClassifier(
            [new MarketplaceCatalogEntry("skills/a", "a", "A skill.")]);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        var index = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "byok-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller,
            issueCapabilityAsync: _ => throw new InvalidOperationException("BYOK must never issue a Copilot capability"),
            hasCapabilityAsync: _ => throw new InvalidOperationException("BYOK must never check a Copilot capability"),
            useByok: true);

        index.Strategy.Should().Be("llm");
        index.RequiresGitHubConnection.Should().BeFalse();
        index.Entries.Should().ContainSingle(e => e.Location == "skills/a");
        classifier.ByokInvocations.Should().Be(1);
    }

    [Fact]
    public async Task Indexer_serves_a_cached_llm_result_to_a_byok_caller_without_a_capability_check()
    {
        var classifier = new FakeClassifier(
            [new MarketplaceCatalogEntry("skills/a", "a", "A skill.")]);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        _ = await indexer.GetOrBuildForProjectAsync(
            "acme", "cached-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: "capability-reference", parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller);

        var byokBrowse = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "cached-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller,
            hasCapabilityAsync: _ => throw new InvalidOperationException("a BYOK caller must not check a Copilot binding"),
            useByok: true);

        byokBrowse.RequiresGitHubConnection.Should().BeFalse();
        byokBrowse.Entries.Should().ContainSingle(e => e.Location == "skills/a");
        classifier.Invocations.Should().Be(1, "the cached catalog must not dispatch another model turn");
    }

    [Fact]
    public async Task Indexer_requires_connection_for_byok_classifier_unauthorized()
    {
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), new UnavailableByokClassifier());

        var index = await indexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
            "acme", "byok-unavailable-repo", "main", [new GitHubTreeBlob("skills/a/SKILL.md", 40)],
            capabilityReference: null, parseStrategy: "llm", CancellationToken.None,
            projectId: ProjectRef.Id, caller: Caller, useByok: true);

        index.RequiresGitHubConnection.Should().BeTrue();
        index.Entries.Should().BeEmpty();
    }

    private sealed class FakeClassifier(IReadOnlyList<MarketplaceCatalogEntry> result) : IMarketplaceCatalogClassifier
    {
        public int Invocations { get; private set; }

        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? capabilityRunId, CancellationToken ct)
        {
            Invocations++;
            CapabilityRunIds.Add(capabilityRunId);
            return Task.FromResult<IReadOnlyList<MarketplaceCatalogEntry>?>(result);
        }

        public List<string?> CapabilityRunIds { get; } = [];
    }

    private sealed class UnavailableCapabilityClassifier : IMarketplaceCatalogClassifier
    {
        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? capabilityRunId, CancellationToken ct) =>
            throw new GitHubCopilotUnauthorizedException("Connect a GitHub account with GitHub Copilot access.");
    }

    private sealed class FakeByokClassifier(IReadOnlyList<MarketplaceCatalogEntry> result) : IMarketplaceCatalogClassifier
    {
        public int ByokInvocations { get; private set; }

        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? capabilityRunId, CancellationToken ct) =>
            throw new InvalidOperationException("BYOK browse must call ClassifyWithByokAsync, not the capability-based path.");

        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyWithByokAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, CancellationToken ct)
        {
            ByokInvocations++;
            return Task.FromResult<IReadOnlyList<MarketplaceCatalogEntry>?>(result);
        }
    }

    private sealed class UnavailableByokClassifier : IMarketplaceCatalogClassifier
    {
        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? capabilityRunId, CancellationToken ct) =>
            throw new InvalidOperationException("BYOK browse must call ClassifyWithByokAsync, not the capability-based path.");

        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyWithByokAsync(
            string owner, string repo, string branch, IReadOnlyList<string> treePaths, CancellationToken ct) =>
            throw new GitHubCopilotUnauthorizedException("BYOK provider unavailable.");
    }

    private sealed class RecordingTreeClient(IReadOnlyList<GitHubTreeBlob> blobs, Func<string, string?> content) : IGitHubSkillTreeClient
    {
        private readonly List<string> _rawRequests = new();
        private readonly List<string?> _tokensSeen = new();
        public IReadOnlyList<string> RawRequests => _rawRequests;
        public IReadOnlyList<string?> TokensSeen => _tokensSeen;

        public Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
            string owner, string repo, string branch, string subpath, string? token, CancellationToken ct)
        {
            lock (_tokensSeen) _tokensSeen.Add(token);
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
            lock (_tokensSeen) _tokensSeen.Add(token);
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

    private sealed class AllowAllProjectRoles : IProjectRoleAuthorizationService
    {
        public bool IsPlatformAdmin(CallerContext caller) => false;
        public Task<ProjectRole?> GetEffectiveRoleAsync(CallerContext caller, ProjectId projectId, CancellationToken ct = default) => Task.FromResult<ProjectRole?>(ProjectRole.Owner);
        public Task<bool> HasRoleAsync(CallerContext caller, ProjectId projectId, ProjectRole minimumRole, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(CallerContext caller, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<ProjectId, ProjectRole>>(new Dictionary<ProjectId, ProjectRole>());
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
