namespace Scaffolder.Domain;

/// <summary>Controls how broadly an approval is applied.</summary>
public enum ApprovalScope
{
    /// <summary>Approve only this specific request.</summary>
    Once,

    /// <summary>Approve all future requests from the same tool+URL combo for this run.</summary>
    Run,

    /// <summary>Persist a permanent allow policy for this tool+URL combo across all runs.</summary>
    Always,
}

/// <summary>
/// Provides a per-tool-call HITL approval gate.
/// When a blocked tool (e.g. web_fetch) fires, the permission handler atomically registers
/// and suspends by awaiting <see cref="WaitForApprovalAsync"/>. The frontend renders a HITL card
/// and the operator calls <see cref="GrantAsync"/> or <see cref="Deny"/> via the API.
/// </summary>
public interface IToolApprovalGate
{
    /// <summary>
    /// Atomically registers the pending approval request (with its tool+URL context) and
    /// suspends until the operator grants or denies the request, or <paramref name="timeout"/> elapses.
    /// Returns <see langword="true"/> if approved, <see langword="false"/> if denied or timed out.
    /// </summary>
    Task<bool> WaitForApprovalAsync(
        string runId,
        string requestId,
        string toolName,
        string? url,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Grants the pending approval for <paramref name="requestId"/> within <paramref name="runId"/>
    /// using the specified <paramref name="scope"/>.
    /// </summary>
    /// <returns><see langword="true"/> if a pending request was found and resolved; <see langword="false"/> if not found (already resolved or expired).</returns>
    Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope);

    /// <summary>Denies the pending approval for <paramref name="requestId"/> within <paramref name="runId"/>.</summary>
    /// <returns><see langword="true"/> if a pending request was found and resolved; <see langword="false"/> if not found (already resolved or expired).</returns>
    bool Deny(string runId, string requestId);

    /// <summary>
    /// Returns <see langword="true"/> if the tool+URL combo is already covered by a run-scoped
    /// or always-allowed policy, meaning no HITL card should be shown.
    /// </summary>
    bool IsAutoApproved(string runId, string toolName, string? url);

    /// <summary>Clears all pending approvals for a run (called on run completion).</summary>
    void Clear(string runId);
}
