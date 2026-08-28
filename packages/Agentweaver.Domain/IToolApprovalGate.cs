namespace Agentweaver.Domain;

/// <summary>Controls how broadly an approval is applied.</summary>
public enum ApprovalScope
{
    /// <summary>Approve only this specific request.</summary>
    Once,

    /// <summary>Approve all future requests from this tool for the current orchestration run.</summary>
    Run,

    /// <summary>Persist an eligible-tool policy for future runs in the same project, owned by the same persisted user.</summary>
    Always,

    /// <summary>Approve all future requests from this tool (any URL) for this run.</summary>
    Tool,
}

/// <summary>Server-visible lifecycle state for a HITL tool-approval request.</summary>
public enum ToolApprovalRequestState
{
    Unknown,
    Pending,
    Approved,
    Denied,
    Expired,
}

/// <summary>Server-side context captured when a tool-approval request is armed.</summary>
public sealed record ToolApprovalRequestContext(string ToolName, string? Url);

/// <summary>Canonical policy semantics shared by durable and in-memory approval gates.</summary>
public static class ToolApprovalPolicySemantics
{
    private static readonly HashSet<string> AlwaysEligibleTools = new(StringComparer.Ordinal)
    {
        "web_fetch",
    };

    public static bool IsAlwaysEligible(string toolName) =>
        AlwaysEligibleTools.Contains(toolName);

    public static string RiskFor(string toolName) =>
        toolName switch
        {
            "web_fetch" => "network-read/v1",
            "start_preview" => "preview-process/v1",
            _ => "approval-gated-tool/v1",
        };
}

/// <summary>Resolves a run's canonical owner from server-controlled persisted state.</summary>
public interface IToolApprovalOwnerResolver
{
    string? GetCanonicalOwner(string runId);
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
    /// Returns <see langword="true"/> if the tool is covered by a run-scoped policy or an eligible
    /// project- and owner-bound always policy, meaning no HITL card should be shown.
    /// </summary>
    bool IsAutoApproved(string runId, string toolName, string? url);

    /// <summary>Returns the server-visible lifecycle state for a tool-approval request.</summary>
    ToolApprovalRequestState GetRequestState(string runId, string requestId) =>
        IsKnownRequest(runId, requestId) ? ToolApprovalRequestState.Pending : ToolApprovalRequestState.Unknown;

    /// <summary>
    /// Returns the server-captured context for a known approval request. Callers must not use
    /// client-supplied tool metadata when creating a broader approval policy.
    /// </summary>
    ToolApprovalRequestContext? GetRequestContext(string runId, string requestId) => null;

    /// <summary>
    /// Returns <see langword="true"/> if a tool-approval request with <paramref name="requestId"/>
    /// was ever registered for <paramref name="runId"/>. Prefer <see cref="GetRequestState"/> when
    /// a caller needs to distinguish pending, resolved, expired, and unknown requests.
    /// </summary>
    bool IsKnownRequest(string runId, string requestId) => true;

    /// <summary>
    /// Returns <see langword="true"/> if the run currently has at least one ARMED tool-approval
    /// request — registered and still awaiting the operator's grant/deny decision (not yet resolved,
    /// denied, expired, or cleared). A run parked on an approval card is not "idle": it is actively
    /// blocked on the accountable human, so idle/close sweeps must not seal it out from under a human
    /// who has merely stepped away. Mirrors the coordinator's indefinite-safe HITL wait.
    /// </summary>
    bool HasArmedApproval(string runId) => false;

    /// <summary>Clears all pending approvals for a run (called on run completion).</summary>
    void Clear(string runId);

    /// <summary>
    /// Registers a parent–child relationship so that run-scoped policies granted on a child run
    /// are also visible to its sibling child runs within the same orchestration (i.e. stored under
    /// the real parent run ID too).
    /// Call this once when a coordinator child run is dispatched.
    /// </summary>
    void RegisterParentRun(string childRunId, string parentRunId);
}
