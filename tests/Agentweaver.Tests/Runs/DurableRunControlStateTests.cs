using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Runs;

public sealed class DurableRunControlStateTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _providers = [];
    private readonly TestSqliteDb _runDb;
    private readonly IRunStore _runStore;

    public DurableRunControlStateTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
        _connectionString = $"DataSource=file:run-control-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var scope = NewProvider().CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void RunOptions_AreVisibleAcrossReplicas()
    {
        var replicaA = NewOptionsStore();
        var replicaB = NewOptionsStore();

        replicaA.SetAutoApproveTools("run-1", true);
        replicaB.Get("run-1").AutoApproveTools.Should().BeTrue();

        replicaB.SetAutopilot("run-1", true);
        replicaA.Get("run-1").Should().Be(new RunOptions(AutoApproveTools: true, Autopilot: true));

        replicaA.Clear("run-1");
        replicaB.Get("run-1").Should().Be(new RunOptions());
    }

    [Fact]
    public async Task ApprovalGrant_OnAnotherReplica_ResolvesWaitingRun()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();
        var run = await InsertOwnedRunAsync("owner");
        var runId = run.Id.ToString();

        var wait = owner.WaitForApprovalAsync(
            runId, "req-1", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(() => secondary.GrantAsync(runId, "req-1", ApprovalScope.Run));

        (await wait).Should().BeTrue();
        secondary.IsAutoApproved(runId, "web_fetch", "https://example.test/another-path").Should().BeTrue();
    }

    [Fact]
    public async Task RunScopedApproval_OnChild_IsVisibleToSiblingViaParent()
    {
        var child = NewApprovalGate();
        var sibling = NewApprovalGate();
        var parentRun = await InsertOwnedRunAsync("owner");
        var childRun = await InsertOwnedRunAsync("owner");
        var siblingRun = await InsertOwnedRunAsync("owner");
        var parentId = parentRun.Id.ToString();
        var childId = childRun.Id.ToString();
        var siblingId = siblingRun.Id.ToString();
        child.RegisterParentRun(childId, parentId);
        sibling.RegisterParentRun(siblingId, parentId);

        var wait = child.WaitForApprovalAsync(
            childId, "req-2", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => sibling.GrantAsync(childId, "req-2", ApprovalScope.Tool));

        (await wait).Should().BeTrue();
        sibling.IsAutoApproved(siblingId, "web_fetch", "https://other.test").Should().BeTrue();
    }

    [Fact]
    public async Task RunScopedApproval_OnChild_DoesNotOutliveFailedParent()
    {
        var parent = await InsertOwnedRunAsync("owner");
        var child = await InsertOwnedRunAsync("owner");
        var sibling = await InsertOwnedRunAsync("owner");
        var futureChild = await InsertOwnedRunAsync("owner");
        var gate = NewApprovalGate();
        var parentId = parent.Id.ToString();
        var childId = child.Id.ToString();
        var siblingId = sibling.Id.ToString();
        gate.RegisterParentRun(childId, parentId);
        gate.RegisterParentRun(siblingId, parentId);

        var wait = gate.WaitForApprovalAsync(
            childId, "failed-parent", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => gate.GrantAsync(childId, "failed-parent", ApprovalScope.Run));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(siblingId, "web_fetch", "https://before-failure.test").Should().BeTrue(
            "an active coordinator propagates its child's session policy to active siblings");

        await _runStore.UpdateStatusAsync(parent.Id, RunStatus.Failed, DateTimeOffset.UtcNow);

        (await _runStore.GetAsync(sibling.Id))!.Status.Should().Be(RunStatus.InProgress);
        gate.IsAutoApproved(siblingId, "web_fetch", "https://after-failure.test").Should().BeFalse(
            "an active sibling must not inherit a failed coordinator's stale policy");

        gate.RegisterParentRun(futureChild.Id.ToString(), parentId);
        gate.IsAutoApproved(futureChild.Id.ToString(), "web_fetch", "https://future-child.test").Should().BeFalse(
            "a future child must not inherit a failed coordinator's stale policy");
    }

    [Fact]
    public async Task RunScopedApproval_OnChild_DoesNotReviveAfterParentRecovery()
    {
        var parent = await InsertOwnedRunAsync("owner");
        var firstChild = await InsertOwnedRunAsync("owner");
        var recoveredChild = await InsertOwnedRunAsync("owner");
        var gate = NewApprovalGate();
        var parentId = parent.Id.ToString();

        gate.RegisterParentRun(firstChild.Id.ToString(), parentId);
        var wait = gate.WaitForApprovalAsync(
            firstChild.Id.ToString(), "before-terminalization", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);
        (await gate.GrantAsync(firstChild.Id.ToString(), "before-terminalization", ApprovalScope.Run)).Should().BeTrue();
        (await wait).Should().BeTrue();

        gate.RegisterParentRun(recoveredChild.Id.ToString(), parentId);
        gate.IsAutoApproved(recoveredChild.Id.ToString(), "web_fetch", "https://active.test").Should().BeTrue(
            "an active coordinator's session policy is inherited by newly dispatched children");

        await _runStore.UpdateStatusAsync(parent.Id, RunStatus.Failed, DateTimeOffset.UtcNow);
        await _runStore.UpdateStatusAsync(parent.Id, RunStatus.InProgress, endedAt: null);

        var postRecoveryChild = await InsertOwnedRunAsync("owner");
        gate.RegisterParentRun(postRecoveryChild.Id.ToString(), parentId);
        gate.IsAutoApproved(postRecoveryChild.Id.ToString(), "web_fetch", "https://recovered.test").Should().BeFalse(
            "the coordinator lifecycle generation advances on terminalization, preventing pre-terminal policies from authorizing recovered children");
    }

    [Fact]
    public async Task RunScopedApproval_OnChild_FailsWhenParentIsNoLongerActive()
    {
        var parent = await InsertOwnedRunAsync("owner");
        var child = await InsertOwnedRunAsync("owner");
        var sibling = await InsertOwnedRunAsync("owner");
        var gate = NewApprovalGate();
        await _runStore.UpdateReviewReadyAsync(parent.Id, "tree", "diff", 1);
        gate.RegisterParentRun(child.Id.ToString(), parent.Id.ToString());

        var wait = gate.WaitForApprovalAsync(
            child.Id.ToString(), "inactive-parent", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);

        (await gate.GrantAsync(child.Id.ToString(), "inactive-parent", ApprovalScope.Run)).Should().BeFalse(
            "a run-scoped child grant would write a policy to its parent, which is awaiting review");
        gate.Deny(child.Id.ToString(), "inactive-parent").Should().BeTrue();
        (await wait).Should().BeFalse();

        (await _runStore.TryTransitionReviewToInProgressAsync(parent.Id)).Should().BeTrue();
        gate.RegisterParentRun(sibling.Id.ToString(), parent.Id.ToString());
        gate.IsAutoApproved(sibling.Id.ToString(), "web_fetch", "https://later-child.test").Should().BeFalse(
            "a late grant must not leave a durable parent policy for children created after the parent resumes");
    }

    [Fact]
    public async Task ResolvedOrClearedRequests_DoNotApproveAgain()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();

        var approved = owner.WaitForApprovalAsync(
            "run-3", "req-3", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => secondary.GrantAsync("run-3", "req-3", ApprovalScope.Once));

        (await approved).Should().BeTrue();
        (await secondary.GrantAsync("run-3", "req-3", ApprovalScope.Once)).Should().BeFalse();
        secondary.Deny("run-3", "req-3").Should().BeFalse();

        var cleared = owner.WaitForApprovalAsync(
            "run-4", "req-4", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(async () =>
        {
            secondary.Clear("run-4");
            await Task.CompletedTask;
            return true;
        });

        (await cleared).Should().BeFalse();
        (await secondary.GrantAsync("run-4", "req-4", ApprovalScope.Once)).Should().BeFalse();
    }

    [Fact]
    public async Task TimeoutLosingClaim_ReturnsWinningApproval()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();
        var wait = owner.WaitForApprovalAsync(
            "run-timeout-grant", "req-timeout-grant", "web_fetch", "https://example.test",
            TimeSpan.FromMilliseconds(200), default);

        await WaitUntilAsync(() =>
            secondary.GrantAsync("run-timeout-grant", "req-timeout-grant", ApprovalScope.Once));

        (await wait).Should().BeTrue("a timeout loser must return the winning approval resolution");
        owner.GetRequestState("run-timeout-grant", "req-timeout-grant")
            .Should().Be(ToolApprovalRequestState.Approved);
    }

    [Fact]
    public async Task TimedOutApproval_RemainsDeniedAndExpired()
    {
        var gate = NewApprovalGate();

        var approved = await gate.WaitForApprovalAsync(
            "run-timeout-deny", "req-timeout-deny", "web_fetch", "https://example.test",
            TimeSpan.FromMilliseconds(50), default);

        approved.Should().BeFalse();
        gate.GetRequestState("run-timeout-deny", "req-timeout-deny")
            .Should().Be(ToolApprovalRequestState.Expired);
    }

    [Fact]
    public async Task AlwaysApproval_IsVisibleToFutureRunForSameOwner_AfterSourceClear()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();
        var project = ProjectId.New();
        var sourceRun = await InsertOwnedRunAsync("alice", project);
        var futureRun = await InsertOwnedRunAsync("alice", project);
        var sourceId = sourceRun.Id.ToString();

        var wait = owner.WaitForApprovalAsync(
            sourceId, "req-5", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => secondary.GrantAsync(sourceId, "req-5", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        secondary.Clear(sourceId);
        owner.IsAutoApproved(
            futureRun.Id.ToString(), "web_fetch", "https://example.test/another-path").Should().BeTrue();
    }

    [Fact]
    public async Task AlwaysApproval_ByAlice_DoesNotAutoApproveBobsPersistedRun()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        IRunStore runStore = new SqliteRunStore(testDb.Db);
        var project = ProjectId.New();
        var aliceRun = NewOwnedRun("alice", project);
        var bobRun = NewOwnedRun("bob", project);
        await runStore.InsertAsync(aliceRun);
        await runStore.InsertAsync(bobRun);

        var gate = NewApprovalGate(runStore);
        var wait = gate.WaitForApprovalAsync(
            aliceRun.Id.ToString(), "req-alice", "web_fetch", "https://example.test/alice",
            TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() =>
            gate.GrantAsync(aliceRun.Id.ToString(), "req-alice", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        (await runStore.GetAsync(aliceRun.Id))!.SubmittingUser.Should().Be("alice");
        (await runStore.GetAsync(bobRun.Id))!.SubmittingUser.Should().Be("bob");
        gate.IsAutoApproved(bobRun.Id.ToString(), "web_fetch", "https://example.test/bob")
            .Should().BeFalse("Alice's Always approval must not authorize Bob's persisted run");
    }

    [Fact]
    public async Task LegacyGlobalAndUnscopedOwnerBucketGrants_AuthorizeNobody()
    {
        var project = ProjectId.New();
        var aliceRun = await InsertOwnedRunAsync("alice", project);
        var state = NewState();
        state.Append(
            "__agentweaver_tool_approvals__",
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });
        state.Append(
            "__agentweaver_tool_approvals_owner_sha256_v1__legacy",
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });
        state.Append(
            "__agentweaver_tool_approvals_owner_sha256_v1__legacy",
            "tool.approval_policy_granted",
            new { owner = "alice", toolId = "web_fetch", riskSemantics = "network-write/v1" });
        state.Append(
            "__agentweaver_tool_approvals_owner_sha256_v1__legacy",
            "tool.approval_policy_granted",
            new { owner = "Alice", toolId = "web_fetch", riskSemantics = "network-read/v1" });

        var gate = NewApprovalGate();

        gate.IsAutoApproved(aliceRun.Id.ToString(), "web_fetch", "https://example.test")
            .Should().BeFalse();
    }

    [Fact]
    public async Task AlwaysApproval_EmptyPersistedOwner_ApprovesRequestButPolicyFailsClosed()
    {
        // The run exists and is active, so PR #972 finding 2's active-run requirement is
        // satisfied; the pending request itself is approved. But its persisted owner is empty,
        // so no subject can be resolved -- the broader durable "always" policy must still fail
        // closed rather than apply to an unidentified owner.
        var runId = (await InsertOwnedRunAsync("")).Id.ToString();
        var gate = NewApprovalGate();
        var wait = gate.WaitForApprovalAsync(
            runId, "req-ownerless", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(() =>
            gate.GrantAsync(runId, "req-ownerless", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(runId, "web_fetch", "https://example.test/next").Should().BeFalse();
    }

    [Fact]
    public async Task AlwaysApproval_RunNotFoundInStore_FailsClosedEntirely()
    {
        // PR #972 finding 2: every non-once scope from every caller -- not only AgentHost-context
        // callers -- now requires an atomic active-run claim/check before any approval or policy
        // event is persisted. A run id absent from the run store can never satisfy "InProgress",
        // so the grant itself must fail closed here, not merely the durable policy: there is no
        // context-based carve-out that would let this succeed.
        var runId = RunId.New().ToString();
        var gate = NewApprovalGate();
        var wait = gate.WaitForApprovalAsync(
            runId, "req-unpersisted", "web_fetch", "https://example.test",
            TimeSpan.FromMilliseconds(300), default);

        (await gate.GrantAsync(runId, "req-unpersisted", ApprovalScope.Always)).Should().BeFalse(
            "a run absent from the run store can never satisfy the active-run requirement");

        (await wait).Should().BeFalse(
            "no approval event was ever persisted for a run that could not be proven active, so the pending request must expire");
        gate.IsAutoApproved(runId, "web_fetch", "https://example.test").Should().BeFalse();
    }

    [Theory]
    [InlineData("start_preview")]
    [InlineData("write_file")]
    [InlineData("unknown_tool")]
    [InlineData("Web_Fetch")]
    public async Task AlwaysApproval_NonEligibleTool_RemainsGatedAcrossRuns(string toolName)
    {
        var project = ProjectId.New();
        var sourceRun = await InsertOwnedRunAsync("alice", project);
        var futureRun = await InsertOwnedRunAsync("alice", project);
        var gate = NewApprovalGate();
        var requestId = $"req-{toolName}";
        var wait = gate.WaitForApprovalAsync(
            sourceRun.Id.ToString(), requestId, toolName, null,
            TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(() =>
            gate.GrantAsync(sourceRun.Id.ToString(), requestId, ApprovalScope.Always));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(sourceRun.Id.ToString(), toolName, null).Should().BeFalse();
        gate.IsAutoApproved(futureRun.Id.ToString(), toolName, null).Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentAlwaysGrants_AcrossReplicas_AppendAndReadSameOwnerPolicy()
    {
        var project = ProjectId.New();
        var sourceA = await InsertOwnedRunAsync("alice", project);
        var sourceB = await InsertOwnedRunAsync("alice", project);
        var future = await InsertOwnedRunAsync("alice", project);
        var replicaA = NewApprovalGate();
        var replicaB = NewApprovalGate();
        var waitA = replicaA.WaitForApprovalAsync(
            sourceA.Id.ToString(), "req-a", "web_fetch", "https://a.test",
            TimeSpan.FromSeconds(5), default);
        var waitB = replicaB.WaitForApprovalAsync(
            sourceB.Id.ToString(), "req-b", "web_fetch", "https://b.test",
            TimeSpan.FromSeconds(5), default);

        var grants = await Task.WhenAll(
            replicaA.GrantAsync(sourceA.Id.ToString(), "req-a", ApprovalScope.Always),
            replicaB.GrantAsync(sourceB.Id.ToString(), "req-b", ApprovalScope.Always));

        grants.Should().OnlyContain(granted => granted);
        (await waitA).Should().BeTrue();
        (await waitB).Should().BeTrue();
        replicaA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test")
            .Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentOnceAndAlwaysDecisions_OnlyTheWinningDecisionCanCreateAPolicy()
    {
        var project = ProjectId.New();
        var source = await InsertOwnedRunAsync("alice", project);
        var future = await InsertOwnedRunAsync("alice", project);
        var replicaA = NewApprovalGate();
        var replicaB = NewApprovalGate();
        var wait = replicaA.WaitForApprovalAsync(
            source.Id.ToString(), "once-vs-always", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);

        var once = replicaA.GrantAsync(source.Id.ToString(), "once-vs-always", ApprovalScope.Once);
        var always = replicaB.GrantAsync(source.Id.ToString(), "once-vs-always", ApprovalScope.Always);
        await Task.WhenAll(once, always);

        (await once).Should().NotBe(await always, "the durable request claim has exactly one winner");
        (await wait).Should().BeTrue();
        replicaA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test")
            .Should().Be(await always,
                "only an Always decision that won the atomic request claim may create a future policy");
    }

    [Fact]
    public async Task ConcurrentAlwaysGrantAndDeny_OnlyTheWinningClaimCanLeaveAPolicy()
    {
        var project = ProjectId.New();
        var source = await InsertOwnedRunAsync("alice", project);
        var future = await InsertOwnedRunAsync("alice", project);
        var replicaA = NewApprovalGate();
        var replicaB = NewApprovalGate();
        var runId = source.Id.ToString();
        var wait = replicaA.WaitForApprovalAsync(
            runId, "always-vs-deny", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var always = Task.Run(async () =>
        {
            await start.Task;
            return await replicaA.GrantAsync(runId, "always-vs-deny", ApprovalScope.Always);
        });
        var deny = Task.Run(async () =>
        {
            await start.Task;
            return replicaB.Deny(runId, "always-vs-deny");
        });
        start.SetResult();

        await Task.WhenAll(always, deny);

        (await always).Should().NotBe(await deny,
            "the exclusive request-stream claim permits exactly one terminal decision");
        if (await deny)
        {
            (await wait).Should().BeFalse();
            replicaA.GetRequestState(runId, "always-vs-deny").Should().Be(ToolApprovalRequestState.Denied);
            replicaA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test")
                .Should().BeFalse("a denial must not leave an Always policy behind");
        }
        else
        {
            (await wait).Should().BeTrue();
            replicaA.GetRequestState(runId, "always-vs-deny").Should().Be(ToolApprovalRequestState.Approved);
            replicaA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test").Should().BeTrue();
        }
    }

    [Fact]
    public async Task AlwaysApproval_DoesNotCrossProjectBoundary()
    {
        var sourceProject = ProjectId.New();
        var otherProject = ProjectId.New();
        var source = await InsertOwnedRunAsync("alice", sourceProject);
        var sameProject = await InsertOwnedRunAsync("alice", sourceProject);
        var otherProjectRun = await InsertOwnedRunAsync("alice", otherProject);
        var gate = NewApprovalGate();

        var wait = gate.WaitForApprovalAsync(
            source.Id.ToString(), "project-boundary", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() =>
            gate.GrantAsync(source.Id.ToString(), "project-boundary", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(sameProject.Id.ToString(), "web_fetch", "https://same-project.test")
            .Should().BeTrue();
        gate.IsAutoApproved(otherProjectRun.Id.ToString(), "web_fetch", "https://other-project.test")
            .Should().BeFalse();
    }

    [Fact]
    public async Task SessionApproval_DoesNotCrossOrchestrationBoundary_AndClearsWithTheSession()
    {
        var project = ProjectId.New();
        var session = await InsertOwnedRunAsync("alice", project);
        var child = await InsertOwnedRunAsync("alice", project);
        var sibling = await InsertOwnedRunAsync("alice", project);
        var otherSession = await InsertOwnedRunAsync("alice", project);
        var otherChild = await InsertOwnedRunAsync("alice", project);
        var gate = NewApprovalGate();
        gate.RegisterParentRun(child.Id.ToString(), session.Id.ToString());
        gate.RegisterParentRun(sibling.Id.ToString(), session.Id.ToString());
        gate.RegisterParentRun(otherChild.Id.ToString(), otherSession.Id.ToString());

        var wait = gate.WaitForApprovalAsync(
            child.Id.ToString(), "session-scope", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() =>
            gate.GrantAsync(child.Id.ToString(), "session-scope", ApprovalScope.Run));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(sibling.Id.ToString(), "web_fetch", "https://sibling.test")
            .Should().BeTrue("a session approval covers other work in the same orchestration");
        gate.IsAutoApproved(otherChild.Id.ToString(), "web_fetch", "https://other-session.test")
            .Should().BeFalse("a session approval must not escape to another orchestration");

        gate.Clear(session.Id.ToString());
        gate.IsAutoApproved(sibling.Id.ToString(), "web_fetch", "https://after-clear.test")
            .Should().BeFalse("ending a session removes its session-only approval");
    }

    [Fact]
    public async Task QuestionAnswer_OnAnotherReplica_ResolvesWaitingRun()
    {
        var owner = NewQuestionGate();
        var secondary = NewQuestionGate();

        var wait = owner.AskAsync(
            "run-6", "q-1", "Which plan?", TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return secondary.Answer("run-6", "q-1", "Use plan B.");
        });

        (await wait).Should().Be("Use plan B.");
        secondary.Answer("run-6", "q-1", "late").Should().BeFalse();
    }

    [Fact]
    public async Task ClearedOrTimedOutQuestions_CannotBeAnsweredLater()
    {
        var owner = NewQuestionGate();
        var secondary = NewQuestionGate();

        var cleared = owner.AskAsync(
            "run-7", "q-2", "Proceed?", TimeSpan.FromSeconds(5), default);
        secondary.Clear("run-7");

        (await cleared).Should().BeNull();
        secondary.Answer("run-7", "q-2", "yes").Should().BeFalse();

        var timedOut = await owner.AskAsync(
            "run-8", "q-3", "Proceed?", TimeSpan.FromMilliseconds(50), default);

        timedOut.Should().BeNull();
        secondary.Answer("run-8", "q-3", "yes").Should().BeFalse();
    }

    [Fact]
    public void ShellApprovals_AreVisibleAcrossReplicasAndConsumedOnce()
    {
        var owner = NewShellApprovalStore();
        var secondary = NewShellApprovalStore();

        secondary.Approve("run-9", "cmd-1");

        owner.IsApproved("run-9", "cmd-1").Should().BeTrue();
        secondary.IsApproved("run-9", "cmd-1").Should().BeFalse();
    }

    [Fact]
    public void ShellDenialsAndClear_AreDurableAcrossReplicas()
    {
        var owner = NewShellApprovalStore();
        var secondary = NewShellApprovalStore();

        owner.Deny("run-10", "cmd-2");
        secondary.IsDenied("run-10", "cmd-2").Should().BeTrue();
        secondary.Approve("run-10", "cmd-2");
        owner.IsApproved("run-10", "cmd-2").Should().BeFalse();

        secondary.Clear("run-10");
        owner.IsDenied("run-10", "cmd-2").Should().BeFalse();
        owner.IsApproved("run-10", "cmd-2").Should().BeFalse();
    }

    private DurableRunOptionsStore NewOptionsStore() => new(NewState());
    private DurableToolApprovalGate NewApprovalGate() => NewApprovalGate(_runStore);
    private DurableToolApprovalGate NewApprovalGate(IRunStore runStore) =>
        new(NewState(), runStore: runStore);

    private DurableQuestionGate NewQuestionGate() => new(NewState());
    private DurableShellApprovalStore NewShellApprovalStore() => new(NewState());

    private DurableRunControlState NewState()
    {
        var provider = NewProvider();
        return new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IRunEventStream>());
    }

    private ServiceProvider NewProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        services.AddDbContextFactory<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        services.AddSingleton<IRunEventStream, EfRunEventStream>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private async Task<Run> InsertOwnedRunAsync(string submittingUser, ProjectId? projectId = null)
    {
        var run = NewOwnedRun(submittingUser, projectId);
        await _runStore.InsertAsync(run);
        return run;
    }

    private static Run NewOwnedRun(string submittingUser, ProjectId? projectId = null) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "approval-scope-test",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "Verify durable tool approval ownership",
        SubmittingUser = submittingUser,
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> action)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await action())
                return;
            await Task.Delay(50);
        }

        false.Should().BeTrue("the pending approval context should become visible");
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _keepAlive.Dispose();
        _runDb.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
