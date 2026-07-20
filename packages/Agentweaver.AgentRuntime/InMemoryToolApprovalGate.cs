using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// In-memory <see cref="IToolApprovalGate"/> that uses <see cref="TaskCompletionSource{T}"/>
/// to suspend the permission handler until the operator grants or denies the request.
/// The gate is keyed by <c>(runId, requestId)</c>; each requestId may only be resolved once.
/// </summary>
public sealed class InMemoryToolApprovalGate : IToolApprovalGate
{
    // Two-level dictionary: runId → requestId → TCS
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskCompletionSource<bool>>> _pending = new();

    // runId → requestId → (toolName, url) — populated by SetRequestContext
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (string ToolName, string? Url)>> _requestContext = new();

    // runId → requestId → terminal state
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ToolApprovalRequestState>> _resolved = new();

    // Run-scoped allowlist: runId → server-derived owner/tool/risk policies.
    private readonly ConcurrentDictionary<string, HashSet<ApprovalPolicy>> _runScopedAllowlist = new();

    // Always-allowed policies survive across runs, but only for the same resolved owner.
    // TODO: persist this to the database so always-allowed policies survive process restarts.
    private readonly HashSet<ApprovalPolicy> _alwaysAllowedPolicies = [];
    private readonly object _alwaysLock = new();
    private readonly IToolApprovalOwnerResolver? _ownerResolver;

    // childRunId → parentRunId — populated by RegisterParentRun
    private readonly ConcurrentDictionary<string, string> _parentRuns = new();

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public InMemoryToolApprovalGate(IToolApprovalOwnerResolver? ownerResolver = null)
    {
        _ownerResolver = ownerResolver;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForApprovalAsync(
        string runId,
        string requestId,
        string toolName,
        string? url,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // Atomically store the tool+url context before the TCS is visible to callers.
        var runCtx = _requestContext.GetOrAdd(runId, _ => new ConcurrentDictionary<string, (string, string?)>());
        runCtx[requestId] = (toolName, url);

        var runPending = _pending.GetOrAdd(runId, _ => new ConcurrentDictionary<string, TaskCompletionSource<bool>>());
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Atomically register or replace an existing entry for this requestId.
        // If a duplicate arrives (retry), the previous TCS is resolved as denied so it doesn't leak.
        runPending.AddOrUpdate(requestId,
            addValueFactory: _ => tcs,
            updateValueFactory: (_, existing) => { existing.TrySetResult(false); return tcs; });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var reg = cts.Token.Register(() =>
            {
                if (tcs.TrySetResult(false))
                    MarkResolved(runId, requestId, ToolApprovalRequestState.Expired);
            });

            var result = await tcs.Task.ConfigureAwait(false);
            MarkResolved(runId, requestId, result ? ToolApprovalRequestState.Approved : ToolApprovalRequestState.Denied);
            return result;
        }
        finally
        {
            runPending.TryRemove(requestId, out _);
        }
    }



    /// <inheritdoc />
    public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope)
    {
        var resolved = Resolve(runId, requestId, result: true);

        if (resolved && scope != ApprovalScope.Once)
        {
            if (_requestContext.TryGetValue(runId, out var runCtx) &&
                runCtx.TryGetValue(requestId, out var ctx) &&
                !string.IsNullOrWhiteSpace(ctx.ToolName) &&
                OwnerOf(runId) is { } owner)
            {
                var policy = new ApprovalPolicy(
                    owner,
                    ctx.ToolName,
                    ToolApprovalPolicySemantics.RiskFor(ctx.ToolName));

                if (scope is ApprovalScope.Run or ApprovalScope.Tool)
                {
                    AddRunPolicy(runId, policy);
                    if (_parentRuns.TryGetValue(runId, out var parentId) &&
                        OwnerOf(parentId) is { } parentOwner &&
                        string.Equals(parentOwner, owner, StringComparison.Ordinal))
                    {
                        AddRunPolicy(parentId, policy);
                    }
                }
                else if (scope == ApprovalScope.Always &&
                         ToolApprovalPolicySemantics.IsAlwaysEligible(ctx.ToolName))
                {
                    lock (_alwaysLock) _alwaysAllowedPolicies.Add(policy);
                }
            }
        }

        return Task.FromResult(resolved);
    }

    /// <inheritdoc />
    public bool Deny(string runId, string requestId) => Resolve(runId, requestId, result: false);

    /// <inheritdoc />
    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        if (string.IsNullOrWhiteSpace(toolName) || OwnerOf(runId) is not { } owner)
            return false;

        var risk = ToolApprovalPolicySemantics.RiskFor(toolName);
        if (ToolApprovalPolicySemantics.IsAlwaysEligible(toolName))
        {
            lock (_alwaysLock)
            {
                if (_alwaysAllowedPolicies.Any(p => PolicyMatches(p, owner, toolName, risk)))
                    return true;
            }
        }

        if (IsInRunAllowlist(runId, owner, toolName, risk))
            return true;

        if (_parentRuns.TryGetValue(runId, out var parentId) &&
            OwnerOf(parentId) is { } parentOwner &&
            string.Equals(parentOwner, owner, StringComparison.Ordinal) &&
            IsInRunAllowlist(parentId, parentOwner, toolName, risk))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsKnownRequest(string runId, string requestId) =>
        _requestContext.TryGetValue(runId, out var runCtx) && runCtx.ContainsKey(requestId);

    /// <inheritdoc />
    public bool HasArmedApproval(string runId) =>
        // A pending entry exists only while a request is awaiting the operator's grant/deny decision;
        // it is removed in WaitForApprovalAsync's finally once resolved, denied, expired, or cancelled.
        _pending.TryGetValue(runId, out var runPending) && !runPending.IsEmpty;

    /// <inheritdoc />
    public ToolApprovalRequestState GetRequestState(string runId, string requestId)
    {
        if (_resolved.TryGetValue(runId, out var runResolved) &&
            runResolved.TryGetValue(requestId, out var state))
            return state;

        if (_pending.TryGetValue(runId, out var runPending) && runPending.ContainsKey(requestId))
            return ToolApprovalRequestState.Pending;

        return IsKnownRequest(runId, requestId)
            ? ToolApprovalRequestState.Denied
            : ToolApprovalRequestState.Unknown;
    }

    /// <inheritdoc />
    public void RegisterParentRun(string childRunId, string parentRunId) =>
        _parentRuns[childRunId] = parentRunId;

    /// <inheritdoc />
    public void Clear(string runId)
    {
        if (_pending.TryRemove(runId, out var runPending))
        {
            foreach (var tcs in runPending.Values)
                tcs.TrySetResult(false);
        }

        _requestContext.TryRemove(runId, out _);
        _resolved.TryRemove(runId, out _);
        _runScopedAllowlist.TryRemove(runId, out _);
        _parentRuns.TryRemove(runId, out _);
        // Always-allowed policies are intentionally not cleared — they survive run boundaries.
    }

    private bool Resolve(string runId, string requestId, bool result)
    {
        if (!_pending.TryGetValue(runId, out var runPending)) return false;
        if (!runPending.TryGetValue(requestId, out var tcs)) return false;
        var resolved = tcs.TrySetResult(result);
        if (resolved)
            MarkResolved(runId, requestId, result ? ToolApprovalRequestState.Approved : ToolApprovalRequestState.Denied);
        return resolved;
    }

    private void MarkResolved(string runId, string requestId, ToolApprovalRequestState state)
    {
        if (state is ToolApprovalRequestState.Denied &&
            _resolved.TryGetValue(runId, out var existingRun) &&
            existingRun.TryGetValue(requestId, out var existing) &&
            existing is ToolApprovalRequestState.Expired)
        {
            return;
        }

        var runResolved = _resolved.GetOrAdd(runId, _ => new ConcurrentDictionary<string, ToolApprovalRequestState>());
        runResolved[requestId] = state;
    }

    private void AddRunPolicy(string runId, ApprovalPolicy policy)
    {
        var allowlist = _runScopedAllowlist.GetOrAdd(runId, _ => []);
        lock (allowlist) allowlist.Add(policy);
    }

    private bool IsInRunAllowlist(string runId, string owner, string toolName, string risk)
    {
        if (!_runScopedAllowlist.TryGetValue(runId, out var allowlist)) return false;
        lock (allowlist)
            return allowlist.Any(p => PolicyMatches(p, owner, toolName, risk));
    }

    private string? OwnerOf(string runId)
    {
        try
        {
            var owner = _ownerResolver?.GetCanonicalOwner(runId);
            return string.IsNullOrWhiteSpace(owner) ? null : owner;
        }
        catch
        {
            return null;
        }
    }

    private static bool PolicyMatches(
        ApprovalPolicy policy,
        string owner,
        string toolName,
        string risk) =>
        string.Equals(policy.Owner, owner, StringComparison.Ordinal)
        && string.Equals(policy.ToolId, toolName, StringComparison.Ordinal)
        && string.Equals(policy.RiskSemantics, risk, StringComparison.Ordinal);

    private sealed record ApprovalPolicy(string Owner, string ToolId, string RiskSemantics);
}
