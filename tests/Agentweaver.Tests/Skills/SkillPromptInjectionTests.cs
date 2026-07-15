using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Skills;

/// <summary>
/// Regression coverage for issue #336: skills assigned to an agent must actually reach that agent's
/// assembled system-prompt context. The store layer (assign + <see cref="ISkillStore.ListActiveSkillsForAgentAsync"/>)
/// is exercised in <see cref="SkillCatalogTests"/>; this suite covers the previously-UNTESTED
/// composition step — <see cref="SkillPromptComposer"/> — that turns an agent's assigned skills into
/// the progressive-disclosure block, plus the shared marker used to make delivery observable in the
/// runtime <c>agent.system_prompt</c> event (<c>skillsContextIncluded</c>).
/// </summary>
public sealed class SkillPromptInjectionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _worktree;
    private readonly SqliteDb _db;

    public SkillPromptInjectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-skill-inject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _worktree = Path.Combine(_dir, "worktree");
        Directory.CreateDirectory(_worktree);
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

    private static Skill NewSkill(ProjectId projectId, string name, string instructions, string description)
    {
        var now = DateTimeOffset.UtcNow;
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = projectId,
            Name = name,
            Description = description,
            Instructions = instructions,
            Provenance = SkillProvenance.Manual,
            ContentHash = SkillParser.ComputeContentHash(name, description, instructions, Array.Empty<SkillResource>()),
            Status = SkillStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private SkillPromptComposer Composer() =>
        new(new SqliteSkillStore(_db), NullLogger<SkillPromptComposer>.Instance);

    [Fact]
    public async Task Compose_AssignedActiveSkill_AppearsInSystemPromptBlock()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        const string token = "SKILL-ACTIVE-SKL-9X3M7-HARNESS-VERIFY";
        var skill = NewSkill(project, "harness-verify-skill",
            instructions: $"IMPORTANT: begin every response with '{token}'.",
            description: "Compliance marker skill.");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        var block = await Composer().ComposeAsync(project, "Rogers", _worktree, CancellationToken.None);

        block.Should().NotBeNullOrEmpty();
        block!.Should().Contain(SkillPromptMarkers.SectionHeading);
        block.Should().Contain("harness-verify-skill");
        block.Should().Contain("Compliance marker skill.");
        // Progressive disclosure: the block references the materialized SKILL.md rather than inlining it.
        block.Should().Contain($"{SkillPromptComposer.SkillsRelativeDir}/");
        block.Should().Contain("SKILL.md");

        // The delivery flag used by the agent-runtime agent.system_prompt event must light up for this block.
        SkillPromptMarkers.ContainsSkillContext(block).Should().BeTrue();
    }

    [Fact]
    public async Task Compose_MaterializesSkillBodyIntoWorktree()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        var skill = NewSkill(project, "pr-review",
            instructions: "Follow the PR review checklist verbatim.",
            description: "Reviews pull requests.");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        await Composer().ComposeAsync(project, "Rogers", _worktree, CancellationToken.None);

        var dir = SkillPromptComposer.StagingDirName(skill);
        var mdPath = Path.Combine(_worktree, ".agentweaver", "skills", dir, "SKILL.md");
        File.Exists(mdPath).Should().BeTrue();
        var md = await File.ReadAllTextAsync(mdPath);
        md.Should().Contain("Follow the PR review checklist verbatim.");
        md.Should().Contain("name: pr-review");
    }

    [Fact]
    public async Task Compose_NoAssignedSkills_ReturnsNull_AndFlagIsFalse()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        // A skill exists but is NOT assigned to this agent.
        var skill = NewSkill(project, "unassigned", "Body.", "Unassigned skill.");
        await store.InsertAsync(skill);

        var block = await Composer().ComposeAsync(project, "Rogers", _worktree, CancellationToken.None);

        block.Should().BeNull();
        SkillPromptMarkers.ContainsSkillContext(block).Should().BeFalse();
    }

    [Fact]
    public async Task Compose_NoWorktree_InlinesFullInstructions_NoDanglingPointer()
    {
        // Regression for #336: when there is no writable worktree at compose time (e.g. pod-per-run /
        // warm-pool execution where the agent runs on a different filesystem), the composer must NOT
        // emit a `SKILL.md` pointer to a file it never wrote. Instead it inlines the full instructions
        // so the skill's content — including its verification token — still reaches the agent.
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        const string token = "SKILL-ACTIVE-X7K9PQR2-HARNESS-VERIFY";
        var skill = NewSkill(project, "skill-verify-x7k9pqr2",
            instructions: $"IMPORTANT: begin every response with '{token}'. Then do the customer research.",
            description: "Customer research compliance skill.");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        var missingWorktree = Path.Combine(_dir, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        var block = await Composer().ComposeAsync(project, "Rogers", missingWorktree, CancellationToken.None);

        block.Should().NotBeNullOrEmpty();
        SkillPromptMarkers.ContainsSkillContext(block).Should().BeTrue();
        block!.Should().Contain("skill-verify-x7k9pqr2");
        // The actual instructions + token must be present verbatim in the prompt string.
        block.Should().Contain(token);
        block.Should().Contain("do the customer research");
        // And there must be NO dangling SKILL.md path pointer, since no file was materialized.
        block.Should().NotContain(SkillPromptComposer.SkillsRelativeDir);
    }

    [Fact]
    public async Task Compose_EmptyWorktreePath_InlinesInstructions()
    {
        // A named agent whose run carries no worktree path at all must still receive its skill content
        // inline rather than a pointer to a non-existent file.
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        const string token = "SKILL-ACTIVE-EMPTYWT-VERIFY";
        var skill = NewSkill(project, "empty-worktree-skill",
            instructions: $"Prefix responses with '{token}'.",
            description: "Marker skill.");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        var block = await Composer().ComposeAsync(project, "Rogers", worktreePath: "", CancellationToken.None);

        block.Should().NotBeNullOrEmpty();
        block!.Should().Contain(token);
        block.Should().NotContain(SkillPromptComposer.SkillsRelativeDir);
    }

    [Fact]
    public async Task Compose_WithWorktree_EmitsPointer_NotInlineBody()
    {
        // Complementary assertion: when the worktree IS available, progressive disclosure holds — the
        // block references the materialized SKILL.md and does not inline the full instruction body.
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        const string token = "SKILL-ACTIVE-POINTER-VERIFY";
        var skill = NewSkill(project, "pointer-skill",
            instructions: $"Secret body: {token} plus a long checklist that should stay on disk.",
            description: "Disk-backed skill.");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        var block = await Composer().ComposeAsync(project, "Rogers", _worktree, CancellationToken.None);

        block.Should().NotBeNullOrEmpty();
        block!.Should().Contain($"{SkillPromptComposer.SkillsRelativeDir}/");
        block.Should().Contain("SKILL.md");
        // Progressive disclosure: the body/token lives in the file, not inline in the prompt.
        block.Should().NotContain(token);
        // ...and the file genuinely exists with the token.
        var dir = SkillPromptComposer.StagingDirName(skill);
        var mdPath = Path.Combine(_worktree, ".agentweaver", "skills", dir, "SKILL.md");
        File.Exists(mdPath).Should().BeTrue();
        (await File.ReadAllTextAsync(mdPath)).Should().Contain(token);
    }

    [Fact]
    public void ContainsSkillContext_CharterOnlyContext_IsFalse()
    {
        // A charter+memory context with no skills block must NOT be reported as skills-included —
        // this is the exact "assigned but not delivered" gap (#336) the flag makes observable.
        const string charterOnly = "You are Rogers, the researcher.\n\n---\n\n## Team Decisions\n- Ship it.";
        SkillPromptMarkers.ContainsSkillContext(charterOnly).Should().BeFalse();
        SkillPromptMarkers.ContainsSkillContext(null).Should().BeFalse();
        SkillPromptMarkers.ContainsSkillContext("").Should().BeFalse();
    }
}
