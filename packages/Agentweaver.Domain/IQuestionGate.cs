namespace Agentweaver.Domain;

/// <summary>
/// Provides a per-question blocking HITL gate for the <c>ask_question</c> tool.
/// When an agent calls <c>ask_question</c> it registers a pending request and suspends by
/// awaiting <see cref="AskAsync"/>. The operator (or, for a coordinator child, the coordinator)
/// supplies free-text via <see cref="Answer"/>, which resumes the agent with that answer.
/// Mirrors <see cref="IToolApprovalGate"/> but returns an answer string instead of a bool.
/// </summary>
public interface IQuestionGate
{
    /// <summary>
    /// Atomically registers the pending question and suspends until it is answered via
    /// <see cref="Answer"/>, the run is cleared, or <paramref name="timeout"/> elapses.
    /// Returns the answer text, or <see langword="null"/> on timeout/cancellation.
    /// </summary>
    Task<string?> AskAsync(
        string runId,
        string requestId,
        string question,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Supplies the answer for <paramref name="requestId"/> within <paramref name="runId"/>,
    /// resuming the suspended agent.
    /// </summary>
    /// <returns><see langword="true"/> if a pending question was found and resolved; <see langword="false"/> if not found (already answered or expired).</returns>
    bool Answer(string runId, string requestId, string answer);

    /// <summary>Clears all pending questions for a run (called on run completion). Pending waits resolve to <see langword="null"/>.</summary>
    void Clear(string runId);
}
