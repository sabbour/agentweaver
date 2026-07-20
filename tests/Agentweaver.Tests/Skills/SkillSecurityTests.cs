using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Skills;

/// <summary>
/// Review-fix coverage for the skill catalog: child-run progressive-disclosure injection (#1),
/// zip-slip / rooted-path containment (#2), stale materialized-dir cleanup (#3), and slug-collision
/// isolation (#6). These exercise the REAL <see cref="SkillPromptComposer"/> against a temp worktree
/// and the REAL <see cref="SkillEndpoints.ExpandZip"/> against a crafted archive — no mocks.
/// </summary>
public sealed class SkillSecurityTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDb _db;

    public SkillSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-skillsec-" + Guid.NewGuid().ToString("N"));
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
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static Skill NewSkill(ProjectId projectId, string name, IReadOnlyList<SkillResource>? resources = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = projectId,
            Name = name,
            Description = $"{name} description",
            Instructions = "Full instruction body.",
            Resources = resources ?? Array.Empty<SkillResource>(),
            Provenance = SkillProvenance.ConnectedRepoSync,
            SourceRepository = "owner/repo",
            SourceLocation = $".github/skills/{name}",
            ContentHash = "hash-" + name,
            Status = SkillStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private SkillPromptComposer NewComposer(ISkillStore store) =>
        new(store, NullLogger<SkillPromptComposer>.Instance);

    private string NewWorktree()
    {
        var wt = Path.Combine(_dir, "wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wt);
        return wt;
    }

    // ── #1: assigned active skills reach a CHILD (coordinator-dispatched) agent ────

    [Fact]
    public async Task ComposeAsync_InjectsAssignedActiveSkill_ForChildWorkerAgent()
    {
        // A coordinator-dispatched worker run carries AgentName + ProjectId + WorktreePath — exactly the
        // inputs the child branch of RunOrchestrator.BuildContextAsync now feeds to the composer. This
        // proves an assigned active skill produces prompt metadata and a readable on-disk SKILL.md.
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        await SkillTestProject.InsertAsync(_db, project, _dir);
        var skill = NewSkill(project, "pr-review");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Worker", DateTimeOffset.UtcNow);

        var worktree = NewWorktree();
        var block = await NewComposer(store).ComposeAsync(project, "Worker", worktree, CancellationToken.None);

        block.Should().NotBeNull();
        block!.Should().Contain("pr-review");
        var dir = SkillPromptComposer.StagingDirName(skill);
        block.Should().Contain($"{SkillPromptComposer.SkillsRelativeDir}/{dir}/SKILL.md");
        File.Exists(Path.Combine(worktree, ".agentweaver", "skills", dir, "SKILL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task ComposeAsync_ReturnsNull_WhenAgentHasNoAssignedSkills()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        await SkillTestProject.InsertAsync(_db, project, _dir);
        await store.InsertAsync(NewSkill(project, "unassigned"));

        var block = await NewComposer(store).ComposeAsync(project, "Worker", NewWorktree(), CancellationToken.None);
        block.Should().BeNull();
    }

    // ── #2: zip-slip / rooted-path containment ────────────────────────────────────

    [Theory]
    [InlineData("C:/evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../b")]
    [InlineData("a//b")]
    [InlineData("name:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeRelative_RejectsUnsafePaths(string raw)
    {
        SkillPaths.NormalizeRelative(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("docs/guide.md", "docs/guide.md")]
    [InlineData("/leading/slash.md", "leading/slash.md")]
    [InlineData("nested\\win\\path.md", "nested/win/path.md")]
    public void NormalizeRelative_AcceptsAndNormalizesSafePaths(string raw, string expected)
    {
        SkillPaths.NormalizeRelative(raw).Should().Be(expected);
    }

    [Fact]
    public async Task ComposeAsync_DoesNotWriteResourceOutsideSkillDir()
    {
        // A malicious bundled resource whose path escapes the skill dir must be dropped, never written.
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        await SkillTestProject.InsertAsync(_db, project, _dir);
        var evil = new[]
        {
            new SkillResource { RelativePath = "C:/evil.txt", Content = "owned" },
            new SkillResource { RelativePath = "../escape.txt", Content = "owned" },
            new SkillResource { RelativePath = "safe/ok.txt", Content = "fine" },
        };
        var skill = NewSkill(project, "malicious", evil);
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Worker", DateTimeOffset.UtcNow);

        var worktree = NewWorktree();
        await NewComposer(store).ComposeAsync(project, "Worker", worktree, CancellationToken.None);

        var skillDir = Path.Combine(worktree, ".agentweaver", "skills", SkillPromptComposer.StagingDirName(skill));
        File.Exists(Path.Combine(skillDir, "safe", "ok.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "evil.txt")).Should().BeFalse();
        File.Exists(Path.Combine(worktree, "escape.txt")).Should().BeFalse();
        File.Exists("C:\\evil.txt").Should().BeFalse();
    }

    [Fact]
    public void ExpandZip_RejectsTraversalEntryName()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("../evil.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("owned");
        }
        ms.Position = 0;

        var files = new List<UploadedSkillFile>();
        var act = () => SkillEndpoints.ExpandZip(ms, files);
        act.Should().Throw<InvalidDataException>().WithMessage("*unsafe path*");
    }

    [Fact]
    public void ExpandZip_ExtractsSafeEntries()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("SKILL.md");
            using var w = new StreamWriter(entry.Open());
            w.Write("---\nname: z\ndescription: d\n---\nbody");
        }
        ms.Position = 0;

        var files = new List<UploadedSkillFile>();
        SkillEndpoints.ExpandZip(ms, files);

        files.Should().ContainSingle();
        files[0].RelativePath.Should().Be("SKILL.md");
    }

    [Fact]
    public void ExpandZip_SkipsOversizedEntry_WithoutWritingIt()
    {
        // An entry larger than the per-entry cap is skipped (not decompressed into a stored file),
        // mirroring the filesystem SafeReadText bail — the zip-bomb guard.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var big = zip.CreateEntry("huge.txt");
            using (var w = new StreamWriter(big.Open()))
                w.Write(new string('a', 2 * 1024 * 1024)); // 2 MB > 512 KB per-entry cap
            var ok = zip.CreateEntry("SKILL.md");
            using (var w = new StreamWriter(ok.Open()))
                w.Write("---\nname: z\ndescription: d\n---\nbody");
        }
        ms.Position = 0;

        var files = new List<UploadedSkillFile>();
        SkillEndpoints.ExpandZip(ms, files);

        files.Should().NotContain(f => f.RelativePath == "huge.txt");
        files.Should().Contain(f => f.RelativePath == "SKILL.md");
    }

    // ── #3: stale materialized dirs are removed ───────────────────────────────────

    [Fact]
    public async Task ComposeAsync_RemovesStaleDir_WhenSkillUnassigned()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        await SkillTestProject.InsertAsync(_db, project, _dir);
        var skill = NewSkill(project, "temporary");
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Worker", DateTimeOffset.UtcNow);

        var worktree = NewWorktree();
        var composer = NewComposer(store);
        await composer.ComposeAsync(project, "Worker", worktree, CancellationToken.None);

        var dir = Path.Combine(worktree, ".agentweaver", "skills", SkillPromptComposer.StagingDirName(skill));
        Directory.Exists(dir).Should().BeTrue();

        // Unassign, recompose (worktree reused): the stale dir must be gone so the removed skill's full
        // instructions can no longer be read even though the prompt no longer advertises it.
        await store.UnassignAsync(project, skill.Id, "Worker");
        var block = await composer.ComposeAsync(project, "Worker", worktree, CancellationToken.None);

        block.Should().BeNull();
        Directory.Exists(dir).Should().BeFalse();
    }

    // ── #6: distinct names that slugify identically get isolated dirs ─────────────

    [Fact]
    public void StagingDirName_IsUnique_ForNamesThatSlugifyIdentically()
    {
        var project = ProjectId.New();
        var a = NewSkill(project, "PR Review");
        var b = NewSkill(project, "pr-review");

        SkillPromptComposer.StagingDirName(a).Should().NotBe(SkillPromptComposer.StagingDirName(b));
        SkillPromptComposer.StagingDirName(a).Should().StartWith("pr-review-");
        SkillPromptComposer.StagingDirName(b).Should().StartWith("pr-review-");
    }
}
