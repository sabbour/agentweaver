using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly RunStreamStore _streamStore;

    public FakeCoordinatorSpecDrafter(RunStreamStore streamStore) => _streamStore = streamStore;

    public CoordinatorDraftInput? LastInput { get; private set; }
    public bool BlockUntilCancelled { get; set; }
    public bool CancellationObserved { get; private set; }
    public Exception? ExceptionToThrow { get; set; }
    public AgentProviderException? ProviderFailureToThrow { get; set; }

    public async Task<OutcomeSpecDraft> DraftAsync(
        CoordinatorDraftInput input, string charter, string? memoryContext, CancellationToken ct)
    {
        LastInput = input;
        if (ProviderFailureToThrow is { } providerFailure)
        {
            await EmitProviderFailureAsync(input.RunId, providerFailure);
            throw providerFailure;
        }
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;
        if (BlockUntilCancelled)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

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

        return new OutcomeSpecDraft(desired, scope, assumptions, questions);
    }

    private async Task EmitProviderFailureAsync(string runId, AgentProviderException providerFailure)
    {
        var entry = _streamStore.Get(runId)
            ?? throw new InvalidOperationException($"Missing run stream for coordinator run {runId}.");
        await using var clientFactory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(),
            new FixedGitHubCopilotCapabilityCredentialProvider());
        await using var agent = new CopilotAIAgent(
            clientFactory,
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance);
        agent.SetTurnStreamWriter(new RecordingChannelWriter(entry));
        agent.EmitProviderFailure(providerFailure);
    }
}
