using System.Collections.Immutable;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CoordinatorDecisionFlowTests
{
    private const string Owner = "owner-1";

    [Fact]
    public void OutcomeMustBeConfirmedBeforeWorkflowSelectionAndPlanDecomposition()
    {
        var state = CoordinatorDecisionState.Create(4);
        var proposal = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            state, Outcome(), "outcome-confirmation-1", Owner);

        Assert.True(proposal.IsSuccess);
        Assert.False(proposal.State!.CanDecompose);
        Assert.Equal(CoordinatorGateKind.OutcomeConfirmation, proposal.State.PendingGate!.Kind);

        var blocked = CoordinatorDecisionFlow.SelectWorkflow(
            proposal.State,
            Catalog(WorkflowTestData.Definition()),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        Assert.False(blocked.IsSuccess);
        Assert.Contains(blocked.Issues, issue =>
            issue.Code == WorkflowValidationCode.CoordinatorTransitionBlocked);

        var confirmed = proposal.State.ApplyGateAnswer(Answer(
            proposal.State.PendingGate.RequestId,
            Owner,
            4,
            CoordinatorGateChoices.Approve));
        Assert.Equal(CoordinatorGateAnswerStatus.Accepted, confirmed.Status);
        Assert.True(confirmed.AuthorizesTransition);
        Assert.True(confirmed.State.OutcomeConfirmed);

        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmed.State,
            Catalog(WorkflowTestData.Definition()),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        Assert.True(selected.IsSuccess);
        Assert.True(selected.State!.CanDecompose);

        var proposedPlan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext(),
            "plan-confirmation-1",
            Owner);
        Assert.True(proposedPlan.IsSuccess);
        Assert.False(proposedPlan.State!.CanDispatch);
        Assert.Null(proposedPlan.State.ConfirmedWorkPlan);
        Assert.Equal(CoordinatorGateKind.WorkPlanConfirmation, proposedPlan.State.PendingGate!.Kind);

        var planConfirmed = proposedPlan.State.ApplyGateAnswer(Answer(
            proposedPlan.State.PendingGate.RequestId,
            Owner,
            4,
            CoordinatorGateChoices.Approve));
        Assert.True(planConfirmed.AuthorizesTransition);
        Assert.True(planConfirmed.State.CanDispatch);
        Assert.NotNull(planConfirmed.State.ConfirmedWorkPlan);
    }

    [Fact]
    public void MessageAcknowledgmentNeverAuthorizesOrResolvesTheCurrentGate()
    {
        var proposal = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(4),
            Outcome(),
            "outcome-confirmation-ack",
            Owner);
        var state = proposal.State!;
        var gate = state.PendingGate!;

        var acknowledged = state.AcknowledgeMessage(
            new CoordinatorMessageAcknowledgment(gate.RequestId, Owner, state.Fence));
        var stale = state.AcknowledgeMessage(
            new CoordinatorMessageAcknowledgment("old-request", Owner, state.Fence));

        Assert.True(acknowledged.IsAcknowledged);
        Assert.False(acknowledged.AuthorizesTransition);
        Assert.Same(state, acknowledged.State);
        Assert.Same(gate, acknowledged.State.PendingGate);
        Assert.False(acknowledged.State.OutcomeConfirmed);
        Assert.Empty(acknowledged.State.DecisionReceipts);
        Assert.False(stale.IsAcknowledged);
        Assert.False(stale.AuthorizesTransition);
        Assert.Contains(stale.Issues, issue =>
            issue.Code == WorkflowValidationCode.StaleDecisionRequest);
    }

    [Fact]
    public void GeneratedWorkflowIsValidatedBeforeFirstUseGateAndCannotDecomposeUntilConfirmed()
    {
        var generated = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.Generated,
            WorkflowTestData.Open("implement", 0, [], minimum: 1)) with
        {
            Id = "workflow.generated",
            Revision = "generated-revision"
        };
        var state = ConfirmedOutcomeState();

        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            state,
            Catalog(WorkflowTestData.Definition(), generated),
            "workflow.generated",
            "generated-first-use-1",
            Owner);

        Assert.True(selected.IsSuccess);
        Assert.True(selected.State!.SelectedWorkflow!.RequiresFirstUseConfirmation);
        Assert.Equal(CoordinatorGateKind.GeneratedWorkflowConfirmation, selected.State.PendingGate!.Kind);
        Assert.False(selected.State.CanDecompose);

        var prematurePlan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext(),
            "plan-confirmation-2",
            Owner);
        Assert.False(prematurePlan.IsSuccess);
        Assert.Contains(prematurePlan.Issues, issue =>
            issue.Code == WorkflowValidationCode.CoordinatorTransitionBlocked);

        var accepted = selected.State.ApplyGateAnswer(Answer(
            selected.State.PendingGate.RequestId,
            Owner,
            selected.State.Fence,
            CoordinatorGateChoices.Approve));
        Assert.True(accepted.AuthorizesTransition);
        Assert.True(accepted.State.CanDecompose);

        var plan = CoordinatorDecisionFlow.ProposeWorkPlan(
            accepted.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")) with
            {
                WorkflowId = generated.Id,
                DefinitionRevision = generated.Revision,
                CatalogVersion = generated.CatalogVersion
            },
            WorkflowTestData.SelectionContext(),
            "plan-confirmation-3",
            Owner);
        Assert.True(plan.IsSuccess);
    }

    [Fact]
    public void InvalidWorkflowSelectionUsesTheActualAuthorizedDefaultAndReturnsReasons()
    {
        var defaultWorkflow = WorkflowTestData.Definition();
        var malformedGenerated = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.Generated,
            WorkflowTestData.Open("candidate", 0, [], minimum: 0)) with
        {
            Id = "generated.workflow",
            Steps = default
        };

        var invalidGenerated = WorkflowSelectionResolver.Resolve(
            Catalog(defaultWorkflow, malformedGenerated),
            "generated.workflow");
        var unavailable = WorkflowSelectionResolver.Resolve(
            Catalog(defaultWorkflow),
            "not-authorized");
        var malformed = WorkflowSelectionResolver.Resolve(
            Catalog(defaultWorkflow),
            " ");

        Assert.True(invalidGenerated.IsValid);
        Assert.True(invalidGenerated.UsedAuthorizedDefault);
        Assert.Equal(defaultWorkflow.Id, invalidGenerated.SelectedWorkflow!.Definition.Id);
        Assert.Contains(invalidGenerated.Reasons, issue =>
            issue.Code == WorkflowValidationCode.InvalidWorkflowSelection);
        Assert.Contains(invalidGenerated.Reasons, issue =>
            issue.Code == WorkflowValidationCode.InvalidStepCount);

        Assert.True(unavailable.IsValid);
        Assert.True(unavailable.UsedAuthorizedDefault);
        Assert.Equal(defaultWorkflow.Id, unavailable.SelectedWorkflow!.Definition.Id);
        Assert.Contains(unavailable.Reasons, issue =>
            issue.Code == WorkflowValidationCode.WorkflowSelectionNotAuthorized);
        Assert.True(malformed.IsValid);
        Assert.True(malformed.UsedAuthorizedDefault);
        Assert.Contains(malformed.Reasons, issue =>
            issue.Code == WorkflowValidationCode.InvalidWorkflowSelection);
    }

    [Fact]
    public void WorkPlanRevisionReturnsVisibleDiffAndRequiresCurrentRequestIdConfirmation()
    {
        var state = ConfirmedPlanState();
        var revised = WorkflowTestData.Plan(
            WorkflowTestData.Item("first"),
            WorkflowTestData.Item("second"));

        var revision = CoordinatorDecisionFlow.ReviseWorkPlan(
            state,
            revised,
            WorkflowTestData.SelectionContext(),
            "scope-confirmation-1",
            Owner);

        Assert.True(revision.IsSuccess);
        Assert.True(revision.Value!.RequiresConfirmation);
        Assert.Equal(["second"], revision.Value.ScopeDiff.AddedWorkItemIds.ToArray());
        Assert.Equal("scope-confirmation-1", revision.State!.PendingGate!.RequestId);
        Assert.False(revision.State.CanDispatch);

        var stale = revision.State.ApplyGateAnswer(Answer(
            "scope-confirmation-old",
            Owner,
            revision.State.Fence,
            CoordinatorGateChoices.Approve));
        Assert.Equal(CoordinatorGateAnswerStatus.Rejected, stale.Status);
        Assert.False(stale.AuthorizesTransition);
        Assert.Equal(WorkflowValidationCode.StaleDecisionRequest, Assert.Single(stale.Issues).Code);
        Assert.False(stale.State.CanDispatch);

        var answer = Answer(
            revision.State.PendingGate.RequestId,
            Owner,
            revision.State.Fence,
            CoordinatorGateChoices.Approve);
        var accepted = revision.State.ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.Accepted, accepted.Status);
        Assert.True(accepted.AuthorizesTransition);
        Assert.True(accepted.State.CanDispatch);
        Assert.Equal(["first", "second"], accepted.State.ConfirmedWorkPlan!.Plan.Items
            .Select(item => item.Id).ToArray());

        var retry = accepted.State.ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.AlreadyApplied, retry.Status);
        Assert.False(retry.AuthorizesTransition);
        Assert.True(retry.State.CanDispatch);
    }

    [Fact]
    public void QuestionAnswersNeedCurrentActorAndFenceAndNeverAuthorizeAProtectedTransition()
    {
        var asked = CoordinatorDecisionFlow.AskQuestion(
            CoordinatorDecisionState.Create(10),
            "question-request-1",
            "scope-question",
            "Which repository should be changed?",
            Owner,
            ["repository-a", "repository-b"],
            allowsFreeform: false);
        Assert.True(asked.IsSuccess);

        var freeform = asked.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            "question-request-1", Owner, 10, null, "repository-c"));
        Assert.Equal(CoordinatorGateAnswerStatus.Rejected, freeform.Status);
        Assert.Contains(freeform.Issues, issue =>
            issue.Code == WorkflowValidationCode.FreeformDecisionNotAllowed);

        var wrongActor = asked.State.ApplyGateAnswer(Answer(
            "question-request-1",
            "different-actor",
            10,
            "repository-a"));
        Assert.Equal(CoordinatorGateAnswerStatus.Rejected, wrongActor.Status);
        Assert.Contains(wrongActor.Issues, issue =>
            issue.Code == WorkflowValidationCode.UnauthorizedDecisionActor);

        var answer = Answer("question-request-1", Owner, 10, "repository-a");
        var accepted = asked.State.ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.Accepted, accepted.Status);
        Assert.False(accepted.AuthorizesTransition);

        var duplicate = accepted.State.ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.AlreadyApplied, duplicate.Status);
        Assert.False(duplicate.AuthorizesTransition);

        var fenced = accepted.State.AdvanceFence(11).ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.Rejected, fenced.Status);
        Assert.False(fenced.AuthorizesTransition);
        Assert.Contains(fenced.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidDecisionFence);
    }

    [Fact]
    public void PendingGateStateCanBeRevalidatedOnRestoreAndRequestIdsCannotBeReused()
    {
        var proposed = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(5),
            Outcome(),
            "durable-outcome-request",
            Owner);
        Assert.True(proposed.IsSuccess);
        var pending = proposed.State!;

        var restored = CoordinatorDecisionState.Restore(
            pending.Fence,
            pending.OutcomeSpec,
            pending.OutcomeConfirmed,
            pending.NextClarifyingQuestionIndex,
            pending.SelectedWorkflow,
            pending.WorkflowConfirmed,
            pending.ConfirmedWorkPlan,
            pending.CandidateWorkPlan,
            pending.LastScopeDiff,
            pending.PendingGate,
            pending.DecisionReceipts);

        Assert.True(restored.IsValid);
        Assert.Equal("durable-outcome-request", restored.Value!.PendingGate!.RequestId);
        var accepted = restored.Value.ApplyGateAnswer(Answer(
            restored.Value.PendingGate.RequestId,
            Owner,
            5,
            CoordinatorGateChoices.Approve));
        Assert.True(accepted.AuthorizesTransition);

        var reusedId = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            accepted.State,
            Outcome(),
            "durable-outcome-request",
            Owner);
        Assert.False(reusedId.IsSuccess);
        Assert.Contains(reusedId.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidDecisionRequestId);

        var invalidRestore = CoordinatorDecisionState.Restore(
            6,
            pending.OutcomeSpec,
            pending.OutcomeConfirmed,
            pending.NextClarifyingQuestionIndex,
            pending.SelectedWorkflow,
            pending.WorkflowConfirmed,
            pending.ConfirmedWorkPlan,
            pending.CandidateWorkPlan,
            pending.LastScopeDiff,
            pending.PendingGate,
            pending.DecisionReceipts);
        Assert.False(invalidRestore.IsValid);
        Assert.Contains(invalidRestore.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidDecisionFence);
    }

    [Fact]
    public void OutcomeClarificationUsesQuestionGateBeforeOutcomeConfirmation()
    {
        var state = CoordinatorDecisionState.Create(1);
        var proposal = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            state,
            Outcome() with { ClarifyingQuestions = ["Which target environment?"] },
            "outcome-question-1",
            Owner);

        Assert.True(proposal.IsSuccess);
        Assert.Equal(CoordinatorGateKind.Question, proposal.State!.PendingGate!.Kind);
        Assert.False(proposal.State.OutcomeConfirmed);
        Assert.False(proposal.State.CanDecompose);

        var answer = proposal.State.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposal.State.PendingGate.RequestId,
            Owner,
            proposal.State.Fence,
            null,
            "the staging environment"));
        Assert.Equal(CoordinatorGateAnswerStatus.Accepted, answer.Status);
        Assert.False(answer.AuthorizesTransition);
        Assert.False(answer.State.OutcomeConfirmed);

        var revised = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            answer.State,
            Outcome() with { Assumptions = "Target is staging.", ClarifyingQuestions = [] },
            "outcome-confirmation-2",
            Owner);
        Assert.True(revised.IsSuccess);
        Assert.Equal(CoordinatorGateKind.OutcomeConfirmation, revised.State!.PendingGate!.Kind);
    }

    [Fact]
    public void EveryMaterialOutcomeQuestionMustBeAnsweredBeforeTheDraftCanBeReplacedOrConfirmed()
    {
        var proposed = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(2),
            Outcome() with
            {
                ClarifyingQuestions = ["Which target environment?", "Which deployment region?"]
            },
            "clarification-request-1",
            Owner);
        Assert.True(proposed.IsSuccess);
        Assert.Equal("Which target environment?", proposed.State!.PendingGate!.Prompt);

        var firstAnswer = proposed.State.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposed.State.PendingGate.RequestId,
            Owner,
            2,
            null,
            "staging"));
        Assert.Equal(1, firstAnswer.State.NextClarifyingQuestionIndex);
        Assert.False(firstAnswer.State.OutcomeConfirmed);

        var skipped = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            firstAnswer.State,
            Outcome(),
            "outcome-confirmation-skipped",
            Owner);
        Assert.False(skipped.IsSuccess);
        Assert.Contains(skipped.Issues, issue =>
            issue.Code == WorkflowValidationCode.CoordinatorTransitionBlocked);

        var secondQuestion = CoordinatorDecisionFlow.AskNextOutcomeClarifyingQuestion(
            firstAnswer.State,
            "clarification-request-2",
            Owner);
        Assert.True(secondQuestion.IsSuccess);
        Assert.Equal("Which deployment region?", secondQuestion.Value!.Prompt);
        var secondAnswer = secondQuestion.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            secondQuestion.State.PendingGate!.RequestId,
            Owner,
            2,
            null,
            "west"));
        Assert.Equal(2, secondAnswer.State.NextClarifyingQuestionIndex);
        Assert.False(secondAnswer.AuthorizesTransition);

        var revised = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            secondAnswer.State,
            Outcome() with { Assumptions = "Target is staging in west." },
            "outcome-confirmation-final",
            Owner);
        Assert.True(revised.IsSuccess);
        Assert.Equal(CoordinatorGateKind.OutcomeConfirmation, revised.State!.PendingGate!.Kind);
    }

    [Fact]
    public void AssemblyRequestMustMatchAConfirmedCatalogPlatformGate()
    {
        var state = ConfirmedPlanState();
        var request = new CoordinatorAssemblyRequest(
            "assembly-request-1",
            state.SelectedWorkflow!.Definition.Id,
            state.SelectedWorkflow.Definition.Revision,
            state.ConfirmedWorkPlan!.Plan.Id,
            "review",
            WorkflowPlatformGate.IndependentReview);

        var valid = CoordinatorDecisionFlow.RequestAssembly(state, request);
        var wrongGate = CoordinatorDecisionFlow.RequestAssembly(
            state,
            request with { Gate = WorkflowPlatformGate.BuildTest });
        var wrongPlan = CoordinatorDecisionFlow.RequestAssembly(
            state,
            request with { WorkPlanId = "another-plan" });

        Assert.True(valid.IsSuccess);
        Assert.Equal(WorkflowStepMode.Platform, valid.Value!.Step.Mode);
        Assert.False(wrongGate.IsSuccess);
        Assert.Contains(wrongGate.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidAssemblyRequest);
        Assert.False(wrongPlan.IsSuccess);
        Assert.Contains(wrongPlan.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidAssemblyRequest);
    }

    private static CoordinatorOutcomeSpecification Outcome(
        ImmutableArray<string> clarifyingQuestions = default) =>
        new(
            "outcome-1",
            "Implement the requested capability.",
            "A validated implementation is available.",
            "Only the requested workflow behavior changes.",
            "Existing provider pins remain authoritative.",
            clarifyingQuestions.IsDefault ? [] : clarifyingQuestions);

    private static AuthorizedWorkflowCatalog Catalog(
        WorkflowDefinition defaultWorkflow,
        params WorkflowDefinition[] available) =>
        new(defaultWorkflow, [.. available]);

    private static CoordinatorDecisionState ConfirmedOutcomeState()
    {
        var proposed = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(3),
            Outcome(),
            "outcome-confirmation-1",
            Owner);
        Assert.True(proposed.IsSuccess);
        return proposed.State!.ApplyGateAnswer(Answer(
            proposed.State.PendingGate!.RequestId,
            Owner,
            proposed.State.Fence,
            CoordinatorGateChoices.Approve)).State;
    }

    private static CoordinatorDecisionState ConfirmedPlanState()
    {
        var outcome = ConfirmedOutcomeState();
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            outcome,
            Catalog(WorkflowTestData.Definition()),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        Assert.True(selected.IsSuccess);

        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext(),
            "plan-confirmation-1",
            Owner);
        Assert.True(proposed.IsSuccess);
        return proposed.State!.ApplyGateAnswer(Answer(
            proposed.State.PendingGate!.RequestId,
            Owner,
            proposed.State.Fence,
            CoordinatorGateChoices.Approve)).State;
    }

    private static CoordinatorGateAnswer Answer(
        string requestId,
        string actorId,
        long fence,
        string choice) =>
        new(requestId, actorId, fence, choice, null);
}
