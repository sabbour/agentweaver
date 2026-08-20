using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Workflow;

/// <summary>
/// Regression coverage for issue #539: the DB-backed team ledger (<c>.squad/decisions.md</c> and
/// friends) must be materialized into the RUN'S git worktree at commit time so it rides the same
/// commit/push flow as the run's other deliverables. Previously the mirror was only written into the
/// project's base working directory, which is never part of a committed/pushed run branch, so users
/// never saw their decisions/memory persisted in the repository.
/// </summary>
public sealed class WorktreeMemoryMirrorTests : IDisposable
{
    private readonly string _repoPath;
    private readonly string _basePath;
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public WorktreeMemoryMirrorTests()
    {
        _repoPath = MakeTempDir("repo");
        _basePath = MakeTempDir("worktrees");

        Repository.Init(_repoPath);
        using (var repo = new Repository(_repoPath))
        {
            File.WriteAllText(Path.Combine(_repoPath, "readme.txt"), "initial");
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", sig, sig);
            if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
                repo.Branches.Rename(repo.Head, "main");
        }

        // Shared in-memory SQLite kept alive for the lifetime of the test so scoped MemoryDbContext
        // instances resolved through the scope factory observe the seeded rows.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(opts => opts.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        }
    }

    [Fact]
    public void CommitChanges_MirrorsTeamLedgerIntoWorktreeAndCommitsIt()
    {
        var runId = RunId.New();
        var projectId = ProjectId.New();

        var manager = CreateWorktreeManager();
        var worktree = manager.AddWorktree(_repoPath, "main", runId);

        // Seed an active decision that the mirror must materialize.
        SeedDecision(projectId.ToString(), "morpheus", "Adopt event sourcing", "All writes go through the log.");

        // A run whose worktree is the per-run worktree and whose project owns the seeded decision.
        var runStore = new StubRunStore(new Run
        {
            Id = runId,
            RepositoryPath = _repoPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "do work",
            SubmittingUser = "tester",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktree.WorktreePath,
            ProjectId = projectId,
        });

        var adapter = new WorktreeOperationsAdapter(
            manager,
            new RunStreamStore(),
            new ScopeFactory(_provider, runStore),
            NullLogger<WorktreeOperationsAdapter>.Instance);

        // Simulate the agent producing a deliverable in the worktree.
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "feature.txt"), "the deliverable");

        var treeHash = adapter.CommitChanges(worktree.WorktreePath, runId.ToString());

        // The ledger mirror must exist on disk in the WORKTREE (not the base checkout)...
        var decisionsPath = Path.Combine(worktree.WorktreePath, ".squad", "decisions.md");
        File.Exists(decisionsPath).Should().BeTrue(
            "the ledger must be mirrored into the run's worktree so it rides the commit");
        File.ReadAllText(decisionsPath).Should().Contain("Adopt event sourcing");

        // ...and it must be part of the committed tree, alongside the deliverable.
        treeHash.Should().NotBeNullOrEmpty();
        using var committed = new Repository(worktree.WorktreePath);
        var tree = committed.Head.Tip!.Tree;
        ResolveTreeEntry(tree, ".squad/decisions.md").Should().NotBeNull(
            "the mirrored decisions.md must be committed so it is pushed with the run");
        ResolveTreeEntry(tree, "feature.txt").Should().NotBeNull();
    }

    [Fact]
    public void CommitChanges_WithNoLedgerContent_DoesNotInjectEmptyDecisionsFile()
    {
        var runId = RunId.New();
        var projectId = ProjectId.New();

        var manager = CreateWorktreeManager();
        var worktree = manager.AddWorktree(_repoPath, "main", runId);

        // No decisions/memory seeded for this project → nothing to mirror.
        var runStore = new StubRunStore(new Run
        {
            Id = runId,
            RepositoryPath = _repoPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "do work",
            SubmittingUser = "tester",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktree.WorktreePath,
            ProjectId = projectId,
        });

        var adapter = new WorktreeOperationsAdapter(
            manager,
            new RunStreamStore(),
            new ScopeFactory(_provider, runStore),
            NullLogger<WorktreeOperationsAdapter>.Instance);

        File.WriteAllText(Path.Combine(worktree.WorktreePath, "feature.txt"), "the deliverable");
        adapter.CommitChanges(worktree.WorktreePath, runId.ToString());

        File.Exists(Path.Combine(worktree.WorktreePath, ".squad", "decisions.md")).Should().BeFalse(
            "repositories that never used the memory feature must not be polluted with an empty decisions.md");
    }

    private WorktreeManager CreateWorktreeManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = _basePath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
            })
            .Build();
        return new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    private void SeedDecision(string projectId, string agentName, string title, string content)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.Decisions.Add(new Decision
        {
            ProjectId = projectId,
            AgentName = agentName,
            Type = "architectural",
            Status = "active",
            Title = title,
            Content = content,
            SourceKind = MemorySourceKinds.Human,
            SourceIdentity = "test-owner",
            TrustState = MemoryTrustStates.Approved,
            ApprovedBy = "test-owner",
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    private static TreeEntry? ResolveTreeEntry(Tree tree, string path)
    {
        TreeEntry? entry = null;
        Tree? current = tree;
        foreach (var segment in path.Split('/'))
        {
            if (current is null) return null;
            entry = current[segment];
            if (entry is null) return null;
            current = entry.TargetType == TreeEntryTargetType.Tree ? (Tree)entry.Target : null;
        }
        return entry;
    }

    private static string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-mirror-test-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        foreach (var dir in new[] { _repoPath, _basePath })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Scope factory that also injects a stub <see cref="IRunStore"/> into each scope, so the
    /// adapter can resolve both the run and the (real SQLite) memory context from one provider.</summary>
    private sealed class ScopeFactory(ServiceProvider inner, IRunStore runStore) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), runStore);

        private sealed class Scope(IServiceScope inner, IRunStore runStore) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(IRunStore) ? runStore : inner.ServiceProvider.GetService(serviceType);

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>Minimal <see cref="IRunStore"/> stub — only <see cref="GetAsync"/> is exercised.</summary>
    private sealed class StubRunStore(Run run) : IRunStore
    {
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) =>
            Task.FromResult<Run?>(runId == run.Id ? run : null);

        public Task<bool> TrySetTerminalStatusAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default) => throw new NotImplementedException();

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
