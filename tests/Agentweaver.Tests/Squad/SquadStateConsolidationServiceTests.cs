using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
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

        var services = new ServiceCollection();
        services.AddSingleton<IProjectStore>(_projects);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _service = new SquadStateConsolidationService(
            scopeFactory, _mergeLock, configuration,
            NullLogger<SquadStateConsolidationService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // =========================================================================
    // A tick appends every inbox entry into decisions.md, clears the inbox, and
    // leaves the checked-out working tree clean.
    // =========================================================================
    [Fact]
    public async Task RunTick_AppendsInboxEntries_ClearsInbox_AndKeepsWorkingTreeClean()
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

        decisions.Should().Contain("Externalized Squad state.");
        decisions.Should().Contain("UI polish decision.");
        decisions.Should().Contain("## existing", "existing ledger content must be preserved");
        decisions.Should().Contain("<!-- squad-consolidated:", "each entry gets a content-addressed marker");

        // Inbox entries removed from the committed tree.
        repo.Head.Tip.Tree[".squad/decisions/inbox/dozer-first-decision.md"].Should().BeNull();
        repo.Head.Tip.Tree[".squad/decisions/inbox/trinity-second-decision.md"].Should().BeNull();

        // Working tree reconciled (no dangling staged/untracked changes for the touched paths).
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });
        status.IsDirty.Should().BeFalse(
            "the consolidation commit must reconcile the checked-out working tree so git status is clean");

        // The inbox files are gone from disk too.
        File.Exists(Path.Combine(repoPath, ".squad", "decisions", "inbox", "dozer-first-decision.md")).Should().BeFalse();
    }

    // =========================================================================
    // Re-running consolidation is a no-op: no duplicated content, nothing to do.
    // =========================================================================
    [Fact]
    public async Task Consolidate_IsIdempotent_SecondPassIsNoOp()
    {
        var repoPath = CreateSquadRepo(
            decisions: "# Squad Decisions\n",
            inbox: new() { ["dozer-idem.md"] = "# Dozer\n\nOnly-once content.\n" });

        var project = await SeedActiveProjectAsync(repoPath);

        var firstAppended = await _service.ConsolidateProjectAsync(project, CancellationToken.None);
        firstAppended.Should().Be(1);

        using (var repo = new Repository(repoPath))
        {
            var occurrences = CountOccurrences(ReadTreeText(repo, ".squad/decisions.md"), "Only-once content.");
            occurrences.Should().Be(1);
        }

        var secondAppended = await _service.ConsolidateProjectAsync(project, CancellationToken.None);
        secondAppended.Should().Be(0, "the inbox is already drained; a re-tick appends nothing");

        using (var repo = new Repository(repoPath))
        {
            var occurrences = CountOccurrences(ReadTreeText(repo, ".squad/decisions.md"), "Only-once content.");
            occurrences.Should().Be(1, "content must never be duplicated across ticks");
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
