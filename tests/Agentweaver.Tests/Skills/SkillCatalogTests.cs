using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Squad.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Tests.Skills;

/// <summary>
/// Unit coverage for the per-project skill catalog (issues #51/#56): SKILL.md parsing/validation,
/// content-hash idempotency, the SQLite-backed <see cref="ISkillStore"/> (catalog + assignments),
/// and progressive-disclosure filtering (only Active + assigned skills reach a given agent).
/// </summary>
public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDb _db;

    public SkillCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "agentweaver.db"),
            })
            .Build();
        _db = new SqliteDb(config);
        _db.EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string SkillMd(string name, string description, string body = "Do the thing.") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n";

    private static SkillCatalogService DiscoveryService() => new(
        null!, null!, null!, new SkillParser(), null!, null!, NullLogger<SkillCatalogService>.Instance);

    // ── Parser ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidSkill_ExtractsNameDescriptionAndBody()
    {
        var parser = new SkillParser();
        var result = parser.Parse(SkillMd("pr-review", "Reviews pull requests.", "Follow the checklist."));

        result.IsValid.Should().BeTrue();
        result.Name.Should().Be("pr-review");
        result.Description.Should().Be("Reviews pull requests.");
        result.Instructions.Should().Be("Follow the checklist.");
    }

    [Fact]
    public void Parse_MissingName_IsRejectedWithClearError()
    {
        var parser = new SkillParser();
        var result = parser.Parse("---\ndescription: No name here.\n---\n\nBody.\n");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("name"));
    }

    [Fact]
    public void Parse_WithoutFrontmatter_IsRejected()
    {
        var parser = new SkillParser();
        var result = parser.Parse("# Just markdown, no frontmatter\n");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("frontmatter"));
    }

    [Fact]
    public void ComputeContentHash_IsStable_AndSensitiveToContent()
    {
        var res = Array.Empty<SkillResource>();
        var h1 = SkillParser.ComputeContentHash("n", "d", "instructions", res);
        var h2 = SkillParser.ComputeContentHash("n", "d", "instructions", res);
        var h3 = SkillParser.ComputeContentHash("n", "d", "different", res);

        h1.Should().Be(h2);
        h1.Should().NotBe(h3);
    }

    [Fact]
    public void DiscoverSkills_FindsGenericFolderOfSkillDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "aw-skill-discover-" + Guid.NewGuid().ToString("N"));
        try
        {
            var skillDir = Path.Combine(root, "skills", "summarize");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), SkillMd("summarize", "Summarizes text."));

            var discovered = DiscoveryService().DiscoverSkills(root, "skills");

            discovered.Should().ContainSingle();
            discovered[0].RelativeLocation.Should().Be("skills/summarize");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void DiscoverSkills_FindsSingleSkillAtSubpathRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aw-skill-single-" + Guid.NewGuid().ToString("N"));
        try
        {
            var skillDir = Path.Combine(root, "skills", "review");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), SkillMd("review", "Reviews code."));

            var discovered = DiscoveryService().DiscoverSkills(root, "skills/review");

            discovered.Should().ContainSingle();
            discovered[0].RelativeLocation.Should().Be("skills/review/SKILL.md");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("good-skill", null)]
    [InlineData("BadSkill", "slug")]
    [InlineData("bad/skill", "slug")]
    [InlineData("bad_skill", "slug")]
    public void ValidateCreateRequest_EnforcesSlugName(string name, string? errorContains)
    {
        var error = SkillCatalogService.ValidateCreateRequest(new CreateSkillRequestDto(name, null, "d", "body"));
        if (errorContains is null) error.Should().BeNull();
        else error.Should().Contain(errorContains);
    }

    // ── Import source parsing / SSRF host allowlist ──────────────────────────────

    [Theory]
    // Non-GitHub / internal hosts must never be turned into a clone/fetch target.
    [InlineData("https://kubernetes.default.svc/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://127.0.0.1/owner/repo")]
    [InlineData("https://10.1.2.3/owner/repo")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://evil.com/owner/repo")]
    // Non-https schemes are rejected explicitly.
    [InlineData("http://github.com/owner/repo")]
    [InlineData("git://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git")]
    // Userinfo tricks — effective host is evil.com.
    [InlineData("https://github.com@evil.com/owner/repo")]
    // Non-default port variants.
    [InlineData("https://github.com:1234/owner/repo")]
    [InlineData("https://raw.githubusercontent.com:8443/owner/repo/main/SKILL.md")]
    public void ParseImportSource_RejectsNonGitHubOrUnsafeSources(string input)
    {
        var act = () => SkillCatalogService.SkillImportSource.Parse(input);
        act.Should().Throw<SkillImportException>();
    }

    [Fact]
    public void ParseImportSource_AcceptsPublicGitHubRepo()
    {
        var source = SkillCatalogService.SkillImportSource.Parse("https://github.com/owner/repo");
        source.CloneUrl.Should().Be("https://github.com/owner/repo.git");
        source.RawSkillUri.Should().BeNull();
        source.SourceRepository.Should().Be("owner/repo");
    }

    [Fact]
    public void ParseImportSource_AcceptsShorthandOwnerRepo()
    {
        var source = SkillCatalogService.SkillImportSource.Parse("owner/repo");
        source.CloneUrl.Should().Be("https://github.com/owner/repo.git");
    }

    [Fact]
    public void ParseImportSource_AcceptsRawSkillMdUrl()
    {
        var source = SkillCatalogService.SkillImportSource.Parse(
            "https://raw.githubusercontent.com/owner/repo/main/SKILL.md");
        source.RawSkillUri.Should().NotBeNull();
        source.CloneUrl.Should().BeNull();
        source.SourceRepository.Should().Be("owner/repo");
    }

    [Fact]
    public void ParseImportSource_HostCheckIsCaseInsensitive()
    {
        var source = SkillCatalogService.SkillImportSource.Parse("https://GitHub.com/Owner/Repo");
        source.CloneUrl.Should().Be("https://github.com/Owner/Repo.git");
    }

    [Fact]
    public void ParseImportSource_TreeUrl_DefersRefResolution()
    {
        // The ref boundary for tree/blob URLs is resolved post-clone against real refs, so Parse
        // must not eagerly assume parts[3] is the ref (which mis-resolves slash-containing branches).
        var source = SkillCatalogService.SkillImportSource.Parse(
            "https://github.com/owner/repo/tree/release/v2/skills");
        source.CloneUrl.Should().Be("https://github.com/owner/repo.git");
        source.CheckoutRef.Should().BeNull();
        source.RefSegments.Should().Equal("release", "v2", "skills");
    }

    [Fact]
    public void IsAllowedCloneHost_OnlyTrueForGitHubHttps()
    {
        SkillCatalogService.SkillImportSource.IsAllowedCloneHost("https://github.com/owner/repo.git").Should().BeTrue();
        SkillCatalogService.SkillImportSource.IsAllowedCloneHost("https://evil.com/owner/repo.git").Should().BeFalse();
        SkillCatalogService.SkillImportSource.IsAllowedCloneHost("http://github.com/owner/repo.git").Should().BeFalse();
        SkillCatalogService.SkillImportSource.IsAllowedCloneHost("https://github.com@evil.com/x").Should().BeFalse();
        SkillCatalogService.SkillImportSource.IsAllowedCloneHost("https://github.com:1234/owner/repo.git").Should().BeFalse();
    }

    // ── Store: catalog ──────────────────────────────────────────────────────────

    private static Skill NewSkill(ProjectId projectId, string name, SkillStatus status = SkillStatus.Active)
    {
        var now = DateTimeOffset.UtcNow;
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = projectId,
            Name = name,
            Description = $"{name} description",
            Instructions = "Body.",
            Provenance = SkillProvenance.ConnectedRepoSync,
            SourceRepository = "owner/repo",
            SourceLocation = $".github/skills/{name}",
            ContentHash = SkillParser.ComputeContentHash(name, $"{name} description", "Body.", Array.Empty<SkillResource>()),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static Project NewProject(ProjectId id, string workingDirectory) => new()
    {
        Id = id,
        Name = "Skill defaults test",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = workingDirectory,
        DefaultBranch = "main",
        Owner = "test",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static CastMember Member(string name, string roleId) => new(
        name,
        new Role(roleId, roleId, "test role", "test", [], [], []),
        "charter.md",
        CastMemberStatus.Active,
        false);

    [Fact]
    public async Task Store_InsertGetList_RoundTrips()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var skill = NewSkill(project, "code-review");

        await store.InsertAsync(skill);

        var fetched = await store.GetAsync(project, skill.Id);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("code-review");

        var list = await store.ListByProjectAsync(project);
        list.Should().ContainSingle(s => s.Id == skill.Id);
    }

    [Fact]
    public async Task Store_GetByName_IsCaseInsensitive()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        await store.InsertAsync(NewSkill(project, "Code-Review"));

        var byLower = await store.GetByNameAsync(project, "code-review");
        byLower.Should().NotBeNull();
    }

    [Fact]
    public async Task Store_Delete_CascadesAssignments()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var skill = NewSkill(project, "docs");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Smith", DateTimeOffset.UtcNow);

        (await store.ListAssignmentsByProjectAsync(project)).Should().HaveCount(1);

        (await store.DeleteAsync(project, skill.Id)).Should().BeTrue();
        (await store.ListAssignmentsByProjectAsync(project)).Should().BeEmpty();
    }

    // ── Store: assignments + progressive disclosure ───────────────────────────────

    [Fact]
    public async Task Assign_IsIdempotent()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var skill = NewSkill(project, "lint");
        await store.InsertAsync(skill);

        await store.AssignAsync(project, skill.Id, "Neo", DateTimeOffset.UtcNow);
        await store.AssignAsync(project, skill.Id, "Neo", DateTimeOffset.UtcNow);

        var assignments = await store.ListAssignmentsByProjectAsync(project);
        assignments.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListActiveSkillsForAgent_ReturnsOnlyAssignedActiveSkills()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();

        var assignedActive = NewSkill(project, "assigned-active");
        var assignedMissing = NewSkill(project, "assigned-missing", SkillStatus.Missing);
        var unassignedActive = NewSkill(project, "unassigned-active");
        await store.InsertAsync(assignedActive);
        await store.InsertAsync(assignedMissing);
        await store.InsertAsync(unassignedActive);

        await store.AssignAsync(project, assignedActive.Id, "Smith", DateTimeOffset.UtcNow);
        await store.AssignAsync(project, assignedMissing.Id, "Smith", DateTimeOffset.UtcNow);
        // unassignedActive intentionally left unassigned.

        var forSmith = await store.ListActiveSkillsForAgentAsync(project, "Smith");

        forSmith.Should().ContainSingle();
        forSmith[0].Name.Should().Be("assigned-active");
    }

    [Fact]
    public async Task Unassign_RemovesAssignment_AndDropsFromAgentView()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var skill = NewSkill(project, "format");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Trinity", DateTimeOffset.UtcNow);

        (await store.UnassignAsync(project, skill.Id, "Trinity")).Should().BeTrue();
        (await store.ListActiveSkillsForAgentAsync(project, "Trinity")).Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultsApply_InsertsAndAssignsAtomically()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var builtIn = NewSkill(project, "system-design") with { Provenance = SkillProvenance.BuiltIn };
        var initialSkills = await store.ListByProjectAsync(project);
        var initialAssignments = await store.ListAssignmentsByProjectAsync(project);
        var assignment = new SkillAssignment
        {
            ProjectId = project,
            SkillId = builtIn.Id,
            AgentName = "Tank",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await store.ApplyDefaultsAsync(new SkillDefaultsStorePlan(
            project,
            SkillCatalogStateFingerprint.Compute(initialSkills, initialAssignments),
            [builtIn],
            [],
            [assignment]));

        result.Should().Be(SkillDefaultsStoreApplyResult.Applied);
        (await store.GetAsync(project, builtIn.Id)).Should().NotBeNull();
        (await store.ListAssignmentsByProjectAsync(project))
            .Should().ContainSingle(a => a.SkillId == builtIn.Id && a.AgentName == "Tank");
    }

    [Fact]
    public async Task DefaultsApply_RejectsStaleStateWithoutPartialWrites()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var initialSkills = await store.ListByProjectAsync(project);
        var initialAssignments = await store.ListAssignmentsByProjectAsync(project);
        var planned = NewSkill(project, "threat-modeling") with { Provenance = SkillProvenance.BuiltIn };
        await store.InsertAsync(NewSkill(project, "concurrent-change"));

        var result = await store.ApplyDefaultsAsync(new SkillDefaultsStorePlan(
            project,
            SkillCatalogStateFingerprint.Compute(initialSkills, initialAssignments),
            [planned],
            [],
            []));

        result.Should().Be(SkillDefaultsStoreApplyResult.Stale);
        (await store.GetByNameAsync(project, "threat-modeling")).Should().BeNull();
        (await store.GetByNameAsync(project, "concurrent-change")).Should().NotBeNull();
    }

    [Fact]
    public async Task DefaultsApply_ReactivatesByteIdenticalBuiltInBeforeAssignment()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var inactive = NewSkill(project, "api-data-safety", SkillStatus.Missing) with
        {
            Provenance = SkillProvenance.BuiltIn,
        };
        await store.InsertAsync(inactive);
        var initialSkills = await store.ListByProjectAsync(project);
        var initialAssignments = await store.ListAssignmentsByProjectAsync(project);
        var active = inactive with { Status = SkillStatus.Active, UpdatedAt = DateTimeOffset.UtcNow };

        var result = await store.ApplyDefaultsAsync(new SkillDefaultsStorePlan(
            project,
            SkillCatalogStateFingerprint.Compute(initialSkills, initialAssignments),
            [],
            [active],
            [new SkillAssignment
            {
                ProjectId = project,
                SkillId = inactive.Id,
                AgentName = "Tank",
                CreatedAt = DateTimeOffset.UtcNow,
            }]));

        result.Should().Be(SkillDefaultsStoreApplyResult.Applied);
        (await store.GetAsync(project, inactive.Id))!.Status.Should().Be(SkillStatus.Active);
        (await store.ListActiveSkillsForAgentAsync(project, "Tank"))
            .Should().ContainSingle(s => s.Id == inactive.Id);
    }

    [Fact]
    public async Task DefaultsPreview_FailsClosedWhenRoleIsAmbiguous()
    {
        var store = new SqliteSkillStore(_db);
        var project = NewProject(ProjectId.New(), _dir);
        var service = new SkillDefaultsService(store, null!);
        var blueprint = new Blueprint("defaults", "Defaults", "test", ["lead-architect"], ["default"], "default", "default")
        {
            SkillBindings = [new BlueprintSkillBinding("lead-architect", ["system-design"])],
        };
        var team = new Team(project.Name, "test", [Member("Tank", "lead-architect"), Member("Neo", "lead-architect")]);

        var preview = await service.PreviewAsync(project, blueprint, team);

        preview.CanApply.Should().BeFalse();
        preview.Errors.Should().ContainSingle(e => e.Contains("multiple active confirmed members"));
    }

    [Fact]
    public async Task DefaultsPreview_ManualSameNameBlocksBuiltInReplacement()
    {
        var store = new SqliteSkillStore(_db);
        var project = NewProject(ProjectId.New(), _dir);
        await store.InsertAsync(NewSkill(project.Id, "system-design") with { Provenance = SkillProvenance.Manual });
        var service = new SkillDefaultsService(store, null!);
        var blueprint = new Blueprint("defaults", "Defaults", "test", ["lead-architect"], ["default"], "default", "default")
        {
            SkillBindings = [new BlueprintSkillBinding("lead-architect", ["system-design"])],
        };
        var team = new Team(project.Name, "test", [Member("Tank", "lead-architect")]);

        var preview = await service.PreviewAsync(project, blueprint, team);

        preview.CanApply.Should().BeTrue();
        preview.Assignments.Should().ContainSingle(a => a.Action == "blocked" && a.AgentName == "Tank");
    }

    [Fact]
    public void EfDefaultsApply_ClassifiesOnlyNestedSerializationFailureAsStale()
    {
        var serialization = new DbUpdateException(
            "outer",
            new InvalidOperationException(
                "middle",
                new PostgresException("serialization", "ERROR", "ERROR", "40001")));
        var unique = new DbUpdateException(
            "outer",
            new PostgresException("duplicate", "ERROR", "ERROR", "23505"));

        EfSkillStore.IsSerializationFailure(serialization).Should().BeTrue();
        EfSkillStore.IsSerializationFailure(unique).Should().BeFalse();
    }
}
