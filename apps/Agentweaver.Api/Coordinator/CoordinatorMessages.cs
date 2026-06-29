namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Workflow entry input for a coordinator run. Carries the human's goal plus the context the
/// drafting executor needs to produce an outcome spec. <see cref="ReviseFeedback"/> is null on
/// the first draft and set when the human requested changes, so the executor re-drafts.
/// </summary>
public sealed record CoordinatorDraftInput(
    string RunId,
    string ProjectId,
    string Goal,
    string SubmittingUser,
    string RepositoryPath,
    string? ModelId,
    string? WorkflowOverrideId = null,
    string? ReviseFeedback = null);

/// <summary>
/// Data surfaced to the external caller (the confirm/revise endpoints) through the
/// await-confirmation request port. Mirrors <c>WorkflowReviewRequest</c> for the review gate.
/// </summary>
public sealed record CoordinatorOutcomeSpecRequest(
    string RunId,
    int SpecId,
    string Goal,
    string DesiredOutcome,
    string Scope,
    string Assumptions,
    string? ClarifyingQuestions,
    string Status);

/// <summary>
/// Response provided by the human through the await-confirmation request port. Mirrors
/// <c>WorkflowReviewDecision</c>. Exactly one of <see cref="Confirmed"/> / <see cref="Revise"/>
/// is meaningful: confirm advances the spec and terminates the run (Phase 1, dispatch is Phase 2);
/// revise re-drafts the spec and re-suspends at the gate.
/// </summary>
public sealed record CoordinatorOutcomeSpecDecision(
    bool Confirmed,
    bool Revise = false,
    string? ConfirmedBy = null,
    string? ReviseFeedback = null);

/// <summary>Terminal workflow output for a coordinator run.</summary>
public sealed record CoordinatorOutcome(string RunId, int SpecId, string Status);
