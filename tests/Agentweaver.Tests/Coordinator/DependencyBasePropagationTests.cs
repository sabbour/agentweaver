using System.Reflection;
using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Run = Agentweaver.Domain.Run;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Issue #197 dependency-base propagation fix. The bug: <c>run.Diff</c> is a best-effort DISPLAY
/// string that <see cref="WorktreeOperationsAdapter.GetDiff"/> can return EMPTY for even after a real
/// commit (it swallows all exceptions). Both the dependency-base rebuild
/// (<see cref="CoordinatorDispatchService"/>) and the final collective assembly
/// (<see cref="CoordinatorAssemblyService"/>) used to gate branch inclusion on
/// <c>!string.IsNullOrEmpty(run.Diff)</c>, silently DROPPING committed child branches whose display
/// diff was swallowed. The authoritative artifact is the committed worktree BRANCH (tip tree ==
/// <see cref="Run.TreeHash"/>).
///
/// These tests exercise the FIXED behaviour end-to-end against a real temp git repo, a real
/// <see cref="SqliteRunStore"/> and a real EF <see cref="MemoryDbContext"/>. They genuinely fail
/// against the pre-fix code (which required a non-empty diff to include a branch).
/// </summary>
public sealed class DependencyBasePropagationTests : IAsyncDisposable
{
    private readonly List<string> _tempRepoDirs = [];
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly WorktreeManager _worktree =
        new(new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

    public DependencyBasePropagationTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // WorktreeManager validity helpers (BLOCKING #2/#3) — pure git.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BranchTipMatchesTree_TrueWhenTipTreeMatches_FalseWhenStale()
    {
        var repo = CreateTempGitRepo();
        var (branch, treeSha) = CommitChildBranch(repo, "agentweaver/child-a", "app.cs", "v1");

        _worktree.BranchTipMatchesTree(repo, branch, treeSha).Should().BeTrue();
        _worktree.BranchTipMatchesTree(repo, branch, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef").Should().BeFalse(
            "a recorded tree hash that does not match the branch tip means the branch is stale/diverged");
        _worktree.BranchTipMatchesTree(repo, branch, null).Should().BeTrue("empty contract passes on existence alone");
        _worktree.BranchTipMatchesTree(repo, "agentweaver/missing", treeSha).Should().BeFalse();
    }

    [Fact]
    public void BranchContains_TrueWhenHeadReachable_FalseWhenNot()
    {
        var repo = CreateTempGitRepo();
        var (childA, _) = CommitChildBranch(repo, "agentweaver/child-a", "a.cs", "a");
        var (childB, _) = CommitChildBranch(repo, "agentweaver/child-b", "b.cs", "b");

        _worktree.BuildIntegrationBranch(repo, "main", "agentweaver/integration/coord-1", new[] { childA })
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);

        var aTip = _worktree.GetBranchTipCommitSha(repo, childA)!;
        var bTip = _worktree.GetBranchTipCommitSha(repo, childB)!;

        _worktree.BranchContains(repo, "agentweaver/integration/coord-1", aTip).Should().BeTrue(
            "child-a was merged into the integration branch");
        _worktree.BranchContains(repo, "agentweaver/integration/coord-1", bTip).Should().BeFalse(
            "child-b was never merged");
        _worktree.BranchContains(repo, "agentweaver/integration/coord-1", "").Should().BeTrue(
            "an empty candidate is vacuously contained");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // DependencyBranchInclusion.Evaluate — the inclusion authority (root-cause of #197).
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inclusion_IncludesCommittedChild_EvenWhenDiffWouldBeEmpty()
    {
        var repo = CreateTempGitRepo();
        var (branch, treeSha) = CommitChildBranch(repo, "agentweaver/child-a", "app.cs", "real work");

        // Diff is intentionally not even a parameter: inclusion is decided purely from branch validity.
        DependencyBranchInclusion.Evaluate(_worktree, repo, branch, treeSha)
            .Should().Be(BranchInclusionOutcome.Include);
    }

    [Fact]
    public void Inclusion_ExcludesMissingBranch_AndTreeMismatch_AndIncludesNoOp()
    {
        var repo = CreateTempGitRepo();
        var (branch, treeSha) = CommitChildBranch(repo, "agentweaver/child-a", "app.cs", "real work");
        var noOpTreeSha = OriginTreeSha(repo); // a no-op child: tip tree == origin tree

        DependencyBranchInclusion.Evaluate(_worktree, repo, null, treeSha)
            .Should().Be(BranchInclusionOutcome.ExcludeMissingBranch);
        DependencyBranchInclusion.Evaluate(_worktree, repo, "agentweaver/missing", treeSha)
            .Should().Be(BranchInclusionOutcome.ExcludeMissingBranch);
        DependencyBranchInclusion.Evaluate(_worktree, repo, branch, noOpTreeSha)
            .Should().Be(BranchInclusionOutcome.ExcludeTreeMismatch,
                "a recorded tree hash that does not match the branch tip is a stale/diverged branch");

        // Genuine no-op: the committed branch is at origin (no changes). It is still a VALID artifact and
        // must be included (BuildIntegrationBranch no-ops it) — never dropped, never hangs.
        var noOpBranch = CreateBranchAtOrigin(repo, "agentweaver/child-noop");
        DependencyBranchInclusion.Evaluate(_worktree, repo, noOpBranch, OriginTreeSha(repo))
            .Should().Be(BranchInclusionOutcome.Include);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // RebuildDependencyBaseBranchAsync (#1) — end-to-end via reflection with real store + repo.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rebuild_IncludesEmptyDiffCommittedDependency_AndFilesReachBase()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-rebuild-1";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 2);

        // Dependency #0 committed a real file but its display diff was swallowed (empty string).
        var (depBranch, depTree) = CommitChildBranch(repo, "agentweaver/dep", "impl.cs", "the app");
        var depRunId = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[0], depBranch, depTree, diff: "");
        await SetChildRunIdAsync(subtaskIds[0], depRunId);

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Pending,
        };
        var edges = new List<(int, int)> { (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        await InvokeRebuildAsync(sut, Context(coordRunId, repo), planId, statusById, edges);

        var integration = CoordinatorAssemblyService.IntegrationBranchName(coordRunId);
        using var r = new Repository(repo);
        var tip = r.Branches[integration]!.Tip;
        tip["impl.cs"].Should().NotBeNull(
            "the committed dependency must be included by branch validity even though run.Diff was empty (#197)");
    }

    [Fact]
    public async Task Rebuild_MultiDependency_MergedInTopologicalOrder()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-rebuild-multi";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 3);

        var (depA, treeA) = CommitChildBranch(repo, "agentweaver/dep-a", "a.cs", "A");
        var (depB, treeB) = CommitChildBranch(repo, "agentweaver/dep-b", "b.cs", "B");
        var runA = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[0], depA, treeA, diff: "");
        var runB = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[1], depB, treeB, diff: "");
        await SetChildRunIdAsync(subtaskIds[0], runA);
        await SetChildRunIdAsync(subtaskIds[1], runB);

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Completed,
            [subtaskIds[2]] = SubtaskStatus.Pending,
        };
        // #2 depends on #1 depends on #0.
        var edges = new List<(int, int)> { (subtaskIds[2], subtaskIds[1]), (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        await InvokeRebuildAsync(sut, Context(coordRunId, repo), planId, statusById, edges);

        using var r = new Repository(repo);
        var tip = r.Branches[CoordinatorAssemblyService.IntegrationBranchName(coordRunId)]!.Tip;
        tip["a.cs"].Should().NotBeNull("both dependency branches must be merged into the base");
        tip["b.cs"].Should().NotBeNull();
    }

    [Fact]
    public async Task Rebuild_ExcludesStaleMismatchedDependencyBranch()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-rebuild-stale";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 2);

        var (depBranch, _) = CommitChildBranch(repo, "agentweaver/dep", "impl.cs", "the app");
        // Record a WRONG tree hash (stale/mismatch vs the branch tip) — must be excluded loudly.
        var depRunId = await SeedAssembleReadyChildAsync(
            coordRunId, subtaskIds[0], depBranch, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", diff: "");
        await SetChildRunIdAsync(subtaskIds[0], depRunId);

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Pending,
        };
        var edges = new List<(int, int)> { (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        await InvokeRebuildAsync(sut, Context(coordRunId, repo), planId, statusById, edges);

        using var r = new Repository(repo);
        var tip = r.Branches[CoordinatorAssemblyService.IntegrationBranchName(coordRunId)]!.Tip;
        tip["impl.cs"].Should().BeNull("a branch whose tip tree != recorded handoff hash must not be propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // ResolveChildBaseBranchAsync (#4) — mandatory contains-check + repair.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_RepairsIncompleteIntegrationBranch_BeforeReturning()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-resolve-repair";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 2);

        var (depBranch, depTree) = CommitChildBranch(repo, "agentweaver/dep", "impl.cs", "the app");
        var depRunId = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[0], depBranch, depTree, diff: "");
        await SetChildRunIdAsync(subtaskIds[0], depRunId);

        // Build an INCOMPLETE integration branch (missing the dependency head) to simulate a
        // clobbered/stale rebuild that a concurrent writer left behind.
        var integration = CoordinatorAssemblyService.IntegrationBranchName(coordRunId);
        _worktree.BuildIntegrationBranch(repo, "main", integration, Array.Empty<string>())
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using (var pre = new Repository(repo))
            pre.Branches[integration]!.Tip["impl.cs"].Should().BeNull("precondition: base is missing the dependency");

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Pending,
        };
        var edges = new List<(int, int)> { (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        var resolved = await InvokeResolveAsync(sut, Context(coordRunId, repo), planId, subtaskIds[1], statusById, edges);

        resolved.Should().Be(integration, "a repairable base must be repaired and returned, not blocked");
        using var r = new Repository(repo);
        r.Branches[integration]!.Tip["impl.cs"].Should().NotBeNull(
            "the contains-check must repair the integration branch to include the required dependency head");
    }

    [Fact]
    public async Task Resolve_NoOpDependency_DispatchProceeds()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-resolve-noop";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 2);

        // Dependency committed NO changes (branch at origin). Its diff is empty and it is a no-op.
        var noOpBranch = CreateBranchAtOrigin(repo, "agentweaver/dep-noop");
        var depRunId = await SeedAssembleReadyChildAsync(
            coordRunId, subtaskIds[0], noOpBranch, OriginTreeSha(repo), diff: "");
        await SetChildRunIdAsync(subtaskIds[0], depRunId);

        var integration = CoordinatorAssemblyService.IntegrationBranchName(coordRunId);
        _worktree.BuildIntegrationBranch(repo, "main", integration, new[] { noOpBranch })
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Pending,
        };
        var edges = new List<(int, int)> { (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        var resolved = await InvokeResolveAsync(sut, Context(coordRunId, repo), planId, subtaskIds[1], statusById, edges);

        resolved.Should().Be(integration, "a genuine no-op dependency must not block or hang dispatch");
    }

    [Fact]
    public async Task Resolve_UsesNewTip_AfterInPlaceSteerRecommit()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-resolve-steer";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 2);

        // First assemble_ready with an original commit.
        var (depBranch, tree1) = CommitChildBranch(repo, "agentweaver/dep", "impl.cs", "v1");
        var depRunId = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[0], depBranch, tree1, diff: "");
        await SetChildRunIdAsync(subtaskIds[0], depRunId);

        // In-place steer: the SAME branch is re-committed with a NEW tip, and the run row's TreeHash is
        // updated to the new tip tree (mirrors ExecuteInPlaceSteerAsync flipping back to InProgress and
        // re-reaching assemble_ready with a new commit).
        var tree2 = AmendChildBranch(repo, depBranch, "impl.cs", "v2 - steered");
        tree2.Should().NotBe(tree1);
        await ReAssembleReadyChildAsync(depRunId, depBranch, tree2, diff: "");

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.AssembleReady,
            [subtaskIds[1]] = SubtaskStatus.Pending,
        };
        var edges = new List<(int, int)> { (subtaskIds[1], subtaskIds[0]) };

        var sut = BuildDispatch(repo);
        // A stale integration branch exists but is missing the (new) dependency head -> Resolve repairs
        // it, and the repair must use the NEW steered tip, not the original one.
        var integration = CoordinatorAssemblyService.IntegrationBranchName(coordRunId);
        _worktree.BuildIntegrationBranch(repo, "main", integration, Array.Empty<string>())
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);

        var resolved = await InvokeResolveAsync(sut, Context(coordRunId, repo), planId, subtaskIds[1], statusById, edges);

        resolved.Should().Be(integration);
        using var r = new Repository(repo);
        var newTip = _worktree.GetBranchTipCommitSha(repo, depBranch)!;
        _worktree.BranchContains(repo, integration, newTip).Should().BeTrue(
            "the rebuilt base must contain the NEW steered tip, not a stale one");
        ReadBlob(r, r.Branches[integration]!.Tip["impl.cs"]).Should().Be("v2 - steered");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // BuildAssemblyInputsAsync (BLOCKING #1) — final collective assembly inclusion.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinalAssembly_IncludesEmptyDiffCommittedChild()
    {
        var repo = CreateTempGitRepo();
        var coordRunId = "coord-final-assembly";
        var (planId, subtaskIds) = await SeedPlanAsync(coordRunId, 1);

        var (branch, tree) = CommitChildBranch(repo, "agentweaver/child", "impl.cs", "the app");
        var runId = await SeedAssembleReadyChildAsync(coordRunId, subtaskIds[0], branch, tree, diff: "");
        await SetChildRunIdAsync(subtaskIds[0], runId);

        var subtasks = await LoadSubtasksAsync(planId);
        var edges = new List<(int, int)>();

        var sut = BuildAssembly(repo);
        var (branchesInOrder, includedIds) = await InvokeBuildAssemblyInputsAsync(
            sut, Context(coordRunId, repo), subtasks, edges);

        includedIds.Should().Contain(subtaskIds[0],
            "the final collective assembly must include a committed child even when its display diff was swallowed (#197)");
        branchesInOrder.Should().Contain(branch);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Reflection + harness helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private CoordinatorDispatchService BuildDispatch(string repoPath)
    {
        _ = repoPath;
        return new CoordinatorDispatchService(
            _runStore,
            _streamStore,
            orchestrator: null!,
            worktreeManager: _worktree,
            steering: null!,
            new NoopAssembly(),
            _scopeFactory,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance);
    }

    private CoordinatorAssemblyService BuildAssembly(string repoPath)
    {
        _ = repoPath;
        return new CoordinatorAssemblyService(
            _runStore,
            _streamStore,
            assemblyStore: null!,
            reviewGate: null!,
            pipeline: null!,
            _scopeFactory,
            _provider,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorAssemblyService>.Instance,
            worktreeManager: _worktree);
    }

    private static async Task InvokeRebuildAsync(
        CoordinatorDispatchService sut,
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int, int)> edges)
    {
        var m = typeof(CoordinatorDispatchService).GetMethod(
            "RebuildDependencyBaseBranchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)m.Invoke(sut, new object[] { context, workPlanId, statusById, edges, CancellationToken.None })!;
    }

    private static async Task<string?> InvokeResolveAsync(
        CoordinatorDispatchService sut,
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int, int)> edges)
    {
        var m = typeof(CoordinatorDispatchService).GetMethod(
            "ResolveChildBaseBranchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<string?>)m.Invoke(
            sut, new object[] { context, workPlanId, subtaskId, statusById, edges, CancellationToken.None })!;
        return await task;
    }

    private static async Task<(List<string> Branches, List<int> Included)> InvokeBuildAssemblyInputsAsync(
        CoordinatorAssemblyService sut,
        CoordinatorDispatchContext context,
        IReadOnlyCollection<Subtask> subtasks,
        IReadOnlyCollection<(int, int)> edges)
    {
        var m = typeof(CoordinatorAssemblyService).GetMethod(
            "BuildAssemblyInputsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)m.Invoke(sut, new object[] { context, subtasks, edges, CancellationToken.None })!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var branches = (List<string>)result.GetType().GetProperty("BranchesInOrder")!.GetValue(result)!;
        var included = (List<int>)result.GetType().GetProperty("IncludedSubtaskIds")!.GetValue(result)!;
        return (branches, included);
    }

    private static CoordinatorDispatchContext Context(string coordRunId, string repoPath) =>
        new(coordRunId, repoPath, "main", "owner", null);

    private async Task<(int PlanId, List<int> SubtaskIds)> SeedPlanAsync(string coordinatorRunId, int subtaskCount)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Status = WorkPlanStatus.Dispatching,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        foreach (var i in Enumerable.Range(0, subtaskCount))
        {
            var subtask = new Subtask
            {
                WorkPlanId = plan.Id,
                Title = $"t{i}",
                Scope = "s",
                AssignedAgent = "morpheus",
                SelectedModelId = "gpt",
                Phase = "execution",
                IsolationStrategy = "worktree",
                Status = SubtaskStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            ids.Add(subtask.Id);
        }

        return (plan.Id, ids);
    }

    private async Task<List<Subtask>> LoadSubtasksAsync(int planId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().Where(s => s.WorkPlanId == planId).ToListAsync();
    }

    private async Task SetChildRunIdAsync(int subtaskId, string childRunId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        row.ChildRunId = childRunId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedAssembleReadyChildAsync(
        string coordinatorRunId, int subtaskId, string worktreeBranch, string treeHash, string diff)
    {
        var id = RunId.New();
        await _runStore.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = coordinatorRunId,
            SubtaskId = subtaskId.ToString(),
        });
        // Atomic write of tree_hash + worktree_branch + diff (mirrors production assemble_ready).
        (await _runStore.SetAssembleReadyAsync(
            id, treeHash, worktreeBranch, diff, 1, DateTimeOffset.UtcNow)).Should().BeTrue();
        return id.ToString();
    }

    private async Task ReAssembleReadyChildAsync(string runId, string worktreeBranch, string treeHash, string diff)
    {
        // Simulate an in-place steer re-commit: reset to InProgress then re-set assemble_ready with the
        // NEW tip tree hash on the SAME branch.
        var id = RunId.Parse(runId);
        await _runStore.UpdateStatusAsync(id, RunStatus.InProgress, null);
        (await _runStore.SetAssembleReadyAsync(
            id, treeHash, worktreeBranch, diff, 2, DateTimeOffset.UtcNow)).Should().BeTrue();
    }

    // ── git helpers (mirror IntegrationBranchBuilderTests) ────────────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-depbase-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");
        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);
        return repoPath;
    }

    /// <summary>Creates a branch off main with one commit adding a file; returns (branch, tipTreeSha).</summary>
    private static (string Branch, string TreeSha) CommitChildBranch(
        string repositoryPath, string branchName, string filePath, string fileContent)
    {
        var tree = WriteCommit(repositoryPath, branchName, filePath, fileContent, fromExistingTip: false);
        return (branchName, tree);
    }

    /// <summary>Adds a second commit to an existing branch; returns the new tip tree sha.</summary>
    private static string AmendChildBranch(
        string repositoryPath, string branchName, string filePath, string fileContent) =>
        WriteCommit(repositoryPath, branchName, filePath, fileContent, fromExistingTip: true);

    private static string WriteCommit(
        string repositoryPath, string branchName, string filePath, string fileContent, bool fromExistingTip)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"] ?? throw new InvalidOperationException("main not found");
        var branch = repo.Branches[branchName]
            ?? (fromExistingTip ? throw new InvalidOperationException($"{branchName} missing") : repo.CreateBranch(branchName, main.Tip));

        var tmp = Path.Combine(repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, fileContent, Encoding.UTF8);
        try
        {
            var blob = repo.ObjectDatabase.CreateBlob(tmp);
            var treeDef = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var commit = repo.ObjectDatabase.CreateCommit(
                sig, sig, "work", newTree, new[] { branch.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], commit.Id);
            return newTree.Sha;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string CreateBranchAtOrigin(string repositoryPath, string branchName)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"]!;
        repo.CreateBranch(branchName, main.Tip);
        return branchName;
    }

    private static string OriginTreeSha(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Branches["main"]!.Tip.Tree.Sha;
    }

    private static string ReadBlob(Repository repo, TreeEntry? entry)
    {
        entry.Should().NotBeNull();
        using var content = ((Blob)entry!.Target).GetContentStream();
        using var reader = new StreamReader(content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
        foreach (var dir in _tempRepoDirs)
        {
            try { DeleteDirectory(dir); }
            catch { /* best effort */ }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed class NoopAssembly : ICoordinatorAssembly
    {
        public void StartAssembly(CoordinatorDispatchContext context) { }
        public void EnsureFinalScribe(Run coordinatorRun) { }
        public bool IsAssemblyActive(string coordinatorRunId) => false;
        public void AbandonStaleReview(CoordinatorDispatchContext context) { }
        public void FailAssembly(CoordinatorDispatchContext context, string reason) { }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
