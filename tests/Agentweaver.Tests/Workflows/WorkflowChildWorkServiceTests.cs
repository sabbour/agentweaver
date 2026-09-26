using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
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

        (await peer.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeFalse();

        _runtime.Started.Should().ContainSingle();
        (await GetPlanAsync(attached.WorkPlanId)).CoordinatorPodId.Should().Be("pod-a");
    }

    [Fact]
    public async Task RestartDuringExecution_StaleReplicaLease_RearmsOnOneNewPod()
    {
        var attached = await CreateAsync(Request());
        _runtime.AlwaysReportDispatchInactive = true;
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
        _runtime.DurableParentSteps.Should().ContainSingle();
        _runtime.DurableParentSteps[0].Payload.GetProperty("status").GetString()
            .Should().Be("child_work_ready");
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
    public async Task SuccessfulJoin_UsesPersistedBranchOrdinal_NotChildCompletionOrRunIdOrder()
    {
        var attached = await CreateAsync(Request());
        var first = NewRun(RunId.New(), DomainRunStatus.AssembleReady) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[0].SubtaskId.ToString(),
            Result = "first-output",
            EndedAt = DateTimeOffset.UtcNow.AddSeconds(2),
        };
        var second = NewRun(RunId.New(), DomainRunStatus.AssembleReady) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[1].SubtaskId.ToString(),
            Result = "second-output",
            EndedAt = DateTimeOffset.UtcNow,
        };
        await _runStore.InsertAsync(second);
        await _runStore.InsertAsync(first);
        await SetBranchRunsAsync(
            attached.WorkPlanId,
            WorkPlanStatus.Complete,
            [
                (attached.Branches[0].SubtaskId, first.Id.ToString(), SubtaskStatus.Completed),
                (attached.Branches[1].SubtaskId, second.Id.ToString(), SubtaskStatus.Completed),
            ]);
        _runtime.DeliverResult = true;

        await _service.SweepAsync();

        var result = _runtime.Deliveries.Should().ContainSingle().Subject;
        second.EndedAt.Should().BeBefore(first.EndedAt!.Value,
            "branch two must finish first so completion order differs from declaration order");
        result.Succeeded.Should().BeTrue();
        result.Branches.Select(branch => branch.NodeId).Should().Equal("research-a", "research-b");
        result.JoinedOutput.Should().Be(
            "[1. research-a]\nfirst-output\n\n[2. research-b]\nsecond-output");
    }

    [Theory]
    [InlineData(SubtaskStatus.Failed)]
    [InlineData(SubtaskStatus.Blocked)]
    [InlineData(SubtaskStatus.Cancelled)]
    [InlineData(SubtaskStatus.RaiFlagged)]
    public async Task NonSuccessfulBranch_CannotProduceSuccessfulJoin(string branchStatus)
    {
        var attached = await CreateAsync(Request());
        await SetBranchRunsAsync(
            attached.WorkPlanId,
            WorkPlanStatus.Complete,
            [
                (attached.Branches[0].SubtaskId, null, SubtaskStatus.Completed),
                (attached.Branches[1].SubtaskId, null, branchStatus),
            ]);
        _runtime.DeliverResult = true;

        await _service.SweepAsync();

        var result = _runtime.Deliveries.Should().ContainSingle().Subject;
        result.Succeeded.Should().BeFalse();
        result.Branches.Should().Contain(branch => branch.Status == branchStatus);
        _runtime.DurableParentSteps.Should().BeEmpty(
            "failed or cancelled branch work must not emit a success-ready event");
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

        await _service.CancelForParentAsync(_parent.Id.ToString());

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
    public async Task TopLevelParentCancellation_SelectsCoordinatorPlan_AndCancelsEveryActiveChild()
    {
        var firstChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var secondChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var (planId, activeSubtaskIds, pendingSubtaskId) = await SeedTopLevelFanPlanAsync(
            firstChild,
            secondChild);

        (await _service.CancelForParentAsync(_parent.Id.ToString())).Should().Be(1);

        var plan = await GetPlanAsync(planId);
        plan.Status.Should().Be(WorkPlanStatus.Cancelled);
        plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().BeEquivalentTo(
            firstChild.Id.ToString(),
            secondChild.Id.ToString());
        _runtime.Cancelled.Select(run => run.Id).Should().NotContain(_parent.Id);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var activeSubtasks = await db.Subtasks.AsNoTracking()
            .Where(subtask => activeSubtaskIds.Contains(subtask.Id))
            .ToListAsync();
        activeSubtasks.Should().OnlyContain(subtask => subtask.Status == SubtaskStatus.Running);
        var pendingSubtask = await db.Subtasks.AsNoTracking().SingleAsync(row => row.Id == pendingSubtaskId);
        pendingSubtask.Status.Should().Be(SubtaskStatus.Pending);
        pendingSubtask.ChildRunId.Should().BeNull();

        (await _service.TryStartDispatchAsync(planId)).Should().BeFalse();
        (await _service.TryPrepareResumeAsync(planId)).Should().BeFalse();
        _runtime.Started.Should().BeEmpty();
        _runtime.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedParentCancellation_CancelsChildThatBecameActiveAfterSuppression()
    {
        var attached = await CreateAsync(Request());
        (await _service.TryStartDispatchAsync(attached.WorkPlanId)).Should().BeTrue();

        await _service.CancelForParentAsync(_parent.Id.ToString());

        var lateChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[0].SubtaskId.ToString(),
        };
        await _runStore.InsertAsync(lateChild);
        await SetBranchRunsAsync(
            attached.WorkPlanId,
            WorkPlanStatus.Cancelled,
            [
                (attached.Branches[0].SubtaskId, lateChild.Id.ToString(), SubtaskStatus.Running),
                (attached.Branches[1].SubtaskId, null, SubtaskStatus.Pending),
            ]);

        await _service.CancelForParentAsync(_parent.Id.ToString());

        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().Contain(lateChild.Id.ToString());
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        (await _service.TryPrepareResumeAsync(attached.WorkPlanId)).Should().BeFalse();
        _runtime.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartRecovery_ReissuesTopLevelCancellationForLateActiveChild()
    {
        var firstChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var secondChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var (planId, _, pendingSubtaskId) = await SeedTopLevelFanPlanAsync(firstChild, secondChild);
        await _service.CancelForParentAsync(_parent.Id.ToString());

        var lateChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
            SubtaskId = pendingSubtaskId.ToString(),
        };
        await _runStore.InsertAsync(lateChild);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var pendingSubtask = await db.Subtasks.SingleAsync(row => row.Id == pendingSubtaskId);
            pendingSubtask.ChildRunId = lateChild.Id.ToString();
            pendingSubtask.Status = SubtaskStatus.Running;
            await db.SaveChangesAsync();
        }
        _runtime.Cancelled.Clear();

        await _service.PrepareRestartRecoveryAsync();

        _runtime.Cancelled.Select(run => run.Id).Should().Contain(lateChild.Id);
        var plan = await GetPlanAsync(planId);
        plan.Status.Should().Be(WorkPlanStatus.Cancelled);
        plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
    }

    [Fact]
    public async Task RestartRecovery_ReissuesCancellationForChildrenThatTerminalizedAfterSnapshot()
    {
        var firstChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var secondChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var (planId, activeSubtaskIds, pendingSubtaskId) = await SeedTopLevelFanPlanAsync(
            firstChild,
            secondChild);

        await _service.CancelForParentAsync(_parent.Id.ToString());
        (await _runStore.TerminalizeForTestAsync(firstChild.Id, DomainRunStatus.Failed)).Should().BeTrue();
        (await _runStore.TerminalizeForTestAsync(secondChild.Id, DomainRunStatus.Failed)).Should().BeTrue();
        _runtime.Cancelled.Clear();

        await _service.PrepareRestartRecoveryAsync();

        _runtime.Cancelled.Select(run => run.Id).Should().BeEquivalentTo([firstChild.Id, secondChild.Id]);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var activeSubtasks = await db.Subtasks.AsNoTracking()
            .Where(subtask => activeSubtaskIds.Contains(subtask.Id))
            .ToListAsync();
        activeSubtasks.Should().OnlyContain(subtask =>
            subtask.CancellationRequestedAt != null
            && subtask.CancellationRequestedByRunId == _parent.Id.ToString());
        var pendingSubtask = await db.Subtasks.AsNoTracking().SingleAsync(row => row.Id == pendingSubtaskId);
        pendingSubtask.CancellationRequestedAt.Should().BeNull(
            "a child that was not active at the cancellation snapshot must not gain false provenance");
        (await GetPlanAsync(planId)).ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        _runtime.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartRecovery_ReissuesCancellationForLateActiveChild()
    {
        var attached = await CreateAsync(Request());
        await _service.CancelForParentAsync(_parent.Id.ToString());

        var lateChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[0].SubtaskId.ToString(),
        };
        await _runStore.InsertAsync(lateChild);
        await SetBranchRunsAsync(
            attached.WorkPlanId,
            WorkPlanStatus.Cancelled,
            [
                (attached.Branches[0].SubtaskId, lateChild.Id.ToString(), SubtaskStatus.Running),
                (attached.Branches[1].SubtaskId, null, SubtaskStatus.Pending),
            ]);
        _runtime.Cancelled.Clear();

        await _service.PrepareRestartRecoveryAsync();

        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().Contain(lateChild.Id.ToString());
        _runtime.Started.Should().ContainSingle("restart recovery must not restart a cancelled fan");
    }

    [Fact]
    public async Task RestartRecovery_ReissuesCancellationForCoordinatorThatTerminalizedAfterSnapshot()
    {
        var attached = await CreateAsync(Request());

        await _service.CancelForParentAsync(_parent.Id.ToString());
        (await _runStore.TerminalizeForTestAsync(
            RunId.Parse(attached.ChildCoordinatorRunId),
            DomainRunStatus.Failed)).Should().BeTrue();
        _runtime.Cancelled.Clear();

        await _service.PrepareRestartRecoveryAsync();

        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().Contain(attached.ChildCoordinatorRunId);
        var plan = await GetPlanAsync(attached.WorkPlanId);
        plan.CoordinatorCancellationRequestedAt.Should().NotBeNull();
        plan.CoordinatorCancellationRequestedByRunId.Should().Be(_parent.Id.ToString());
        plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
    }

    [Fact]
    public async Task ParentCancellation_DoesNotRecancelAlreadyTerminalChild()
    {
        var attached = await CreateAsync(Request());
        var completedChild = NewRun(RunId.New(), DomainRunStatus.AssembleReady) with
        {
            ParentRunId = attached.ChildCoordinatorRunId,
            SubtaskId = attached.Branches[0].SubtaskId.ToString(),
        };
        await _runStore.InsertAsync(completedChild with { Status = DomainRunStatus.InProgress });
        (await _runStore.TerminalizeForTestAsync(completedChild.Id, DomainRunStatus.AssembleReady))
            .Should().BeTrue();
        await SetBranchRunsAsync(
            attached.WorkPlanId,
            WorkPlanStatus.Dispatching,
            [
                (attached.Branches[0].SubtaskId, completedChild.Id.ToString(), SubtaskStatus.Completed),
                (attached.Branches[1].SubtaskId, null, SubtaskStatus.Pending),
            ]);

        await _service.CancelForParentAsync(_parent.Id.ToString());

        _runtime.Cancelled.Select(run => run.Id).Should().NotContain(completedChild.Id);
        _runtime.Cancelled.Select(run => run.Id.ToString()).Should().Contain(attached.ChildCoordinatorRunId);
        using var scope = _provider.CreateScope();
        var subtask = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .Subtasks.AsNoTracking().SingleAsync(row => row.Id == attached.Branches[0].SubtaskId);
        subtask.CancellationRequestedAt.Should().BeNull();
        subtask.CancellationRequestedByRunId.Should().BeNull();
    }

    [Fact]
    public async Task CancellationBetweenCheckAndDispatch_WinsPlanCasAndPreventsStart()
    {
        var attached = await _service.PrepareStaticAsync(Request());
        _runtime.BeforeDispatchEnabled = () => CancelParentAndSweepAsync().GetAwaiter().GetResult();

        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), "fan", attached.WorkPlanId)),
            _parent.SubmittingUser);

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

    [Fact]
    public async Task StartFan_AndReattach_PreserveIncomingTaskAndExecutionBase_AndEnsureCoordinatorStream()
    {
        var originalInput = new AgentTurnInput(
            _parent.Id.ToString(),
            "submitted context\n\npredecessor output",
            "C:\\repo\\.agentweaver\\worktrees\\parent",
            "agentweaver/run-parent",
            "C:\\repo",
            "dev",
            ModelSource.GitHubCopilot.ToApiString(),
            "test-model",
            _parent.SubmittingUser,
            ProjectId: _parent.ProjectId!.ToString());
        var original = await CreateAsync(Request() with
        {
            IncomingInput = originalInput,
            ExecutionBaseTreeHash = "tree-at-fan",
        });

        _runtime.AlwaysReportDispatchInactive = true;
        await CreateAsync(Request() with
        {
            IncomingInput = originalInput with
            {
                Task = "edited context",
                WorktreeBranch = "agentweaver/edited",
            },
            ExecutionBaseTreeHash = "edited-tree",
        });

        var plan = await GetPlanAsync(original.WorkPlanId);
        var persistedInput = JsonSerializer.Deserialize<AgentTurnInput>(
            plan.ParentTurnInputJson!,
            JsonDefaults.Options);
        persistedInput.Should().BeEquivalentTo(originalInput);
        plan.ExecutionBaseTreeHash.Should().Be("tree-at-fan");
        _runtime.Started.Should().HaveCount(2);
        _runtime.Started.Should().OnlyContain(context =>
            context.OriginatingBranch == originalInput.WorktreeBranch
            && context.StaticParentTask == originalInput.Task);
        _runtime.EnsuredStreams.Should().OnlyContain(entry =>
            entry.RunId == original.ChildCoordinatorRunId
            && entry.OwnerUser == _parent.SubmittingUser);
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

    private async Task<(int PlanId, int[] ActiveSubtaskIds, int PendingSubtaskId)> SeedTopLevelFanPlanAsync(
        DomainRun firstChild,
        DomainRun secondChild)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var outcomeSpec = new OutcomeSpec
        {
            ProjectId = _parent.ProjectId!.Value.ToString(),
            CoordinatorRunId = _parent.Id.ToString(),
            Goal = "Validate top-level fan cancellation",
            DesiredOutcome = "Cancel every active fan child without dispatching pending work",
            Scope = "Top-level static fan",
            Assumptions = "Two branches are active and downstream work is pending",
            Status = "confirmed",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(outcomeSpec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId = outcomeSpec.Id,
            ProjectId = outcomeSpec.ProjectId,
            CoordinatorRunId = _parent.Id.ToString(),
            WorkflowId = "pm-discovery",
            Status = WorkPlanStatus.Dispatching,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var subtasks = new[]
        {
            new Subtask
            {
                WorkPlanId = plan.Id,
                Title = "Customer signal research",
                Scope = "Research customer signals",
                AssignedAgent = "researcher",
                SelectedModelId = "test-model",
                Phase = "planning",
                IsolationStrategy = "worktree",
                Status = SubtaskStatus.Running,
                ChildRunId = firstChild.Id.ToString(),
                WorkflowBranchNodeId = "customer-signal-research",
                WorkflowBranchOrdinal = 0,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Subtask
            {
                WorkPlanId = plan.Id,
                Title = "Technical feasibility research",
                Scope = "Research technical feasibility",
                AssignedAgent = "researcher",
                SelectedModelId = "test-model",
                Phase = "planning",
                IsolationStrategy = "worktree",
                Status = SubtaskStatus.Running,
                ChildRunId = secondChild.Id.ToString(),
                WorkflowBranchNodeId = "technical-feasibility-research",
                WorkflowBranchOrdinal = 1,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Subtask
            {
                WorkPlanId = plan.Id,
                Title = "Synthesis",
                Scope = "Join branch outputs",
                AssignedAgent = "lead",
                SelectedModelId = "test-model",
                Phase = "planning",
                IsolationStrategy = "worktree",
                Status = SubtaskStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };
        db.Subtasks.AddRange(subtasks);
        await db.SaveChangesAsync();

        await _runStore.InsertAsync(firstChild with { SubtaskId = subtasks[0].Id.ToString() });
        await _runStore.InsertAsync(secondChild with { SubtaskId = subtasks[1].Id.ToString() });

        return (plan.Id, [subtasks[0].Id, subtasks[1].Id], subtasks[2].Id);
    }

    private async Task SetBranchRunsAsync(
        int planId,
        string planStatus,
        IReadOnlyList<(int SubtaskId, string? ChildRunId, string Status)> branches)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.SingleAsync(row => row.Id == planId);
        plan.Status = planStatus;
        foreach (var branch in branches)
        {
            var subtask = await db.Subtasks.SingleAsync(row => row.Id == branch.SubtaskId);
            subtask.ChildRunId = branch.ChildRunId;
            subtask.Status = branch.Status;
        }
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
        public List<(string RunId, string OwnerUser)> EnsuredStreams { get; } = [];
        public List<(string RunId, string EventIdentity, JsonElement Payload)> DurableParentSteps { get; } = [];
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

        public void RecordParentStep(string parentRunId, object payload)
        {
        }

        public Task<bool> RecordParentReadyStepAsync(
            int workPlanId,
            string parentRunId,
            string eventIdentity,
            object payload,
            CancellationToken ct)
        {
            var element = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options);
            if (DurableParentSteps.All(item => item.EventIdentity != eventIdentity))
                DurableParentSteps.Add((parentRunId, eventIdentity, element));
            return Task.FromResult(true);
        }

        public void EnsureRunStream(string runId, string ownerUser)
        {
            EnsuredStreams.Add((runId, ownerUser));
        }

        public Task PublishParentGraphAsync(
            string parentRunId,
            string parentWorkflowNodeId,
            string childCoordinatorRunId,
            CancellationToken ct) => Task.CompletedTask;

        public Task<bool> TryDeliverParentResumeAsync(
            string parentRunId,
            PendingDelivery delivery,
            WorkflowChildWorkResult result,
            CancellationToken ct)
        {
            Deliveries.Add(result);
            return Task.FromResult(DeliverResult);
        }

        public Task CancelRunAsync(DomainRun run, string requestedByRunId, CancellationToken ct)
        {
            Cancelled.Add(run);
            return Task.CompletedTask;
        }
    }
}
