using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
}
