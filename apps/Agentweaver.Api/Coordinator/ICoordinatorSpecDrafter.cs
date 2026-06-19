namespace Agentweaver.Api.Coordinator;

/// <summary>The drafted outcome-spec fields synthesized from the human goal and team context.</summary>
public sealed record OutcomeSpecDraft(
    string DesiredOutcome,
    string Scope,
    string Assumptions,
    string? ClarifyingQuestions);

/// <summary>
/// Drafts a confirmable outcome spec for the coordinator from the human's goal and the team's
/// memories/decisions. The production implementation runs a real Copilot coordinator agent turn
/// and THROWS when the model is unavailable or returns unparseable output, so a coordinator run
/// fails visibly instead of silently degrading to a boilerplate spec. This is the seam tests
/// substitute with a deterministic fake.
/// </summary>
public interface ICoordinatorSpecDrafter
{
    Task<OutcomeSpecDraft> DraftAsync(
        CoordinatorDraftInput input, string charter, string? memoryContext, CancellationToken ct);
}
