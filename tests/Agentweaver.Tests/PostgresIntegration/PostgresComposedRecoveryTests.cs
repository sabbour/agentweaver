using System.Data;
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

    [PostgresFact]
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
            string originalRequestId;
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var plan = await db.WorkPlans.SingleAsync(p => p.Id == seeded.PlanId);
                originalRequestId = plan.ParentResumeRequestId!;
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
            var gate = await verify.PendingRequests.AsNoTracking()
                .SingleAsync(p => p.RunId == seeded.Parent.Id.ToString());
            gate.RequestId.Should().Be(originalRequestId);
            gate.DeliveryState.Should().Be(PendingRequestDeliveryStates.Waiting);
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

    [PostgresFact]
    public async Task ComposedRecovery_RejectsChangedOrPreviouslyDispatchedPlan_WithoutReopening()
    {
        foreach (var change in new[] { "cancellation", "dispatched_work", "previous_child", "dirty_inputs" })
            await AssertComposedRecoveryRejectedAsync(change);
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

    private async Task<ComposedFailureFixture> SeedComposedFailureAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aw-composed-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "demo"));
        SquadTestFixtureHelper.CreateMinimalSquad(directory, "Composed recovery");
        await File.WriteAllTextAsync(Path.Combine(directory, "demo", "source.md"), "Original retained source.\n");
        Repository.Init(directory);
        string tree;
        using (var repository = new Repository(directory))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@example.test", DateTimeOffset.UtcNow);
            tree = repository.Commit("Original inputs", signature, signature).Tree.Sha;
        }
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
            Id = RunId.New(), ProjectId = projectId, RepositoryPath = directory, WorktreePath = directory,
            OriginatingBranch = "main", WorktreeBranch = "retained-parent", TreeHash = tree,
            ModelSource = ModelSource.Byok, ModelId = "test-model", SubmittingUser = "test-user",
            Task = "Retained workflow", Status = RunStatus.Failed, StartedAt = now, EndedAt = now, Result = reason,
        };
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
        var config = new ByokProviderConfiguration(
            "test-recovery-provider", "Recovery provider", "azure", "https://example.test", "test-model", "test-only-key");
        await _fixture.Services.GetRequiredService<RunModelProviderSnapshotStore>().CaptureAsync(
            parent, new EffectiveModelProviderResult.Byok(config.Id, config.Type, config.ExecutionFingerprint()),
            config, CancellationToken.None);
        var input = new AgentTurnInput(
            parent.Id.ToString(), "Original goal with verified branch findings", directory, "retained-parent",
            directory, "main", "byok", "test-model", "test-user", ProjectId: projectId.ToString(),
            ByokProviderFingerprint: config.ExecutionFingerprint());
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = projectId.ToString(), Name = "Recovery test", OriginKind = "blank",
            WorkingDirectory = directory, Owner = "test-user", DefaultProvider = "byok",
            CreatedAt = now, UpdatedAt = now,
        });
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
            ParentResumeState = WorkflowChildWorkResumeStates.Delivered, ExecutionBaseTreeHash = tree,
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
        return new ComposedFailureFixture(parent, child, plan.Id, input, directory);
    }

    private async Task CleanupComposedFailureAsync(ComposedFailureFixture fixture)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans.Where(plan => plan.Id == fixture.PlanId).ExecuteUpdateAsync(updates => updates
            .SetProperty(plan => plan.Status, WorkPlanStatus.Cancelled)
            .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Suppressed)
            .SetProperty(plan => plan.AssemblyStatusReason, (string?)null));
        var store = _fixture.Services.GetRequiredService<IRunStore>();
        var leases = _fixture.Services.GetRequiredService<IRunLeaseStore>();
        foreach (var id in new[] { fixture.Parent.Id, fixture.Child.Id })
        {
            if (await leases.GetActiveClaimAsync(id.ToString()) is { } claim)
                await leases.ReleaseAsync(id.ToString(), claim.OwnerId, claim.FencingToken);
            await store.TerminalizeForTestAsync(id, RunStatus.Failed, "test_cleanup");
        }
        foreach (var file in Directory.EnumerateFiles(fixture.Directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture.Directory, recursive: true);
    }

    private sealed record ComposedFailureFixture(Run Parent, Run Child, int PlanId, AgentTurnInput Input, string Directory);
}
