using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafIndependentReviewTests
{
    [Fact]
    public void ReviewBindsOnlyWithDecisionOwnerAndEnforcesGateOrder()
    {
        var plan = Plan();
        Assert.Equal("maf_execution_executor_unavailable",
            Assert.Throws<CoordinationException>(() =>
                MafExecutionWorkflowBindings.RequireBound(plan, false)).Code);
        var bound = MafExecutionWorkflowBindings.RequireBound(
            plan, false, hasMergeOwner: true, hasIndependentReviewOwner: true);
        MafExecutionWorkflowBindings.RequireBoundAction(
            bound, new MafExecutionAction(plan.Workflow.Definition.Steps[1], null));
        Assert.Equal(WorkflowPlatformGate.IndependentReview,
            Assert.IsType<BoundWorkflowStep.Platform>(bound.Steps[1]).Gate);

        // A merge on a parallel path must not skip the human decision.
        var unordered = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("review", 1, ["implement"]),
            WorkflowTestData.Platform("merge", 2, ["implement"], WorkflowPlatformGate.Merge));
        var unorderedPlan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(unordered), WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext()).Value!;
        Assert.Equal("maf_execution_review_order_invalid",
            Assert.Throws<CoordinationException>(() =>
                MafExecutionWorkflowBindings.RequireBound(
                    unorderedPlan, false, hasMergeOwner: true, hasIndependentReviewOwner: true)).Code);

        // A software review cannot precede its build/test result; content-only plans need no build step.
        var buildAfterReview = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("review", 1, ["implement"]),
            WorkflowTestData.Platform("build", 2, ["implement"], WorkflowPlatformGate.BuildTest));
        Assert.Equal("maf_execution_review_order_invalid",
            Assert.Throws<CoordinationException>(() =>
                MafIndependentReviewContract.RequireGateOrder(buildAfterReview)).Code);
    }

    [Theory]
    [InlineData(CoordinatorGateChoices.Approve, true)]
    [InlineData(CoordinatorGateChoices.Reject, false)]
    public void ReviewAnswerIsBoundToEvidenceAndControlsDependentJoin(
        string choice, bool approved)
    {
        var expected = approved ? MafExecutionTaskStatus.Succeeded : MafExecutionTaskStatus.Failed;
        var plan = Plan();
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var initial = new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add("first", MafExecutionTaskStatus.Succeeded)
        })
        {
            Results = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal).Add("first", "Reviewed source revision A.")
        };
        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), identity, "review-checkpoint", 2, plan,
            plan.Workflow.Definition.Steps[1], initial, 3, "reviewer-1", 1, new string('a', 64));
        var running = CoordinationEndpoints.PrepareReviewCheckpoint(
            new(new("root", "initial"), initial), intent)!;
        var pending = MafExecutionRecoveryProjector.Project(
            new(new("root", "review-checkpoint"), running), plan, 3, true, null);
        var pendingOperation = Assert.Single(pending.PlatformGateOperations);
        Assert.Equal("intentOnly", pendingOperation.EvidenceState);
        Assert.Equal("awaitingApproval", pendingOperation.EffectRecoveryState);
        Assert.Equal(intent.EvidenceHash, pendingOperation.EvidenceHash);
        Assert.Equal(intent.ApprovalRequestId, pendingOperation.ApprovalRequestId);

        var actor = "reviewer-1";
        var confirmed = ConfirmedDecision(plan, actor);
        var requested = CoordinatorDecisionFlow.RequestApproval(
            confirmed, intent.ApprovalRequestId, intent.EvidenceHash, actor);
        Assert.True(requested.IsSuccess);
        Assert.Null(MafIndependentReviewContract.ReadDecision(
            intent, new(requested.State!, 4, intent.AcceptedSelectionHash), actor));
        var answered = requested.State!.ApplyGateAnswer(new(
            intent.ApprovalRequestId, actor, intent.ExecutionFence, choice, null));
        Assert.True(answered.IsAccepted);
        var receipt = MafIndependentReviewContract.ReadDecision(
            intent, new(answered.State, 5, intent.AcceptedSelectionHash), actor)!;
        Assert.Equal(expected, MafIndependentReviewContract.ValidateReceipt(intent, receipt));
        var complete = CoordinationEndpoints.PrepareReviewCompletionCheckpoint(
            new(new("root", "review-checkpoint"), running with { DecisionStateVersion = 5 }),
            intent, receipt)!;
        Assert.Equal(expected, complete.Progress.NonModelSteps["review"]);
        var recovery = MafExecutionRecoveryProjector.Project(
            new(new("root", "review-terminal"), complete), plan, 5, true, null);
        Assert.Equal(choice, Assert.Single(recovery.PlatformGateOperations).ReviewDecision);
        Assert.Equal("terminalReceiptRecorded",
            Assert.Single(recovery.PlatformGateOperations).EvidenceState);
        var frontier = MafExecutionPlanner.BuildFrontier(
            plan, complete.Progress, 0, 0, 10, 10, _ => true);
        Assert.Equal(choice == CoordinatorGateChoices.Approve,
            frontier.ReadyActions.Any(action => action.Step.Id == "merge"));
        if (choice == CoordinatorGateChoices.Reject)
            Assert.Contains("merge", frontier.FailedDependencyIds);

        // A different actor, evidence, decision, or revoked fence cannot create a terminal receipt.
        Assert.Equal("maf_execution_review_decision_conflict",
            Assert.Throws<CoordinationException>(() => MafIndependentReviewContract.ReadDecision(
                intent, new(answered.State, 5, intent.AcceptedSelectionHash), "another-actor")).Code);
        Assert.Equal("maf_execution_review_binding_changed",
            Assert.Throws<CoordinationException>(() => MafIndependentReviewContract.ReadDecision(
                intent, new(answered.State, 6, intent.AcceptedSelectionHash), actor)).Code);
        Assert.Throws<ArgumentException>(() => MafIndependentReviewContract.ValidateReceipt(
            intent, receipt with { EvidenceHash = new string('b', 64) }));
        Assert.Equal("maf_execution_review_binding_changed",
            Assert.Throws<CoordinationException>(() => MafIndependentReviewContract.ReadDecision(
                intent with { ExecutionFence = 2 },
                new(answered.State, 5, intent.AcceptedSelectionHash), actor)).Code);
        Assert.False(MafIndependentReviewContract.IsValidIntent(
            intent with { EvidenceSummary = intent.EvidenceSummary + "changed" }));
        var alteredEvidence = MafIndependentReviewContract.CreateIntent(
            intent.OperationId, identity, intent.CheckpointId, intent.CheckpointRevision,
            plan, plan.Workflow.Definition.Steps[1],
            initial with { Results = initial.Results.SetItem("first", "Different source.") },
            3, "reviewer-1", 1, new string('a', 64));
        Assert.NotEqual(intent.EvidenceHash, alteredEvidence.EvidenceHash);
    }

    [Fact]
    public void DecisionVersionAdvanceRequiresOnlyRecordedReviewTransitions()
    {
        // The original version remains usable while this exact approval is pending or recorded.
        var plan = Plan();
        const string actor = "reviewer-1";
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var initial = new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add("first", MafExecutionTaskStatus.Succeeded)
        })
        {
            Results = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal).Add("first", "Reviewed source revision A.")
        };
        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), identity, "review-checkpoint", 2, plan,
            plan.Workflow.Definition.Steps[1], initial, 3, actor, 1, new string('a', 64));
        var pendingState = CoordinatorDecisionFlow.RequestApproval(
            ConfirmedDecision(plan, actor), intent.ApprovalRequestId, intent.EvidenceHash, actor).State!;
        var pendingCheckpoint = CoordinationEndpoints.PrepareReviewCheckpoint(
            new(new("root", "initial"), initial), intent)!;

        Assert.True(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            pendingCheckpoint, new(pendingState, 4, intent.AcceptedSelectionHash), 3));
        Assert.False(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            pendingCheckpoint, new(pendingState, 5, intent.AcceptedSelectionHash), 3));
        Assert.False(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            pendingCheckpoint, new(pendingState, 4, new string('b', 64)), 3));

        var answered = pendingState.ApplyGateAnswer(new(
            intent.ApprovalRequestId, actor, intent.ExecutionFence, CoordinatorGateChoices.Approve, null)).State;
        var ownerReceipt = MafIndependentReviewContract.ReadDecision(
            intent, new(answered, 5, intent.AcceptedSelectionHash), actor)!;
        var completedCheckpoint = CoordinationEndpoints.PrepareReviewCompletionCheckpoint(
            new(new("root", "review-checkpoint"), pendingCheckpoint with { DecisionStateVersion = 5 }),
            intent, ownerReceipt)!;
        Assert.True(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            completedCheckpoint, new(answered, 5, intent.AcceptedSelectionHash), 3));
        Assert.False(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            completedCheckpoint, new(answered, 6, intent.AcceptedSelectionHash), 3));
    }

    [Fact]
    public void UnchangedDecisionVersionDoesNotRequireReviewHistoryWhenAnotherGateIsPending()
    {
        // Existing fixed-child runtime access must remain valid at its exact decision version, including a pending gate.
        var plan = Plan();
        const string actor = "reviewer-1";
        var state = ConfirmedDecision(plan, actor);
        var pending = CoordinatorDecisionFlow.RequestApproval(
            state, "unrelated-request", "unrelated-subject", actor);
        Assert.True(pending.IsSuccess);
        var checkpoint = new MafExecutionCheckpoint(
            1, plan.Plan.Id, 4, MafExecutionProgress.Empty);

        Assert.True(MafIndependentReviewContract.AllowsDecisionVersionAdvance(
            checkpoint,
            new(pending.State!, 4, new string('a', 64)),
            4));
    }

    [Fact]
    public void ConfirmedIndependentReviewDecisionEnvelopeRestores()
    {
        // The durable owner must be able to rehydrate the accepted review workflow after a restart.
        var plan = Plan();
        const string actorId = "reviewer-1";
        const string issuer = "https://identity.example/";
        using var snapshot = System.Text.Json.JsonDocument.Parse(
            """{"modelSelection":{"reference":"model:provider/alpha"}}""");
        var selection = new AuthorizedRunSelection(
            new("project-1", "run-1", 1, 1, 1, "context-v1", snapshot.RootElement.Clone()),
            new ProjectsAuthorizationContext(
                1, issuer, actorId, "tenant-1", 1, "project-1", "run-1",
                ImmutableArray.Create(new ProjectsAuthority(
                    "project", "project-1",
                    ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var binding = CoordinatorDecisionBinding.Create(
            new CoordinationActor(issuer, actorId), identity, selection, 1);
        var selectionContext = WorkflowTestData.SelectionContext();
        var confirmed = ConfirmedDecision(plan, actorId, selectionContext);
        var envelope = CoordinatorDecisionStateEnvelope.Capture(
            confirmed, binding, selectionContext);

        var untrusted = envelope.Restore(binding);
        Assert.False(untrusted.IsValid);
        var restored = envelope.Restore(
            binding, _ => selectionContext.IsolationProviderBinding);

        Assert.True(restored.IsValid,
            string.Join("; ", restored.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
    }

    [Fact]
    public void ReviewRejectsChangedBuildTestWorkspaceManifest()
    {
        // A BuildTest source is current only while its canonical file manifest and Git tree remain unchanged.
        var template = MafBuildTestCheckpointTests.SeedIntent().EffectiveWorkspaceSource!;
        var bytes = System.Text.Encoding.UTF8.GetBytes("same workspace");
        var capture = new GitWorkspaceCapture(
            template.WorkspaceId, template.Identity.RunId, template.SourceControlResource.ResourceId,
            template.SourceControlResource.Generation, template.WorkspaceIncarnationId,
            template.BaseSha, new string('c', 40), string.Empty,
            [new("src/app.cs", "100644", GitWorkspaceCapturePackage.Hash(bytes), bytes.LongLength,
                bytes.ToImmutableArray())],
            template.BranchName);
        var baseline = GitWorkspaceCapturePackage.Create(capture);
        var source = template with
        {
            BaselineTreeSha = baseline.Manifest.OutputTreeSha,
            BaselineManifestSha256 = baseline.ManifestSha256,
            BaselineManifestByteLength = baseline.ManifestBytes.LongLength
        };
        Assert.True(source.IsValid());
        Assert.True(MafIndependentReviewContract.MatchesBuildTestWorkspaceSource(source, baseline));

        var changedCapture = capture with
        {
            Files = [new("src/app.cs", "100644",
                GitWorkspaceCapturePackage.Hash(System.Text.Encoding.UTF8.GetBytes("changed workspace")),
                "changed workspace".Length,
                System.Text.Encoding.UTF8.GetBytes("changed workspace").ToImmutableArray())],
            OutputTreeSha = new string('d', 40)
        };
        var changed = GitWorkspaceCapturePackage.Create(changedCapture);
        Assert.False(MafIndependentReviewContract.MatchesBuildTestWorkspaceSource(source, changed));
    }

    [Fact]
    public void ReviewEvidenceSupportsMaximumWorkPlanSize()
    {
        // Split the supported 256 items across two bounded 128-item steps that both precede review.
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open(
                "implement-a", 0, [], minimum: 1, maximum: WorkflowDomainLimits.MaximumItemsPerStep,
                roles: ["engineer"]),
            WorkflowTestData.Open(
                "implement-b", 1, [], minimum: 1, maximum: WorkflowDomainLimits.MaximumItemsPerStep,
                roles: ["engineer"]),
            WorkflowTestData.Platform("review", 2, ["implement-a", "implement-b"]));
        workflow = workflow with { MaximumWorkItems = WorkflowDomainLimits.MaximumWorkItems };
        var context = WorkflowTestData.SelectionContext();
        var items = Enumerable.Range(0, WorkflowDomainLimits.MaximumWorkItems)
            .Select(index => WorkflowTestData.Item(
                index.ToString("D3") + new string('x', 120),
                index < WorkflowDomainLimits.MaximumItemsPerStep ? "implement-a" : "implement-b"))
            .ToArray();
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(workflow), WorkflowTestData.Plan(items), context).Value!;
        var results = items.ToImmutableDictionary(
            item => item.Id, _ => new string('r', 128), StringComparer.Ordinal);
        var state = new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty with
        {
            WorkItems = results.Keys.ToImmutableDictionary(
                id => id, _ => MafExecutionTaskStatus.Succeeded, StringComparer.Ordinal)
        })
        {
            Results = results
        };

        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), new("project-1", "run-1", "root"), "review-checkpoint",
            2, plan, workflow.Steps.Single(step => step.Id == "review"),
            state, 3, "reviewer-1", 1, new string('a', 64));

        Assert.True(intent.EvidenceSummary.Length > 16_000);
        Assert.True(MafIndependentReviewContract.IsValidIntent(intent));
        var approval = CoordinatorDecisionFlow.RequestApproval(
            ConfirmedDecision(plan, "reviewer-1"), intent.ApprovalRequestId,
            intent.EvidenceHash, "reviewer-1", "Review the recorded evidence: " + intent.EvidenceSummary);
        Assert.True(approval.IsSuccess);
    }

    private static WorkPlanSnapshot Plan()
    {
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("review", 1, ["implement"]),
            WorkflowTestData.Platform("merge", 2, ["review"], WorkflowPlatformGate.Merge));
        return WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(workflow), WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext()).Value!;
    }

    private static CoordinatorDecisionState ConfirmedDecision(
        WorkPlanSnapshot plan, string actor, WorkPlanRunSelectionContext? selectionContext = null)
    {
        selectionContext ??= WorkflowTestData.SelectionContext();
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(1),
            new("outcome-1", "Implement the change.", "The result is reviewed.",
                "Only the requested changes.", "The source is available.", []),
            "outcome-request", actor);
        var confirmedOutcome = outcome.State!.ApplyGateAnswer(
            new("outcome-request", actor, 1, CoordinatorGateChoices.Approve, null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome, new(plan.Workflow.Definition, [plan.Workflow.Definition]),
            null, null, null);
        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State, plan.Plan, selectionContext, "plan-request", actor);
        Assert.True(proposed.IsSuccess);
        return proposed.State!.ApplyGateAnswer(
            new("plan-request", actor, 1, CoordinatorGateChoices.Approve, null)).State;
    }

}
