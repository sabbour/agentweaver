using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DomainRun = Agentweaver.Domain.Run;
using DomainRunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Tests.Workflows;

public sealed class WorkflowChildWorkServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConnection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly PendingRequestStore _pendingRequests;
    private readonly RecordingRuntime _runtime = new();
    private readonly WorkflowChildWorkService _service;
    private readonly DomainRun _parent;

    public WorkflowChildWorkServiceTests()
    {
        _memoryConnection = new SqliteConnection("DataSource=:memory:");
        _memoryConnection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options => options.UseSqlite(_memoryConnection));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _pendingRequests = new PendingRequestStore(_scopeFactory);

        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
        _service = BuildService("pod-a", _runtime);

        _parent = NewRun(RunId.New(), DomainRunStatus.InProgress);
        _runStore.InsertAsync(_parent).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task DuplicateCreate_ReattachesWithoutDuplicatingPlanBranchesOrChildRun()
    {
        var first = await CreateAsync(Request());
        var second = await CreateAsync(Request());

        second.Reattached.Should().BeTrue();
        second.WorkPlanId.Should().Be(first.WorkPlanId);
        second.ChildCoordinatorRunId.Should().Be(first.ChildCoordinatorRunId);
        second.Branches.Select(branch => (branch.NodeId, branch.Ordinal)).Should().Equal(
            ("research-a", 0),
            ("research-b", 1));

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.WorkPlans.CountAsync()).Should().Be(1);
        (await db.OutcomeSpecs.CountAsync()).Should().Be(1);
        (await db.Subtasks.CountAsync()).Should().Be(2);
        (await _runStore.GetRunsByParentAsync(_parent.Id.ToString())).Should().ContainSingle();
    }

    [Fact]
    public async Task CrashAfterPlanPersistence_BeforeContinuationArm_ReattachesAndOnlyThenDispatches()
    {
        var (persisted, _) = await _service.EnsurePersistedAsync(Request());

        (await _service.TryStartDispatchAsync(persisted.Id)).Should().BeFalse();
        _runtime.Started.Should().BeEmpty("dispatch is fenced until the parent continuation is durable");

        var attached = await CreateAsync(Request());
        attached.Reattached.Should().BeTrue();
        attached.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Waiting);

        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();
        _runtime.Started.Should().ContainSingle()
            .Which.CoordinatorRunId.Should().Be(attached.ChildCoordinatorRunId);
    }

    [Fact]
    public async Task RestartBeforeDispatch_ParksParentInRecoverableWaitingState()
    {
        var (persisted, _) = await _service.EnsurePersistedAsync(Request());

        await _service.PrepareRestartRecoveryAsync();

        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.AwaitingReview);
        (await GetPlanAsync(persisted.Id)).ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Committed);
        _runtime.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task FreshCrossReplicaDispatchLease_AllowsOnlyOneStarter()
    {
        var attached = await CreateAsync(Request());
        _runtime.AlwaysReportDispatchInactive = true;
        var peer = BuildService("pod-b", _runtime);

        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();
        (await peer.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeFalse();

        _runtime.Started.Should().ContainSingle();
        (await GetPlanAsync(attached.WorkPlanId)).CoordinatorPodId.Should().Be("pod-a");
    }

    [Fact]
    public async Task RestartDuringExecution_StaleReplicaLease_RearmsOnOneNewPod()
    {
        var attached = await CreateAsync(Request());
        _runtime.AlwaysReportDispatchInactive = true;
        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();
        await SetDispatchLeaseAsync(attached.WorkPlanId, "pod-a", DateTimeOffset.UtcNow.AddMinutes(-10));

        var restarted = BuildService("pod-b", _runtime, staleSeconds: 10);
        (await restarted.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();

        _runtime.Started.Should().HaveCount(2);
        (await GetPlanAsync(attached.WorkPlanId)).CoordinatorPodId.Should().Be("pod-b");
    }

    [Fact]
    public async Task TerminalPlan_QueuesAndDeliversResumeExactlyOnce()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        _runtime.DeliverResult = true;

        await _service.SweepAsync();
        await _service.SweepAsync();

        _runtime.Deliveries.Should().ContainSingle();
        var state = await _pendingRequests.GetDeliveryStateAsync(
            _parent.Id.ToString(),
            PendingRequestDeliveryKinds.WorkflowChildWork);
        state!.State.Should().Be(PendingRequestDeliveryStates.Delivered);
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Delivered);
        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.InProgress);
    }

    [Fact]
    public async Task RestartAfterTerminalBeforeResume_WaitsForLivePinnedParentThenDelivers()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        _runtime.ParentResumeActive = false;
        _runtime.DeliverResult = true;

        await _service.SweepAsync();

        _runtime.Deliveries.Should().BeEmpty();
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Ready);
        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.AwaitingReview);

        _runtime.ParentResumeActive = true;
        await _service.SweepAsync();

        _runtime.Deliveries.Should().ContainSingle();
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Delivered);
    }

    [Fact]
    public async Task StaleDeliveryClaim_IsRecovered_AndDeliveredStateNoOps()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        (await _service.TryPrepareResumeAsync(attached.WorkPlanId)).Should().BeTrue();

        var abandonedClaim = await _pendingRequests.TryClaimDeliveryAsync(
            _parent.Id.ToString(),
            "dead-owner",
            TimeSpan.FromHours(1));
        abandonedClaim.Should().NotBeNull();
        _runtime.DeliverResult = true;

        (await _service.TryDeliverResumeAsync(
            attached.WorkPlanId,
            "recovery-owner",
            TimeSpan.Zero)).Should().BeTrue();
        (await _service.TryDeliverResumeAsync(
            attached.WorkPlanId,
            "late-owner",
            TimeSpan.Zero)).Should().BeFalse();
        _runtime.Deliveries.Should().ContainSingle();
    }

    [Fact]
    public async Task ParentCancellation_SuppressesResume_StopsNewDispatch_AndCancelsActiveChildren()
    {
        var attached = await CreateAsync(Request());
        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();

        var branchRun = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[0].SubtaskId.ToString(),
        };
        await _runStore.InsertAsync(branchRun);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var subtask = await db.Subtasks.SingleAsync(row => row.Id == attached.Branches[0].SubtaskId);
            subtask.ChildRunId = branchRun.Id.ToString();
            subtask.Status = SubtaskStatus.Running;
            await db.SaveChangesAsync();
        }

        await _runStore.TrySetTerminalOutcomeAsync(
            _parent.Id,
            TerminalRunOutcome.Create(
                DomainRunStatus.Failed,
                EventTypes.RunFailed,
                new { reason = "cancelled" },
                DateTimeOffset.UtcNow,
                _parent.LifecycleGeneration),
            "cancelled");

        await _service.SweepAsync();

        var plan = await GetPlanAsync(attached.WorkPlanId);
        plan.Status.Should().Be(WorkPlanStatus.Cancelled);
        plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().BeEquivalentTo(
            attached.ChildCoordinatorRunId,
            branchRun.Id.ToString());

        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeFalse();
        _runtime.Started.Should().ContainSingle("the parent cancellation cannot launch another dispatch");
    }

    [Fact]
    public async Task CancellationBetweenCheckAndDispatch_WinsPlanCasAndPreventsStart()
    {
        var attached = await CreateAsync(Request());
        _runtime.BeforeDispatchEnabled = () => CancelParentAndSweepAsync().GetAwaiter().GetResult();

        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeFalse();

        _runtime.Started.Should().BeEmpty();
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Suppressed);
    }

    [Fact]
    public async Task CancellationBetweenCheckAndDelivery_DiscardsQueueAndPreventsResume()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        (await _service.TryPrepareResumeAsync(attached.WorkPlanId)).Should().BeTrue();
        _runtime.BeforeParentResumeActiveCheck = () => CancelParentAndSweepAsync().GetAwaiter().GetResult();

        (await _service.TryDeliverResumeAsync(attached.WorkPlanId, "delivery-owner")).Should().BeFalse();

        _runtime.Deliveries.Should().BeEmpty();
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        (await _pendingRequests.GetDeliveryStateAsync(
            _parent.Id.ToString(),
            PendingRequestDeliveryKinds.WorkflowChildWork)).Should().BeNull();
    }

    [Fact]
    public async Task SuppressedPlan_WithAlreadyDeliveredQueue_DoesNotBecomeDelivered()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        (await _service.TryPrepareResumeAsync(attached.WorkPlanId)).Should().BeTrue();
        var delivery = await _pendingRequests.TryClaimDeliveryAsync(
            _parent.Id.ToString(),
            "winner",
            TimeSpan.Zero);
        delivery.Should().NotBeNull();
        (await _pendingRequests.MarkDeliveredAsync(
            _parent.Id.ToString(),
            delivery!.DecisionIdentity,
            delivery.ClaimOwner,
            delivery.ClaimedAt)).Should().BeTrue();
        await SetParentResumeStateAsync(attached.WorkPlanId, WorkflowChildWorkResumeStates.Suppressed);

        (await _service.TryDeliverResumeAsync(attached.WorkPlanId, "late-owner")).Should().BeFalse();

        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        _runtime.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reattach_PreservesPinnedWorkflowIdentityAndDeclaredBranches()
    {
        var original = await CreateAsync(Request());
        var edited = Request() with
        {
            ParentWorkflowId = "workflow-edited-after-suspension",
            ParentJoinNodeId = "replacement-join",
            Branches =
            [
                Branch("replacement-a"),
                Branch("replacement-b"),
            ],
        };

        var reattached = await CreateAsync(edited);
        var plan = await GetPlanAsync(original.WorkPlanId);

        reattached.WorkPlanId.Should().Be(original.WorkPlanId);
        plan.ParentWorkflowId.Should().Be("workflow-v1");
        plan.ParentJoinNodeId.Should().Be("join");
        reattached.Branches.Select(branch => branch.NodeId).Should().Equal("research-a", "research-b");
    }

    private WorkflowChildWorkRequest Request() => new(
        _parent,
        "workflow-v1",
        "fan",
        "join",
        [Branch("research-a"), Branch("research-b")]);

    private static StaticWorkflowBranch Branch(string nodeId) => new(
        nodeId,
        nodeId,
        $"Execute {nodeId}",
        "researcher",
        "test-model");

    private Task<WorkflowChildWorkAttachment> CreateAsync(WorkflowChildWorkRequest request) =>
        _service.CreateOrReattachStaticAsync(request, NewRequest);

    private WorkflowChildWorkService BuildService(
        string podId,
        RecordingRuntime runtime,
        int staleSeconds = 120)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PodId"] = podId,
                ["Coordinator:PodLeaseStaleTtlSeconds"] = staleSeconds.ToString(),
            })
            .Build();
        return new WorkflowChildWorkService(
            _scopeFactory,
            _runStore,
            _pendingRequests,
            runtime,
            NullLogger<WorkflowChildWorkService>.Instance,
            configuration);
    }

    private static ExternalRequest NewRequest(string requestId)
    {
        var port = new RequestPortInfo(
            new TypeId("Agentweaver.Tests", "WorkflowChildWorkRequest"),
            new TypeId("Agentweaver.Tests", "WorkflowChildWorkResult"),
            "workflow-child-work");
        return new ExternalRequest(port, requestId, new PortableValue(requestId));
    }

    private async Task SetPlanAndBranchStatusAsync(int planId, string planStatus, string branchStatus)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.SingleAsync(row => row.Id == planId);
        plan.Status = planStatus;
        foreach (var branch in await db.Subtasks.Where(row => row.WorkPlanId == planId).ToListAsync())
            branch.Status = branchStatus;
        await db.SaveChangesAsync();
    }

    private async Task SetDispatchLeaseAsync(int planId, string podId, DateTimeOffset updatedAt)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.SingleAsync(row => row.Id == planId);
        plan.CoordinatorPodId = podId;
        plan.UpdatedAt = updatedAt;
        await db.SaveChangesAsync();
    }

    private async Task SetParentResumeStateAsync(int planId, string state)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.SingleAsync(row => row.Id == planId);
        plan.ParentResumeState = state;
        await db.SaveChangesAsync();
    }

    private async Task CancelParentAndSweepAsync()
    {
        var parent = await _runStore.GetAsync(_parent.Id);
        await _runStore.TrySetTerminalOutcomeAsync(
            _parent.Id,
            TerminalRunOutcome.Create(
                DomainRunStatus.Failed,
                EventTypes.RunFailed,
                new { reason = "cancelled" },
                DateTimeOffset.UtcNow,
                parent!.LifecycleGeneration),
            "cancelled");
        await _service.SweepAsync();
    }

    private async Task<WorkPlan> GetPlanAsync(int planId)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .WorkPlans.AsNoTracking().SingleAsync(plan => plan.Id == planId);
    }

    private static DomainRun NewRun(RunId id, DomainRunStatus status) => new()
    {
        Id = id,
        RepositoryPath = "C:\\repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "parent workflow",
        SubmittingUser = "octocat",
        Status = status,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = ProjectId.New(),
        ModelId = "test-model",
    };

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _memoryConnection.DisposeAsync();
        await _runDb.DisposeAsync();
    }

    private sealed class RecordingRuntime : IWorkflowChildWorkRuntime
    {
        public bool ParentResumeActive { get; set; } = true;
        public bool AlwaysReportDispatchInactive { get; set; }
        public Action? BeforeDispatchEnabled { get; set; }
        public Action? BeforeParentResumeActiveCheck { get; set; }
        public List<CoordinatorDispatchContext> Started { get; } = [];
        public List<DomainRun> Cancelled { get; } = [];
        public List<WorkflowChildWorkResult> Deliveries { get; } = [];
        public bool DeliverResult { get; set; }

        public bool DispatchEnabled
        {
            get
            {
                var callback = BeforeDispatchEnabled;
                BeforeDispatchEnabled = null;
                callback?.Invoke();
                return true;
            }
        }

        public bool IsDispatchActive(string coordinatorRunId) =>
            !AlwaysReportDispatchInactive
            && Started.Any(context => context.CoordinatorRunId == coordinatorRunId);

        public bool IsParentResumeActive(string parentRunId)
        {
            var callback = BeforeParentResumeActiveCheck;
            BeforeParentResumeActiveCheck = null;
            callback?.Invoke();
            return ParentResumeActive;
        }

        public void StartDispatch(CoordinatorDispatchContext context) => Started.Add(context);

        public Task<bool> TryDeliverParentResumeAsync(
            string parentRunId,
            PendingDelivery delivery,
            WorkflowChildWorkResult result,
            CancellationToken ct)
        {
            Deliveries.Add(result);
            return Task.FromResult(DeliverResult);
        }

        public Task CancelRunAsync(DomainRun run, CancellationToken ct)
        {
            Cancelled.Add(run);
            return Task.CompletedTask;
        }
    }
}
