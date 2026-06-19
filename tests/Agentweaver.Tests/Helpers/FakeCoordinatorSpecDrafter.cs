using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Deterministic, hermetic <see cref="ICoordinatorSpecDrafter"/> for the coordinator suite. It
/// synthesizes a confirmable outcome spec from the goal and team context WITHOUT any model or
/// network call, so the draft -> gate -> confirm/revise lifecycle stays deterministic. This is the
/// test seam that replaces the production Copilot drafter; the boilerplate it produces lives here in
/// the test project, never in production code (production fails the run when the model is
/// unavailable rather than fabricating a spec).
/// </summary>
public sealed class FakeCoordinatorSpecDrafter : ICoordinatorSpecDrafter
{
    public Task<OutcomeSpecDraft> DraftAsync(
        CoordinatorDraftInput input, string charter, string? memoryContext, CancellationToken ct)
    {
        var goal = input.Goal.Trim();
        var hasContext = !string.IsNullOrWhiteSpace(memoryContext);

        var desired =
            $"Deliver the goal as stated: {goal}. Success means the goal is implemented, verified " +
            "against the team's existing boundaries and decisions, and ready for the collective review gate.";

        var scope =
            "In scope: the work required to achieve the stated goal. Out of scope: unrelated changes, " +
            "speculative features, and anything not implied by the goal." +
            (hasContext
                ? " The team's recorded decisions and boundaries constrain this scope and take precedence."
                : string.Empty);

        var assumptions = hasContext
            ? "The team's existing memories and decisions remain authoritative and are assumed current. " +
              "No new decision is required before this work can be scoped."
            : "No prior team memories or decisions were found for this project, so this spec assumes a " +
              "greenfield interpretation of the goal.";

        // On revision, surface the human's feedback in the clarifying questions so the re-draft is
        // observably grounded in it (the revise lifecycle test asserts this).
        var questions = string.IsNullOrEmpty(input.ReviseFeedback)
            ? (goal.Length < 24
                ? "The goal is brief. What concrete outcome, surface, or acceptance signal defines done?"
                : null)
            : "Revision requested: " + input.ReviseFeedback.Trim();

        return Task.FromResult(new OutcomeSpecDraft(desired, scope, assumptions, questions));
    }
}
