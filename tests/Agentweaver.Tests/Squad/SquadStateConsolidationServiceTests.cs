using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Squad;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Squad;

/// <summary>
/// Tests for <see cref="SquadStateConsolidationService"/> (issue #621) over a REAL
/// <see cref="SqliteProjectStore"/>, a REAL <see cref="RepositoryMergeLock"/>, and a REAL on-disk git
/// repository (Principle VII: no mocks of the store or git). Every pass is driven explicitly via
/// <see cref="SquadStateConsolidationService.RunTickAsync"/> /
/// <see cref="SquadStateConsolidationService.ConsolidateProjectAsync"/> — never the wall clock — so the
/// whole "inbox entry → appended to decisions.md → idempotent re-tick" pipeline is deterministic.
/// </summary>
public sealed class SquadStateConsolidationServiceTests : IAsyncDisposable
{
    private readonly TestSqliteDb _testDb;
    private readonly SqliteProjectStore _projects;
    private readonly RepositoryMergeLock _mergeLock;
    private readonly MemoryDbContext _memoryDb;
    private readonly string _memoryDbPath;
    private readonly SquadStateConsolidationService _service;
    private readonly List<string> _tempDirs = new();

    public SquadStateConsolidationServiceTests()
    {
        _testDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _projects = new SqliteProjectStore(_testDb.Db);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
            })
            .Build();

        _mergeLock = new RepositoryMergeLock(configuration, NullLogger<RepositoryMergeLock>.Instance);
        _memoryDbPath = Path.Combine(Path.GetTempPath(), $"aw-squad-ledger-{Guid.NewGuid():N}.db");
        _memoryDb = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={_memoryDbPath}")
                .Options);
        _memoryDb.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton<IProjectStore>(_projects);
        services.AddSingleton(_memoryDb);
        services.AddSingleton(new DecisionLedgerSyncService(
            _memoryDb, _mergeLock, NullLogger<DecisionLedgerSyncService>.Instance));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _service = new SquadStateConsolidationService(
            scopeFactory, configuration,
            NullLogger<SquadStateConsolidationService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();
        await _memoryDb.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_memoryDbPath))
            File.Delete(_memoryDbPath);
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // =========================================================================
    // Repository-controlled Markdown is imported for review, never approved by a background tick.
    // =========================================================================
    [Fact]
    public async Task RunTick_ImportsRepositoryEntriesAsPendingReview_AndKeepsWorkingTreeClean()
    {
        var repoPath = CreateSquadRepo(
            decisions: "# Squad Decisions\n\n## existing\n",
            inbox: new()
            {
                ["dozer-first-decision.md"] = "# Dozer note\n\nExternalized Squad state.\n",
                ["trinity-second-decision.md"] = "# Trinity note\n\nUI polish decision.\n",
            });

        var project = await SeedActiveProjectAsync(repoPath);

        await _service.RunTickAsync(CancellationToken.None);

        using var repo = new Repository(repoPath);
        var decisions = ReadTreeText(repo, ".squad/decisions.md");

        decisions.Should().Be("# Team Decisions\n\n");
        (await _memoryDb.Decisions.CountAsync()).Should().Be(0);
        (await _memoryDb.DecisionInbox.CountAsync(entry => entry.Status == "pending")).Should().Be(3);

        // Repository entries remain in the review inbox until an Owner or Coordinator promotes them.
        repo.Head.Tip.Tree[".squad/decisions/inbox/dozer-first-decision.md"].Should().NotBeNull();
        repo.Head.Tip.Tree[".squad/decisions/inbox/trinity-second-decision.md"].Should().NotBeNull();

        // Working tree reconciled (no dangling staged/untracked changes for the touched paths).
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });
        status.IsDirty.Should().BeFalse(
            "the consolidation commit must reconcile the checked-out working tree so git status is clean");

        File.Exists(Path.Combine(repoPath, ".squad", "decisions", "inbox", "dozer-first-decision.md")).Should().BeTrue();
    }

    // =========================================================================
    // Re-running consolidation must not promote repository content without approval.
    // =========================================================================
    [Fact]
    public async Task Consolidate_LeavesRepositoryInboxPendingAcrossRepeatedPasses()
    {
        var repoPath = CreateSquadRepo(
            decisions: "# Squad Decisions\n",
            inbox: new() { ["dozer-idem.md"] = "# Dozer\n\nOnly-once content.\n" });

        var project = await SeedActiveProjectAsync(repoPath);

        var firstAppended = await _service.ConsolidateProjectAsync(project, CancellationToken.None);
        firstAppended.Should().Be(0);

        using (var repo = new Repository(repoPath))
        {
            ReadTreeText(repo, ".squad/decisions.md").Should().NotContain("Only-once content.");
        }

        var secondAppended = await _service.ConsolidateProjectAsync(project, CancellationToken.None);
        secondAppended.Should().Be(0);

        using (var repo = new Repository(repoPath))
        {
            ReadTreeText(repo, ".squad/decisions.md").Should().NotContain("Only-once content.");
        }
    }

    // =========================================================================
    // No inbox → nothing to do.
    // =========================================================================
    [Fact]
    public async Task Consolidate_NoInbox_IsNoOp()
    {
        var repoPath = CreateSquadRepo(decisions: "# Squad Decisions\n", inbox: new());
        var project = await SeedActiveProjectAsync(repoPath);

        var appended = await _service.ConsolidateProjectAsync(project, CancellationToken.None);

        appended.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentScribeAndConsolidation_KeepRepositoryContentPendingForReview()
    {
        var repoPath = CreateSquadRepo(
            decisions: "# Squad Decisions\n\n## existing\n\nExisting accepted decision.\n",
            inbox: new()
            {
                ["scribe-race.md"] = "# Race decision\n\nPreserve this accepted decision.\n",
            });
        var project = await SeedActiveProjectAsync(repoPath);

        await using var scribeDb = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={_memoryDbPath};Default Timeout=5")
                .Options);
        var scribeSync = new DecisionLedgerSyncService(
            scribeDb, _mergeLock, NullLogger<DecisionLedgerSyncService>.Instance);
        var scribe = new ScribeExportOperation(scribeSync);

        await Task.WhenAll(
            _service.ConsolidateProjectAsync(project, CancellationToken.None),
            scribe.ApplyAsync(
                project.Id.ToString(),
                repoPath,
                project.DefaultBranch,
                "scribe:race:export",
                CancellationToken.None));

        await scribeSync.ExportAndCommitAsync(
            project.Id.ToString(), repoPath, project.DefaultBranch, CancellationToken.None);
        (await _service.ConsolidateProjectAsync(project, CancellationToken.None)).Should().Be(0);

        await using var verify = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={_memoryDbPath}")
                .Options);
        var decisions = await verify.Decisions
            .Where(decision => decision.ProjectId == project.Id.ToString())
            .ToListAsync();
        decisions.Should().BeEmpty();
        (await verify.DecisionInbox.CountAsync(entry => entry.Status == "pending")).Should().Be(2);

        using var repo = new Repository(repoPath);
        var ledger = ReadTreeText(repo, ".squad/decisions.md");
        ledger.Should().Be("# Team Decisions\n\n");
        repo.Head.Tip.Tree[".squad/decisions/inbox/scribe-race.md"].Should().NotBeNull();
    }

    [Fact]
    public async Task Consolidate_ReportsConflictWithoutOverwritingEitherSource()
    {
        var repoPath = CreateSquadRepo(
            decisions: "# Squad Decisions\n",
            inbox: new()
            {
                ["shared-slug.md"] =
                    "---\nagent: repository\nslug: shared-slug\ntype: process\ntitle: Repository title\n---\n\nRepository content\n",
            });
        var project = await SeedActiveProjectAsync(repoPath);
        var now = DateTimeOffset.UtcNow;
        _memoryDb.DecisionInbox.Add(new DecisionInboxEntry
        {
            ProjectId = project.Id.ToString(),
            AgentName = "repository",
            Slug = "shared-slug",
            Type = "process",
            Title = "Database title",
            Content = "Database content",
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _memoryDb.SaveChangesAsync();

        (await _service.ConsolidateProjectAsync(project, CancellationToken.None)).Should().Be(0);

        (await _memoryDb.DecisionInbox.SingleAsync(entry => entry.Slug == "shared-slug"))
            .Content.Should().Be("Database content");
        File.ReadAllText(Path.Combine(
                repoPath, ".squad", "decisions", "inbox", "shared-slug.md"))
            .Should().Contain("Repository content");
        using var repo = new Repository(repoPath);
        ReadTreeText(repo, ".squad/decisions.md").Should().NotContain("Database content");
    }

    [Fact]
    public async Task Refresh_ReplacesExporterOwnedStaleMirrorAfterDatabaseDecisionUpdate()
    {
        var repoPath = CreateSquadRepo("# Team Decisions\n", new());
        var project = await SeedActiveProjectAsync(repoPath);
        var now = DateTimeOffset.UtcNow;
        var decision = new Decision
        {
            ProjectId = project.Id.ToString(),
            AgentName = "owner",
            Type = "technical",
            Status = "active",
            Title = "Stable identity",
            Content = "original content",
            SourceKind = MemorySourceKinds.Human,
            SourceIdentity = "owner",
            TrustState = MemoryTrustStates.Approved,
            ApprovedBy = "owner",
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _memoryDb.Decisions.Add(decision);
        await _memoryDb.SaveChangesAsync();
        var sync = new DecisionLedgerSyncService(_memoryDb, _mergeLock, NullLogger<DecisionLedgerSyncService>.Instance);
        await sync.RefreshAsync(project.Id.ToString(), repoPath, CancellationToken.None);

        decision.Content = "database updated content";
        decision.UpdatedAt = DateTimeOffset.UtcNow;
        await _memoryDb.SaveChangesAsync();
        await sync.RefreshAsync(project.Id.ToString(), repoPath, CancellationToken.None);

        File.ReadAllText(Path.Combine(repoPath, ".squad", "decisions.md"))
            .Should().Contain("database updated content")
            .And.NotContain("original content");
    }

    [Fact]
    public async Task Refresh_ReportsModifiedExporterOwnedMirrorWithoutOverwritingIt()
    {
        var repoPath = CreateSquadRepo("# Team Decisions\n", new());
        var project = await SeedActiveProjectAsync(repoPath);
        var now = DateTimeOffset.UtcNow;
        _memoryDb.Decisions.Add(new Decision
        {
            ProjectId = project.Id.ToString(),
            AgentName = "owner",
            Type = "technical",
            Status = "active",
            Title = "Stable identity",
            Content = "canonical content",
            SourceKind = MemorySourceKinds.Human,
            SourceIdentity = "owner",
            TrustState = MemoryTrustStates.Approved,
            ApprovedBy = "owner",
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _memoryDb.SaveChangesAsync();
        var sync = new DecisionLedgerSyncService(_memoryDb, _mergeLock, NullLogger<DecisionLedgerSyncService>.Instance);
        await sync.RefreshAsync(project.Id.ToString(), repoPath, CancellationToken.None);
        var ledgerPath = Path.Combine(repoPath, ".squad", "decisions.md");
        File.WriteAllText(ledgerPath, File.ReadAllText(ledgerPath).Replace(
            "canonical content", "externally modified content", StringComparison.Ordinal));

        var refresh = () => sync.RefreshAsync(project.Id.ToString(), repoPath, CancellationToken.None);

        await refresh.Should().ThrowAsync<MemoryLedgerExporter.DecisionLedgerConflictException>();
        File.ReadAllText(ledgerPath).Should().Contain("externally modified content");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<Project> SeedActiveProjectAsync(string repoPath)
    {
        var project = MakeProject() with { WorkingDirectory = repoPath, DefaultBranch = "main" };
        await _projects.InsertAsync(project);
        return project;
    }

    private string CreateSquadRepo(string decisions, Dictionary<string, string> inbox)
    {
        var repoPath = MakeTempDir("repo");
        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        WriteFile(repoPath, ".squad/decisions.md", decisions);
        foreach (var (name, content) in inbox)
            WriteFile(repoPath, $".squad/decisions/inbox/{name}", content);

        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("seed squad state", sig, sig);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        return repoPath;
    }

    private static void WriteFile(string repoPath, string relativePath, string content)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string ReadTreeText(Repository repo, string path)
    {
        var entry = repo.Head.Tip.Tree[path];
        if (entry?.Target is not Blob blob) return string.Empty;
        using var stream = blob.GetContentStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-squad621-svc-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
