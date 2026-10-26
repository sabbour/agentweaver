using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    private RuntimeRegistration _rubberduckTestOwner = null!;

    [Fact]
    public async Task PostgresRubberduckRetriesTheSameRecordedOwnerOperationAfterRestart()
    {
        var (plan, intent, _) = await PrepareRubberduckOwnerAsync();
        var responses = new Dictionary<Guid, string>();
        var reviewedOperations = new HashSet<Guid>();
        var ownerCalls = new List<Guid>();
        Task<string> RunOwner(
            MafExecutionRubberduckIntent request, string prompt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains(request.OperationId.ToString("D"), prompt);
            ownerCalls.Add(request.OperationId);
            if (responses.TryGetValue(request.OperationId, out var replay))
                return Task.FromResult(replay);

            reviewedOperations.Add(request.OperationId);
            responses.Add(request.OperationId, "PASS");
            throw new CoordinationException(
                "maf_execution_rubberduck_owner_unavailable", 503);
        }

        var first = CreateRubberduckExecutor(plan, RunOwner);
        Assert.Null(await first.ExecuteAsync(intent, CancellationToken.None));
        var uncertain = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(uncertain);
        Assert.Equal(MafExecutionTaskStatus.Running,
            uncertain.State.Progress.NonModelSteps[intent.StepId]);
        Assert.Equal("maf_execution_rubberduck_owner_unavailable",
            uncertain.State.RubberduckFailureCodes[intent.StepId]);

        // A new executor and checkpoint-store wrapper simulate service restart; the operation key remains durable.
        var restartedStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding);
        var restarted = CreateRubberduckExecutor(plan, RunOwner, restartedStore);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            await restarted.ExecuteAsync(intent, CancellationToken.None));

        var completed = await restartedStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            completed.State.Progress.NonModelSteps[intent.StepId]);
        Assert.Equal(intent.OperationId, completed.State.RubberduckReceipts[intent.StepId].OperationId);
        Assert.Empty(completed.State.RubberduckFailureCodes);
        Assert.Equal([intent.OperationId, intent.OperationId], ownerCalls);
        Assert.Single(reviewedOperations);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            await restarted.ExecuteAsync(intent, CancellationToken.None));
        Assert.Single(reviewedOperations);

        var ready = MafExecutionPlanner.BuildFrontier(
            plan, completed.State.Progress, 0, 0, 10, 10, _ => true);
        Assert.Contains(ready.ReadyActions, action => action.Step.Id == "merge");
    }

    [Fact]
    public async Task PostgresRubberduckRequestChangesStoresFailureAndBlocksMerge()
    {
        var (plan, intent, _) = await PrepareRubberduckOwnerAsync();
        var executor = CreateRubberduckExecutor(
            plan, (_, _, _) => Task.FromResult("REQUEST_CHANGES\nThe result needs a missing test."));

        Assert.Equal(MafExecutionTaskStatus.Failed,
            await executor.ExecuteAsync(intent, CancellationToken.None));

        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(MafExecutionTaskStatus.Failed,
            latest.State.Progress.NonModelSteps[intent.StepId]);
        Assert.Equal("requestChanges", latest.State.RubberduckReceipts[intent.StepId].Outcome);
        Assert.Empty(latest.State.RubberduckFailureCodes);
        var frontier = MafExecutionPlanner.BuildFrontier(
            plan, latest.State.Progress, 0, 0, 10, 10, _ => true);
        Assert.Contains("merge", frontier.FailedDependencyIds);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.Step.Id == "merge");
        var recovery = MafExecutionRecoveryProjector.Project(latest, plan, 1, true, null);
        var reviewOperation = Assert.Single(
            recovery.PlatformGateOperations,
            operation => operation.Gate == WorkflowPlatformGate.IndependentReview);
        Assert.Equal(CoordinatorGateChoices.Approve, reviewOperation.ReviewDecision);
        Assert.Equal("terminalReceiptRecorded", reviewOperation.EvidenceState);
        var operation = Assert.Single(
            recovery.PlatformGateOperations,
            operation => operation.Gate == WorkflowPlatformGate.RubberDuck);
        Assert.Equal(intent.OperationId, operation.OperationId);
        Assert.Equal("requestChanges", operation.ReviewDecision);
        Assert.Equal("terminalReceiptRecorded", operation.EvidenceState);
    }

    [Fact]
    public async Task PostgresRubberduckRejectsChangedRecordedEvidenceBeforeOwnerCall()
    {
        var (plan, intent, _) = await PrepareRubberduckOwnerAsync();
        var ownerCalls = 0;
        var stale = CreateRubberduckExecutor(
            plan,
            (_, _, _) =>
            {
                ownerCalls++;
                return Task.FromResult("PASS");
            },
            evidenceChanged: true);

        var failure = await Assert.ThrowsAsync<CoordinationException>(
            () => stale.ExecuteAsync(intent, CancellationToken.None));

        Assert.Equal("maf_execution_rubberduck_evidence_changed", failure.Code);
        Assert.Equal(0, ownerCalls);
        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Empty(latest.State.RubberduckReceipts);
        Assert.Empty(latest.State.RubberduckFailureCodes);
        Assert.Equal(MafExecutionTaskStatus.Running,
            latest.State.Progress.NonModelSteps[intent.StepId]);
    }

    private async Task<(WorkPlanSnapshot Plan, MafExecutionRubberduckIntent Intent,
        MafExecutionCheckpointSnapshot Initial)> PrepareRubberduckOwnerAsync()
    {
        const string checkpointId = "rubberduck-start";
        const long decisionVersion = 1;
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform(
                "independent-review", 1, ["implement"], WorkflowPlatformGate.IndependentReview),
            WorkflowTestData.Platform(
                "rubberduck", 2, ["independent-review"], WorkflowPlatformGate.RubberDuck),
            WorkflowTestData.Platform("merge", 3, ["rubberduck"], WorkflowPlatformGate.Merge));
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(workflow),
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext()).Value!;
        var identity = _binding.Identity;
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("first", MafExecutionTaskStatus.Succeeded)
        };
        var initialState = new MafExecutionCheckpoint(
            1, plan.Plan.Id, decisionVersion, progress)
        {
            Results = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("first", "The implementation result is recorded.")
        };
        var reviewStep = workflow.Steps.Single(
            step => step.PlatformGate == WorkflowPlatformGate.IndependentReview);
        var reviewIntent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), identity, checkpointId, 1, plan, reviewStep,
            initialState, decisionVersion, _actor.Subject,
            _binding.ExecutionFence, selectionHash);
        var reviewReceipt = new MafReviewTerminalReceipt(
            reviewIntent.OperationId,
            reviewIntent.ApprovalRequestId,
            reviewIntent.EvidenceHash,
            _actor.Subject,
            _binding.ExecutionFence,
            CoordinatorGateChoices.Approve,
            "approved");
        var acceptedState = initialState with
        {
            Progress = progress with
            {
                NonModelSteps = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(reviewStep.Id, MafExecutionTaskStatus.Succeeded)
            },
            ReviewIntents = ImmutableDictionary<string, MafExecutionReviewIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewIntent),
            ReviewReceipts = ImmutableDictionary<string, MafReviewTerminalReceipt>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewReceipt)
        };
        var initial = await _executionStore.AppendAsync(
            checkpointId, null, acceptedState, CancellationToken.None);

        var ownerBinding = new RuntimeBinding(
            _actor.Issuer,
            _actor.Subject,
            "tenant-1",
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            RuntimeActionContract.CoordinatorRootStepId,
            "root-turn",
            1,
            1,
            1,
            _selection.Selection.ContextRevision,
            selectionHash.ToLowerInvariant(),
            _binding.ExecutionFence,
            "environment-1",
            "placement-1",
            1,
            "profile-1",
            new Uri("https://runtime.example/runtime/v1/configure"),
            new Uri("https://runtime.example/runtime/v1/observe"));
        _rubberduckTestOwner = new RuntimeRegistration(
            Guid.NewGuid(), 1, ownerBinding, RuntimeRegistrationState.Active,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var step = workflow.Steps.Single(step => step.PlatformGate == WorkflowPlatformGate.RubberDuck);
        var intent = MafRubberduckReviewContract.CreateIntent(
            Guid.NewGuid(), identity, "rubberduck-intent", 2, plan, step,
            initial.State, decisionVersion, _actor.Subject,
            _binding.ExecutionFence, selectionHash, _rubberduckTestOwner);
        var running = CoordinationEndpoints.PrepareRubberduckCheckpoint(initial, intent)
            ?? throw new InvalidOperationException("The RubberDuck intent was not prepared.");
        _ = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, running, CancellationToken.None);
        return (plan, intent, initial);
    }

    private MafRubberduckReviewExecutor CreateRubberduckExecutor(
        WorkPlanSnapshot plan,
        Func<MafExecutionRubberduckIntent, string, CancellationToken, Task<string>> runOwner,
        MafExecutionCheckpointStore? execution = null,
        bool evidenceChanged = false)
    {
        execution ??= _executionStore;
        var ownerRegistration = _rubberduckTestOwner;
        async Task RequireCurrent(MafExecutionRubberduckIntent intent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (intent.Identity != _binding.Identity ||
                intent.AuthorizedActorId != _actor.Subject ||
                intent.ExecutionFence != _binding.ExecutionFence ||
                intent.OwnerRuntimeInstanceId != ownerRegistration.RuntimeInstanceId ||
                intent.OwnerRegistrationRevision != ownerRegistration.Revision)
                throw new CoordinationException("maf_execution_rubberduck_binding_changed", 409);
            await Task.CompletedTask;
        }
        async Task RequireDecision(
            NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(connection, transaction.Connection);
            await Task.CompletedTask;
        }
        Task RequireEvidence(
            MafExecutionRubberduckIntent intent, MafExecutionCheckpoint state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = MafRubberduckReviewContract.CreateIntent(
                intent.OperationId,
                intent.Identity,
                intent.CheckpointId,
                intent.CheckpointRevision,
                plan,
                plan.Workflow.Definition.Steps.Single(
                    step => step.Id == intent.StepId &&
                            step.PlatformGate == WorkflowPlatformGate.RubberDuck),
                evidenceChanged
                    ? state with { Results = state.Results.SetItem("first", "Changed after intent.") }
                    : state,
                intent.DecisionStateVersion,
                intent.AuthorizedActorId,
                intent.ExecutionFence,
                intent.AcceptedSelectionHash,
                ownerRegistration);
            if (current != intent)
                throw new CoordinationException("maf_execution_rubberduck_evidence_changed", 409);
            return Task.CompletedTask;
        }
        return new MafRubberduckReviewExecutor(
            execution,
            _fixture.DataSource,
            runOwner,
            RequireCurrent,
            RequireDecision,
            RequireEvidence,
            _ => { });
    }
}
