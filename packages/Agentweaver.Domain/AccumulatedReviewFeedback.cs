using System.Text;

namespace Agentweaver.Domain;

/// <summary>
/// STABLE runtime handoff contract (Fix-A/Fix-B "3b") for a SINGLE subtask's assembly-review revision.
/// Lives in <c>Agentweaver.Domain</c> so it is referenceable from BOTH the coordinator
/// (<c>Agentweaver.Api</c>, which PRODUCES it) and the agent-runtime revision seam
/// (<c>Agentweaver.AgentRuntime</c>, which CONSUMES it in <c>StartChildRevisionHandoffAsync</c>).
///
/// The coordinator is the SINGLE SOURCE OF TRUTH: it derives <see cref="PriorRounds"/> from the run's
/// <c>SteeringDirective</c> rows, TARGET-scoped (only rounds that targeted THIS subtask) and
/// REJECTION-scoped (only request-changes / blocking rounds, never advisories). Consumers MUST NOT read
/// <c>SteeringDirective</c> rows directly or define a parallel shape.
/// </summary>
/// <param name="SubtaskId">The subtask whose revision is being handed off.</param>
/// <param name="CurrentChangeRequest">The latest reviewer change-request that triggered this revision.</param>
/// <param name="PriorRounds">All prior rejection rounds for THIS subtask (oldest → newest), so the (possibly different, non-locked-out) agent addresses every requirement — not just the latest complaint (the amnesia root cause).</param>
/// <param name="PriorWorktreeBranch">The prior child's worktree/integration branch, so the consumer can REUSE the branch/worktree while minting a NEW SDK session for the non-locked-out agent (it must NOT resume the prior agent's session).</param>
/// <param name="RenderedGuidance">A deterministic, prompt-ready rendering of this feedback, so the consumer can inject prior feedback into the new agent's task prompt without re-deriving it. Populated by the coordinator producer.</param>
public sealed record AccumulatedReviewFeedback(
    string SubtaskId,
    string CurrentChangeRequest,
    IReadOnlyList<ReviewFeedbackRound> PriorRounds,
    string PriorWorktreeBranch,
    string? RenderedGuidance = null);

/// <summary>One prior assembly-review rejection round for a subtask.</summary>
/// <param name="Round">1-based ordinal (oldest → newest).</param>
/// <param name="Reviewer">Who requested the change (gate source / author, e.g. rubberduck, rai, build-test, a human, or an agent).</param>
/// <param name="Feedback">The reviewer's change-request text for this round (bounded).</param>
/// <param name="At">When the round was recorded.</param>
public sealed record ReviewFeedbackRound(
    int Round,
    string Reviewer,
    string Feedback,
    DateTimeOffset At);

/// <summary>
/// Deterministic rendering of <see cref="AccumulatedReviewFeedback"/> into the revision prompt. The SAME
/// text is used by the conscious fresh/rotated dispatch and the in-place resume, so accumulation is a
/// single source of truth regardless of which path applies the revision. Pass a null/empty
/// <c>priorWorktreeBranch</c> for the in-place resume (the child session is preserved — there is no
/// prior pod/branch to "build on", the agent continues where it left off).
/// </summary>
public static class ReviewFeedbackRenderer
{
    public static string RenderForRevisionPrompt(
        string? currentChangeRequest,
        IReadOnlyList<ReviewFeedbackRound> priorRounds,
        string? priorWorktreeBranch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recovery guidance from the assembly reviewer(s). Address ALL of the following change requests before resubmitting your work for assembly review.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(currentChangeRequest))
        {
            sb.AppendLine($"Latest reviewer feedback: {currentChangeRequest}");
            sb.AppendLine();
        }
        if (priorRounds.Count > 0)
        {
            sb.AppendLine("Accumulated review feedback across ALL prior rounds (address every item; do NOT regress fixes an earlier round already required):");
            foreach (var r in priorRounds)
                sb.AppendLine($"  - [round {r.Round} · {r.Reviewer}] {r.Feedback}");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(priorWorktreeBranch))
        {
            sb.Append("Prior work: a prior attempt on this subtask is preserved on integration/worktree branch ")
              .Append(priorWorktreeBranch)
              .AppendLine(". Build on that prior work and the latest repository state — do NOT start from scratch.");
            sb.AppendLine();
        }
        sb.Append("Re-do this work against the latest repository state and address the feedback above.");
        return sb.ToString();
    }

    /// <summary>Renders the prompt for a fully-built bundle (uses its <see cref="AccumulatedReviewFeedback.PriorWorktreeBranch"/>).</summary>
    public static string RenderForRevisionPrompt(this AccumulatedReviewFeedback feedback) =>
        RenderForRevisionPrompt(feedback.CurrentChangeRequest, feedback.PriorRounds, feedback.PriorWorktreeBranch);
}
