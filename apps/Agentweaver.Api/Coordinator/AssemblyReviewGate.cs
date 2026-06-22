using System.Collections.Concurrent;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// HITL seam for the ONE collective human-review gate (D5). The collective assembly pipeline runs on
/// a background task; when it reaches the review stage it ARMS this gate (registering a pending
/// <see cref="TaskCompletionSource{T}"/> keyed by coordinator run id) and awaits the reviewer's
/// decision, which arrives via <c>POST /api/runs/{coordinatorRunId}/assembly/review</c>. This mirrors
/// the single-run <see cref="PendingRequestStore"/> / RequestPort pattern (owner-scoped, at-most-once
/// consumption) but is service-driven rather than MAF-RequestPort-driven, because the collective
/// review routes BACK to the coordinator (re-dispatch) rather than looping to an agent.
/// </summary>
public sealed class AssemblyReviewGate
{
    private readonly ConcurrentDictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Arms the gate for a coordinator run and returns a task that completes when the reviewer
    /// submits a decision (or faults/cancels). Replaces any prior armed gate for the same run.
    /// </summary>
    public Task<AssemblyReviewDecision> ArmAsync(string coordinatorRunId, string ownerUser, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AssemblyReviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[coordinatorRunId] = new GateEntry(tcs, ownerUser);

        if (ct.CanBeCanceled)
            ct.Register(() =>
            {
                if (_gates.TryRemove(coordinatorRunId, out var entry))
                    entry.Tcs.TrySetCanceled();
            });

        return tcs.Task;
    }

    /// <summary>True when a review decision is currently awaited for the run.</summary>
    public bool IsArmed(string coordinatorRunId) => _gates.ContainsKey(coordinatorRunId);

    /// <summary>
    /// Atomically consumes the armed gate and delivers the decision (at-most-once; double-POST
    /// safe). Verifies the caller owns the pending request (IDOR defense, Guardrail 9).
    /// </summary>
    public AssemblyReviewSubmitResult TrySubmit(string coordinatorRunId, string callerUser, AssemblyReviewDecision decision, string? callerGitHubLogin = null)
    {
        if (!_gates.TryGetValue(coordinatorRunId, out var entry))
            return AssemblyReviewSubmitResult.NotArmed;

        // Identity-aware ownership: the gate owner is the run's SubmittingUser, which for backlog-pickup
        // runs is the captured GitHub login rather than the API-key principal. Match either.
        var owns = string.Equals(entry.OwnerUser, callerUser, StringComparison.Ordinal) ||
                   (callerGitHubLogin is not null && string.Equals(entry.OwnerUser, callerGitHubLogin, StringComparison.Ordinal));
        if (!owns)
            return AssemblyReviewSubmitResult.Forbidden;

        // Atomic removal so a concurrent double-POST cannot deliver twice.
        if (!_gates.TryRemove(coordinatorRunId, out entry))
            return AssemblyReviewSubmitResult.NotArmed;

        return entry.Tcs.TrySetResult(decision)
            ? AssemblyReviewSubmitResult.Accepted
            : AssemblyReviewSubmitResult.NotArmed;
    }

    private readonly record struct GateEntry(TaskCompletionSource<AssemblyReviewDecision> Tcs, string OwnerUser);
}

/// <summary>
/// The reviewer's decision on the collective output. <see cref="TargetFiles"/> is the optional
/// explicit file list (D6 step a) that augments path tokens parsed from <see cref="Feedback"/>.
/// </summary>
public sealed record AssemblyReviewDecision(
    bool Approved,
    bool RequestChanges,
    string? Feedback,
    IReadOnlyList<string>? TargetFiles,
    string Reviewer);

/// <summary>Outcome of <see cref="AssemblyReviewGate.TrySubmit"/> so the HTTP layer can map status codes.</summary>
public enum AssemblyReviewSubmitResult
{
    /// <summary>The decision was delivered to the awaiting pipeline.</summary>
    Accepted,

    /// <summary>No review is currently awaited for this run (not yet armed, or already consumed).</summary>
    NotArmed,

    /// <summary>The caller does not own the pending review request.</summary>
    Forbidden,
}
