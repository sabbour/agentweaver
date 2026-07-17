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
    public async Task AlwaysApproval_IsVisibleToFutureRunForSameOwner_AfterSourceClear()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();
        var sourceRun = await InsertOwnedRunAsync("alice");
        var futureRun = await InsertOwnedRunAsync("alice");
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
        var aliceRun = NewOwnedRun("alice");
        var bobRun = NewOwnedRun("bob");
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
        var aliceRun = await InsertOwnedRunAsync("alice");
        var state = NewState();
        state.Append(
            "__agentweaver_tool_approvals__",
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });
        state.Append(
            DurableToolApprovalGate.OwnerPolicyBucket("alice"),
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });
        state.Append(
            DurableToolApprovalGate.OwnerPolicyBucket("alice"),
            "tool.approval_policy_granted",
            new { owner = "alice", toolId = "web_fetch", riskSemantics = "network-write/v1" });
        state.Append(
            DurableToolApprovalGate.OwnerPolicyBucket("alice"),
            "tool.approval_policy_granted",
            new { owner = "Alice", toolId = "web_fetch", riskSemantics = "network-read/v1" });

        var gate = NewApprovalGate();

        gate.IsAutoApproved(aliceRun.Id.ToString(), "web_fetch", "https://example.test")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlwaysApproval_MissingOrEmptyPersistedOwner_FailsClosed(bool persistEmptyOwner)
    {
        var runId = persistEmptyOwner
            ? (await InsertOwnedRunAsync("")).Id.ToString()
            : RunId.New().ToString();
        var gate = NewApprovalGate();
        var wait = gate.WaitForApprovalAsync(
            runId, "req-ownerless", "web_fetch", "https://example.test",
            TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(() =>
            gate.GrantAsync(runId, "req-ownerless", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        gate.IsAutoApproved(runId, "web_fetch", "https://example.test/next").Should().BeFalse();
    }

    [Theory]
    [InlineData("start_preview")]
    [InlineData("write_file")]
    [InlineData("unknown_tool")]
    [InlineData("Web_Fetch")]
    public async Task AlwaysApproval_NonEligibleTool_RemainsGatedAcrossRuns(string toolName)
    {
        var sourceRun = await InsertOwnedRunAsync("alice");
        var futureRun = await InsertOwnedRunAsync("alice");
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
        var sourceA = await InsertOwnedRunAsync("alice");
        var sourceB = await InsertOwnedRunAsync("alice");
        var future = await InsertOwnedRunAsync("alice");
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

    private async Task<Run> InsertOwnedRunAsync(string submittingUser)
    {
        var run = NewOwnedRun(submittingUser);
        await _runStore.InsertAsync(run);
        return run;
    }

    private static Run NewOwnedRun(string submittingUser) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "approval-scope-test",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "Verify durable tool approval ownership",
        SubmittingUser = submittingUser,
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
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
