using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafExecutionRecoveryProjectionTests
{
    [Fact]
    public void PendingChildIntentMayRepeatAndDoesNotOpenItsJoin()
    {
        var plan = CreatePlan(
            WorkflowTestData.Item("first"),
            WorkflowTestData.Item("followup", dependsOn: ["first"]));
        var first = plan.Plan.Items[0];
        var root = new SessionIdentity("project-1", "run-1", "root");
        var intent = new MafExecutionDispatchIntent(
            first.Id,
            MafExecutionPlanner.CreateChildSessionId(root, plan.Plan.Id, first.Id),
            MafExecutionPlanner.CreateDispatchMessageId(root, plan.Plan.Id, first.Id),
            new string('a', 64));
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                first.Id, MafExecutionTaskStatus.Running)
        };
        var checkpoint = Snapshot(new MafExecutionCheckpoint(
            2, plan.Plan.Id, 2, progress)
        {
            PendingDispatches = ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(first.Id, intent)
        });

        // The projection must retain the uncertain child intent without treating the dependent item as joined.
        var recovery = MafExecutionRecoveryProjector.Project(checkpoint, plan, 2, true, null);

        Assert.Equal("available", recovery.State);
        var firstStep = Assert.Single(recovery.Steps, step => step.WorkItemId == "first");
        Assert.Equal("running", firstStep.CheckpointStatus);
        Assert.Equal("mayRepeat", firstStep.EffectRecoveryState);
        var followup = Assert.Single(recovery.Steps, step => step.WorkItemId == "followup");
        Assert.Equal("waitingOnDependencies", followup.Readiness);
        var pending = Assert.Single(recovery.PendingChildDispatches);
        Assert.Equal(intent.MessageId, pending.MessageId);
        Assert.Equal("mayRepeat", pending.EffectRecoveryState);
    }

    [Fact]
    public void PendingConfirmationGateBlocksDependencyReadyWork()
    {
        var plan = CreatePlan(WorkflowTestData.Item("accepted-work"));
        var checkpoint = Snapshot(new MafExecutionCheckpoint(
            1, plan.Plan.Id, 1, MafExecutionProgress.Empty));
        var gate = new SessionStatusBlocker(
            CoordinationBlockerKind.AwaitingPlanApproval,
            "scope-confirmation",
            [],
            false,
            "Confirm the changed work");

        // Only the confirmed plan reaches this projection; the open gate keeps even its ready work blocked.
        var recovery = MafExecutionRecoveryProjector.Project(checkpoint, plan, 2, false, gate);

        var acceptedWork = Assert.Single(recovery.Steps, step => step.WorkItemId == "accepted-work");
        Assert.Equal("blockedByDecisionGate", acceptedWork.Readiness);
        Assert.Equal("scope-confirmation", recovery.PendingGate?.RequestId);
    }

    [Fact]
    public void ScopeChangingReplanUsesCurrentSelectionAndWaitsForConfirmation()
    {
        const string owner = "owner-1";
        var selectionContext = WorkflowTestData.SelectionContext();
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []));
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(1),
            new CoordinatorOutcomeSpecification(
                "outcome-1",
                "Implement the requested capability.",
                "The validated implementation is available.",
                "Only the requested work scope changes.",
                "The accepted run selections remain authoritative.",
                []),
            "outcome-approval",
            owner);
        Assert.True(outcome.IsSuccess);
        var acceptedOutcome = outcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            outcome.State.PendingGate!.RequestId,
            owner,
            outcome.State.Fence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var selectedWorkflow = CoordinatorDecisionFlow.SelectWorkflow(
            acceptedOutcome,
            new AuthorizedWorkflowCatalog(workflow, [workflow]),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        Assert.True(selectedWorkflow.IsSuccess);
        var proposedPlan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selectedWorkflow.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            selectionContext,
            "plan-approval",
            owner);
        Assert.True(proposedPlan.IsSuccess);
        var acceptedPlan = proposedPlan.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedPlan.State.PendingGate!.RequestId,
            owner,
            proposedPlan.State.Fence,
            CoordinatorGateChoices.Approve,
            null)).State;

        // The revised plan is checked against the accepted run selection before any scope gate is created.
        var staleCatalogPlan = WorkflowTestData.Plan(
            WorkflowTestData.Item("first", modelSelection: "model:provider/gamma"),
            WorkflowTestData.Item("second"));
        var rejectedRevision = CoordinatorDecisionFlow.ReviseWorkPlan(
            acceptedPlan,
            staleCatalogPlan,
            selectionContext,
            "stale-catalog-revision",
            owner);
        Assert.False(rejectedRevision.IsSuccess);
        Assert.Contains(rejectedRevision.Issues, issue =>
            issue.Code == WorkflowValidationCode.ModelSelectionNotEligible);

        var changedPlan = WorkflowTestData.Plan(
            WorkflowTestData.Item("first"),
            WorkflowTestData.Item("second"));
        var revision = CoordinatorDecisionFlow.ReviseWorkPlan(
            acceptedPlan,
            changedPlan,
            selectionContext,
            "scope-confirmation",
            owner);
        Assert.True(revision.IsSuccess);
        Assert.True(revision.Value!.RequiresConfirmation);
        Assert.Equal(CoordinatorGateKind.ScopeChangeConfirmation, revision.State!.PendingGate!.Kind);
        Assert.False(revision.State.CanDispatch);

        var checkpoint = Snapshot(new MafExecutionCheckpoint(
            1, acceptedPlan.ConfirmedWorkPlan!.Plan.Id, 1, MafExecutionProgress.Empty));
        var pendingGate = new SessionStatusBlocker(
            CoordinationBlockerKind.AwaitingPlanApproval,
            revision.State.PendingGate.RequestId,
            ["approve", "reject"],
            false,
            null);
        var recovery = MafExecutionRecoveryProjector.Project(
            checkpoint,
            acceptedPlan.ConfirmedWorkPlan,
            2,
            revision.State.CanDispatch,
            pendingGate);
        var blockedWork = Assert.Single(recovery.Steps, step => step.WorkItemId == "first");
        Assert.Equal("blockedByDecisionGate", blockedWork.Readiness);
        Assert.Equal("scope-confirmation", recovery.PendingGate?.RequestId);

        var approved = revision.State.ApplyGateAnswer(new CoordinatorGateAnswer(
            revision.State.PendingGate.RequestId,
            owner,
            revision.State.Fence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(approved.AuthorizesTransition);
        Assert.True(approved.State.CanDispatch);
        Assert.Equal(["first", "second"], approved.State.ConfirmedWorkPlan!.Plan.Items
            .Select(item => item.Id).ToArray());
    }

    [Fact]
    public void CheckpointProgressFromRemovedSameIdWorkPlanAssociationIsUnavailable()
    {
        var currentPlan = CreatePlan(WorkflowTestData.Item("second"));
        var oldProgress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "first", MafExecutionTaskStatus.Running)
        };
        var checkpoint = Snapshot(new MafExecutionCheckpoint(
            2, currentPlan.Plan.Id, 1, oldProgress));

        // The reused plan ID cannot prove that progress for a removed association belongs to the current plan.
        var recovery = MafExecutionRecoveryProjector.Project(checkpoint, currentPlan, 2, true, null);

        Assert.Equal("unavailable", recovery.State);
        Assert.Equal("maf_execution_checkpoint_progress_mismatch", recovery.UnavailableCode);
        Assert.Empty(recovery.Steps);
    }

    [Fact]
    public void MergeIntentMayRepeatAndTerminalReceiptIsReportedAsRecordedEvidence()
    {
        var (plan, checkpoint, intent) = CreateMergeRecoveryFixture();

        var intentRecovery = MafExecutionRecoveryProjector.Project(
            Snapshot(checkpoint), plan, intent.DecisionStateVersion, true, null);

        var intentOperation = Assert.Single(intentRecovery.PlatformGateOperations);
        Assert.Equal(WorkflowPlatformGate.Merge, intentOperation.Gate);
        Assert.Equal(intent.OperationId, intentOperation.OperationId);
        Assert.Equal("intentOnly", intentOperation.EvidenceState);
        Assert.Equal("mayRepeat", intentOperation.EffectRecoveryState);
        Assert.Equal("mayRepeat",
            Assert.Single(intentRecovery.Steps, step => step.StepId == "merge").EffectRecoveryState);

        var receipt = new MafMergeTerminalReceipt(
            intent.OperationId, intent.OwnerIntentId, intent.PullRequestNumber,
            intent.ExpectedHeadSha, new string('b', 40));
        var completed = checkpoint with
        {
            Revision = 3,
            Progress = checkpoint.Progress with
            {
                NonModelSteps = checkpoint.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Succeeded)
            },
            MergeReceipts = checkpoint.MergeReceipts.Add(intent.StepId, receipt)
        };
        var receiptRecovery = MafExecutionRecoveryProjector.Project(
            Snapshot(completed), plan, intent.DecisionStateVersion, true, null);

        var receiptOperation = Assert.Single(receiptRecovery.PlatformGateOperations);
        Assert.Equal("terminalReceiptRecorded", receiptOperation.EvidenceState);
        Assert.Null(receiptOperation.EffectRecoveryState);
        Assert.Equal("terminalReceiptRecorded",
            Assert.Single(receiptRecovery.Steps, step => step.StepId == "merge").EffectRecoveryState);

        // The persisted receipt is not current evidence if a replan changes this step to another gate.
        var mismatchedPlan = CreateMergePlan(WorkflowPlatformGate.OpenPullRequest);
        var mismatched = MafExecutionRecoveryProjector.Project(
            Snapshot(completed), mismatchedPlan, intent.DecisionStateVersion, true, null);
        Assert.Equal("unavailable", mismatched.State);
        Assert.Equal("maf_execution_checkpoint_progress_mismatch", mismatched.UnavailableCode);
    }

    [Fact]
    public void MissingOrMismatchedCheckpointNeverClaimsWorkIsReady()
    {
        var plan = CreatePlan(WorkflowTestData.Item("accepted-work"));
        var missing = MafExecutionRecoveryProjector.Project(null, plan, 1, true, null);
        var mismatched = MafExecutionRecoveryProjector.Project(
            Snapshot(new MafExecutionCheckpoint(
                1, "different-plan", 1, MafExecutionProgress.Empty)),
            plan,
            1,
            true,
            null);

        Assert.Equal("unavailable", missing.State);
        Assert.Equal("maf_execution_checkpoint_missing", missing.UnavailableCode);
        Assert.Empty(missing.Steps);
        Assert.Equal("unavailable", mismatched.State);
        Assert.Equal("maf_execution_checkpoint_plan_mismatch", mismatched.UnavailableCode);
        Assert.Empty(mismatched.Steps);
    }

    private static (WorkPlanSnapshot Plan, MafExecutionCheckpoint Checkpoint, MafExecutionMergeIntent Intent)
        CreateMergeRecoveryFixture()
    {
        var plan = CreateMergePlan(WorkflowPlatformGate.Merge);
        var source = MafBuildTestSourceInputTests.Fixture().Run;
        var intent = MafMergeContract.CreateIntent(
            Guid.NewGuid(),
            source.Identity,
            "merge-intent",
            2,
            plan.Plan.Id,
            plan.Workflow.Definition.Id,
            plan.Workflow.Definition.Revision,
            "merge",
            1,
            source.Pin.AcceptedRun.Fence,
            source.Pin.AcceptedRun.AcceptedSelectionHash,
            source.Pin,
            42,
            new string('a', 40),
            SourceControlMergeMethod.Rebase);
        var progress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                intent.StepId, MafExecutionTaskStatus.Running)
        };
        var checkpoint = new MafExecutionCheckpoint(
            intent.CheckpointRevision,
            intent.WorkPlanId,
            intent.DecisionStateVersion,
            progress)
        {
            MergeIntents = ImmutableDictionary<string, MafExecutionMergeIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(intent.StepId, intent)
        };
        return (plan, checkpoint, intent);
    }

    private static WorkPlanSnapshot CreateMergePlan(WorkflowPlatformGate gate)
    {
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("merge", 1, ["implement"], gate)));
        var result = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }

    private static MafExecutionCheckpointSnapshot Snapshot(MafExecutionCheckpoint state) =>
        new(new CheckpointInfo("root", "checkpoint-" + state.Revision), state);

    private static WorkPlanSnapshot CreatePlan(params WorkPlanItem[] items)
    {
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, [])));
        var result = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(items), WorkflowTestData.SelectionContext());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }
}
