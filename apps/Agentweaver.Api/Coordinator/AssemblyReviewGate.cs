using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// HITL seam for the ONE collective human-review gate (D5). The collective assembly pipeline runs on
/// a background task; when it reaches the review stage it ARMS this gate (registering a pending
/// <see cref="TaskCompletionSource{T}"/> keyed by coordinator run id) and awaits the reviewer's
/// decision, which arrives via <c>POST /api/runs/{coordinatorRunId}/assembly/review</c>. This mirrors
/// the single-run <see cref="PendingRequestStore"/> / RequestPort pattern (owner-scoped, at-most-once
/// consumption) but is service-driven rather than MAF-RequestPort-driven, because the collective
/// review routes BACK to the coordinator (re-dispatch) rather than looping to an agent.
///
/// <para>The gate waits <b>indefinitely</b> for the human operator. It is intentionally free of any
/// wall-clock timeout: an armed gate ends only when a decision is submitted (<see cref="TrySubmit"/>)
/// or the host is shutting down (the caller's <see cref="CancellationToken"/> fires →
/// <see cref="TaskCanceledException"/>). Faulting an open gate on elapsed time would kill runs that
/// are correctly parked awaiting the accountable human — the durable review record plus the
/// coordinator reconciler recover the gate across restarts, so parking here forever is safe.</para>
/// </summary>
public sealed class AssemblyReviewGate
{
    private readonly object _gateLock = new();
    private readonly Dictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);

    // Constructor keeps the optional IConfiguration parameter for DI compatibility. The former
    // Coordinator:AssemblyReviewTimeoutMinutes setting is intentionally ignored: the human-review gate
    // has no wall-clock timeout (see type remarks). The parameter is retained so existing service
    // registrations and any residual config keys continue to bind without error.
    public AssemblyReviewGate(IConfiguration? configuration = null)
    {
        _ = configuration;
    }

    /// <summary>
    /// Arms the gate for a coordinator run and returns a task that completes when the reviewer
    /// submits a decision, or is cancelled when the host stops (<paramref name="ct"/>). Replaces any
    /// prior armed gate for the same run. There is no timeout — the wait is indefinite by design.
    /// </summary>
    public Task<AssemblyReviewDecision> ArmAsync(string coordinatorRunId, string ownerUser, CancellationToken ct)
    {
        var entry = new GateEntry(
            new TaskCompletionSource<AssemblyReviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously),
            ownerUser);

        var cancellationRegistration = ct.CanBeCanceled
            ? ct.Register(static state =>
            {
                var (gate, runId, gateEntry) = ((AssemblyReviewGate Gate, string RunId, GateEntry Entry))state!;
                gate.CancelEntry(runId, gateEntry);
            }, (this, coordinatorRunId, entry))
            : default;
        entry.SetRegistration(cancellationRegistration);

        lock (_gateLock)
        {
            if (_gates.TryGetValue(coordinatorRunId, out var oldEntry))
            {
                oldEntry.DisposeRegistrations();
                oldEntry.Tcs.TrySetCanceled();
            }

            _gates[coordinatorRunId] = entry;
        }

        if (ct.IsCancellationRequested)
            CancelEntry(coordinatorRunId, entry);

        return entry.Tcs.Task;
    }

    /// <summary>True when a review decision is currently awaited for the run.</summary>
    public bool IsArmed(string coordinatorRunId)
    {
        lock (_gateLock)
            return _gates.ContainsKey(coordinatorRunId);
    }

    /// <summary>
    /// Atomically consumes the armed gate and delivers the decision (at-most-once; double-POST
    /// safe). Verifies the caller owns the pending request (IDOR defense, Guardrail 9).
    /// </summary>
    public AssemblyReviewSubmitResult TrySubmit(string coordinatorRunId, string callerUser, AssemblyReviewDecision decision, string? callerGitHubLogin = null)
    {
        GateEntry entry;
        lock (_gateLock)
        {
            if (!_gates.TryGetValue(coordinatorRunId, out entry!))
                return AssemblyReviewSubmitResult.NotArmed;

            // Identity-aware ownership: the gate owner is the run's SubmittingUser, which for backlog-pickup
            // runs is the captured GitHub login rather than the API-key principal. Match either.
            var owns = string.Equals(entry.OwnerUser, callerUser, StringComparison.Ordinal) ||
                       (callerGitHubLogin is not null && string.Equals(entry.OwnerUser, callerGitHubLogin, StringComparison.Ordinal));
            if (!owns)
                return AssemblyReviewSubmitResult.Forbidden;

            if (!_gates.Remove(coordinatorRunId, out var existing))
                return AssemblyReviewSubmitResult.NotArmed;

            if (!ReferenceEquals(existing.Tcs, entry.Tcs))
            {
                if (!_gates.ContainsKey(coordinatorRunId))
                    _gates[coordinatorRunId] = existing;
                return AssemblyReviewSubmitResult.NotArmed;
            }

            entry = existing;
        }

        entry.DisposeRegistrations();
        return entry.Tcs.TrySetResult(decision)
            ? AssemblyReviewSubmitResult.Accepted
            : AssemblyReviewSubmitResult.NotArmed;
    }

    private void CancelEntry(string coordinatorRunId, GateEntry entry)
    {
        lock (_gateLock)
        {
            if (!_gates.TryGetValue(coordinatorRunId, out var current) ||
                !ReferenceEquals(current.Tcs, entry.Tcs))
                return;

            _gates.Remove(coordinatorRunId);
        }

        entry.DisposeRegistrations();
        entry.Tcs.TrySetCanceled();
    }

    private sealed class GateEntry
    {
        private CancellationTokenRegistration _cancellationRegistration;
        private int _disposed;

        public GateEntry(TaskCompletionSource<AssemblyReviewDecision> tcs, string ownerUser)
        {
            Tcs = tcs;
            OwnerUser = ownerUser;
        }

        public TaskCompletionSource<AssemblyReviewDecision> Tcs { get; }
        public string OwnerUser { get; }

        public void SetRegistration(CancellationTokenRegistration cancellationRegistration)
        {
            _cancellationRegistration = cancellationRegistration;
        }

        public void DisposeRegistrations()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cancellationRegistration.Dispose();
        }
    }
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
