using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
using Agentweaver.Tests.Coordinator;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Tests.PostgresIntegration;

public sealed partial class PostgresAppBootTests
{
    [PostgresFact]
    public async Task ComposedPersistence_LegacySerializableSnapshot_ReproducesObserved40001()
    {
        var seeded = await SeedComposedFailureAsync();
        try
        {
            using var planningScope = _fixture.Services.CreateScope();
            var planning = planningScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var stalePlan = await planning.WorkPlans.SingleAsync(plan => plan.Id == seeded.PlanId);
            await using var transaction = await planning.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await planning.Subtasks.AnyAsync(subtask => subtask.WorkPlanId == seeded.PlanId);
            using (var peerScope = _fixture.Services.CreateScope())
            {
                var peer = peerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await peer.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow));
            }
            stalePlan.UpdatedAt = DateTimeOffset.UtcNow;
            var save = () => planning.SaveChangesAsync();
            var failure = await save.Should().ThrowAsync<InvalidOperationException>();
            ExceptionChain.Contains(failure.Which, exception => exception is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.SerializationFailure }).Should().BeTrue();
            await transaction.RollbackAsync();
        }
        finally
        {
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresFact]
    public async Task ComposedPersistence_WaitsForConcurrentReservedRowUpdate_WithoutReplanning()
    {
        var seeded = await SeedComposedFailureAsync();
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        try
        {
            (await store.TryReopenTerminalToInProgressAsync(seeded.Parent.Id)).Should().BeTrue();
            (await store.TryReopenTerminalToInProgressAsync(seeded.Child.Id)).Should().BeTrue();
            await store.UpdateStatusAsync(seeded.Parent.Id, RunStatus.AwaitingReview, null);
            await store.UpdateStatusAsync(seeded.Child.Id, RunStatus.Pending, null);
            using var holderScope = _fixture.Services.CreateScope();
            var holder = holderScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await holder.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.Status, WorkPlanStatus.Planned)
                    .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Waiting));
            await using var transaction = await holder.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await holder.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow));

            var runner = new TestFileEditAgentRunner
            {
                ExecuteOverride = (_, _) => """
                    [{"title":"Write summary","scope":"Write demo/summary.md from retained inputs.",
                      "role":"lead-architect","complexity":"low","phase":"implementation",
                      "isolation":"worktree","declared_output_paths":["demo/summary.md"],"depends_on":[]}]
                    """,
            };
            var executor = new CoordinatorOrchestratorExecutor(
                new FakeWorkflowAgentFactory(runner),
                _fixture.Services.GetRequiredService<RunStreamStore>(),
                _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                _fixture.Services.GetRequiredService<ILoggerFactory>(),
                _fixture.Services.GetRequiredService<IStoryIndependenceClassifier>(),
                _fixture.Services.GetRequiredService<IAssemblyGateCodeClassifier>(),
                "test-model", null, null);
            var persist = executor.OrchestrateAsync(new CoordinatorDraftInput(
                seeded.Child.Id.ToString(), seeded.Parent.ProjectId!.Value.ToString(),
                "Write summary", seeded.Parent.SubmittingUser, seeded.Directory, "test-model",
                ModelSource: "byok", ByokProviderFingerprint: seeded.Input.ByokProviderFingerprint),
                CancellationToken.None);
            var waiting = false;
            await using var observer = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await observer.OpenAsync();
            for (var attempt = 0; attempt < 100 && !persist.IsCompleted; attempt++)
            {
                await using var command = observer.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE datname = current_database() AND wait_event_type = 'Lock'
                          AND query LIKE 'UPDATE %"WorkPlans"%')
                    """;
                if (await command.ExecuteScalarAsync() is true)
                {
                    waiting = true;
                    break;
                }
                await Task.Delay(25);
            }
            if (!waiting && persist.IsCompleted)
                await persist;
            waiting.Should().BeTrue("the real persistence transaction must contend for the reserved plan row");
            await transaction.CommitAsync();
            var result = await persist.WaitAsync(TimeSpan.FromSeconds(10));
            result.WorkPlanId.Should().Be(seeded.PlanId);
            result.InlineSubtaskCount.Should().Be(1);
            runner.InvocationCount.Should().Be(1, "only persistence may retry, not the model turn");
            using var verifyScope = _fixture.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await verify.Subtasks.CountAsync(subtask => subtask.WorkPlanId == seeded.PlanId)).Should().Be(1);
        }
        finally
        {
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_ReopensOriginalIdentitiesAtomically_PreservesInputs_AndRejectsRepeat()
    {
        var seeded = await SeedComposedFailureAsync();
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var launches = 0;
        var originalBytes = await File.ReadAllBytesAsync(Path.Combine(seeded.Directory, "demo", "source.md"));
        recovery.LaunchOverride = async (parent, input, node, lease, token) =>
        {
            launches++;
            parent.Id.Should().Be(seeded.Parent.Id);
            parent.LifecycleGeneration.Should().Be(seeded.Parent.LifecycleGeneration + 1);
            lease.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
            input.Should().Be(seeded.Input);
            node.Should().Be("compose");
            (await store.GetAsync(seeded.Child.Id, token))!.LifecycleGeneration
                .Should().Be(seeded.Child.LifecycleGeneration + 1);
        };
        try
        {
            await recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            launches.Should().Be(1);
            (await store.GetAsync(seeded.Parent.Id))!.Status.Should().Be(RunStatus.InProgress);
            (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.InProgress);
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking().SingleAsync(plan => plan.Id == seeded.PlanId);
            plan.Status.Should().Be(WorkPlanStatus.Planned);
            plan.CoordinatorRunId.Should().Be(seeded.Child.Id.ToString());
            plan.ParentResumeResultJson.Should().BeNull();
            plan.AssemblyStatusReason.Should().Be("composed_recovery_pending:2");
            (await db.Subtasks.CountAsync(subtask => subtask.WorkPlanId == plan.Id)).Should().Be(0);
            (await db.ExecutionIdentities.CountAsync(identity => identity.RunId == seeded.Parent.Id.ToString()))
                .Should().Be(2);
            var retry = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await retry.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            launches.Should().Be(1);
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(seeded.Directory, "demo", "source.md")))
                .Should().Equal(SHA256.HashData(originalBytes));
        }
        finally
        {
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresFact]
    public async Task ComposedRecovery_PreservesUndispatchedPlan_AndReusesItsSubtasks()
    {
        var seeded = await SeedComposedFailureAsync();
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var launches = 0;
        try
        {
            int firstId, secondId, dependencyId;
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var now = DateTimeOffset.UtcNow;
                var first = new Subtask
                {
                    WorkPlanId = seeded.PlanId, Title = "Read retained input",
                    Scope = "Read demo/source.md", AssignedAgent = "Neo", SelectedModelId = "test-model",
                    Phase = "validation", IsolationStrategy = "worktree",
                    DeclaredOutputPathsJson = "[]", Status = SubtaskStatus.Pending,
                    CreatedAt = now, UpdatedAt = now,
                };
                var second = new Subtask
                {
                    WorkPlanId = seeded.PlanId, Title = "Write summary",
                    Scope = "Write demo/summary.md from retained input", AssignedAgent = "Morpheus",
                    SelectedModelId = "test-model", Phase = "execution", IsolationStrategy = "worktree",
                    DeclaredOutputPathsJson = "[\"demo/summary.md\"]", Status = SubtaskStatus.Pending,
                    CreatedAt = now, UpdatedAt = now,
                };
                db.Subtasks.AddRange(first, second);
                await db.SaveChangesAsync();
                firstId = first.Id;
                secondId = second.Id;
                var dependency = new SubtaskDependency { SubtaskId = secondId, DependsOnSubtaskId = firstId };
                db.SubtaskDependencies.Add(dependency);
                await db.SaveChangesAsync();
                dependencyId = dependency.Id;
            }
            recovery.LaunchOverride = (parent, input, node, lease, _) =>
            {
                launches++;
                parent.Id.Should().Be(seeded.Parent.Id);
                input.Should().Be(seeded.Input);
                node.Should().Be("compose");
                lease.LifecycleGeneration.Should().Be(2);
                return Task.CompletedTask;
            };
            await recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            launches.Should().Be(1);
            using var verifyScope = _fixture.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var planAfter = await verify.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId);
            planAfter.CoordinatorRunId.Should().Be(seeded.Child.Id.ToString());
            planAfter.ParentRunId.Should().Be(seeded.Parent.Id.ToString());
            planAfter.ParentResumeRequestId.Should().BeNull("a fresh MAF continuation has a new request id");
            planAfter.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Committed);
            (await verify.PendingRequests.AsNoTracking()
                .AnyAsync(p => p.RunId == seeded.Parent.Id.ToString()))
                .Should().BeFalse("the delivered failure gate cannot be reused by the new MAF request");
            var rows = await verify.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == seeded.PlanId).OrderBy(s => s.Id).ToArrayAsync();
            rows.Select(s => s.Id).Should().Equal(firstId, secondId);
            rows.Select(s => (s.Title, s.Scope, s.AssignedAgent, s.Phase, s.DeclaredOutputPathsJson))
                .Should().Equal(
                    ("Read retained input", "Read demo/source.md", "Neo", "validation", "[]"),
                    ("Write summary", "Write demo/summary.md from retained input", "Morpheus", "execution", "[\"demo/summary.md\"]"));
            rows.Should().OnlyContain(s => s.Status == SubtaskStatus.Pending && s.ChildRunId == null);
            var edges = await verify.SubtaskDependencies.AsNoTracking()
                .Where(d => d.SubtaskId == secondId).ToArrayAsync();
            edges.Should().ContainSingle().Which.Id.Should().Be(dependencyId);
            edges[0].DependsOnSubtaskId.Should().Be(firstId);

            var runner = new TestFileEditAgentRunner
            {
                ExecuteOverride = (_, _) => throw new InvalidOperationException("persisted subtasks must not be replanned"),
            };
            var executor = new CoordinatorOrchestratorExecutor(
                new FakeWorkflowAgentFactory(runner),
                _fixture.Services.GetRequiredService<RunStreamStore>(),
                _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                _fixture.Services.GetRequiredService<ILoggerFactory>(),
                _fixture.Services.GetRequiredService<IStoryIndependenceClassifier>(),
                _fixture.Services.GetRequiredService<IAssemblyGateCodeClassifier>(),
                "test-model", null, null);
            var reused = await executor.OrchestrateAsync(new CoordinatorDraftInput(
                seeded.Child.Id.ToString(), seeded.Parent.ProjectId!.Value.ToString(),
                "Write summary", seeded.Parent.SubmittingUser, seeded.Directory, "test-model",
                ModelSource: "byok", ByokProviderFingerprint: seeded.Input.ByokProviderFingerprint),
                CancellationToken.None);
            reused.WorkPlanId.Should().Be(seeded.PlanId);
            reused.InlineSubtaskCount.Should().Be(2);
            runner.InvocationCount.Should().Be(0);
            (await verify.Subtasks.CountAsync(s => s.WorkPlanId == seeded.PlanId)).Should().Be(2);
        }
        finally
        {
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresFact]
    public async Task ComposedRecovery_RealMafPort_ArmsFreshGate_WithoutReplayingPredecessors()
    {
        foreach (var source in new[] { ModelSource.Byok, ModelSource.GitHubCopilot })
        {
            var seeded = await SeedComposedFailureAsync(source, copilotCapability: source == ModelSource.GitHubCopilot);
            try
            {
                int subtaskId;
                string oldRequestId;
                using (var scope = _fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                    oldRequestId = (await db.WorkPlans.SingleAsync(p => p.Id == seeded.PlanId))
                        .ParentResumeRequestId!;
                    var now = DateTimeOffset.UtcNow;
                    var subtask = new Subtask
                    {
                        WorkPlanId = seeded.PlanId, Title = "Retained pending work",
                        Scope = "Use demo/source.md", AssignedAgent = "Neo",
                        SelectedModelId = "test-model", Phase = "execution",
                        IsolationStrategy = "worktree", Status = SubtaskStatus.Pending,
                        CreatedAt = now, UpdatedAt = now,
                    };
                    db.Subtasks.Add(subtask);
                    await db.SaveChangesAsync();
                    subtaskId = subtask.Id;
                }

                var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
                var boundary = await _fixture.Services.GetRequiredService<RunOrchestrator>()
                    .ResolveDurableProviderBoundaryAsync(seeded.Parent, CancellationToken.None);
                boundary.Provider.ToModelSource().Should().Be(source);
                if (source == ModelSource.GitHubCopilot)
                    boundary.Provider.Should().BeOfType<EffectiveModelProviderResult.ProjectGitHubCopilot>()
                        .Which.BindingId.Should().Be("test-copilot-binding-" + seeded.Parent.Id);
                await recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
                var pending = _fixture.Services.GetRequiredService<PendingRequestStore>();
                PendingEntry? armed = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!timeout.IsCancellationRequested)
                {
                    armed = await pending.GetAsync(seeded.Parent.Id.ToString());
                    if (armed is not null)
                        break;
                    await Task.Delay(25);
                }
                armed.Should().NotBeNull("the real MAF composed request must reach the normal watcher");
                var newRequestId = armed!.Request.RequestId;
                newRequestId.Should().NotBe(oldRequestId);
                newRequestId.Should().NotBe(WorkflowChildWorkService.ResumeRequestId(
                    seeded.Parent.Id.ToString(), "compose", seeded.PlanId),
                    "the static-fan stable request ID helper is not the composed MAF port");
                var requestState = await pending.GetRequestStateAsync(seeded.Parent.Id.ToString());
                requestState.Should().NotBeNull();
                requestState!.Value.RequestId.Should().Be(newRequestId);
                requestState.Value.PortInfo.Should().Be(armed.Request.PortInfo);
                requestState.Value.DeliveryState.Should().Be(PendingRequestDeliveryStates.Waiting);
                var store = _fixture.Services.GetRequiredService<IRunStore>();
                while (!timeout.IsCancellationRequested
                    && (await store.GetAsync(seeded.Parent.Id))!.Status != RunStatus.AwaitingReview)
                    await Task.Delay(25);
                (await store.GetAsync(seeded.Parent.Id))!.Status.Should().Be(RunStatus.AwaitingReview);
                (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.InProgress);
                using var verifyScope = _fixture.Services.CreateScope();
                var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var plan = await verify.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId);
                plan.CoordinatorRunId.Should().Be(seeded.Child.Id.ToString());
                plan.ParentRunId.Should().Be(seeded.Parent.Id.ToString());
                plan.ParentResumeRequestId.Should().Be(newRequestId);
                plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Waiting);
                plan.ParentResumeResultJson.Should().BeNull();
                JsonSerializer.Deserialize<AgentTurnInput>(plan.ParentTurnInputJson!, JsonDefaults.Options)
                    .Should().Be(seeded.Input);
                var steps = _fixture.Services.GetRequiredService<RunStreamStore>()
                    .Get(seeded.Parent.Id.ToString())!.GetSnapshotSince(0).Events
                    .Where(evt => evt.Type == EventTypes.WorkflowStep)
                    .Select(evt => JsonSerializer.Serialize(evt.Payload)).ToArray();
                steps.Should().Contain(step => step.Contains("\"compose\"", StringComparison.Ordinal));
                steps.Should().NotContain(step => step.Contains("\"verify\"", StringComparison.Ordinal)
                    || step.Contains("\"fan\"", StringComparison.Ordinal));
                (await verify.PendingRequests.AsNoTracking()
                    .SingleAsync(p => p.RunId == seeded.Parent.Id.ToString()))
                    .RequestId.Should().Be(newRequestId);
                var rows = await verify.Subtasks.AsNoTracking()
                    .Where(s => s.WorkPlanId == plan.Id).ToArrayAsync();
                rows.Should().ContainSingle().Which.Id.Should().Be(subtaskId);
                rows.Should().OnlyContain(s => s.Status == SubtaskStatus.Pending && s.ChildRunId == null);
                (await verify.WorkPlans.CountAsync(p => p.ParentRunId == seeded.Parent.Id.ToString()))
                    .Should().Be(1);
                var staleResult = new WorkflowChildWorkResult(
                    seeded.PlanId, seeded.Child.Id.ToString(), "retained-composed", "compose",
                    null, false, WorkPlanStatus.AssemblyFailed, seeded.Parent.Result, [], string.Empty);
                var oldIdentity = PendingRequestStore.CreateDecisionIdentity(oldRequestId, staleResult);
                (await pending.TryQueueDeliveryAsync(seeded.Parent.Id.ToString(),
                    PendingRequestDeliveryKinds.WorkflowChildWork, oldIdentity, staleResult,
                    seeded.Parent.SubmittingUser)).Should().BeFalse();
                (await _fixture.Services.GetRequiredService<WorkflowChildWorkService>()
                    .TryDeliverResumeAsync(seeded.PlanId, "stale-delivery")).Should().BeFalse();
                (await pending.GetAsync(seeded.Parent.Id.ToString()))!.Request.RequestId
                    .Should().Be(newRequestId);
            }
            finally
            {
                await CleanupComposedFailureAsync(seeded);
            }
        }
    }

    [PostgresFact]
    public async Task ComposedRecovery_UnavailableAcceptedCopilotCapability_RejectsBeforeReopen()
    {
        var seeded = await SeedComposedFailureAsync(ModelSource.GitHubCopilot);
        try
        {
            var action = () => _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>()
                .ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            var boundary = await _fixture.Services.GetRequiredService<RunOrchestrator>()
                .ResolveDurableProviderBoundaryAsync(seeded.Parent, CancellationToken.None);
            boundary.Provider.ToModelSource().Should().Be(ModelSource.GitHubCopilot);
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*unavailable launch capability requirements*");
            var store = _fixture.Services.GetRequiredService<IRunStore>();
            (await store.GetAsync(seeded.Parent.Id))!.Status.Should().Be(RunStatus.Failed);
            (await store.GetAsync(seeded.Parent.Id))!.LifecycleGeneration.Should()
                .Be(seeded.Parent.LifecycleGeneration);
            (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.Failed);
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId);
            plan.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Delivered);
            (await db.PendingRequests.AsNoTracking()
                .SingleAsync(p => p.RunId == seeded.Parent.Id.ToString()))
                .RequestId.Should().Be(plan.ParentResumeRequestId);
        }
        finally
        {
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresFact]
    public async Task ComposedRecovery_CrashAfterAtomicReset_StartupReentersOnlyComposedStage()
    {
        var seeded = await SeedComposedFailureAsync();
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        recovery.LaunchOverride = (_, _, _, _, _) => throw new InvalidOperationException("simulated launch crash");
        try
        {
            var crash = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated launch crash");
            (await store.GetAsync(seeded.Parent.Id))!.Status.Should().Be(RunStatus.InProgress);
            var launches = 0;
            recovery.LaunchOverride = (parent, input, node, lease, _) =>
            {
                launches++;
                parent.Id.Should().Be(seeded.Parent.Id);
                input.Should().Be(seeded.Input);
                node.Should().Be("compose");
                lease.LifecycleGeneration.Should().Be(2);
                return Task.CompletedTask;
            };
            await _fixture.Services.GetRequiredService<WorkflowRestartService>().RecoverAsync(CancellationToken.None);
            launches.Should().Be(1, "startup must honor the committed recovery marker before generic fan guards");
            (await store.GetAsync(seeded.Parent.Id))!.LifecycleGeneration.Should().Be(2);
        }
        finally
        {
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_PostCommitPhysicalChange_RemainsRetryableWithoutReplaying()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
        var source = Path.Combine(seeded.Directory, "demo", "source.md");
        var originalBytes = await File.ReadAllBytesAsync(source);
        var launches = 0;
        try
        {
            recovery.AfterCommitOverride = async _ =>
                await File.AppendAllTextAsync(source, "post-commit change");
            recovery.LaunchOverride = (_, _, _, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            };
            var failed = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await failed.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            launches.Should().Be(0);
            var parent = (await store.GetAsync(seeded.Parent.Id))!;
            parent.Status.Should().Be(RunStatus.InProgress);
            parent.LifecycleGeneration.Should().Be(seeded.Parent.LifecycleGeneration + 1);
            (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.InProgress);
            (await recovery.HasPendingRecoveryAsync(parent, CancellationToken.None)).Should().BeTrue();
            (await leases.GetActiveClaimAsync(parent.Id.ToString())).Should().BeNull();

            recovery.AfterCommitOverride = null;
            var blocked = () => recovery.ResumeFailedAsync(parent, CancellationToken.None);
            await blocked.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            await _fixture.Services.GetRequiredService<WorkflowRestartService>()
                .RecoverAsync(CancellationToken.None);
            launches.Should().Be(0);
            (await recovery.HasPendingRecoveryAsync(parent, CancellationToken.None)).Should().BeTrue();

            await File.WriteAllBytesAsync(source, originalBytes);
            var competing = await leases.TryClaimAsync(
                parent.Id.ToString(), "competing-actor", TimeSpan.FromMinutes(5));
            competing.Claimed.Should().BeTrue();
            var busy = () => recovery.ResumeFailedAsync(parent, CancellationToken.None);
            await busy.Should().ThrowAsync<WorkflowComposedRecoveryException>()
                .WithMessage("composed_recovery_busy");
            (await store.GetAsync(parent.Id))!.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
            await leases.ReleaseAsync(parent.Id.ToString(), "competing-actor", competing.FencingToken);

            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Waiting));
            }
            (await recovery.CanRetryPendingAsync(parent, CancellationToken.None)).Should().BeFalse();
            var alreadyLaunched = () => recovery.ResumeFailedAsync(parent, CancellationToken.None);
            await alreadyLaunched.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Committed));
            }
            await recovery.ResumeFailedAsync(parent, CancellationToken.None);
            launches.Should().Be(1);
            (await store.GetAsync(parent.Id))!.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
            (await store.GetAsync(seeded.Child.Id))!.Id.Should().Be(seeded.Child.Id);
            using var verifyScope = _fixture.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await verifyDb.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId);
            plan.CoordinatorRunId.Should().Be(seeded.Child.Id.ToString());
            plan.ParentRunId.Should().Be(parent.Id.ToString());
            (await verifyDb.Subtasks.CountAsync(s => s.WorkPlanId == seeded.PlanId)).Should().Be(0);
        }
        finally
        {
            recovery.AfterCommitOverride = null;
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_PostCommitBranchChange_StartupRetriesSameOriginalIds()
    {
        foreach (var changeTree in new[] { false, true })
        {
            var seeded = await SeedComposedFailureAsync(agentCommit: true);
            var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
            var launches = 0;
            try
            {
                recovery.AfterCommitOverride = _ =>
                {
                    RunGit(seeded.Directory, "checkout", "-b", "changed-branch");
                    if (changeTree)
                    {
                        File.AppendAllText(Path.Combine(seeded.Directory, "demo", "source.md"), "new tree");
                        CommitFixture(seeded.Directory, "Changed recovery input");
                    }
                    return Task.CompletedTask;
                };
                recovery.LaunchOverride = (_, _, _, _, _) =>
                {
                    launches++;
                    return Task.CompletedTask;
                };
                var failed = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
                await failed.Should().ThrowAsync<WorkflowComposedRecoveryException>();
                recovery.AfterCommitOverride = null;
                var store = _fixture.Services.GetRequiredService<IRunStore>();
                var parent = (await store.GetAsync(seeded.Parent.Id))!;
                (await recovery.CanRetryPendingAsync(parent, CancellationToken.None)).Should().BeTrue();
                await _fixture.Services.GetRequiredService<WorkflowRestartService>()
                    .RecoverAsync(CancellationToken.None);
                launches.Should().Be(0);
                RunGit(seeded.Directory, "checkout", seeded.Parent.WorktreeBranch!);
                await _fixture.Services.GetRequiredService<WorkflowRestartService>()
                    .RecoverAsync(CancellationToken.None);
                launches.Should().Be(1);
                (await store.GetAsync(parent.Id))!.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
                (await store.GetAsync(seeded.Child.Id))!.Id.Should().Be(seeded.Child.Id);
            }
            finally
            {
                recovery.AfterCommitOverride = null;
                recovery.LaunchOverride = null;
                await CleanupComposedFailureAsync(seeded);
            }
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_PostCommitValidInputChange_RefusesLaunchUntilRestored()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
        var launches = 0;
        try
        {
            recovery.AfterCommitOverride = async _ =>
            {
                using var scope = _fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(plan => plan.ParentTurnInputJson,
                        JsonSerializer.Serialize(seeded.Input with { Task = "Different valid task" },
                            JsonDefaults.Options)));
            };
            recovery.LaunchOverride = (_, _, _, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            };
            var changed = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await changed.Should().ThrowAsync<WorkflowComposedRecoveryException>()
                .WithMessage("composed_recovery_input_changed");
            launches.Should().Be(0, "neither stale input nor unapproved changed input may launch");
            var parent = (await store.GetAsync(seeded.Parent.Id))!;
            parent.Status.Should().Be(RunStatus.InProgress);
            parent.LifecycleGeneration.Should().Be(seeded.Parent.LifecycleGeneration + 1);
            (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.InProgress);
            (await recovery.CanRetryPendingAsync(parent, CancellationToken.None)).Should().BeTrue();
            (await recovery.HasPendingRecoveryAsync(parent, CancellationToken.None)).Should().BeTrue();
            (await leases.GetActiveClaimAsync(parent.Id.ToString())).Should().BeNull();
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var plan = await db.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == seeded.PlanId);
                (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == parent.Id.ToString()))
                    .OwnerId.Should().BeNull("the rejected launch must release its recovery claim");
                plan.ParentTurnInputJson.Should().Be(JsonSerializer.Serialize(
                    seeded.Input with { Task = "Different valid task" }, JsonDefaults.Options));
                plan.AssemblyStatusReason.Should().Be(
                    WorkflowComposedRecoveryService.RecoveryMarkerPrefix + parent.LifecycleGeneration);
                plan.CoordinatorRunId.Should().Be(seeded.Child.Id.ToString());
                (await db.Subtasks.CountAsync(s => s.WorkPlanId == seeded.PlanId)).Should().Be(0);
                await db.WorkPlans.Where(p => p.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(p => p.ParentTurnInputJson,
                        JsonSerializer.Serialize(seeded.Input, JsonDefaults.Options)));
            }
            recovery.AfterCommitOverride = null;
            await recovery.ResumeFailedAsync(parent, CancellationToken.None);
            launches.Should().Be(1);
            (await store.GetAsync(parent.Id))!.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
        }
        finally
        {
            recovery.AfterCommitOverride = null;
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_PostCommitProviderChange_RefusesLaunchUntilRestored()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var launches = 0;
        try
        {
            recovery.AfterCommitOverride = async _ =>
            {
                using var scope = _fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        plan => plan.ParentTurnInputJson,
                        JsonSerializer.Serialize(seeded.Input with { ByokProviderFingerprint = "changed" },
                            JsonDefaults.Options)));
            };
            recovery.LaunchOverride = (_, _, _, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            };
            var failed = () => recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await failed.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            recovery.AfterCommitOverride = null;
            var store = _fixture.Services.GetRequiredService<IRunStore>();
            var parent = (await store.GetAsync(seeded.Parent.Id))!;
            var blocked = () => recovery.ResumeFailedAsync(parent, CancellationToken.None);
            await blocked.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            launches.Should().Be(0);
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(update => update.SetProperty(plan => plan.ParentTurnInputJson,
                        JsonSerializer.Serialize(seeded.Input, JsonDefaults.Options)));
            }
            await recovery.ResumeFailedAsync(parent, CancellationToken.None);
            launches.Should().Be(1);
            (await store.GetAsync(parent.Id))!.LifecycleGeneration.Should().Be(parent.LifecycleGeneration);
        }
        finally
        {
            recovery.AfterCommitOverride = null;
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresFact]
    public async Task ComposedRecovery_RejectsChangedOrPreviouslyDispatchedPlan_WithoutReopening()
    {
        foreach (var change in new[] { "cancellation", "dispatched_work", "previous_child", "dirty_inputs" })
            await AssertComposedRecoveryRejectedAsync(change);
    }

    [PostgresRequiredFact]
    public async Task ComposedProducer_PublishesAcceptedAgentCommitBeforePlanCapture()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
        try
        {
            (await store.TryReopenTerminalToInProgressAsync(seeded.Parent.Id)).Should().BeTrue();
            var claimed = await leases.TryClaimAsync(
                seeded.Parent.Id.ToString(), "composed-producer-test", TimeSpan.FromMinutes(5));
            claimed.Claimed.Should().BeTrue();
            var producerLease = new RunLeaseClaim(
                "composed-producer-test", claimed.FencingToken,
                (await store.GetAsync(seeded.Parent.Id))!.LifecycleGeneration);
            var output = new AgentTurnOutput(
                seeded.Parent.Id.ToString(), seeded.CapturedTree, "Committed metadata", 1,
                seeded.Directory, seeded.Parent.WorktreeBranch!,
                seeded.Parent.RepositoryPath, seeded.Parent.OriginatingBranch, false,
                SubmittingUser: seeded.Parent.SubmittingUser,
                ProjectId: seeded.Parent.ProjectId!.Value.ToString(),
                ModelSource: seeded.Input.ModelSource, ModelId: seeded.Parent.ModelId,
                ByokProviderFingerprint: seeded.Input.ByokProviderFingerprint);
            var factory = _fixture.Services.GetRequiredService<RunWorkflowFactory>();
            seeded.Parent.RepositoryPath.Should().NotBe(seeded.Directory);
            seeded.Parent.TreeHash.Should().NotBe(seeded.CapturedTree);
            foreach (var invalid in new[]
            {
                output with { RunId = seeded.Child.Id.ToString() },
                output with { RepositoryPath = seeded.Directory },
                output with { WorktreeBranch = "agentweaver/unrelated" },
                output with { ModelSource = "github-copilot" },
                output with { ModelId = "other" },
                output with { ByokProviderFingerprint = "other" },
                output with { ProjectId = ProjectId.New().ToString() },
            })
            {
                var reject = () => factory.PublishComposedAgentTreeAsync(
                    seeded.Input, invalid, producerLease, CancellationToken.None);
                await reject.Should().ThrowAsync<InvalidOperationException>();
            }
            (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.Parent.TreeHash);
            await leases.ReleaseAsync(
                seeded.Parent.Id.ToString(), producerLease.OwnerId, producerLease.FencingToken);
            var replacement = await leases.TryClaimAsync(
                seeded.Parent.Id.ToString(), "replacement-producer", TimeSpan.FromMinutes(5));
            replacement.Claimed.Should().BeTrue();
            var stale = () => factory.PublishComposedAgentTreeAsync(
                seeded.Input, output, producerLease, CancellationToken.None);
            await stale.Should().ThrowAsync<InvalidOperationException>();
            (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.Parent.TreeHash);
            await leases.ReleaseAsync(seeded.Parent.Id.ToString(), "replacement-producer", replacement.FencingToken);
            var renewed = await leases.TryClaimAsync(
                seeded.Parent.Id.ToString(), "composed-producer-test", TimeSpan.FromMinutes(5));
            renewed.Claimed.Should().BeTrue();
            producerLease = producerLease with { FencingToken = renewed.FencingToken };
            await factory.PublishComposedAgentTreeAsync(seeded.Input, output, producerLease, CancellationToken.None);
            (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.CapturedTree);

            var changed = () => factory.PublishComposedAgentTreeAsync(
                seeded.Input, output with { TreeHash = seeded.Parent.TreeHash! }, producerLease, CancellationToken.None);
            await changed.Should().ThrowAsync<InvalidOperationException>();
            (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.CapturedTree);
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.Runs.Where(run => run.RunId == seeded.Parent.Id.ToString())
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, RunStatus.Failed.ToApiString()));
            var terminal = () => factory.PublishComposedAgentTreeAsync(
                seeded.Input, output, producerLease, CancellationToken.None);
            await terminal.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedPublication_RefusesStaleClaimOrChangedRoot()
    {
        foreach (var change in new[]
        {
            "foreign", "stale_token", "expired", "generation", "tree", "worktree",
            "branch", "project", "child", "current_revision", "reviewed", "terminal",
        })
        {
            var seeded = await SeedComposedFailureAsync(agentCommit: true);
            try
            {
                var store = _fixture.Services.GetRequiredService<IRunStore>();
                var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
                (await store.TryReopenTerminalToInProgressAsync(seeded.Parent.Id)).Should().BeTrue();
                var expected = (await store.GetAsync(seeded.Parent.Id))!;
                var claimed = await leases.TryClaimAsync(
                    seeded.Parent.Id.ToString(), "producer", TimeSpan.FromMinutes(5));
                claimed.Claimed.Should().BeTrue();
                var fence = new RunLeaseClaim(
                    change == "foreign" ? "other" : "producer",
                    claimed.FencingToken + (change == "stale_token" ? 1 : 0),
                    expected.LifecycleGeneration);
                if (change == "expired")
                    await leases.ReleaseAsync(seeded.Parent.Id.ToString(), "producer", claimed.FencingToken);
                using (var scope = _fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                    var row = db.Runs.Where(run => run.RunId == seeded.Parent.Id.ToString());
                    switch (change)
                    {
                        case "generation":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(
                                run => run.LifecycleGeneration, run => run.LifecycleGeneration + 1));
                            break;
                        case "tree":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(run => run.TreeHash, "external"));
                            break;
                        case "worktree":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(run => run.WorktreePath, "other"));
                            break;
                        case "branch":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(run => run.WorktreeBranch, "other"));
                            break;
                        case "project":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(
                                run => run.ProjectId, ProjectId.New().ToString()));
                            break;
                        case "child":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(run => run.ParentRunId, "other"));
                            break;
                        case "reviewed":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(
                                run => run.ApprovedOutputRevisionId, "reviewed"));
                            break;
                        case "current_revision":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(
                                run => run.CurrentOutputRevisionId, "reviewed"));
                            break;
                        case "terminal":
                            await row.ExecuteUpdateAsync(set => set.SetProperty(run => run.Status, "failed"));
                            break;
                    }
                }
                (await store.TryPublishComposedAgentTreeAsync(
                    expected, seeded.CapturedTree, fence)).Should().BeFalse(change);
                (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().NotBe(seeded.CapturedTree);
            }
            finally
            {
                await CleanupComposedFailureAsync(seeded);
            }
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_CapturedAgentCommit_ReopensOriginalPlanAndPendingChild()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var launches = 0;
        recovery.LaunchOverride = (parent, input, node, _, _) =>
        {
            launches++;
            parent.Id.Should().Be(seeded.Parent.Id);
            parent.TreeHash.Should().Be(seeded.CapturedTree);
            input.Should().Be(seeded.Input);
            node.Should().Be("compose");
            return Task.CompletedTask;
        };
        try
        {
            int subtaskId;
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var subtask = new Subtask
                {
                    WorkPlanId = seeded.PlanId, Title = "Retained work", Scope = "Use committed input",
                    AssignedAgent = "Neo", SelectedModelId = "test-model", Phase = "execution",
                    IsolationStrategy = "worktree", Status = SubtaskStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Subtasks.Add(subtask);
                await db.SaveChangesAsync();
                subtaskId = subtask.Id;
            }
            (await _fixture.Services.GetRequiredService<IRunStore>()
                .GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.Parent.TreeHash);
            await recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            launches.Should().Be(1);
            var store = _fixture.Services.GetRequiredService<IRunStore>();
            (await store.GetAsync(seeded.Parent.Id))!.TreeHash.Should().Be(seeded.CapturedTree);
            (await store.GetAsync(seeded.Child.Id))!.Status.Should().Be(RunStatus.InProgress);
            using var verifyScope = _fixture.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await verify.WorkPlans.SingleAsync(p => p.Id == seeded.PlanId)).CoordinatorRunId
                .Should().Be(seeded.Child.Id.ToString());
            (await verify.Subtasks.SingleAsync(s => s.WorkPlanId == seeded.PlanId)).Id
                .Should().Be(subtaskId);
        }
        finally
        {
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_CompetingClaims_OnlyOneReopensOriginalIds()
    {
        var seeded = await SeedComposedFailureAsync(agentCommit: true);
        var recovery = _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>();
        var launches = 0;
        recovery.LaunchOverride = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            return Task.CompletedTask;
        };
        try
        {
            async Task<bool> TryResumeAsync()
            {
                try
                {
                    await recovery.ResumeFailedAsync(seeded.Parent, CancellationToken.None);
                    return true;
                }
                catch (WorkflowComposedRecoveryException)
                {
                    return false;
                }
            }
            var results = await Task.WhenAll(TryResumeAsync(), TryResumeAsync());
            results.Should().ContainSingle(success => success);
            launches.Should().Be(1);
            (await _fixture.Services.GetRequiredService<IRunStore>()
                .GetAsync(seeded.Parent.Id))!.LifecycleGeneration.Should().Be(2);
        }
        finally
        {
            recovery.LaunchOverride = null;
            await CleanupComposedFailureAsync(seeded);
        }
    }

    [PostgresRequiredFact]
    public async Task ComposedRecovery_CapturedAgentCommit_RejectsChangedIdentityOrTree()
    {
        foreach (var change in new[] { "tree_c", "dirty", "untracked", "branch",
                     "root", "repository", "origin", "model", "provider", "pending_delivery",
                     "generation", "child_generation", "captured_base", "subtask_dispatched",
                     "approved_revision" })
        {
            var seeded = await SeedComposedFailureAsync(agentCommit: true);
            try
            {
                using var scope = _fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                if (change is "tree_c" or "dirty" or "untracked")
                {
                    var path = Path.Combine(seeded.Directory, "demo",
                        change == "untracked" ? "unexpected.md" : "source.md");
                    await File.AppendAllTextAsync(path, "external change");
                    if (change == "tree_c")
                        CommitFixture(seeded.Directory, "External commit");
                }
                else if (change == "branch")
                    RunGit(seeded.Directory, "switch", "-c", "unexpected");
                else if (change == "pending_delivery")
                    await db.PendingRequests.Where(p => p.RunId == seeded.Parent.Id.ToString())
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Delivering));
                else if (change == "generation")
                    await db.Runs.Where(r => r.RunId == seeded.Parent.Id.ToString())
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1));
                else if (change == "child_generation")
                    await db.Runs.Where(r => r.RunId == seeded.Child.Id.ToString())
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1));
                else if (change == "approved_revision")
                    await db.Runs.Where(r => r.RunId == seeded.Parent.Id.ToString())
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(r => r.ApprovedOutputRevisionId, "approved-output"));
                else if (change == "captured_base")
                    await db.WorkPlans.Where(p => p.Id == seeded.PlanId)
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(p => p.ExecutionBaseTreeHash, seeded.Parent.TreeHash));
                else if (change == "subtask_dispatched")
                {
                    db.Subtasks.Add(new Subtask
                    {
                        WorkPlanId = seeded.PlanId, Title = "Prior work", Scope = "Prior work",
                        AssignedAgent = "Neo", SelectedModelId = "test-model", Phase = "execution",
                        IsolationStrategy = "worktree", Status = SubtaskStatus.Running,
                        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync();
                }
                else
                {
                    var plan = await db.WorkPlans.SingleAsync(p => p.Id == seeded.PlanId);
                    var input = seeded.Input with
                    {
                        RunId = change == "root" ? RunId.New().ToString() : seeded.Input.RunId,
                        RepositoryPath = change == "repository" ? seeded.Directory : seeded.Input.RepositoryPath,
                        OriginatingBranch = change == "origin" ? "other" : seeded.Input.OriginatingBranch,
                        ModelId = change == "model" ? "other" : seeded.Input.ModelId,
                        ByokProviderFingerprint = change == "provider" ? "other" : seeded.Input.ByokProviderFingerprint,
                    };
                    plan.ParentTurnInputJson = JsonSerializer.Serialize(input, JsonDefaults.Options);
                    await db.SaveChangesAsync();
                }
                var action = () => _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>()
                    .ResumeFailedAsync(seeded.Parent, CancellationToken.None);
                await action.Should().ThrowAsync<WorkflowComposedRecoveryException>(change);
                (await _fixture.Services.GetRequiredService<IRunStore>().GetAsync(seeded.Parent.Id))!
                    .Status.Should().Be(RunStatus.Failed);
            }
            finally
            {
                await CleanupComposedFailureAsync(seeded);
            }
        }
    }

    private async Task AssertComposedRecoveryRejectedAsync(string change)
    {
        var seeded = await SeedComposedFailureAsync();
        try
        {
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (change == "cancellation")
                await db.WorkPlans.Where(plan => plan.Id == seeded.PlanId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(plan => plan.CoordinatorCancellationRequestedAt, DateTimeOffset.UtcNow));
            else if (change is "dispatched_work" or "previous_child")
            {
                db.Subtasks.Add(new Subtask
                {
                    WorkPlanId = seeded.PlanId, Title = "Existing child", Scope = "Preserve it",
                    AssignedAgent = "Alpha", SelectedModelId = "test-model", Phase = "implementation",
                    IsolationStrategy = "worktree",
                    Status = change == "dispatched_work" ? SubtaskStatus.Running : SubtaskStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
                if (change == "previous_child")
                {
                    var subtask = await db.Subtasks.SingleAsync(s => s.WorkPlanId == seeded.PlanId);
                    await _fixture.Services.GetRequiredService<IRunStore>().InsertAsync(seeded.Child with
                    {
                        Id = RunId.New(), ParentRunId = seeded.Child.Id.ToString(),
                        SubtaskId = subtask.Id.ToString(), Status = RunStatus.Failed,
                    });
                }
            }
            else
                await File.AppendAllTextAsync(Path.Combine(seeded.Directory, "demo", "source.md"), "changed");
            var action = () => _fixture.Services.GetRequiredService<WorkflowComposedRecoveryService>()
                .ResumeFailedAsync(seeded.Parent, CancellationToken.None);
            await action.Should().ThrowAsync<WorkflowComposedRecoveryException>();
            var current = await _fixture.Services.GetRequiredService<IRunStore>().GetAsync(seeded.Parent.Id);
            current!.Status.Should().Be(RunStatus.Failed);
            current.LifecycleGeneration.Should().Be(seeded.Parent.LifecycleGeneration);
        }
        finally
        {
            await CleanupComposedFailureAsync(seeded);
        }
    }

    private async Task<ComposedFailureFixture> SeedComposedFailureAsync(
        ModelSource source = ModelSource.Byok, bool copilotCapability = false, bool agentCommit = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aw-composed-recovery-{Guid.NewGuid():N}");
        var baseRepository = Path.Combine(root, "base");
        var directory = Path.Combine(root, "parent");
        Directory.CreateDirectory(Path.Combine(baseRepository, "demo"));
        SquadTestFixtureHelper.CreateMinimalSquad(baseRepository, "Composed recovery");
        await File.WriteAllTextAsync(Path.Combine(baseRepository, "demo", "source.md"), "Original retained source.\n");
        Repository.Init(baseRepository);
        string tree;
        using (var repository = new Repository(baseRepository))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@example.test", DateTimeOffset.UtcNow);
            tree = repository.Commit("Original inputs", signature, signature).Tree.Sha;
        }
        RunGit(baseRepository, "branch", "-M", "main");
        var definition = new WorkflowDefinition
        {
            Id = "retained-composed", Name = "Retained composed", Start = "verify",
            Nodes =
            [
                new WorkflowNode { Id = "verify", Label = "Verify", Type = WorkflowNodeType.Prompt },
                new WorkflowNode
                {
                    Id = "compose", Label = "Compose", Type = WorkflowNodeType.CoordinatorComposed,
                    Prompt = "Write summary from actual retained source.",
                },
                new WorkflowNode { Id = "done", Label = "Done", Type = WorkflowNodeType.Terminal },
            ],
            Edges =
            [
                new WorkflowEdge { From = "verify", To = "compose" },
                new WorkflowEdge { From = "compose", To = "done" },
            ],
        };
        var now = DateTimeOffset.UtcNow;
        var projectId = ProjectId.New();
        const string reason = "composed_decomposition_failed:database serialization conflict";
        var parent = new Run
        {
            Id = RunId.New(), ProjectId = projectId, RepositoryPath = baseRepository, WorktreePath = directory,
            OriginatingBranch = "main", WorktreeBranch = "", TreeHash = tree,
            ModelSource = source, ModelId = "test-model", SubmittingUser = "test-user",
            Task = "Retained workflow", Status = RunStatus.Failed, StartedAt = now, EndedAt = now, Result = reason,
        };
        parent = parent with { WorktreeBranch = $"agentweaver/{parent.Id}" };
        RunGit(baseRepository, "worktree", "add", "-b", parent.WorktreeBranch, directory, "main");
        var capturedTree = tree;
        if (agentCommit)
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "demo", "fan-result.md"), "Retained fan projection.\n");
            parent = parent with { TreeHash = CommitFixture(directory, "Fan input projection") };
            await File.AppendAllTextAsync(Path.Combine(directory, ".squad", "decisions.md"), "\nAgent turn committed.\n");
            capturedTree = CommitFixture(directory, "Agent turn committed");
        }
        var child = parent with
        {
            Id = RunId.New(), ParentRunId = parent.Id.ToString(), SubtaskId = "workflow-node:compose",
            AgentName = "Coordinator", WorktreePath = null, WorktreeBranch = null, TreeHash = null,
        };
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        await store.InsertAsync(parent);
        await store.UpdateExecutableWorkflowPinAsync(
            parent.Id, ExecutableWorkflowSnapshots.Create(definition, "recovery-test"));
        parent = (await store.GetAsync(parent.Id))!;
        await store.InsertAsync(child);
        var config = source == ModelSource.Byok
            ? new ByokProviderConfiguration(
                "test-recovery-provider", "Recovery provider", "azure", "https://example.test", "test-model", "test-only-key")
            : null;
        var provider = source == ModelSource.Byok
            ? (EffectiveModelProviderResult)new EffectiveModelProviderResult.Byok(
                config!.Id, config.Type, config.ExecutionFingerprint())
            : copilotCapability
                ? new EffectiveModelProviderResult.ProjectGitHubCopilot(
                    "test-copilot-binding-" + parent.Id, "test-user", "v1")
                : new EffectiveModelProviderResult.PlatformGitHubCopilot("test-copilot-binding", null, "v1");
        await _fixture.Services.GetRequiredService<RunModelProviderSnapshotStore>().CaptureAsync(
            parent, provider, config, CancellationToken.None);
        var input = new AgentTurnInput(
            parent.Id.ToString(), "Original goal with verified branch findings", directory, parent.WorktreeBranch!,
            baseRepository, "main", source.ToApiString(), "test-model", "test-user", ProjectId: projectId.ToString(),
            ByokProviderFingerprint: config?.ExecutionFingerprint());
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = projectId.ToString(), Name = "Recovery test", OriginKind = "blank",
            WorkingDirectory = baseRepository, Owner = "test-user", DefaultProvider = source.ToApiString(),
            CreatedAt = now, UpdatedAt = now,
        });
        if (copilotCapability)
        {
            var reference = "copilot-app-project-" + parent.Id;
            db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
            {
                Id = "test-copilot-binding-" + parent.Id,
                ProjectId = projectId.ToString(),
                EntraObjectId = "test-user",
                CredentialReference = reference,
                CredentialVersion = "v1",
                GrantDigest = "test-copilot-grant",
                Status = GitHubBindingStatus.Active,
                BoundAt = now,
            });
            await _fixture.Services.GetRequiredService<ISecretStore>().SetSecretAsync(reference,
                JsonSerializer.Serialize(new
                {
                    status = "signed-in",
                    accessToken = "hermetic-test-token",
                    expiresAt = now.AddHours(1),
                    githubLogin = "test-user",
                }));
        }
        var spec = new OutcomeSpec
        {
            ProjectId = projectId.ToString(), CoordinatorRunId = child.Id.ToString(), Goal = "Write summary",
            DesiredOutcome = "Write summary", Scope = "Retained inputs", Assumptions = string.Empty,
            Status = "confirmed", AllowTaskPromotion = false, CreatedAt = now, UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id, ProjectId = projectId.ToString(), CoordinatorRunId = child.Id.ToString(),
            ParentRunId = parent.Id.ToString(), ParentWorkflowId = definition.Id, ParentWorkflowNodeId = "compose",
            ParentResumeState = WorkflowChildWorkResumeStates.Delivered, ExecutionBaseTreeHash = capturedTree,
            ParentTurnInputJson = JsonSerializer.Serialize(input, JsonDefaults.Options),
            Status = WorkPlanStatus.AssemblyFailed, AssemblyStatusReason = reason, CreatedAt = now, UpdatedAt = now,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        plan.ParentResumeRequestId = Guid.NewGuid().ToString("N");
        plan.ParentResumeResultJson = JsonSerializer.Serialize(new WorkflowChildWorkResult(
            plan.Id, child.Id.ToString(), definition.Id, "compose", null, false,
            WorkPlanStatus.AssemblyFailed, reason, [], string.Empty), JsonDefaults.Options);
        db.PendingRequests.Add(new PendingRequestRecord
        {
            RunId = parent.Id.ToString(), RequestId = plan.ParentResumeRequestId, RequestJson = "{}",
            OwnerUser = parent.SubmittingUser, DeliveryKind = PendingRequestDeliveryKinds.WorkflowChildWork,
            DeliveryState = PendingRequestDeliveryStates.Delivered, ResponseJson = plan.ParentResumeResultJson,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new ComposedFailureFixture(parent, child, plan.Id, input, directory, root, capturedTree);
    }

    private async Task CleanupComposedFailureAsync(ComposedFailureFixture fixture)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans.Where(plan => plan.Id == fixture.PlanId).ExecuteUpdateAsync(updates => updates
            .SetProperty(plan => plan.Status, WorkPlanStatus.Cancelled)
            .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Suppressed)
            .SetProperty(plan => plan.AssemblyStatusReason, (string?)null));
        if (fixture.Parent.ModelSource == ModelSource.GitHubCopilot)
        {
            await db.ProjectCopilotBindings.Where(binding =>
                binding.ProjectId == fixture.Parent.ProjectId!.Value.ToString()).ExecuteDeleteAsync();
            await _fixture.Services.GetRequiredService<ISecretStore>()
                .DeleteSecretAsync("copilot-app-project-" + fixture.Parent.Id);
        }
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
        foreach (var id in new[] { fixture.Parent.Id, fixture.Child.Id })
        {
            if (await leases.GetActiveClaimAsync(id.ToString()) is { } claim)
                await leases.ReleaseAsync(id.ToString(), claim.OwnerId, claim.FencingToken);
            await store.TerminalizeForTestAsync(id, RunStatus.Failed, "test_cleanup");
        }
        foreach (var file in Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture.Root, recursive: true);
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git worktree fixture failed: {error}");
    }

    private static string CommitFixture(string directory, string message)
    {
        using var repository = new Repository(directory);
        Commands.Stage(repository, "*");
        var signature = new Signature("Test", "test@example.test", DateTimeOffset.UtcNow);
        return repository.Commit(message, signature, signature).Tree.Sha;
    }

    private sealed record ComposedFailureFixture(
        Run Parent, Run Child, int PlanId, AgentTurnInput Input, string Directory, string Root, string CapturedTree);
}
