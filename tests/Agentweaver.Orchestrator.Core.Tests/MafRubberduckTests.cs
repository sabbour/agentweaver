using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafRubberduckTests
{
    [Fact]
    public void RubberduckBindsOnlyWhenItsConcreteOwnerIsAvailable()
    {
        var (plan, _, _, _) = CreateFixture();
        Assert.Equal("maf_execution_executor_unavailable",
            Assert.Throws<CoordinationException>(() =>
                MafExecutionWorkflowBindings.RequireBound(
                    plan, false, hasMergeOwner: true, hasIndependentReviewOwner: true)).Code);

        var bound = MafExecutionWorkflowBindings.RequireBound(
            plan, false, hasMergeOwner: true,
            hasIndependentReviewOwner: true, hasRubberduckOwner: true);

        var action = new MafExecutionAction(plan.Workflow.Definition.Steps.Single(
            step => step.PlatformGate == WorkflowPlatformGate.RubberDuck), null);
        MafExecutionWorkflowBindings.RequireBoundAction(bound, action);
        Assert.Equal(WorkflowPlatformGate.RubberDuck,
            Assert.IsType<BoundWorkflowStep.Platform>(bound.Steps.Single(
                step => step.Definition.Id == action.Step.Id)).Gate);
    }

    [Theory]
    [InlineData("PASS", "passed", "Succeeded")]
    [InlineData("REQUEST_CHANGES\nAdd a test for the edge case.", "requestChanges", "Failed")]
    [InlineData("pass", "failed", "Failed")]
    [InlineData("PASS\nREQUEST_CHANGES", "failed", "Failed")]
    [InlineData("REQUEST_CHANGES", "failed", "Failed")]
    public void RubberduckAcceptsOnlyOneRecordedVerdict(
        string output, string outcome, string status)
    {
        var (plan, state, owner, identity) = CreateFixture();
        var intent = CreateIntent(plan, state, owner, identity);

        var receipt = MafRubberduckReviewContract.ReadOwnerResult(intent, output);

        Assert.Equal(outcome, receipt.Outcome);
        Assert.Equal(status, MafRubberduckReviewContract.ValidateReceipt(intent, receipt).ToString());
        if (outcome == "failed")
            Assert.NotNull(receipt.FailureCode);
    }

    [Fact]
    public void RubberduckIntentBindsRecordedAncestorsOwnerAndAuthority()
    {
        var (plan, state, owner, identity) = CreateFixture();
        var intent = CreateIntent(plan, state, owner, identity);
        var alteredState = state with
        {
            Results = state.Results.SetItem("first", "A different recorded result.")
        };
        var changedEvidence = CreateIntent(
            plan, alteredState, owner, identity, intent.OperationId,
            intent.CheckpointId, intent.CheckpointRevision);

        Assert.NotEqual(intent.EvidenceHash, changedEvidence.EvidenceHash);
        Assert.Contains("first", intent.EvidenceSummary);
        Assert.Contains("independent-review", intent.EvidenceSummary);
        Assert.Equal("You are RubberDuck", MafRubberduckReviewContract.CreatePrompt(intent)[..18]);

        var staleOwner = owner with
        {
            Binding = owner.Binding with { AcceptedSelectionHash = new string('b', 64) }
        };
        Assert.Equal("maf_execution_rubberduck_owner_unavailable",
            Assert.Throws<CoordinationException>(() =>
                CreateIntent(plan, state, staleOwner, identity)).Code);
        Assert.Throws<ArgumentException>(() => MafRubberduckReviewContract.ValidateReceipt(
            intent,
            MafRubberduckReviewContract.ReadOwnerResult(intent, "PASS") with
            {
                ActorId = "another-actor"
            }));
    }

    [Fact]
    public void RubberduckCheckpointAndRecoveryKeepFailureVisibleAndBlockMerge()
    {
        var (plan, state, owner, identity) = CreateFixture();
        var intent = CreateIntent(plan, state, owner, identity);
        var runningState = CoordinationEndpoints.PrepareRubberduckCheckpoint(
            new(new("root", "prior-checkpoint"), state), intent)!;
        var running = new MafExecutionCheckpointSnapshot(
            new("root", "rubberduck-intent"), runningState);
        var failedAttempt = CoordinationEndpoints.PrepareRubberduckFailureCheckpoint(
            running, intent, "maf_execution_rubberduck_owner_unavailable")!;

        var failureRecovery = MafExecutionRecoveryProjector.Project(
            new(new("root", "rubberduck-retry"), failedAttempt), plan, 3, true, null);
        var failureOperation = Assert.Single(
            failureRecovery.PlatformGateOperations,
            operation => operation.Gate == WorkflowPlatformGate.RubberDuck);
        Assert.Equal("intentOnly", failureOperation.EvidenceState);
        Assert.Equal("maf_execution_rubberduck_owner_unavailable", failureOperation.FailureCode);
        Assert.Equal("running", Assert.Single(
            failureRecovery.Steps, step => step.StepId == "rubberduck").CheckpointStatus);
        Assert.DoesNotContain(
            MafExecutionPlanner.BuildFrontier(plan, failedAttempt.Progress, 0, 0, 10, 10, _ => true)
                .ReadyActions,
            action => action.Step.Id == "merge");

        var rejected = MafRubberduckReviewContract.ReadOwnerResult(
            intent, "REQUEST_CHANGES\nFix the recorded issue.");
        var completed = CoordinationEndpoints.PrepareRubberduckCompletionCheckpoint(
            new(new("root", "rubberduck-retry"), failedAttempt), intent, rejected)!;
        var frontier = MafExecutionPlanner.BuildFrontier(
            plan, completed.Progress, 0, 0, 10, 10, _ => true);

        Assert.Equal(MafExecutionTaskStatus.Failed, completed.Progress.NonModelSteps["rubberduck"]);
        Assert.Contains("merge", frontier.FailedDependencyIds);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.Step.Id == "merge");
        Assert.DoesNotContain("rubberduck", completed.RubberduckFailureCodes.Keys);
    }

    [Fact]
    public void RubberduckCheckpointTransitionRejectsChangedIntentAndReceipt()
    {
        var (plan, state, owner, identity) = CreateFixture();
        var intent = CreateIntent(plan, state, owner, identity);
        var running = CoordinationEndpoints.PrepareRubberduckCheckpoint(
            new(new("root", "prior-checkpoint"), state), intent)!;
        var receipt = MafRubberduckReviewContract.ReadOwnerResult(intent, "PASS");
        var complete = CoordinationEndpoints.PrepareRubberduckCompletionCheckpoint(
            new(new("root", "rubberduck-intent"), running), intent, receipt)!;

        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            running with
            {
                RubberduckIntents = running.RubberduckIntents.SetItem(
                    intent.StepId, intent with { EvidenceHash = new string('b', 64) })
            },
            complete));
        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            complete,
            complete with
            {
                RubberduckReceipts = complete.RubberduckReceipts.SetItem(
                    intent.StepId, receipt with { Outcome = "requestChanges" })
            }));
    }

    private static MafExecutionRubberduckIntent CreateIntent(
        WorkPlanSnapshot plan,
        MafExecutionCheckpoint state,
        RuntimeRegistration owner,
        SessionIdentity identity,
        Guid? operationId = null,
        string checkpointId = "rubberduck-checkpoint",
        long checkpointRevision = 4) =>
        MafRubberduckReviewContract.CreateIntent(
            operationId ?? Guid.NewGuid(),
            identity,
            checkpointId,
            checkpointRevision,
            plan,
            plan.Workflow.Definition.Steps.Single(
                step => step.PlatformGate == WorkflowPlatformGate.RubberDuck),
            state,
            3,
            "actor-1",
            7,
            new string('a', 64),
            owner);

    private static (WorkPlanSnapshot Plan, MafExecutionCheckpoint State,
        RuntimeRegistration Owner, SessionIdentity Identity) CreateFixture()
    {
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
        var identity = new SessionIdentity("project-1", "run-1", "root");
        const string actor = "actor-1";
        const long fence = 7;
        const long decisionVersion = 3;
        var selectionHash = new string('a', 64);
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "first", MafExecutionTaskStatus.Succeeded)
        };
        var initial = new MafExecutionCheckpoint(1, plan.Plan.Id, decisionVersion, progress)
        {
            Results = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("first", "Reviewed implementation output.")
        };
        var reviewStep = workflow.Steps.Single(step =>
            step.PlatformGate == WorkflowPlatformGate.IndependentReview);
        var reviewIntent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), identity, "review-checkpoint", 2, plan, reviewStep,
            initial, decisionVersion, actor, fence, selectionHash);
        var reviewReceipt = new MafReviewTerminalReceipt(
            reviewIntent.OperationId,
            reviewIntent.ApprovalRequestId,
            reviewIntent.EvidenceHash,
            actor,
            fence,
            CoordinatorGateChoices.Approve,
            "approved");
        var state = initial with
        {
            Revision = 3,
            Progress = progress with
            {
                NonModelSteps = progress.NonModelSteps.SetItem(
                    reviewStep.Id, MafExecutionTaskStatus.Succeeded)
            },
            ReviewIntents = ImmutableDictionary<string, MafExecutionReviewIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewIntent),
            ReviewReceipts = ImmutableDictionary<string, MafReviewTerminalReceipt>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewReceipt)
        };
        var binding = new RuntimeBinding(
            "https://identity.example/",
            actor,
            "tenant-1",
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            RuntimeActionContract.CoordinatorRootStepId,
            "root-turn",
            1,
            1,
            1,
            "context-v1",
            selectionHash,
            fence,
            "environment-1",
            "placement-1",
            1,
            "profile-1",
            new Uri("https://runtime.example/runtime/v1/configure"),
            new Uri("https://runtime.example/runtime/v1/observe"));
        var owner = new RuntimeRegistration(
            Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active,
            DateTimeOffset.UtcNow.AddMinutes(5));
        return (plan, state, owner, identity);
    }
}
