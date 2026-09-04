namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Workflow entry input for a coordinator run. Carries the human's goal plus the context the
/// drafting executor needs to produce an outcome spec. <see cref="ReviseFeedback"/> is null on
/// the first draft and set when the human requested changes, so the executor re-drafts.
/// <see cref="PriorDraft"/> carries the already-reviewed previous draft forward on a revision so the
/// drafter can treat its established requirements as locked invariants (preserved verbatim or
/// stronger) and change only what the feedback targets, instead of silently regressing unrelated
/// constraints when it re-drafts (issue #315). It is null on the first draft.
/// <see cref="SubmittingUserDisplayName"/> is the human-readable identity (display name / GitHub
/// login) for <see cref="SubmittingUser"/>, when known. Direct mode auto-confirms the outcome spec
/// with no confirmation gate, so it attributes <c>ConfirmedBy</c> to this value, falling back to the
/// raw <see cref="SubmittingUser"/> (e.g. an Entra OID) only when unknown (#853/#854).
/// </summary>
public sealed record CoordinatorDraftInput(
    string RunId,
    string ProjectId,
    string Goal,
    string SubmittingUser,
    string RepositoryPath,
    string? ModelId,
    string? WorkflowOverrideId = null,
    string? ReviseFeedback = null,
    string? OutcomeSpecGenerationModel = null,
    OutcomeSpecDraft? PriorDraft = null,
    string? SubmittingUserDisplayName = null);

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
    bool AllowTaskPromotion = false,
    string? ReviseFeedback = null);

/// <summary>Terminal workflow output for a coordinator run.</summary>
public sealed record CoordinatorOutcome(string RunId, int SpecId, string Status);

/// <summary>
/// Raised when outcome-spec drafting exceeds its coordinator-level wall-clock bound. The bound
/// covers provider setup and session creation as well as the model turn, which have looser runtime
/// defaults and can otherwise leave the durable coordinator run in <c>drafting</c> indefinitely.
/// </summary>
public sealed class CoordinatorOutcomeSpecDraftTimeoutException(
    string runId,
    TimeSpan timeout,
    Exception? innerException = null)
    : TimeoutException(
        $"Coordinator outcome-spec drafting for run '{runId}' exceeded {timeout.TotalSeconds:n0} seconds.",
        innerException);
