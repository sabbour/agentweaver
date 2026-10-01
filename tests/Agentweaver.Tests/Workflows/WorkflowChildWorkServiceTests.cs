using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Api.Webhooks;
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
using Repository = LibGit2Sharp.Repository;
using Commands = LibGit2Sharp.Commands;
using Signature = LibGit2Sharp.Signature;
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
        services.AddSingleton<ISecretStore, InMemorySecretStore>();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<GitHubConnectionsPersistenceStore>();
        services.AddScoped<IGitHubConnectionsCredentialVault, GitHubConnectionsCredentialVault>();
        services.AddScoped<RepoAppInstallationTokenService>();
        services.AddScoped<GitHubCapabilityBroker>();
        services.AddScoped<RunGitHubCapabilitySnapshotLifecycle>();
        services.AddSingleton<RunActiveClaimGuard>();
        services.AddSingleton<RunStreamStore>();
        services.AddSingleton(new WorktreeManager(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = Path.Combine(Path.GetTempPath(),
                    "agentweaver-fan-service-" + Guid.NewGuid().ToString("N")),
            }).Build(),
            NullLogger<WorktreeManager>.Instance));
        services.AddSingleton<IWorktreeOperations>(provider => new WorktreeOperationsAdapter(
            provider.GetRequiredService<WorktreeManager>(),
            provider.GetRequiredService<RunStreamStore>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorktreeOperationsAdapter>.Instance));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _pendingRequests = new PendingRequestStore(_scopeFactory);

        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
        _service = BuildService("pod-a", _runtime);

        _parent = NewRun(RunId.New(), DomainRunStatus.InProgress);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.Projects.Add(new ProjectRecord
            {
                ProjectId = _parent.ProjectId!.Value.ToString(),
                OriginKind = "blank",
                Name = "Test workflow",
                WorkingDirectory = _parent.RepositoryPath,
                Owner = _parent.SubmittingUser,
                DefaultProvider = "github-copilot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
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
    public async Task ComposedPlan_ReattachesBeforeDecomposition_WithoutCreatingStaticBranches()
    {
        var request = ComposedRequest() with
        {
            IncomingInput = ComposedRequest().IncomingInput with
            {
                Task = "Original request\n\n[Ordered parallel branch results]\n" +
                       "[1. incident-brief-writer]\nSynthetic incident details\n\n" +
                       "[2. response-checklist-writer]\nRecovery checklist",
            },
        };
        var first = await _service.PrepareComposedAsync(request);
        var second = await BuildService("second-pod", _runtime).PrepareComposedAsync(
            request with { IncomingInput = request.IncomingInput with { Task = "edited context" } });

        second.Reattached.Should().BeTrue();
        second.WorkPlanId.Should().Be(first.WorkPlanId);
        second.ChildCoordinatorRunId.Should().Be(first.ChildCoordinatorRunId);
        second.Branches.Should().BeEmpty();
        var plan = await GetPlanAsync(first.WorkPlanId);
        plan.ParentJoinNodeId.Should().BeNull();
        plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Committed);
        var expectedOutcome = $"{request.Prompt}\n\n[Parent workflow context]\n{request.IncomingInput.Task}";
        (await _runStore.GetAsync(RunId.Parse(first.ChildCoordinatorRunId)))!.Task
            .Should().Be(request.Prompt);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.OutcomeSpecs.CountAsync()).Should().Be(1);
        var spec = await db.OutcomeSpecs.SingleAsync();
        spec.Goal.Should().Be(request.Prompt);
        spec.DesiredOutcome.Should().Be(expectedOutcome);
        var fallback = CoordinatorOrchestratorExecutor.DecomposeDeterministic(spec);
        fallback.Should().ContainSingle();
        CoordinatorDispatchService.BuildCanonicalSubtaskTask(new Subtask
        {
            Title = fallback[0].Title,
            Scope = fallback[0].Scope,
            Phase = fallback[0].Phase,
            AssignedAgent = "core-implementer",
            SelectedModelId = "test-model",
            IsolationStrategy = "worktree",
            Status = SubtaskStatus.Pending,
        }).Should().Contain(request.IncomingInput.Task);
        CoordinatorDispatchService.BuildComposedParentContext(plan)
            .Should().Contain(request.IncomingInput.Task).And.NotContain("edited context");
        JsonSerializer.Deserialize<AgentTurnInput>(plan.ParentTurnInputJson!, JsonDefaults.Options)!
            .Task.Should().Be(request.IncomingInput.Task);
        (await db.WorkPlans.CountAsync()).Should().Be(1);
        (await db.Subtasks.CountAsync()).Should().Be(0);
        (await _runStore.GetRunsByParentAsync(_parent.Id.ToString())).Should().ContainSingle();
    }

    [Fact]
    public async Task ComposedAssembly_CheckpointSurvivesRestart_AndDeliversTypedResultOnce()
    {
        _runtime.AllowDispatch = false;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Waiting);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Assembling;
            await db.SaveChangesAsync();
        }

        var assembly = new WorkflowComposedAssembly(
            "agentweaver/integration-child", "tree-sha", "diff --git", ["run-child"]);
        _runtime.ParentResumeActive = false;
        _runtime.DeliverResult = true;
        await _service.StageComposedAssemblyAsync(attached.WorkPlanId, assembly, CancellationToken.None);
        await MarkParentTreeTransferredAsync(assembly.TreeHash);
        await _service.CompleteComposedAssemblyAsync(attached.WorkPlanId, assembly, null, CancellationToken.None);
        var checkpoint = await GetPlanAsync(attached.WorkPlanId);
        checkpoint.Status.Should().Be(WorkPlanStatus.Complete);
        checkpoint.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Ready);
        JsonSerializer.Deserialize<WorkflowChildWorkResult>(
            checkpoint.ParentResumeResultJson!, JsonDefaults.Options)!.Assembly
            .Should().BeEquivalentTo(assembly);

        _runtime.ParentResumeActive = true;
        var restarted = BuildService("third-pod", _runtime);
        await restarted.PrepareRestartRecoveryAsync();
        await restarted.SweepAsync();
        await restarted.SweepAsync();
        _runtime.Deliveries.Should().ContainSingle()
            .Which.Assembly.Should().BeEquivalentTo(assembly);
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Delivered);
    }

    [Fact]
    public async Task ComposedAssembly_StagedBeforeTransfer_NeverResumesParentUntilTransferCompletes()
    {
        _runtime.AllowDispatch = false;
        _runtime.DeliverResult = true;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Assembling;
            await db.SaveChangesAsync();
        }
        var assembled = new WorkflowComposedAssembly("integration", "tree", "diff", ["child"]);
        await _service.StageComposedAssemblyAsync(attached.WorkPlanId, assembled, CancellationToken.None);
        await BuildService("restarted", _runtime).SweepAsync();

        (await GetPlanAsync(attached.WorkPlanId)).Status.Should().Be(WorkPlanStatus.Assembling);
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Waiting);
        _runtime.Deliveries.Should().BeEmpty();
        (await _service.GetStagedComposedAssemblyAsync(attached.WorkPlanId, CancellationToken.None))!
            .Should().BeEquivalentTo(assembled);

        var premature = () => _service.CompleteComposedAssemblyAsync(
            attached.WorkPlanId, assembled, null, CancellationToken.None);
        await premature.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*before the verified parent tree is installed*");
        await MarkParentTreeTransferredAsync(assembled.TreeHash);
        await _service.CompleteComposedAssemblyAsync(attached.WorkPlanId, assembled, null, CancellationToken.None);
        await BuildService("restarted-again", _runtime).SweepAsync();
        _runtime.Deliveries.Should().ContainSingle();
    }

    [Fact]
    public async Task ComposedAssembly_LateFailureCannotOverwriteStagedSuccess()
    {
        _runtime.AllowDispatch = false;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Assembling;
            await db.SaveChangesAsync();
        }
        var assembled = new WorkflowComposedAssembly("integration", "transferred-tree", "diff", ["child"]);
        await _service.StageComposedAssemblyAsync(attached.WorkPlanId, assembled, CancellationToken.None);
        await MarkParentTreeTransferredAsync(assembled.TreeHash);

        (await _service.CompleteComposedAssemblyAsync(
            attached.WorkPlanId, null, "metadata_update_failed", CancellationToken.None))
            .Should().BeFalse();

        var planAfterFailure = await GetPlanAsync(attached.WorkPlanId);
        planAfterFailure.Status.Should().Be(WorkPlanStatus.Assembling);
        planAfterFailure.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Waiting);
        (await _service.GetStagedComposedAssemblyAsync(
            attached.WorkPlanId, CancellationToken.None))!.Should().BeEquivalentTo(assembled);
        _runtime.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task ComposedAssembly_CrashAfterTransferBeforeRunUpdate_ReconcilesOnlyStagedTree()
    {
        _runtime.AllowDispatch = false;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        await _runStore.UpdateWorktreeAsync(
            _parent.Id, request.IncomingInput.WorktreePath,
            Agentweaver.Api.Git.WorktreeManager.BranchNameFor(_parent.Id));
        await _runStore.UpdateAssemblyArtifactsAsync(_parent.Id, "parent-tree", string.Empty);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Assembling;
            await db.SaveChangesAsync();
        }
        var assembled = new WorkflowComposedAssembly("integration", "transferred-tree", "diff", []);
        await _service.StageComposedAssemblyAsync(attached.WorkPlanId, assembled, CancellationToken.None);

        var persistedParent = (await _runStore.GetAsync(_parent.Id))!;
        (await _service.TryRestoreTransferredParentTreeAsync(
            persistedParent, "unrelated-tree", CancellationToken.None)).Should().BeFalse();
        (await _runStore.GetAsync(_parent.Id))!.TreeHash.Should().Be("parent-tree");
        (await BuildService("after-crash", _runtime).TryRestoreTransferredParentTreeAsync(
            persistedParent, assembled.TreeHash, CancellationToken.None)).Should().BeTrue();
        (await _runStore.GetAsync(_parent.Id))!.TreeHash.Should().Be(assembled.TreeHash);
    }

    [Fact]
    public async Task ComposedAssembly_FailedChildPlan_DeliversOneTerminalFailure()
    {
        _runtime.AllowDispatch = false;
        _runtime.DeliverResult = true;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Assembling;
            await db.SaveChangesAsync();
        }

        (await _service.CompleteComposedAssemblyAsync(
            attached.WorkPlanId, null, "required_child_failed", CancellationToken.None)).Should().BeFalse();
        await BuildService("retry-pod", _runtime).SweepAsync();

        _runtime.Deliveries.Should().ContainSingle()
            .Which.FailureReason.Should().Be("required_child_failed");
        _runtime.Deliveries[0].Succeeded.Should().BeFalse();
        _runtime.Deliveries[0].Assembly.Should().BeNull();
        (await GetPlanAsync(attached.WorkPlanId)).Status.Should().Be(WorkPlanStatus.AssemblyFailed);
        (await _runStore.GetAsync(RunId.Parse(attached.ChildCoordinatorRunId)))!
            .Status.Should().Be(DomainRunStatus.Failed);
    }

    [Fact]
    public async Task ComposedAssembly_RestartAfterCheckpointBeforeRunTerminal_RecoversChildAndParent()
    {
        _runtime.AllowDispatch = false;
        _runtime.DeliverResult = true;
        var request = ComposedRequest();
        var attached = await _service.PrepareComposedAsync(request);
        await _service.ArmContinuationAsync(
            attached.WorkPlanId,
            NewRequest(WorkflowChildWorkService.ResumeRequestId(
                _parent.Id.ToString(), request.ParentWorkflowNodeId, attached.WorkPlanId)),
            _parent.SubmittingUser);
        var assembly = new WorkflowComposedAssembly("integration", "tree", "diff", []);
        var checkpoint = new WorkflowChildWorkResult(
            attached.WorkPlanId, attached.ChildCoordinatorRunId,
            request.ParentWorkflowId, request.ParentWorkflowNodeId,
            null, true, WorkPlanStatus.Complete, null, [], "assembled", assembly);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
            plan.Status = WorkPlanStatus.Complete;
            plan.ParentResumeResultJson = JsonSerializer.Serialize(checkpoint, JsonDefaults.Options);
            await db.SaveChangesAsync();
        }

        await BuildService("recovery-pod", _runtime).SweepAsync();
        await _service.SweepAsync();

        (await _runStore.GetAsync(RunId.Parse(attached.ChildCoordinatorRunId)))!
            .Status.Should().Be(DomainRunStatus.Completed);
        _runtime.Deliveries.Should().ContainSingle()
            .Which.Assembly.Should().BeEquivalentTo(assembly);
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
    public async Task StartupPreparation_DoesNotParkParentWithoutItsExecutionLease()
    {
        var (persisted, _) = await _service.EnsurePersistedAsync(Request());

        await _service.PrepareRestartRecoveryAsync();

        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.InProgress);
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
    public async Task NoRestart_ObservedAdvanceWinsDeliveryAck_ReconcilesPlanWithoutReplayingJoin()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        _runtime.DeliverResult = true;
        _runtime.OnDelivered = async () =>
        {
            (await _pendingRequests.MarkObservedWorkflowAdvanceAsync(_parent.Id.ToString()))
                .Should().BeFalse("an unrelated queued event does not prove fan-in advanced");
            (await _pendingRequests.MarkObservedWorkflowAdvanceAsync(
                _parent.Id.ToString(), "fan-in-discovery-fan-in")).Should().BeTrue();
        };

        await _service.SweepAsync();
        await _service.SweepAsync();

        _runtime.Deliveries.Should().ContainSingle();
        _runtime.DurableParentSteps.Should().ContainSingle();
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Delivered);
        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.InProgress);
        (await _service.HasDeliveredParentResumeAsync(_parent.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task ApiOnlySecondStartup_AfterJoinedResume_PreservesActiveParentAndBranchIdentities()
    {
        var attached = await CreateAsync(Request());
        await SetPlanAndBranchStatusAsync(attached.WorkPlanId, WorkPlanStatus.Complete, SubtaskStatus.Completed);
        _runtime.DeliverResult = true;
        await _service.SweepAsync();
        var originalIds = attached.Branches.Select(branch => branch.SubtaskId).ToArray();

        await BuildService("second-api-pod", _runtime).PrepareRestartRecoveryAsync();
        await _service.SweepAsync();

        (await _runStore.GetAsync(_parent.Id))!.Status.Should().Be(DomainRunStatus.InProgress,
            "the second API must not park a live synthesis or make review dispatch inactive");
        (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
            .Should().Be(WorkflowChildWorkResumeStates.Delivered);
        (await _service.HasDeliveredParentResumeAsync(_parent.Id.ToString())).Should().BeTrue();
        attached.Branches.Select(branch => branch.SubtaskId).Should().Equal(originalIds);
        _runtime.Deliveries.Should().ContainSingle();
        _runtime.DurableParentSteps.Should().ContainSingle();
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
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, "missing_child")]
    [InlineData(false, false, "undeclared_path")]
    [InlineData(false, false, "cross_project")]
    [InlineData(false, false, "missing_revision")]
    [InlineData(true, false, "moved_parent")]
    [InlineData(true, false, "wrong_generation")]
    [InlineData(true, false, "cancel_before_apply")]
    [InlineData(true, false, "reopened_before_apply")]
    [InlineData(true, false, "missing_worktree")]
    [InlineData(false, false, "late_cross_project")]
    [InlineData(false, false, "late_owner_takeover")]
    public async Task DeclaredFanArtifacts_ProjectBeforeResume_AndComposedChildInheritsSameBytes(
        bool crashAfterPreparedIntent, bool crashAfterRef, string? invalidSource)
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "agentweaver-fan-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        var manager = _provider.GetRequiredService<WorktreeManager>();
        try
        {
            Repository.Init(repositoryPath);
            using var repository = new Repository(repositoryPath);
            File.WriteAllText(Path.Combine(repositoryPath, "base.txt"), "base");
            Commands.Stage(repository, "base.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var baseline = repository.Commit("base", signature, signature, new LibGit2Sharp.CommitOptions());
            var parent = NewRun(RunId.New(), DomainRunStatus.InProgress) with
            {
                ProjectId = _parent.ProjectId,
                RepositoryPath = repositoryPath,
                OriginatingBranch = repository.Head.FriendlyName,
            };
            var parentWorktree = manager.AddWorktree(repositoryPath, parent.OriginatingBranch, parent.Id);
            parent = parent with
            {
                WorktreePath = parentWorktree.WorktreePath,
                WorktreeBranch = parentWorktree.BranchName,
            };
            await _runStore.InsertAsync(parent);
            var incoming = new AgentTurnInput(parent.Id.ToString(), "Synthetic incident",
                parentWorktree.WorktreePath, parentWorktree.BranchName,
                repositoryPath, parent.OriginatingBranch,
                "github-copilot", "test-model", parent.SubmittingUser,
                FanExecutionBaseCommitHash: baseline.Sha);
            var request = new WorkflowChildWorkRequest(parent, "custom-demo", "fan", "join",
                [
                    Branch("incident") with { DeclaredOutputPaths = ["demo/incident-brief.md"] },
                    Branch("checklist") with { DeclaredOutputPaths = ["demo/response-checklist.md"] },
                ], incoming, baseline.Tree.Sha);
            _runtime.AllowDispatch = false;
            var attached = await CreateAsync(request);
            var runs = new List<(int SubtaskId, string? ChildRunId, string Status)>();
            var original = new[] { "incident source bytes", "checklist source bytes" };
            for (var i = 0; i < attached.Branches.Count; i++)
            {
                var childId = RunId.New();
                var worktree = manager.AddWorktree(repositoryPath, parent.OriginatingBranch, childId);
                Directory.CreateDirectory(Path.Combine(worktree.WorktreePath, "demo"));
                var file = i == 0 ? "incident-brief.md" : "response-checklist.md";
                File.WriteAllText(Path.Combine(worktree.WorktreePath, "demo", file), original[i]);
                var tree = manager.CommitChanges(worktree.WorktreePath, childId);
                var capture = RunOutputTreeCapture.CaptureDeclaredFiles(repositoryPath,
                    worktree.BranchName, tree, [$"demo/{file}"]);
                await _runStore.InsertAsync(NewRun(childId, DomainRunStatus.InProgress) with
                {
                    ProjectId = parent.ProjectId,
                    RepositoryPath = repositoryPath,
                    OriginatingBranch = parent.OriginatingBranch,
                    ParentRunId = attached.ChildCoordinatorRunId,
                    SubtaskId = attached.Branches[i].SubtaskId.ToString(),
                    WorktreePath = worktree.WorktreePath,
                    WorktreeBranch = worktree.BranchName,
                    Result = "File written.",
                });
                (await _runStore.TryMutateTerminalOutcomeAsync(childId,
                    new TerminalRunMutation(
                        TerminalRunOutcome.Create(DomainRunStatus.AssembleReady,
                            EventTypes.RunAssembleReady, new { treeHash = tree },
                            DateTimeOffset.UtcNow, 1), null,
                        TreeHash: tree, WorktreeBranch: worktree.BranchName,
                        FanDeclaredFiles: new FanDeclaredFilesPublication(
                            attached.WorkPlanId.ToString(), capture.CommitHash, tree, capture.Files))))
                    .Should().BeTrue();
                runs.Add((attached.Branches[i].SubtaskId, childId.ToString(), SubtaskStatus.Completed));
                manager.RemoveWorktree(repositoryPath, worktree.WorktreePath, worktree.BranchName);
                (await _runStore.ListOutputRevisionsAsync(childId)).Should().ContainSingle();
            }
            await SetBranchRunsAsync(attached.WorkPlanId, WorkPlanStatus.Complete, runs);
            if (invalidSource is not null
                && invalidSource is not ("moved_parent" or "late_cross_project" or "late_owner_takeover"
                    or "wrong_generation"
                    or "cancel_before_apply" or "reopened_before_apply" or "missing_worktree"))
            {
                if (invalidSource is "cross_project" or "missing_revision")
                {
                    await using var connection = await _runDb.Db.OpenConnectionAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = invalidSource == "cross_project"
                        ? "UPDATE runs SET project_id=$value WHERE run_id=$run;"
                        : "UPDATE runs SET current_output_revision_id=$value WHERE run_id=$run;";
                    command.Parameters.AddWithValue("$value", invalidSource == "cross_project"
                        ? ProjectId.New().ToString() : Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("$run", runs[0].ChildRunId!);
                    (await command.ExecuteNonQueryAsync()).Should().Be(1);
                }
                else
                {
                    using var scope = _provider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                    var branch = await db.Subtasks.SingleAsync(s => s.Id == runs[0].SubtaskId);
                    if (invalidSource == "missing_child")
                        branch.ChildRunId = RunId.New().ToString();
                    else
                        branch.DeclaredOutputPathsJson = "[\"demo/other.md\"]";
                    await db.SaveChangesAsync();
                }
                await _service.SweepAsync();
                var failurePlan = await GetPlanAsync(attached.WorkPlanId);
                failurePlan.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
                var failed = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    failurePlan.ParentResumeResultJson!, JsonDefaults.Options)!;
                failed.Succeeded.Should().BeFalse();
                failed.FanProjection.Should().BeNull();
                failed.FailureReason.Should().NotBeNullOrWhiteSpace();
                File.Exists(Path.Combine(parentWorktree.WorktreePath, "demo", "incident-brief.md"))
                    .Should().BeFalse();
                manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
                return;
            }
            if (crashAfterPreparedIntent)
            {
                var branches = new List<WorkflowChildWorkBranch>();
                var retained = new List<RunOutputTree.File>();
                for (var i = 0; i < runs.Count; i++)
                {
                    var child = (await _runStore.GetAsync(RunId.Parse(runs[i].ChildRunId!)))!;
                    var revision = await ((IRunStore)_runStore).ResolveOutputRevisionAsync(
                        child.Id, child.CurrentOutputRevisionId!);
                    retained.AddRange(revision.ResolveFiles());
                    branches.Add(new WorkflowChildWorkBranch(runs[i].SubtaskId,
                        attached.Branches[i].NodeId, i, runs[i].Status,
                        child.Id.ToString(), WorktreeBranch: child.WorktreeBranch,
                        TreeHash: child.TreeHash, OutputRevisionId: revision.RevisionId));
                }
                var prepared = manager.PrepareFanInputProjection(
                    repositoryPath, parentWorktree.WorktreePath, parent.Id,
                    baseline.Sha, baseline.Tree.Sha, retained);
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var plan = await db.WorkPlans.SingleAsync(row => row.Id == attached.WorkPlanId);
                plan.ParentResumeResultJson = JsonSerializer.Serialize(
                    new WorkflowChildWorkResult(attached.WorkPlanId, attached.ChildCoordinatorRunId,
                        "custom-demo", "fan", "join", true, WorkPlanStatus.Complete, null,
                        branches, "compact branch references",
                        FanProjection: new WorkflowFanProjection(
                            parent.LifecycleGeneration + (invalidSource == "wrong_generation" ? 1 : 0),
                            baseline.Sha, baseline.Tree.Sha,
                            prepared.CommitHash, prepared.TreeHash)),
                    JsonDefaults.Options);
                await db.SaveChangesAsync();
                if (crashAfterRef)
                    manager.ApplyFanInputProjection(parentWorktree.WorktreePath, parent.Id,
                        baseline.Sha, prepared.CommitHash, prepared.TreeHash);
                using var stillParked = new Repository(parentWorktree.WorktreePath);
                stillParked.Head.Tip.Id.Sha.Should().Be(
                    crashAfterRef ? prepared.CommitHash : baseline.Sha);
            }
            if (invalidSource is "moved_parent" or "wrong_generation")
            {
                if (invalidSource == "moved_parent")
                {
                    File.WriteAllText(Path.Combine(parentWorktree.WorktreePath, "unrelated.txt"), "unexpected parent edit");
                    manager.CommitChanges(parentWorktree.WorktreePath, parent.Id);
                }
                await _service.SweepAsync();
                var failed = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    (await GetPlanAsync(attached.WorkPlanId)).ParentResumeResultJson!, JsonDefaults.Options)!;
                failed.Succeeded.Should().BeFalse();
                failed.FailureReason.Should().Be("fan_projection_base_changed");
                File.Exists(Path.Combine(parentWorktree.WorktreePath, "demo", "incident-brief.md"))
                    .Should().BeFalse();
                manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
                return;
            }
            if (invalidSource is "cancel_before_apply" or "reopened_before_apply")
            {
                _service.BeforeFanProjectionFenceOverride = async () =>
                {
                    _service.BeforeFanProjectionFenceOverride = null;
                    if (invalidSource == "cancel_before_apply")
                        await _service.CancelForParentAsync(parent.Id.ToString());
                    else
                    {
                        await using var connection = await _runDb.Db.OpenConnectionAsync();
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                            UPDATE runs SET lifecycle_generation=lifecycle_generation+1
                             WHERE run_id=$run;
                            """;
                        command.Parameters.AddWithValue("$run", parent.Id.ToString());
                        (await command.ExecuteNonQueryAsync()).Should().Be(1);
                    }
                };
                await _service.SweepAsync();
                var fenced = await GetPlanAsync(attached.WorkPlanId);
                if (invalidSource == "cancel_before_apply")
                {
                    fenced.Status.Should().Be(WorkPlanStatus.Cancelled);
                    fenced.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
                    (await _runStore.GetAsync(parent.Id))!.Status.Should().Be(DomainRunStatus.AwaitingReview,
                        "suppression wins before the parent run's separate terminal transition");
                }
                else
                {
                    fenced.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
                    fenced.AssemblyStatusReason.Should().Be("fan_projection_fence_lost");
                }
                File.Exists(Path.Combine(parentWorktree.WorktreePath, "demo", "incident-brief.md"))
                    .Should().BeFalse();
                using (var intact = new Repository(parentWorktree.WorktreePath))
                    intact.Head.Tip.Id.Sha.Should().Be(baseline.Sha);
                (await _runStore.GetAsync(parent.Id))!.TreeHash.Should().BeNull();
                manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
                return;
            }
            if (invalidSource == "missing_worktree")
            {
                Directory.Delete(parentWorktree.WorktreePath, recursive: true);
                var stalePath = Path.Combine(repositoryPath, "missing-parent-checkout");
                await _runStore.UpdateWorktreeAsync(parent.Id, stalePath, parentWorktree.BranchName);
                IRunStore guarded = new PreviewPublicationLeaseRunStore(
                    new RunActiveClaimGuardedRunStore(
                        _runStore, _provider.GetRequiredService<RunActiveClaimGuard>()));
                var recoveredService = BuildService("recovered-pod", _runtime, runStore: guarded);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                _runtime.DeliverResult = false;
                await recoveredService.SweepAsync(deadline.Token);
                (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
                    .Should().Be(WorkflowChildWorkResumeStates.Ready);
                var storedParent = (await guarded.GetAsync(parent.Id))!;
                storedParent.WorktreePath.Should().Be(parentWorktree.WorktreePath,
                    "the recovered checkout path must be reflected atomically with the projection receipt");
                storedParent.TreeHash.Should().NotBeNullOrWhiteSpace();
                File.ReadAllText(Path.Combine(storedParent.WorktreePath!, "demo", "incident-brief.md"))
                    .Should().Be(original[0]);
                File.ReadAllText(Path.Combine(storedParent.WorktreePath!, "demo", "response-checklist.md"))
                    .Should().Be(original[1]);
                _runtime.DeliverResult = true;
                await BuildService("receipt-pod", _runtime, runStore: guarded).SweepAsync(deadline.Token);
                (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
                    .Should().Be(WorkflowChildWorkResumeStates.Delivered);
                manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
                return;
            }
            _runtime.DeliverResult = false;
            await _service.SweepAsync();
            (await GetPlanAsync(attached.WorkPlanId)).ParentResumeState
                .Should().Be(WorkflowChildWorkResumeStates.Ready);
            _runtime.Deliveries.Should().ContainSingle();
            if (invalidSource is "late_cross_project" or "late_owner_takeover")
            {
                var recoveredService = BuildService("recovered-pod", _runtime);
                if (invalidSource == "late_cross_project")
                {
                    await using var connection = await _runDb.Db.OpenConnectionAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE runs SET project_id=$project WHERE run_id=$run;";
                    command.Parameters.AddWithValue("$project", ProjectId.New().ToString());
                    command.Parameters.AddWithValue("$run", runs[0].ChildRunId!);
                    (await command.ExecuteNonQueryAsync()).Should().Be(1);
                }
                else
                    recoveredService.BeforeFanProjectionFenceOverride = async () =>
                    {
                        using var scope = _provider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                        var pending = await db.PendingRequests.SingleAsync(r => r.RunId == parent.Id.ToString());
                        pending.DeliveryClaimOwner = "takeover-owner";
                        pending.DeliveryClaimedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync();
                    };
                await recoveredService.SweepAsync();
                var blocked = await GetPlanAsync(attached.WorkPlanId);
                blocked.Status.Should().Be(WorkPlanStatus.AssemblyBlocked);
                blocked.AssemblyStatusReason.Should().Be(invalidSource == "late_cross_project"
                    ? "fan_projection_provenance_mismatch" : "fan_projection_fence_lost");
                _runtime.Deliveries.Should().ContainSingle();
                manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
                return;
            }
            _runtime.DeliverResult = true;
            await BuildService("recovered-pod", _runtime).SweepAsync();
            _runtime.Deliveries.Should().HaveCount(2);
            _runtime.Deliveries[1].FanProjection!.PreparedCommitHash
                .Should().Be(_runtime.Deliveries[0].FanProjection!.PreparedCommitHash);
            var delivered = _runtime.Deliveries[1];
            delivered.FanProjection.Should().NotBeNull();
            delivered.JoinedOutput.Should().NotContain("source bytes").And.NotContain("diff --git");
            File.ReadAllText(Path.Combine(parentWorktree.WorktreePath, "demo", "incident-brief.md"))
                .Should().Be(original[0]);
            File.ReadAllText(Path.Combine(parentWorktree.WorktreePath, "demo", "response-checklist.md"))
                .Should().Be(original[1]);
            var composed = manager.AddWorktree(repositoryPath, parentWorktree.BranchName, RunId.New());
            File.ReadAllText(Path.Combine(composed.WorktreePath, "demo", "incident-brief.md"))
                .Should().Be(original[0]);
            manager.RemoveWorktree(repositoryPath, composed.WorktreePath, composed.BranchName);
            manager.RemoveWorktree(repositoryPath, parentWorktree.WorktreePath, parentWorktree.BranchName);
        }
        finally
        {
            if (Directory.Exists(repositoryPath))
            {
                foreach (var file in Directory.GetFiles(repositoryPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(repositoryPath, recursive: true);
            }
        }
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
    public async Task ParentCancellation_WhenOneChildCancellationFails_AttemptsEverySnapshottedChild()
    {
        var firstChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var secondChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var thirdChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var fourthChild = NewRun(RunId.New(), DomainRunStatus.InProgress) with
        {
            ParentRunId = _parent.Id.ToString(),
        };
        var (firstPlanId, firstActiveSubtaskIds, _) = await SeedTopLevelFanPlanAsync(firstChild, secondChild);
        var (secondPlanId, secondActiveSubtaskIds, _) = await SeedTopLevelFanPlanAsync(thirdChild, fourthChild);
        var runtime = new RecordingRuntime { FailFirstCancellation = true };
        var service = BuildService("pod-failure-isolation", runtime);

        var act = async () => await service.CancelForParentAsync(_parent.Id.ToString());

        var failure = await act.Should().ThrowAsync<AggregateException>();
        failure.Which.InnerExceptions.Should().ContainSingle();
        runtime.CancellationAttempts.Select(run => run.Id)
            .Should().BeEquivalentTo([firstChild.Id, secondChild.Id, thirdChild.Id, fourthChild.Id]);
        using var scope = _provider.CreateScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .Subtasks.AsNoTracking()
            .Where(subtask => firstActiveSubtaskIds.Concat(secondActiveSubtaskIds).Contains(subtask.Id))
            .ToListAsync();
        claimed.Should().OnlyContain(subtask =>
            subtask.CancellationRequestedAt != null
            && subtask.CancellationRequestedByRunId == _parent.Id.ToString());
        (await GetPlanAsync(firstPlanId)).ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        (await GetPlanAsync(secondPlanId)).ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
    }

    [Fact]
    public async Task ParentCancellation_TwoActiveChildren_TerminalRaceAndRestart_PersistOneAttributableEventEach()
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
        var eventDirectory = Path.Combine(
            Path.GetTempPath(),
            "aw-child-cancellation-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(eventDirectory);
        try
        {
            var eventConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(eventDirectory, "agentweaver.db"),
                })
                .Build();
            CreateRunEventsTable(Path.Combine(eventDirectory, "memory.db"));
            var firstEventStream = new SqliteRunEventStream(eventConfig);
            var firstRuntime = new PersistingCancellationRuntime(
                _runStore,
                firstEventStream,
                [firstChild.Id, secondChild.Id],
                terminalizeBeforeFirstCancellation: true);
            var firstService = BuildService("pod-production-race", firstRuntime);

            await firstService.CancelForParentAsync(_parent.Id.ToString());

            var restartedEventStream = new SqliteRunEventStream(eventConfig);
            var restartedRuntime = new PersistingCancellationRuntime(
                _runStore,
                restartedEventStream,
                [firstChild.Id, secondChild.Id],
                terminalizeBeforeFirstCancellation: false);
            var restartedService = BuildService("pod-restarted", restartedRuntime);
            await restartedService.PrepareRestartRecoveryAsync();
            await restartedService.SweepAsync();

            foreach (var childId in new[] { firstChild.Id, secondChild.Id })
            {
                var events = await restartedEventStream.GetPersistedEventsAsync(childId.ToString());
                var cancellations = events.Where(evt => evt.Type == EventTypes.RunCancelled).ToList();
                cancellations.Should().ContainSingle();
                var payload = JsonSerializer.SerializeToElement(cancellations[0].Payload);
                payload.GetProperty("reason").GetString().Should().Be("parent_cancelled");
                payload.GetProperty("requested").GetBoolean().Should().BeTrue();
                payload.GetProperty("requestedByRunId").GetString().Should().Be(_parent.Id.ToString());
                (await _runStore.GetAsync(childId))!.Status.Should().Be(DomainRunStatus.Failed);
            }

            restartedRuntime.Deliveries.Should().BeEmpty("cancelled work cannot resume its parent join");
            (await restartedEventStream.GetPersistedEventsAsync(_parent.Id.ToString()))
                .Should().NotContain(evt => evt.Type == EventTypes.WorkflowStep);
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var activeSubtasks = await db.Subtasks.AsNoTracking()
                .Where(subtask => activeSubtaskIds.Contains(subtask.Id))
                .ToListAsync();
            activeSubtasks.Should().OnlyContain(subtask =>
                subtask.CancellationRequestedAt != null
                && subtask.CancellationRequestedByRunId == _parent.Id.ToString());
            var pendingSubtask = await db.Subtasks.AsNoTracking()
                .SingleAsync(subtask => subtask.Id == pendingSubtaskId);
            pendingSubtask.Status.Should().Be(SubtaskStatus.Pending);
            pendingSubtask.CancellationRequestedAt.Should().BeNull();
            var plan = await GetPlanAsync(planId);
            plan.Status.Should().Be(WorkPlanStatus.Cancelled);
            plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Suppressed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(eventDirectory, recursive: true);
        }
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

    private WorkflowComposedWorkRequest ComposedRequest() => new(
        _parent, "workflow-v1", "dynamic-plan", "Derive dependent implementation tasks",
        new AgentTurnInput(
            _parent.Id.ToString(), _parent.Task,
            "C:\\repo\\.agentweaver\\worktrees\\parent", "agentweaver/parent",
            _parent.RepositoryPath, _parent.OriginatingBranch,
            ModelSource.GitHubCopilot.ToApiString(), _parent.ModelId,
            _parent.SubmittingUser, ProjectId: _parent.ProjectId!.ToString()),
        "parent-tree");

    private async Task MarkParentTreeTransferredAsync(string treeHash)
    {
        await _runStore.UpdateWorktreeAsync(
            _parent.Id, ComposedRequest().IncomingInput.WorktreePath,
            Agentweaver.Api.Git.WorktreeManager.BranchNameFor(_parent.Id));
        await _runStore.UpdateAssemblyArtifactsAsync(_parent.Id, treeHash, "assembled");
    }

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
        IWorkflowChildWorkRuntime runtime,
        int staleSeconds = 120,
        IRunStore? runStore = null)
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
            runStore ?? _runStore,
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

    private static void CreateRunEventsTable(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventIdentity" TEXT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX "IX_RunEvents_RunId_Sequence"
                ON "RunEvents" ("RunId", "Sequence");
            CREATE UNIQUE INDEX "IX_RunEvents_RunId_EventIdentity"
                ON "RunEvents" ("RunId", "EventIdentity")
                WHERE "EventIdentity" IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _memoryConnection.DisposeAsync();
        await _runDb.DisposeAsync();
    }

    private sealed class RecordingRuntime : IWorkflowChildWorkRuntime
    {
        public bool ParentResumeActive { get; set; } = true;
        public bool AllowDispatch { get; set; } = true;
        public bool AlwaysReportDispatchInactive { get; set; }
        public Action? BeforeDispatchEnabled { get; set; }
        public Action? BeforeParentResumeActiveCheck { get; set; }
        public List<CoordinatorDispatchContext> Started { get; } = [];
        public List<DomainRun> Cancelled { get; } = [];
        public List<DomainRun> CancellationAttempts { get; } = [];
        public List<WorkflowChildWorkResult> Deliveries { get; } = [];
        public List<(string RunId, string OwnerUser)> EnsuredStreams { get; } = [];
        public List<(string RunId, string EventIdentity, JsonElement Payload)> DurableParentSteps { get; } = [];
        public bool DeliverResult { get; set; }
        public Func<Task>? OnDelivered { get; set; }
        public bool FailFirstCancellation { get; set; }
        private int _cancellationFailures;

        public bool DispatchEnabled
        {
            get
            {
                var callback = BeforeDispatchEnabled;
                BeforeDispatchEnabled = null;
                callback?.Invoke();
                return AllowDispatch;
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
        public void StartAssembly(CoordinatorDispatchContext context) => Started.Add(context);

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

        public async Task<bool> TryDeliverParentResumeAsync(
            string parentRunId,
            PendingDelivery delivery,
            WorkflowChildWorkResult result,
            CancellationToken ct)
        {
            Deliveries.Add(result);
            if (OnDelivered is not null)
                await OnDelivered();
            return DeliverResult;
        }

        public Task CancelRunAsync(DomainRun run, string requestedByRunId, CancellationToken ct)
        {
            CancellationAttempts.Add(run);
            if (FailFirstCancellation && Interlocked.Exchange(ref _cancellationFailures, 1) == 0)
                throw new InvalidOperationException("forced child cancellation failure");
            Cancelled.Add(run);
            return Task.CompletedTask;
        }
    }

    private sealed class PersistingCancellationRuntime(
        IRunStore runStore,
        IRunEventStream eventStream,
        IReadOnlyList<RunId> racedChildIds,
        bool terminalizeBeforeFirstCancellation) : IWorkflowChildWorkRuntime
    {
        private readonly RunStreamStore _streamStore = new(eventStream);
        private int _raceTriggered;

        public List<WorkflowChildWorkResult> Deliveries { get; } = [];
        public bool DispatchEnabled => true;
        public bool IsDispatchActive(string coordinatorRunId) => false;
        public bool IsParentResumeActive(string parentRunId) => false;
        public void StartDispatch(CoordinatorDispatchContext context) { }
        public void StartAssembly(CoordinatorDispatchContext context) { }
        public void RecordParentStep(string parentRunId, object payload) { }
        public Task<bool> RecordParentReadyStepAsync(
            int workPlanId,
            string parentRunId,
            string eventIdentity,
            object payload,
            CancellationToken ct) => Task.FromResult(false);
        public void EnsureRunStream(string runId, string ownerUser) =>
            _ = _streamStore.Get(runId) ?? _streamStore.Create(runId, ownerUser);
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
            return Task.FromResult(false);
        }

        public async Task CancelRunAsync(DomainRun run, string requestedByRunId, CancellationToken ct)
        {
            if (terminalizeBeforeFirstCancellation
                && Interlocked.Exchange(ref _raceTriggered, 1) == 0)
            {
                foreach (var childId in racedChildIds)
                {
                    (await runStore.TerminalizeForTestAsync(
                        childId,
                        DomainRunStatus.Failed,
                        ct: CancellationToken.None)).Should().BeTrue();
                }
            }

            EnsureRunStream(run.Id.ToString(), run.SubmittingUser);
            await EndpointHelpers.CancelRunWorkAsync(
                run,
                runStore,
                _streamStore,
                new RunWorkflowRegistry(),
                new NoOpWorktreeOperations(),
                NullLogger.Instance,
                ct,
                eventStream: eventStream,
                reason: "parent_cancelled",
                requestedByRunId: requestedByRunId);
        }
    }

    private sealed class NoOpWorktreeOperations : IWorktreeOperations
    {
        public bool WorktreeExists(string worktreePath) => false;
        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException();
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException();
        public int GetStepCount(string runId) => throw new NotImplementedException();
        public MergeResult MergeWorktree(
            string repositoryPath,
            string originatingBranch,
            string worktreeBranch,
            string expectedTreeHash) => throw new NotImplementedException();
        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch) =>
            throw new NotImplementedException();
        public string? GetTreeHash(string worktreePath) => null;
    }
}
