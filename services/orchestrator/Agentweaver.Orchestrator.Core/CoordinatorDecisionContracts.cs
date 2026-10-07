using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public sealed record CoordinatorOutcomeSpecification(
    string Id,
    string Goal,
    string DesiredOutcome,
    string Scope,
    string Assumptions,
    ImmutableArray<string> ClarifyingQuestions);

public sealed class CoordinatorOutcomeSpecSnapshot
{
    internal CoordinatorOutcomeSpecSnapshot(CoordinatorOutcomeSpecification specification) =>
        Specification = specification;

    public CoordinatorOutcomeSpecification Specification { get; }
}

public static class CoordinatorOutcomeSpecValidator
{
    public static WorkflowValidationResult<CoordinatorOutcomeSpecSnapshot> ValidateAndSnapshot(
        CoordinatorOutcomeSpecification? specification)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (specification is null)
        {
            Add(issues, "outcome", "An outcome specification is required.");
            return WorkflowValidationResult<CoordinatorOutcomeSpecSnapshot>.Failure(issues.ToImmutable());
        }

        if (!WorkflowValidationSupport.IsStableId(specification.Id))
            Add(issues, "outcome.id", "Outcome specification ID must be a stable identifier.");
        ValidateText(specification.Goal, "outcome.goal", required: true, issues);
        ValidateText(specification.DesiredOutcome, "outcome.desiredOutcome", required: true, issues);
        ValidateText(specification.Scope, "outcome.scope", required: true, issues);
        ValidateText(specification.Assumptions, "outcome.assumptions", required: true, issues);

        if (specification.ClarifyingQuestions.IsDefault ||
            specification.ClarifyingQuestions.Length > WorkflowDomainLimits.MaximumOutcomeQuestions)
        {
            Add(issues, "outcome.clarifyingQuestions",
                $"Clarifying questions must be initialized and contain at most {WorkflowDomainLimits.MaximumOutcomeQuestions} entries.");
        }
        else
        {
            for (var index = 0; index < specification.ClarifyingQuestions.Length; index++)
                ValidateText(
                    specification.ClarifyingQuestions[index],
                    $"outcome.clarifyingQuestions[{index}]",
                    required: true,
                    issues);
        }

        if (issues.Count > 0)
            return WorkflowValidationResult<CoordinatorOutcomeSpecSnapshot>.Failure(issues.ToImmutable());

        return WorkflowValidationResult<CoordinatorOutcomeSpecSnapshot>.Success(
            new CoordinatorOutcomeSpecSnapshot(specification));
    }

    private static void ValidateText(
        string? value,
        string path,
        bool required,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) ||
            value is not null && value.Length > WorkflowDomainLimits.MaximumWorkTextLength)
            Add(issues, path,
                $"Value must be non-empty and no longer than {WorkflowDomainLimits.MaximumWorkTextLength} characters.");
    }

    private static void Add(
        ImmutableArray<WorkflowValidationIssue>.Builder issues,
        string path,
        string message) =>
        issues.Add(new WorkflowValidationIssue(
            WorkflowValidationCode.InvalidOutcomeSpecification,
            path,
            message));
}

/// <summary>
/// Project-authorized definitions for a run. The default is the actual authorized project default,
/// not a synthesized fallback; callers must apply project authorization before constructing this catalog.
/// </summary>
public sealed record AuthorizedWorkflowCatalog(
    WorkflowDefinition DefaultWorkflow,
    ImmutableArray<WorkflowDefinition> AvailableWorkflows);

public sealed record WorkflowSelectionResolution(
    WorkflowDefinitionSnapshot? SelectedWorkflow,
    bool UsedAuthorizedDefault,
    ImmutableArray<WorkflowValidationIssue> Reasons)
{
    public bool IsValid => SelectedWorkflow is not null;
}

public static class WorkflowSelectionResolver
{
    public static WorkflowSelectionResolution Resolve(
        AuthorizedWorkflowCatalog? catalog,
        string? requestedWorkflowId)
    {
        if (catalog?.DefaultWorkflow is null)
            return Failure(
                WorkflowValidationCode.InvalidWorkflowSelection,
                "workflowCatalog.defaultWorkflow",
                "The authorized project default workflow is required.");

        var defaultResult = WorkflowDefinitionValidator.ValidateAndSnapshot(catalog.DefaultWorkflow);
        if (!defaultResult.IsValid)
            return new WorkflowSelectionResolution(null, false, defaultResult.Issues);

        var authorizedDefault = defaultResult.Value!;
        if (requestedWorkflowId is null ||
            string.Equals(requestedWorkflowId, catalog.DefaultWorkflow.Id, StringComparison.Ordinal))
            return new WorkflowSelectionResolution(authorizedDefault, true, []);

        if (!WorkflowValidationSupport.IsStableId(requestedWorkflowId))
            return Fallback(
                authorizedDefault,
                WorkflowValidationCode.InvalidWorkflowSelection,
                "workflowSelection.workflowId",
                "Requested workflow ID must be a stable identifier.");

        if (catalog.AvailableWorkflows.IsDefault)
            return Fallback(
                authorizedDefault,
                WorkflowValidationCode.InvalidWorkflowSelection,
                "workflowCatalog.availableWorkflows",
                "The authorized workflow catalog must be initialized.");

        var matches = catalog.AvailableWorkflows
            .Where(workflow => workflow is not null &&
                               string.Equals(workflow.Id, requestedWorkflowId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            return Fallback(
                authorizedDefault,
                WorkflowValidationCode.WorkflowSelectionNotAuthorized,
                "workflowSelection.workflowId",
                matches.Length == 0
                    ? $"Workflow '{requestedWorkflowId}' is not in the authorized project catalog."
                    : $"Workflow ID '{requestedWorkflowId}' is ambiguous in the authorized project catalog.");

        var selectedResult = WorkflowDefinitionValidator.ValidateAndSnapshot(matches[0]);
        if (!selectedResult.IsValid)
        {
            var reasons = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
            reasons.Add(new WorkflowValidationIssue(
                WorkflowValidationCode.InvalidWorkflowSelection,
                "workflowSelection.workflowId",
                $"Workflow '{requestedWorkflowId}' is invalid; the authorized project default was selected."));
            reasons.AddRange(selectedResult.Issues);
            return new WorkflowSelectionResolution(authorizedDefault, true, reasons.ToImmutable());
        }

        return new WorkflowSelectionResolution(selectedResult.Value!, false, []);
    }

    private static WorkflowSelectionResolution Fallback(
        WorkflowDefinitionSnapshot authorizedDefault,
        WorkflowValidationCode code,
        string path,
        string message) =>
        new(
            authorizedDefault,
            true,
            [new WorkflowValidationIssue(code, path, message)]);

    private static WorkflowSelectionResolution Failure(
        WorkflowValidationCode code,
        string path,
        string message) =>
        new(
            null,
            false,
            [new WorkflowValidationIssue(code, path, message)]);
}

public enum CoordinatorGateKind
{
    OutcomeConfirmation,
    GeneratedWorkflowConfirmation,
    WorkPlanConfirmation,
    ScopeChangeConfirmation,
    Question,
    Approval
}

public static class CoordinatorGateChoices
{
    public const string Approve = "approve";
    public const string Reject = "reject";
}

public sealed record CoordinatorGateRequest(
    string RequestId,
    CoordinatorGateKind Kind,
    string SubjectId,
    string AuthorizedActorId,
    long Fence,
    ImmutableArray<string> AllowedChoices,
    bool AllowsFreeform,
    string? Prompt = null,
    bool ResolvesOutcomeClarification = false);

public sealed record CoordinatorGateAnswer(
    string RequestId,
    string ActorId,
    long Fence,
    string? ChoiceId,
    string? FreeformAnswer);

public sealed record CoordinatorMessageAcknowledgment(
    string RequestId,
    string ActorId,
    long Fence);

public sealed class CoordinatorMessageAcknowledgmentResult
{
    internal CoordinatorMessageAcknowledgmentResult(
        bool acknowledged,
        CoordinatorDecisionState state,
        ImmutableArray<WorkflowValidationIssue> issues) =>
        (IsAcknowledged, State, Issues) = (acknowledged, state, issues);

    public bool IsAcknowledged { get; }
    public CoordinatorDecisionState State { get; }
    public ImmutableArray<WorkflowValidationIssue> Issues { get; }
    public bool AuthorizesTransition => false;
}

public sealed record CoordinatorGateDecisionReceipt(
    string RequestId,
    CoordinatorGateKind Kind,
    string SubjectId,
    string ActorId,
    long Fence,
    string? ChoiceId,
    string? FreeformAnswer);

public enum CoordinatorGateAnswerStatus
{
    Accepted,
    AlreadyApplied,
    Rejected
}

public sealed record CoordinatorGateAnswerResult(
    CoordinatorGateAnswerStatus Status,
    CoordinatorDecisionState State,
    CoordinatorGateDecisionReceipt? Receipt,
    ImmutableArray<WorkflowValidationIssue> Issues,
    bool AuthorizesTransition)
{
    public bool IsAccepted =>
        Status is CoordinatorGateAnswerStatus.Accepted or CoordinatorGateAnswerStatus.AlreadyApplied;
}

public sealed record CoordinatorDecisionTransition<T>(
    CoordinatorDecisionState? State,
    T? Value,
    ImmutableArray<WorkflowValidationIssue> Issues)
    where T : class
{
    public bool IsSuccess => State is not null && Value is not null;
}

public sealed record CoordinatorWorkflowSelection(
    WorkflowDefinitionSnapshot Workflow,
    bool UsedAuthorizedDefault,
    ImmutableArray<WorkflowValidationIssue> Reasons);

public sealed record CoordinatorWorkPlanRevision(
    WorkPlanSnapshot Candidate,
    WorkPlanScopeDiff ScopeDiff,
    bool RequiresConfirmation);

public sealed record CoordinatorAssemblyRequest(
    string RequestId,
    string WorkflowId,
    string DefinitionRevision,
    string WorkPlanId,
    string WorkflowStepId,
    WorkflowPlatformGate Gate);

public sealed class CoordinatorAssemblyRequestSnapshot
{
    internal CoordinatorAssemblyRequestSnapshot(
        CoordinatorAssemblyRequest request,
        WorkflowDefinitionSnapshot workflow,
        WorkPlanSnapshot workPlan,
        WorkflowStepDefinition step) =>
        (Request, Workflow, WorkPlan, Step) = (request, workflow, workPlan, step);

    public CoordinatorAssemblyRequest Request { get; }
    public WorkflowDefinitionSnapshot Workflow { get; }
    public WorkPlanSnapshot WorkPlan { get; }
    public WorkflowStepDefinition Step { get; }
}

public sealed class CoordinatorDecisionState
{
    internal CoordinatorDecisionState(
        long fence,
        CoordinatorOutcomeSpecSnapshot? outcomeSpec,
        bool outcomeConfirmed,
        int nextClarifyingQuestionIndex,
        WorkflowDefinitionSnapshot? selectedWorkflow,
        bool workflowConfirmed,
        WorkPlanSnapshot? confirmedWorkPlan,
        WorkPlanSnapshot? candidateWorkPlan,
        WorkPlanScopeDiff? lastScopeDiff,
        CoordinatorGateRequest? pendingGate,
        ImmutableArray<CoordinatorGateDecisionReceipt> decisionReceipts) =>
        (Fence, OutcomeSpec, OutcomeConfirmed, NextClarifyingQuestionIndex, SelectedWorkflow, WorkflowConfirmed,
            ConfirmedWorkPlan, CandidateWorkPlan, LastScopeDiff, PendingGate, DecisionReceipts) =
        (fence, outcomeSpec, outcomeConfirmed, nextClarifyingQuestionIndex, selectedWorkflow, workflowConfirmed,
            confirmedWorkPlan, candidateWorkPlan, lastScopeDiff, pendingGate, decisionReceipts);

    public long Fence { get; }
    public CoordinatorOutcomeSpecSnapshot? OutcomeSpec { get; }
    public bool OutcomeConfirmed { get; }
    public int NextClarifyingQuestionIndex { get; }
    public WorkflowDefinitionSnapshot? SelectedWorkflow { get; }
    public bool WorkflowConfirmed { get; }
    public WorkPlanSnapshot? ConfirmedWorkPlan { get; }
    public WorkPlanSnapshot? CandidateWorkPlan { get; }
    public WorkPlanScopeDiff? LastScopeDiff { get; }
    public CoordinatorGateRequest? PendingGate { get; }
    public ImmutableArray<CoordinatorGateDecisionReceipt> DecisionReceipts { get; }

    public bool CanDecompose =>
        OutcomeConfirmed && OutcomeSpec is not null &&
        WorkflowConfirmed && SelectedWorkflow is not null &&
        PendingGate is null;

    public bool CanDispatch =>
        CanDecompose && ConfirmedWorkPlan is not null && CandidateWorkPlan is null;

    public static CoordinatorDecisionState Create(long fence)
    {
        if (fence <= 0)
            throw new ArgumentOutOfRangeException(nameof(fence), "Fence must be positive.");

        return new CoordinatorDecisionState(
            fence, null, false, 0, null, false, null, null, null, null, []);
    }

    public static WorkflowValidationResult<CoordinatorDecisionState> Restore(
        long fence,
        CoordinatorOutcomeSpecSnapshot? outcomeSpec,
        bool outcomeConfirmed,
        int nextClarifyingQuestionIndex,
        WorkflowDefinitionSnapshot? selectedWorkflow,
        bool workflowConfirmed,
        WorkPlanSnapshot? confirmedWorkPlan,
        WorkPlanSnapshot? candidateWorkPlan,
        WorkPlanScopeDiff? lastScopeDiff,
        CoordinatorGateRequest? pendingGate,
        ImmutableArray<CoordinatorGateDecisionReceipt> decisionReceipts)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (fence <= 0)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionFence,
                "decisionState.fence", "Fence must be positive."));
        if (outcomeConfirmed && outcomeSpec is null)
            issues.Add(Issue(WorkflowValidationCode.OutcomeConfirmationRequired,
                "decisionState.outcome", "A confirmed outcome specification is required."));
        var questionCount = outcomeSpec?.Specification.ClarifyingQuestions.Length ?? 0;
        if (nextClarifyingQuestionIndex < 0 || nextClarifyingQuestionIndex > questionCount)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "decisionState.nextClarifyingQuestionIndex",
                "Clarifying-question progress must be within the outcome specification."));
        if (outcomeConfirmed && nextClarifyingQuestionIndex != questionCount)
            issues.Add(Issue(WorkflowValidationCode.OutcomeConfirmationRequired,
                "decisionState.outcome",
                "Outcome confirmation cannot precede answers to all clarifying questions."));
        if (selectedWorkflow is not null &&
            (outcomeSpec is null || !outcomeConfirmed))
            issues.Add(Issue(WorkflowValidationCode.OutcomeConfirmationRequired,
                "decisionState.workflow", "Workflow selection cannot precede outcome confirmation."));
        if (workflowConfirmed && selectedWorkflow is null)
            issues.Add(Issue(WorkflowValidationCode.WorkflowConfirmationRequired,
                "decisionState.workflow", "A confirmed workflow definition is required."));

        if ((confirmedWorkPlan is not null || candidateWorkPlan is not null) &&
            (selectedWorkflow is null || !workflowConfirmed))
            issues.Add(Issue(WorkflowValidationCode.WorkflowConfirmationRequired,
                "decisionState.workPlan", "Work plans require a confirmed workflow definition."));

        if (confirmedWorkPlan is not null &&
            !MatchesWorkflow(confirmedWorkPlan, selectedWorkflow))
            issues.Add(Issue(WorkflowValidationCode.WorkPlanDefinitionMismatch,
                "decisionState.confirmedWorkPlan", "Confirmed work plan does not match the selected workflow."));
        if (candidateWorkPlan is not null &&
            !MatchesWorkflow(candidateWorkPlan, selectedWorkflow))
            issues.Add(Issue(WorkflowValidationCode.WorkPlanDefinitionMismatch,
                "decisionState.candidateWorkPlan", "Candidate work plan does not match the selected workflow."));

        if (decisionReceipts.IsDefault ||
            decisionReceipts.Length > WorkflowDomainLimits.MaximumDecisionReceipts)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "decisionState.decisionReceipts", "Decision receipts must be initialized and bounded."));
        else
        {
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < decisionReceipts.Length; index++)
            {
                var receipt = decisionReceipts[index];
                var hasChoice = !string.IsNullOrWhiteSpace(receipt?.ChoiceId);
                var hasFreeform = !string.IsNullOrWhiteSpace(receipt?.FreeformAnswer);
                if (receipt is null ||
                    !WorkflowValidationSupport.IsStableId(receipt.RequestId) ||
                    !Enum.IsDefined(receipt.Kind) ||
                    !WorkflowValidationSupport.IsStableId(receipt.SubjectId) ||
                    !WorkflowValidationSupport.IsStableId(receipt.ActorId) ||
                    receipt.Fence <= 0 ||
                    receipt.Fence > fence ||
                    !requestIds.Add(receipt.RequestId) ||
                    hasChoice == hasFreeform ||
                    hasChoice && !WorkflowValidationSupport.IsStableId(receipt.ChoiceId) ||
                    hasFreeform && receipt.FreeformAnswer!.Length > WorkflowDomainLimits.MaximumWorkTextLength ||
                    receipt.Kind != CoordinatorGateKind.Question &&
                    (!hasChoice ||
                     receipt.ChoiceId is not (CoordinatorGateChoices.Approve or CoordinatorGateChoices.Reject)))
                    issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                        $"decisionState.decisionReceipts[{index}]",
                        "Decision receipt is malformed or duplicated."));
            }
        }

        if (pendingGate is not null)
        {
            var gateIssues = CoordinatorDecisionFlow.ValidateGate(pendingGate);
            issues.AddRange(gateIssues);
            if (pendingGate.Fence != fence)
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionFence,
                    "decisionState.pendingGate.fence", "Pending gate must use the current run fence."));
            if (!decisionReceipts.IsDefault && decisionReceipts.Any(receipt =>
                    receipt is not null &&
                    string.Equals(receipt.RequestId, pendingGate.RequestId, StringComparison.Ordinal)))
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionRequestId,
                    "decisionState.pendingGate.requestId",
                    "A pending gate request ID cannot already have a decision receipt."));
            if (!MatchesPendingGate(
                    pendingGate,
                    outcomeSpec,
                    outcomeConfirmed,
                    nextClarifyingQuestionIndex,
                    selectedWorkflow,
                    workflowConfirmed,
                    confirmedWorkPlan,
                    candidateWorkPlan))
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                    "decisionState.pendingGate",
                    "Pending gate does not match the current outcome, workflow, and work-plan state."));
        }
        else if (candidateWorkPlan is not null)
        {
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "decisionState.candidateWorkPlan",
                "A candidate work plan must remain behind its pending confirmation gate."));
        }

        if (issues.Count > 0)
            return WorkflowValidationResult<CoordinatorDecisionState>.Failure(issues.ToImmutable());

        return WorkflowValidationResult<CoordinatorDecisionState>.Success(
            new CoordinatorDecisionState(
                fence,
                outcomeSpec,
                outcomeConfirmed,
                nextClarifyingQuestionIndex,
                selectedWorkflow,
                workflowConfirmed,
                confirmedWorkPlan,
                candidateWorkPlan,
                lastScopeDiff,
                pendingGate,
                decisionReceipts));
    }

    public CoordinatorDecisionState AdvanceFence(long fence)
    {
        if (fence <= Fence)
            throw new ArgumentOutOfRangeException(nameof(fence), "A new fence must be greater than the current fence.");

        return Copy(
            fence: fence,
            candidateWorkPlan: null,
            setCandidateWorkPlan: true,
            pendingGate: null,
            clearPendingGate: true);
    }

    public CoordinatorGateAnswerResult ApplyGateAnswer(CoordinatorGateAnswer? answer)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (answer is null)
            return Rejected(this, Issue(WorkflowValidationCode.InvalidDecisionAnswer,
                "gateAnswer", "A typed gate answer is required."));

        if (answer.Fence != Fence)
            return Rejected(this, Issue(WorkflowValidationCode.InvalidDecisionFence,
                "gateAnswer.fence", "The answer fence does not match the current run fence."));

        var previous = DecisionReceipts.FirstOrDefault(receipt =>
            string.Equals(receipt.RequestId, answer.RequestId, StringComparison.Ordinal));
        if (previous is not null)
        {
            if (SameAnswer(previous, answer))
                return new CoordinatorGateAnswerResult(
                    CoordinatorGateAnswerStatus.AlreadyApplied,
                    this,
                    previous,
                    [],
                    AuthorizesTransition: false);

            return Rejected(this, Issue(WorkflowValidationCode.StaleDecisionRequest,
                "gateAnswer.requestId", "The request ID was already answered with a different decision."));
        }

        var gate = PendingGate;
        if (gate is null ||
            !string.Equals(gate.RequestId, answer.RequestId, StringComparison.Ordinal))
            return Rejected(this, Issue(WorkflowValidationCode.StaleDecisionRequest,
                "gateAnswer.requestId", "The answer does not match the current pending request ID."));

        if (!WorkflowValidationSupport.IsStableId(answer.ActorId) ||
            !string.Equals(answer.ActorId, gate.AuthorizedActorId, StringComparison.Ordinal))
            return Rejected(this, Issue(WorkflowValidationCode.UnauthorizedDecisionActor,
                "gateAnswer.actorId", "The answer actor is not authorized for this pending request."));

        if (!TryValidateAnswerValue(gate, answer, issues))
            return new CoordinatorGateAnswerResult(
                CoordinatorGateAnswerStatus.Rejected,
                this,
                null,
                issues.ToImmutable(),
                AuthorizesTransition: false);

        var receipt = new CoordinatorGateDecisionReceipt(
            gate.RequestId,
            gate.Kind,
            gate.SubjectId,
            answer.ActorId,
            answer.Fence,
            answer.ChoiceId,
            answer.FreeformAnswer);
        var next = ApplyAcceptedDecision(gate, receipt);
        var authorizes = gate.Kind != CoordinatorGateKind.Question &&
                         string.Equals(answer.ChoiceId, CoordinatorGateChoices.Approve, StringComparison.Ordinal);
        return new CoordinatorGateAnswerResult(
            CoordinatorGateAnswerStatus.Accepted,
            next,
            receipt,
            [],
            authorizes);
    }

    public CoordinatorMessageAcknowledgmentResult AcknowledgeMessage(
        CoordinatorMessageAcknowledgment? acknowledgment)
    {
        if (acknowledgment is null)
            return AcknowledgmentRejected(WorkflowValidationCode.InvalidDecisionAnswer,
                "messageAcknowledgment", "A typed message acknowledgment is required.");
        if (PendingGate is null ||
            !string.Equals(acknowledgment.RequestId, PendingGate.RequestId, StringComparison.Ordinal))
            return AcknowledgmentRejected(WorkflowValidationCode.StaleDecisionRequest,
                "messageAcknowledgment.requestId",
                "The acknowledgment does not match the current pending request ID.");
        if (acknowledgment.Fence != Fence || acknowledgment.Fence != PendingGate.Fence)
            return AcknowledgmentRejected(WorkflowValidationCode.InvalidDecisionFence,
                "messageAcknowledgment.fence",
                "The acknowledgment fence does not match the current run fence.");
        if (!WorkflowValidationSupport.IsStableId(acknowledgment.ActorId) ||
            !string.Equals(acknowledgment.ActorId, PendingGate.AuthorizedActorId, StringComparison.Ordinal))
            return AcknowledgmentRejected(WorkflowValidationCode.UnauthorizedDecisionActor,
                "messageAcknowledgment.actorId",
                "The acknowledgment actor is not authorized for this pending request.");

        return new CoordinatorMessageAcknowledgmentResult(true, this, []);
    }

    private CoordinatorMessageAcknowledgmentResult AcknowledgmentRejected(
        WorkflowValidationCode code,
        string path,
        string message) =>
        new(false, this, [Issue(code, path, message)]);

    internal CoordinatorDecisionState Copy(
        long? fence = null,
        CoordinatorOutcomeSpecSnapshot? outcomeSpec = null,
        bool setOutcomeSpec = false,
        bool? outcomeConfirmed = null,
        int? nextClarifyingQuestionIndex = null,
        WorkflowDefinitionSnapshot? selectedWorkflow = null,
        bool setSelectedWorkflow = false,
        bool? workflowConfirmed = null,
        WorkPlanSnapshot? confirmedWorkPlan = null,
        bool setConfirmedWorkPlan = false,
        WorkPlanSnapshot? candidateWorkPlan = null,
        bool setCandidateWorkPlan = false,
        WorkPlanScopeDiff? lastScopeDiff = null,
        bool setLastScopeDiff = false,
        CoordinatorGateRequest? pendingGate = null,
        bool clearPendingGate = false,
        ImmutableArray<CoordinatorGateDecisionReceipt>? decisionReceipts = null) =>
        new(
            fence ?? Fence,
            setOutcomeSpec ? outcomeSpec : OutcomeSpec,
            outcomeConfirmed ?? OutcomeConfirmed,
            nextClarifyingQuestionIndex ?? NextClarifyingQuestionIndex,
            setSelectedWorkflow ? selectedWorkflow : SelectedWorkflow,
            workflowConfirmed ?? WorkflowConfirmed,
            setConfirmedWorkPlan ? confirmedWorkPlan : ConfirmedWorkPlan,
            setCandidateWorkPlan ? candidateWorkPlan : CandidateWorkPlan,
            setLastScopeDiff ? lastScopeDiff : LastScopeDiff,
            clearPendingGate ? null : pendingGate ?? PendingGate,
            decisionReceipts ?? DecisionReceipts);

    private CoordinatorDecisionState ApplyAcceptedDecision(
        CoordinatorGateRequest gate,
        CoordinatorGateDecisionReceipt receipt)
    {
        var receipts = DecisionReceipts.Add(receipt);
        if (receipts.Length > WorkflowDomainLimits.MaximumDecisionReceipts)
            receipts = receipts.RemoveAt(0);

        var next = Copy(
            clearPendingGate: true,
            decisionReceipts: receipts);
        var approved = string.Equals(receipt.ChoiceId, CoordinatorGateChoices.Approve, StringComparison.Ordinal);
        switch (gate.Kind)
        {
            case CoordinatorGateKind.OutcomeConfirmation:
                return next.Copy(outcomeConfirmed: approved);
            case CoordinatorGateKind.GeneratedWorkflowConfirmation:
                return next.Copy(workflowConfirmed: approved);
            case CoordinatorGateKind.WorkPlanConfirmation:
                return approved
                    ? next.Copy(
                        confirmedWorkPlan: CandidateWorkPlan,
                        setConfirmedWorkPlan: true,
                        candidateWorkPlan: null,
                        setCandidateWorkPlan: true)
                    : next.Copy(candidateWorkPlan: null, setCandidateWorkPlan: true);
            case CoordinatorGateKind.ScopeChangeConfirmation:
                return approved
                    ? next.Copy(
                        confirmedWorkPlan: CandidateWorkPlan,
                        setConfirmedWorkPlan: true,
                        candidateWorkPlan: null,
                        setCandidateWorkPlan: true)
                    : next.Copy(candidateWorkPlan: null, setCandidateWorkPlan: true);
            case CoordinatorGateKind.Question:
                return gate.ResolvesOutcomeClarification
                    ? next.Copy(nextClarifyingQuestionIndex: NextClarifyingQuestionIndex + 1)
                    : next;
            case CoordinatorGateKind.Approval:
                return next;
            default:
                return next;
        }
    }

    private static bool TryValidateAnswerValue(
        CoordinatorGateRequest gate,
        CoordinatorGateAnswer answer,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var hasChoice = !string.IsNullOrWhiteSpace(answer.ChoiceId);
        var hasFreeform = !string.IsNullOrWhiteSpace(answer.FreeformAnswer);
        if (hasChoice == hasFreeform)
        {
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionAnswer,
                "gateAnswer", "Supply exactly one allowed choice or free-form answer."));
            return false;
        }

        if (hasChoice)
        {
            if (!WorkflowValidationSupport.IsStableId(answer.ChoiceId) ||
                gate.AllowedChoices.IsDefault ||
                !gate.AllowedChoices.Contains(answer.ChoiceId!, StringComparer.Ordinal))
            {
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionAnswer,
                    "gateAnswer.choiceId", "The choice is not allowed by the current request."));
                return false;
            }
            return true;
        }

        if (!gate.AllowsFreeform)
        {
            issues.Add(Issue(WorkflowValidationCode.FreeformDecisionNotAllowed,
                "gateAnswer.freeformAnswer", "The current request does not allow a free-form answer."));
            return false;
        }

        if (answer.FreeformAnswer!.Length > WorkflowDomainLimits.MaximumWorkTextLength)
        {
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionAnswer,
                "gateAnswer.freeformAnswer",
                $"Free-form answers must not exceed {WorkflowDomainLimits.MaximumWorkTextLength} characters."));
            return false;
        }

        return true;
    }

    private static bool SameAnswer(
        CoordinatorGateDecisionReceipt receipt,
        CoordinatorGateAnswer answer) =>
        receipt.Fence == answer.Fence &&
        string.Equals(receipt.ActorId, answer.ActorId, StringComparison.Ordinal) &&
        string.Equals(receipt.ChoiceId, answer.ChoiceId, StringComparison.Ordinal) &&
        string.Equals(receipt.FreeformAnswer, answer.FreeformAnswer, StringComparison.Ordinal);

    private static CoordinatorGateAnswerResult Rejected(
        CoordinatorDecisionState state,
        WorkflowValidationIssue issue) =>
        new(
            CoordinatorGateAnswerStatus.Rejected,
            state,
            null,
            [issue],
            AuthorizesTransition: false);

    private static WorkflowValidationIssue Issue(
        WorkflowValidationCode code,
        string path,
        string message) =>
        new(code, path, message);

    private static bool MatchesWorkflow(
        WorkPlanSnapshot workPlan,
        WorkflowDefinitionSnapshot? workflow) =>
        workflow is not null &&
        string.Equals(workPlan.Workflow.Definition.Id, workflow.Definition.Id, StringComparison.Ordinal) &&
        string.Equals(workPlan.Workflow.Definition.Revision, workflow.Definition.Revision, StringComparison.Ordinal) &&
        string.Equals(workPlan.Workflow.Definition.CatalogVersion, workflow.Definition.CatalogVersion, StringComparison.Ordinal);

    private static bool MatchesPendingGate(
        CoordinatorGateRequest gate,
        CoordinatorOutcomeSpecSnapshot? outcomeSpec,
        bool outcomeConfirmed,
        int nextClarifyingQuestionIndex,
        WorkflowDefinitionSnapshot? selectedWorkflow,
        bool workflowConfirmed,
        WorkPlanSnapshot? confirmedWorkPlan,
        WorkPlanSnapshot? candidateWorkPlan) =>
        gate.Kind switch
        {
            CoordinatorGateKind.OutcomeConfirmation =>
                outcomeSpec is not null &&
                !outcomeConfirmed &&
                nextClarifyingQuestionIndex == outcomeSpec.Specification.ClarifyingQuestions.Length &&
                selectedWorkflow is null &&
                confirmedWorkPlan is null &&
                candidateWorkPlan is null &&
                string.Equals(gate.SubjectId, outcomeSpec.Specification.Id, StringComparison.Ordinal),
            CoordinatorGateKind.GeneratedWorkflowConfirmation =>
                selectedWorkflow is not null &&
                selectedWorkflow.RequiresFirstUseConfirmation &&
                !workflowConfirmed &&
                confirmedWorkPlan is null &&
                candidateWorkPlan is null &&
                string.Equals(gate.SubjectId, selectedWorkflow.Definition.Id, StringComparison.Ordinal),
            CoordinatorGateKind.WorkPlanConfirmation =>
                candidateWorkPlan is not null &&
                confirmedWorkPlan is null &&
                string.Equals(gate.SubjectId, candidateWorkPlan.Plan.Id, StringComparison.Ordinal),
            CoordinatorGateKind.ScopeChangeConfirmation =>
                candidateWorkPlan is not null &&
                confirmedWorkPlan is not null &&
                string.Equals(gate.SubjectId, candidateWorkPlan.Plan.Id, StringComparison.Ordinal),
            CoordinatorGateKind.Question when gate.ResolvesOutcomeClarification =>
                outcomeSpec is not null &&
                !outcomeConfirmed &&
                candidateWorkPlan is null &&
                nextClarifyingQuestionIndex < outcomeSpec.Specification.ClarifyingQuestions.Length &&
                string.Equals(gate.SubjectId, outcomeSpec.Specification.Id, StringComparison.Ordinal) &&
                string.Equals(
                    gate.Prompt,
                    outcomeSpec.Specification.ClarifyingQuestions[nextClarifyingQuestionIndex],
                    StringComparison.Ordinal),
            CoordinatorGateKind.Question or CoordinatorGateKind.Approval =>
                candidateWorkPlan is null,
            _ => false
        };
}

public static class CoordinatorDecisionFlow
{
    public static CoordinatorDecisionTransition<CoordinatorOutcomeSpecSnapshot> ProposeOutcomeSpec(
        CoordinatorDecisionState? state,
        CoordinatorOutcomeSpecification? specification,
        string? requestId,
        string? authorizedActorId)
    {
        if (!CanStartTransition(state, "outcome", out var blocked))
            return Failure<CoordinatorOutcomeSpecSnapshot>(blocked);
        if (state!.OutcomeSpec is { } priorOutcome &&
            state.NextClarifyingQuestionIndex < priorOutcome.Specification.ClarifyingQuestions.Length)
            return Failure<CoordinatorOutcomeSpecSnapshot>(Issue(
                WorkflowValidationCode.CoordinatorTransitionBlocked,
                "outcome.clarifyingQuestions",
                "Resolve every pending outcome clarification before replacing the draft."));

        var validated = CoordinatorOutcomeSpecValidator.ValidateAndSnapshot(specification);
        if (!validated.IsValid)
            return Failure<CoordinatorOutcomeSpecSnapshot>(validated.Issues);

        var snapshot = validated.Value!;
        var question = snapshot.Specification.ClarifyingQuestions.FirstOrDefault();
        var kind = question is null
            ? CoordinatorGateKind.OutcomeConfirmation
            : CoordinatorGateKind.Question;
        var gate = CreateGate(
            requestId,
            kind,
            snapshot.Specification.Id,
            authorizedActorId,
            state!.Fence,
            question is null ? [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject] : [],
            allowsFreeform: question is not null,
            prompt: question,
            resolvesOutcomeClarification: question is not null);
        if (gate.Value is null)
            return Failure<CoordinatorOutcomeSpecSnapshot>(gate.Issues);
        if (!HasFreshRequestId(state!, gate.Value, out var requestIssue))
            return Failure<CoordinatorOutcomeSpecSnapshot>(requestIssue);

        var next = state.Copy(
            outcomeSpec: snapshot,
            setOutcomeSpec: true,
            outcomeConfirmed: false,
            nextClarifyingQuestionIndex: 0,
            selectedWorkflow: null,
            setSelectedWorkflow: true,
            workflowConfirmed: false,
            confirmedWorkPlan: null,
            setConfirmedWorkPlan: true,
            candidateWorkPlan: null,
            setCandidateWorkPlan: true,
            lastScopeDiff: null,
            setLastScopeDiff: true,
            pendingGate: gate.Value);
        return Success(next, snapshot);
    }

    public static CoordinatorDecisionTransition<CoordinatorWorkflowSelection> SelectWorkflow(
        CoordinatorDecisionState? state,
        AuthorizedWorkflowCatalog? catalog,
        string? requestedWorkflowId,
        string? firstUseRequestId,
        string? authorizedActorId)
    {
        if (!CanStartTransition(state, "workflowSelection", out var blocked))
            return Failure<CoordinatorWorkflowSelection>(blocked);
        if (state!.OutcomeSpec is null || !state.OutcomeConfirmed)
            return Failure<CoordinatorWorkflowSelection>(Issue(
                WorkflowValidationCode.OutcomeConfirmationRequired,
                "outcome",
                "The outcome specification must be explicitly confirmed before workflow selection."));

        var resolution = WorkflowSelectionResolver.Resolve(catalog, requestedWorkflowId);
        if (!resolution.IsValid)
            return Failure<CoordinatorWorkflowSelection>(resolution.Reasons);

        var selected = resolution.SelectedWorkflow!;
        CoordinatorGateRequest? gate = null;
        if (selected.RequiresFirstUseConfirmation)
        {
            var gateResult = CreateGate(
                firstUseRequestId,
                CoordinatorGateKind.GeneratedWorkflowConfirmation,
                selected.Definition.Id,
                authorizedActorId,
                state.Fence,
                [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject],
                allowsFreeform: false,
                prompt: null);
            if (gateResult.Value is null)
                return Failure<CoordinatorWorkflowSelection>(gateResult.Issues);
            if (!HasFreshRequestId(state, gateResult.Value, out var requestIssue))
                return Failure<CoordinatorWorkflowSelection>(requestIssue);
            gate = gateResult.Value;
        }

        var next = state.Copy(
            selectedWorkflow: selected,
            setSelectedWorkflow: true,
            workflowConfirmed: gate is null,
            confirmedWorkPlan: null,
            setConfirmedWorkPlan: true,
            candidateWorkPlan: null,
            setCandidateWorkPlan: true,
            lastScopeDiff: null,
            setLastScopeDiff: true,
            pendingGate: gate,
            clearPendingGate: gate is null);
        return new CoordinatorDecisionTransition<CoordinatorWorkflowSelection>(
            next,
            new CoordinatorWorkflowSelection(
                selected,
                resolution.UsedAuthorizedDefault,
                resolution.Reasons),
            resolution.Reasons);
    }

    public static CoordinatorDecisionTransition<WorkPlanSnapshot> ProposeWorkPlan(
        CoordinatorDecisionState? state,
        WorkPlan? plan,
        WorkPlanRunSelectionContext? selectionContext,
        string? requestId,
        string? authorizedActorId)
    {
        if (!CanStartTransition(state, "workPlan", out var blocked))
            return Failure<WorkPlanSnapshot>(blocked);
        if (state!.OutcomeSpec is null || !state.OutcomeConfirmed)
            return Failure<WorkPlanSnapshot>(Issue(
                WorkflowValidationCode.OutcomeConfirmationRequired,
                "outcome",
                "The outcome specification must be confirmed before work-plan decomposition."));
        if (state.SelectedWorkflow is null || !state.WorkflowConfirmed)
            return Failure<WorkPlanSnapshot>(Issue(
                WorkflowValidationCode.WorkflowConfirmationRequired,
                "workflow",
                "An eligible workflow must be selected and any generated definition confirmed before decomposition."));
        if (state.ConfirmedWorkPlan is not null)
            return Failure<WorkPlanSnapshot>(Issue(
                WorkflowValidationCode.WorkPlanConfirmationRequired,
                "workPlan",
                "Use revise_work_plan after a work plan has been confirmed."));

        var validated = WorkPlanValidator.ValidateAndSnapshot(
            state.SelectedWorkflow,
            plan,
            selectionContext);
        if (!validated.IsValid)
            return Failure<WorkPlanSnapshot>(validated.Issues);

        var candidate = validated.Value!;
        var gate = CreateGate(
            requestId,
            CoordinatorGateKind.WorkPlanConfirmation,
            candidate.Plan.Id,
            authorizedActorId,
            state.Fence,
            [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject],
            allowsFreeform: false,
            prompt: null);
        if (gate.Value is null)
            return Failure<WorkPlanSnapshot>(gate.Issues);
        if (!HasFreshRequestId(state, gate.Value, out var requestIssue))
            return Failure<WorkPlanSnapshot>(requestIssue);

        var next = state.Copy(
            candidateWorkPlan: candidate,
            setCandidateWorkPlan: true,
            lastScopeDiff: null,
            setLastScopeDiff: true,
            pendingGate: gate.Value);
        return Success(next, candidate);
    }

    public static CoordinatorDecisionTransition<CoordinatorWorkPlanRevision> ReviseWorkPlan(
        CoordinatorDecisionState? state,
        WorkPlan? revisedPlan,
        WorkPlanRunSelectionContext? selectionContext,
        string? requestId,
        string? authorizedActorId)
    {
        if (!CanStartTransition(state, "workPlanRevision", out var blocked))
            return Failure<CoordinatorWorkPlanRevision>(blocked);
        if (state is null || !state.CanDispatch ||
            state.SelectedWorkflow is null || state.ConfirmedWorkPlan is null)
            return Failure<CoordinatorWorkPlanRevision>(Issue(
                WorkflowValidationCode.WorkPlanConfirmationRequired,
                "workPlan",
                "A confirmed work plan is required before it can be revised."));

        var validated = WorkPlanValidator.ValidateAndSnapshot(
            state.SelectedWorkflow,
            revisedPlan,
            selectionContext);
        if (!validated.IsValid)
            return Failure<CoordinatorWorkPlanRevision>(validated.Issues);

        var candidate = validated.Value!;
        var diff = WorkflowScopeDiffer.Compare(state.ConfirmedWorkPlan, candidate);
        CoordinatorGateRequest? gate = null;
        if (diff.RequiresConfirmation)
        {
            var gateResult = CreateGate(
                requestId,
                CoordinatorGateKind.ScopeChangeConfirmation,
                candidate.Plan.Id,
                authorizedActorId,
                state.Fence,
                [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject],
                allowsFreeform: false,
                prompt: null);
            if (gateResult.Value is null)
                return Failure<CoordinatorWorkPlanRevision>(gateResult.Issues);
            if (!HasFreshRequestId(state, gateResult.Value, out var requestIssue))
                return Failure<CoordinatorWorkPlanRevision>(requestIssue);
            gate = gateResult.Value;
        }

        var next = gate is null
            ? state.Copy(
                confirmedWorkPlan: candidate,
                setConfirmedWorkPlan: true,
                candidateWorkPlan: null,
                setCandidateWorkPlan: true,
                lastScopeDiff: diff,
                setLastScopeDiff: true)
            : state.Copy(
                candidateWorkPlan: candidate,
                setCandidateWorkPlan: true,
                lastScopeDiff: diff,
                setLastScopeDiff: true,
                pendingGate: gate);

        return Success(
            next,
            new CoordinatorWorkPlanRevision(candidate, diff, diff.RequiresConfirmation));
    }

    public static CoordinatorDecisionTransition<CoordinatorAssemblyRequestSnapshot> RequestAssembly(
        CoordinatorDecisionState? state,
        CoordinatorAssemblyRequest? request)
    {
        // This binds the request to the confirmed catalog entry; durable readiness and action grants
        // remain owner checks before an executor is scheduled.
        if (state is null || !state.CanDispatch)
            return Failure<CoordinatorAssemblyRequestSnapshot>(Issue(
                WorkflowValidationCode.WorkPlanConfirmationRequired,
                "assembly",
                "Assembly cannot be requested until the outcome, workflow, and work plan are confirmed."));
        if (request is null)
            return Failure<CoordinatorAssemblyRequestSnapshot>(Issue(
                WorkflowValidationCode.InvalidAssemblyRequest,
                "assemblyRequest",
                "A typed assembly request is required."));

        var workflow = state.SelectedWorkflow!;
        var plan = state.ConfirmedWorkPlan!;
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (!WorkflowValidationSupport.IsStableId(request.RequestId) ||
            !WorkflowValidationSupport.IsStableId(request.WorkflowId) ||
            !WorkflowValidationSupport.IsOpaqueReference(request.DefinitionRevision) ||
            !WorkflowValidationSupport.IsStableId(request.WorkPlanId) ||
            !WorkflowValidationSupport.IsStableId(request.WorkflowStepId) ||
            !Enum.IsDefined(request.Gate))
            issues.Add(Issue(WorkflowValidationCode.InvalidAssemblyRequest,
                "assemblyRequest", "Assembly request identifiers and gate must be valid."));

        if (!string.Equals(request.WorkflowId, workflow.Definition.Id, StringComparison.Ordinal) ||
            !string.Equals(request.DefinitionRevision, workflow.Definition.Revision, StringComparison.Ordinal) ||
            !string.Equals(request.WorkPlanId, plan.Plan.Id, StringComparison.Ordinal))
            issues.Add(Issue(WorkflowValidationCode.InvalidAssemblyRequest,
                "assemblyRequest", "Assembly request must target the exact confirmed workflow definition and work plan."));

        var step = workflow.Definition.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.WorkflowStepId, StringComparison.Ordinal));
        if (step is null ||
            step.Mode != WorkflowStepMode.Platform ||
            step.PlatformGate != request.Gate)
            issues.Add(Issue(WorkflowValidationCode.InvalidAssemblyRequest,
                "assemblyRequest.workflowStepId",
                "Assembly requests must target a matching platform-owned gate in the selected catalog."));

        if (issues.Count > 0)
            return Failure<CoordinatorAssemblyRequestSnapshot>(issues.ToImmutable());

        return Success(
            state,
            new CoordinatorAssemblyRequestSnapshot(request, workflow, plan, step!));
    }

    public static CoordinatorDecisionTransition<CoordinatorGateRequest> AskQuestion(
        CoordinatorDecisionState? state,
        string? requestId,
        string? questionId,
        string? prompt,
        string? authorizedActorId,
        ImmutableArray<string> allowedChoices = default,
        bool allowsFreeform = true)
    {
        if (!CanStartTransition(state, "question", out var blocked))
            return Failure<CoordinatorGateRequest>(blocked);

        var gate = CreateGate(
            requestId,
            CoordinatorGateKind.Question,
            questionId,
            authorizedActorId,
            state!.Fence,
            allowedChoices.IsDefault ? [] : allowedChoices,
            allowsFreeform,
            prompt);
        if (gate.Value is null)
            return Failure<CoordinatorGateRequest>(gate.Issues);
        if (!HasFreshRequestId(state!, gate.Value, out var requestIssue))
            return Failure<CoordinatorGateRequest>(requestIssue);

        var next = state.Copy(pendingGate: gate.Value);
        return Success(next, gate.Value);
    }

    public static CoordinatorDecisionTransition<CoordinatorGateRequest> AskNextOutcomeClarifyingQuestion(
        CoordinatorDecisionState? state,
        string? requestId,
        string? authorizedActorId)
    {
        if (!CanStartTransition(state, "outcome.clarifyingQuestions", out var blocked))
            return Failure<CoordinatorGateRequest>(blocked);
        if (state!.OutcomeSpec is null ||
            state.NextClarifyingQuestionIndex >=
            state.OutcomeSpec.Specification.ClarifyingQuestions.Length)
            return Failure<CoordinatorGateRequest>(Issue(
                WorkflowValidationCode.CoordinatorTransitionBlocked,
                "outcome.clarifyingQuestions",
                "There is no unanswered outcome clarification."));

        var question = state.OutcomeSpec.Specification.ClarifyingQuestions[
            state.NextClarifyingQuestionIndex];
        var gate = CreateGate(
            requestId,
            CoordinatorGateKind.Question,
            state.OutcomeSpec.Specification.Id,
            authorizedActorId,
            state.Fence,
            [],
            allowsFreeform: true,
            prompt: question,
            resolvesOutcomeClarification: true);
        if (gate.Value is null)
            return Failure<CoordinatorGateRequest>(gate.Issues);
        if (!HasFreshRequestId(state, gate.Value, out var requestIssue))
            return Failure<CoordinatorGateRequest>(requestIssue);

        var next = state.Copy(pendingGate: gate.Value);
        return Success(next, gate.Value);
    }

    public static CoordinatorDecisionTransition<CoordinatorGateRequest> RequestApproval(
        CoordinatorDecisionState? state,
        string? requestId,
        string? subjectId,
        string? authorizedActorId,
        string? prompt = null)
    {
        if (!CanStartTransition(state, "approval", out var blocked))
            return Failure<CoordinatorGateRequest>(blocked);

        var gate = CreateGate(
            requestId,
            CoordinatorGateKind.Approval,
            subjectId,
            authorizedActorId,
            state!.Fence,
            [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject],
            allowsFreeform: false,
            prompt);
        if (gate.Value is null)
            return Failure<CoordinatorGateRequest>(gate.Issues);
        if (!HasFreshRequestId(state!, gate.Value, out var requestIssue))
            return Failure<CoordinatorGateRequest>(requestIssue);

        var next = state.Copy(pendingGate: gate.Value);
        return Success(next, gate.Value);
    }

    private static WorkflowValidationResult<CoordinatorGateRequest> CreateGate(
        string? requestId,
        CoordinatorGateKind kind,
        string? subjectId,
        string? authorizedActorId,
        long fence,
        ImmutableArray<string> allowedChoices,
        bool allowsFreeform,
        string? prompt,
        bool resolvesOutcomeClarification = false)
    {
        var gate = new CoordinatorGateRequest(
            requestId ?? string.Empty,
            kind,
            subjectId ?? string.Empty,
            authorizedActorId ?? string.Empty,
            fence,
            allowedChoices,
            allowsFreeform,
            prompt,
            resolvesOutcomeClarification);
        var issues = ValidateGate(gate);
        return issues.IsEmpty
            ? WorkflowValidationResult<CoordinatorGateRequest>.Success(gate)
            : WorkflowValidationResult<CoordinatorGateRequest>.Failure(issues);
    }

    internal static ImmutableArray<WorkflowValidationIssue> ValidateGate(CoordinatorGateRequest gate)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (!WorkflowValidationSupport.IsStableId(gate.RequestId))
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionRequestId,
                "gate.requestId", "Request ID must be a stable identifier."));
        if (!Enum.IsDefined(gate.Kind) ||
            !WorkflowValidationSupport.IsStableId(gate.SubjectId) ||
            !WorkflowValidationSupport.IsStableId(gate.AuthorizedActorId))
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "gate", "Gate kind, subject, and authorized actor must be valid."));
        if (gate.ResolvesOutcomeClarification && gate.Kind != CoordinatorGateKind.Question)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "gate.resolvesOutcomeClarification",
                "Only a question gate can resolve an outcome clarification."));
        if (gate.Fence <= 0)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionFence,
                "gate.fence", "Gate fence must be positive."));
        if (gate.AllowedChoices.IsDefault || gate.AllowedChoices.Length > WorkflowDomainLimits.MaximumEligibilityValues)
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "gate.allowedChoices", "Allowed choices must be initialized and bounded."));
        else
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < gate.AllowedChoices.Length; index++)
            {
                var choice = gate.AllowedChoices[index];
                if (!WorkflowValidationSupport.IsStableId(choice) || !distinct.Add(choice))
                    issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                        $"gate.allowedChoices[{index}]", "Choices must be distinct stable identifiers."));
            }
        }

        if (gate.Kind == CoordinatorGateKind.Question)
        {
            if (string.IsNullOrWhiteSpace(gate.Prompt) ||
                gate.Prompt.Length > WorkflowDomainLimits.MaximumWorkTextLength)
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                    "gate.prompt", "A question gate needs a non-empty bounded prompt."));
            if (gate.AllowedChoices.IsEmpty && !gate.AllowsFreeform)
                issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                    "gate", "A question gate needs an allowed choice or free-form answer."));
        }
        else if (gate.AllowsFreeform ||
                 gate.AllowedChoices.IsDefault ||
                 !gate.AllowedChoices.SequenceEqual(
                     [CoordinatorGateChoices.Approve, CoordinatorGateChoices.Reject],
                     StringComparer.Ordinal))
        {
            issues.Add(Issue(WorkflowValidationCode.InvalidDecisionGate,
                "gate.allowedChoices", "Confirmation and approval gates accept only explicit approve or reject choices."));
        }

        return issues.ToImmutable();
    }

    private static bool CanStartTransition(
        CoordinatorDecisionState? state,
        string path,
        out ImmutableArray<WorkflowValidationIssue> issues)
    {
        if (state is null)
        {
            issues = [Issue(WorkflowValidationCode.CoordinatorTransitionBlocked,
                path, "Current coordinator decision state is required.")];
            return false;
        }

        if (state.PendingGate is not null)
        {
            issues = [Issue(WorkflowValidationCode.CoordinatorTransitionBlocked,
                path, "Resolve the current request-id-bound gate before starting another transition.")];
            return false;
        }

        issues = [];
        return true;
    }

    private static bool HasFreshRequestId(
        CoordinatorDecisionState state,
        CoordinatorGateRequest gate,
        out WorkflowValidationIssue issue)
    {
        if (state.DecisionReceipts.Any(receipt =>
                string.Equals(receipt.RequestId, gate.RequestId, StringComparison.Ordinal)))
        {
            issue = Issue(
                WorkflowValidationCode.InvalidDecisionRequestId,
                "gate.requestId",
                "Request IDs cannot be reused after a decision has been applied.");
            return false;
        }

        issue = default!;
        return true;
    }

    private static CoordinatorDecisionTransition<T> Success<T>(
        CoordinatorDecisionState state,
        T value)
        where T : class =>
        new(state, value, []);

    private static CoordinatorDecisionTransition<T> Failure<T>(
        ImmutableArray<WorkflowValidationIssue> issues)
        where T : class =>
        new(null, null, issues);

    private static CoordinatorDecisionTransition<T> Failure<T>(
        WorkflowValidationIssue issue)
        where T : class =>
        Failure<T>([issue]);

    private static WorkflowValidationIssue Issue(
        WorkflowValidationCode code,
        string path,
        string message) =>
        new(code, path, message);
}
