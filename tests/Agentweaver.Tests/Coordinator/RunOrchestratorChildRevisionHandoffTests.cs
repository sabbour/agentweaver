using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="RunOrchestrator.StartChildRevisionHandoffAsync"/> (Fix-A(3a) Path-2 —
/// conscious different-agent hand-off under the Reviewer Rejection Lockout Protocol). The launch
/// itself cannot complete here (no real workflow factory), so these tests assert the pre-launch
/// invariants that make the hand-off lockout-correct and context-complete:
/// <list type="bullet">
/// <item>REUSES the prior child's worktree/branch when it is present and unlocked (preserves prior work).</item>
/// <item>Falls back to a FRESH worktree branched from the prior branch when the prior worktree is missing.</item>
/// <item>Mints a NEW run identity (distinct from the locked-out author) → a distinct deterministic
///       SDK session id, and threads the accumulated review guidance into the new agent's task prompt.</item>
/// </list>
/// Real stores, no mocks (Principle VII): a real <see cref="SqliteRunStore"/> and a real
/// <see cref="WorktreeManager"/> over an on-disk git repo.
/// </summary>
public sealed class RunOrchestratorChildRevisionHandoffTests : IAsyncDisposable
{
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<string> _tempDirs = [];

    public RunOrchestratorChildRevisionHandoffTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task Handoff_ReusesPriorWorktree_InjectsGuidance_UnderNewRunIdentity()
    {
        var (repoPath, worktreesBase) = CreateRepository();
        var manager = BuildWorktreeManager(worktreesBase);
        var orchestrator = BuildOrchestrator(manager);

        // A prior (now locked-out) child left a real worktree on its own branch.
        var priorChildId = RunId.New();
        var priorInfo = manager.AddWorktree(repoPath, "main", priorChildId);
        var priorChild = NewChildRun("morpheus") with
        {
            Id = priorChildId,
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            WorktreePath = priorInfo.WorktreePath,
            WorktreeBranch = priorInfo.BranchName,
        };

        // The coordinator hands the revision to a DIFFERENT agent with the accumulated feedback.
        var newAgentRun = NewChildRun("trinity") with
        {
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            Task = "Original subtask: build the widget.",
        };
        var feedback = new AccumulatedReviewFeedback(
            SubtaskId: "7",
            CurrentChangeRequest: "Add tests.",
            PriorRounds: new[] { new ReviewFeedbackRound(1, "rubberduck", "No tests present.", DateTimeOffset.UtcNow) },
            PriorWorktreeBranch: priorInfo.BranchName,
            RenderedGuidance: "ACCUMULATED-GUIDANCE: address every prior round.");

        // Launch fails (no real workflow factory), but all pre-launch invariants are already applied.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            orchestrator.StartChildRevisionHandoffAsync(newAgentRun, priorChild, feedback, default));

        // A NEW run identity (distinct from the locked-out author) → distinct SDK session id.
        newAgentRun.Id.Should().NotBe(priorChildId, "the new agent must NOT resume the locked-out author's run/session");

        var persisted = await _runStore.GetAsync(newAgentRun.Id);
        persisted.Should().NotBeNull("the hand-off run row must be persisted under the new run id");
        persisted!.Status.Should().Be(RunStatus.InProgress);

        // Prior WORK is preserved: the prior worktree/branch is REUSED (no fresh worktree branched).
        persisted.WorktreePath.Should().Be(priorInfo.WorktreePath);
        persisted.WorktreeBranch.Should().Be(priorInfo.BranchName);

        // The accumulated guidance is threaded into the new agent's task prompt (full prior context).
        persisted.Task.Should().Contain("Original subtask: build the widget.");
        persisted.Task.Should().Contain("ACCUMULATED-GUIDANCE: address every prior round.");

        // A NEW stream entry keyed on the new run id records a VISIBLE hand-off event.
        var entry = _streamStore.Get(newAgentRun.Id.ToString());
        entry.Should().NotBeNull("the hand-off must open a NEW stream, not reuse the locked-out author's stream");
        var events = entry!.GetSnapshotSince(0).Events;
        var handoff = events.Should().ContainSingle(e => e.Type == "coordinator.child_revision_handoff").Subject;
        System.Text.Json.JsonSerializer.Serialize(handoff.Payload)
            .Should().Contain("reused_prior");
    }

    [Fact]
    public async Task Handoff_WhenPriorWorktreeMissing_FallsBackToFreshBranchFromPriorBranch()
    {
        var (repoPath, worktreesBase) = CreateRepository();
        var manager = BuildWorktreeManager(worktreesBase);
        var orchestrator = BuildOrchestrator(manager);

        // The prior child's physical worktree is GONE, but its committed work lives on a branch.
        var priorChild = NewChildRun("morpheus") with
        {
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            WorktreePath = Path.Combine(worktreesBase, "does-not-exist"),
            WorktreeBranch = "main",
        };

        var newAgentRun = NewChildRun("trinity") with
        {
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            Task = "Original subtask.",
        };
        var feedback = new AccumulatedReviewFeedback(
            SubtaskId: "7",
            CurrentChangeRequest: "Fix it.",
            PriorRounds: [],
            PriorWorktreeBranch: "main", // committed prior work base
            RenderedGuidance: "GUIDANCE-TEXT");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            orchestrator.StartChildRevisionHandoffAsync(newAgentRun, priorChild, feedback, default));

        var persisted = await _runStore.GetAsync(newAgentRun.Id);
        persisted.Should().NotBeNull();
        // A FRESH worktree branched from the prior branch — NOT the missing prior worktree path.
        persisted!.WorktreePath.Should().NotBe(priorChild.WorktreePath);
        persisted.WorktreeBranch.Should().NotBe("main", "the fallback branches a fresh worktree off the prior branch");
        persisted.Task.Should().Contain("GUIDANCE-TEXT");

        var entry = _streamStore.Get(newAgentRun.Id.ToString());
        entry.Should().NotBeNull();
        var events = entry!.GetSnapshotSince(0).Events;
        var handoff = events.Should().ContainSingle(e => e.Type == "coordinator.child_revision_handoff").Subject;
        System.Text.Json.JsonSerializer.Serialize(handoff.Payload)
            .Should().Contain("fresh_from_prior_branch");
    }

    private RunOrchestrator BuildOrchestrator(WorktreeManager manager) => new(
        _runStore,
        _streamStore,
        manager,
        workflowFactory: null!,
        registry: null!,
        watchLoop: null!,
        _scopeFactory,
        configuration: null!,
        NullLogger<RunOrchestrator>.Instance);

    private static Run NewChildRun(string agentName) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "child-repo",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "do the subtask",
        SubmittingUser = "alice",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        AgentName = agentName,
        ParentRunId = RunId.New().ToString(),
        SubtaskId = "7",
    };

    private (string RepoPath, string WorktreesBase) CreateRepository()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-handoff-repo-{Guid.NewGuid():N}");
        var worktreesBase = Path.Combine(Path.GetTempPath(), $"aw-handoff-wt-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);
        _tempDirs.Add(worktreesBase);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "init");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("init", sig, sig);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");
        Commands.Checkout(repo, repo.Head.Tip);

        return (repoPath, worktreesBase);
    }

    private static WorktreeManager BuildWorktreeManager(string worktreesBase)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = worktreesBase,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@test.com",
            })
            .Build();
        return new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
