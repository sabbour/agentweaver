using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// In-memory <see cref="IToolApprovalGate"/> that uses <see cref="TaskCompletionSource{T}"/>
/// to suspend the permission handler until the operator grants or denies the request.
/// The gate is keyed by <c>(runId, requestId)</c>; each requestId may only be resolved once.
/// </summary>
public sealed class InMemoryToolApprovalGate : IToolApprovalGate, IProvisionalToolApprovalGate
{
    // Two-level dictionary: runId → requestId → TCS
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PendingApproval>> _pending = new();

    // runId → requestId → (toolName, url) — populated by SetRequestContext
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (string ToolName, string? Url)>> _requestContext = new();

    // runId → requestId → terminal state
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ToolApprovalRequestState>> _resolved = new();

    // Run-scoped allowlist: runId → server-derived owner/tool/risk policies.
    private readonly ConcurrentDictionary<string, HashSet<ScopedApprovalPolicy>> _runScopedAllowlist = new();

    // Always-allowed policies survive across runs, but only for the same resolved owner.
    // TODO: persist this to the database so always-allowed policies survive process restarts.
    private readonly HashSet<ScopedApprovalPolicy> _alwaysAllowedPolicies = [];
    private readonly object _alwaysLock = new();
    private readonly IToolApprovalOwnerResolver? _ownerResolver;

    // childRunId → parentRunId — populated by RegisterParentRun
    private readonly ConcurrentDictionary<string, string> _parentRuns = new();

    // Scoped policies are applied locally before the API commits their durable counterpart. Keep
    // their exact destinations until the API either finalizes or rolls back the provisional grant.
    private readonly ConcurrentDictionary<ScopeGrantKey, ScopeGrant> _scopeGrants = new();

    // A finalized pod-local scope remains lifecycle-bound. Retaining its exact grant lets Clear
    // remove only policies that must be revoked when the pod learns its run is no longer active.
    private readonly ConcurrentDictionary<ScopeGrantKey, ScopeGrant> _finalizedScopeGrants = new();

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

        var runPending = _pending.GetOrAdd(runId, _ => new ConcurrentDictionary<string, PendingApproval>());
        var pending = new PendingApproval(
            resolutionState => MarkResolved(runId, requestId, resolutionState));

        // Atomically register or replace an existing entry for this requestId.
        // If a duplicate arrives (retry), the previous TCS is resolved as denied so it doesn't leak.
        runPending.AddOrUpdate(requestId,
            addValueFactory: _ => pending,
            updateValueFactory: (_, existing) =>
            {
                existing.TryResolve(ToolApprovalRequestState.Denied);
                return pending;
            });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var reg = cts.Token.Register(() => pending.TryResolve(ToolApprovalRequestState.Expired));

            var result = await pending.Completion.Task.ConfigureAwait(false);
            return result;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, PendingApproval>>)runPending)
                .Remove(new KeyValuePair<string, PendingApproval>(requestId, pending));
        }
    }



    /// <inheritdoc />
    public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope)
    {
        var resolved = Resolve(
            runId,
            requestId,
            result: true,
            beforeCompletion: () => ApplyScopePolicy(runId, requestId, scope));
        return Task.FromResult(resolved);
    }

    /// <inheritdoc />
    public Task<bool> GrantProvisionalScopeAsync(
        string runId,
        string requestId,
        ApprovalScope scope,
        string scopeGrantId,
        DateTimeOffset expiresAt)
    {
        if (scope == ApprovalScope.Once
            || string.IsNullOrWhiteSpace(scopeGrantId)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }

        var resolved = Resolve(
            runId,
            requestId,
            result: true,
            beforeCompletion: () => ApplyScopePolicy(
                runId,
                requestId,
                scope,
                scopeGrantId,
                expiresAt));
        return Task.FromResult(resolved);
    }

    /// <inheritdoc />
    public bool Deny(string runId, string requestId) => Resolve(runId, requestId, result: false);

    /// <inheritdoc />
    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        ExpireProvisionalScopeGrants();
        if (string.IsNullOrWhiteSpace(toolName) || OwnerOf(runId) is not { } owner)
            return false;

        var risk = ToolApprovalPolicySemantics.RiskFor(toolName);
        if (ToolApprovalPolicySemantics.IsAlwaysEligible(toolName))
        {
            lock (_alwaysLock)
            {
                if (_alwaysAllowedPolicies.Any(p => PolicyMatches(p.Policy, owner, toolName, risk)))
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
    public ToolApprovalRequestContext? GetRequestContext(string runId, string requestId) =>
        _requestContext.TryGetValue(runId, out var runCtx) &&
        runCtx.TryGetValue(requestId, out var context)
            ? new ToolApprovalRequestContext(context.ToolName, context.Url)
            : null;

    /// <inheritdoc />
    public bool HasArmedApproval(string runId) =>
        // A pending entry exists only while a request is awaiting the operator's grant/deny decision;
        // it is removed in WaitForApprovalAsync's finally once resolved, denied, expired, or cancelled.
        _pending.TryGetValue(runId, out var runPending) && !runPending.IsEmpty;

    /// <inheritdoc />
    public string? GetScopeGrantId(string runId, string requestId)
    {
        ExpireProvisionalScopeGrants();
        return _scopeGrants.TryGetValue(new ScopeGrantKey(runId, requestId), out var grant)
            ? grant.Id
            : null;
    }

    /// <inheritdoc />
    public bool RollbackScopeGrant(string runId, string requestId, string scopeGrantId)
    {
        if (string.IsNullOrWhiteSpace(scopeGrantId))
            return false;

        var key = new ScopeGrantKey(runId, requestId);
        if (!_scopeGrants.TryGetValue(key, out var grant)
            || !string.Equals(grant.Id, scopeGrantId, StringComparison.Ordinal)
            || !((ICollection<KeyValuePair<ScopeGrantKey, ScopeGrant>>)_scopeGrants)
                .Remove(new KeyValuePair<ScopeGrantKey, ScopeGrant>(key, grant)))
        {
            return false;
        }

        RemoveScopePolicies(runId, grant);

        return true;
    }

    /// <inheritdoc />
    public bool FinalizeScopeGrant(string runId, string requestId, string scopeGrantId)
    {
        ExpireProvisionalScopeGrants();
        if (string.IsNullOrWhiteSpace(scopeGrantId))
            return false;

        var key = new ScopeGrantKey(runId, requestId);
        if (!_scopeGrants.TryGetValue(key, out var grant)
            || !string.Equals(grant.Id, scopeGrantId, StringComparison.Ordinal)
            || !((ICollection<KeyValuePair<ScopeGrantKey, ScopeGrant>>)_scopeGrants)
                .Remove(new KeyValuePair<ScopeGrantKey, ScopeGrant>(key, grant)))
        {
            return false;
        }

        _finalizedScopeGrants[key] = grant;
        return true;
    }

    /// <inheritdoc />
    public ToolApprovalRequestState GetRequestState(string runId, string requestId)
    {
        if (_pending.TryGetValue(runId, out var runPending) &&
            runPending.TryGetValue(requestId, out var pending))
        {
            return pending.ResolutionState ?? ToolApprovalRequestState.Pending;
        }

        if (_resolved.TryGetValue(runId, out var runResolved) &&
            runResolved.TryGetValue(requestId, out var state))
            return state;

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
            foreach (var pending in runPending.Values)
                pending.TryResolve(ToolApprovalRequestState.Denied);
        }

        _requestContext.TryRemove(runId, out _);
        _resolved.TryRemove(runId, out _);
        _runScopedAllowlist.TryRemove(runId, out _);
        _parentRuns.TryRemove(runId, out _);
        RemoveLifecycleBoundScopePolicies(runId, _scopeGrants);
        RemoveLifecycleBoundScopePolicies(runId, _finalizedScopeGrants);
        // Always-allowed policies are intentionally not cleared — they survive run boundaries.
    }

    private bool Resolve(
        string runId,
        string requestId,
        bool result,
        Action? beforeCompletion = null)
    {
        if (!_pending.TryGetValue(runId, out var runPending)) return false;
        if (!runPending.TryGetValue(requestId, out var pending)) return false;
        return pending.TryResolve(
            result ? ToolApprovalRequestState.Approved : ToolApprovalRequestState.Denied,
            beforeCompletion);
    }

    private void ApplyScopePolicy(
        string runId,
        string requestId,
        ApprovalScope scope,
        string? provisionalScopeGrantId = null,
        DateTimeOffset? provisionalExpiresAt = null)
    {
        if (scope == ApprovalScope.Once ||
            !_requestContext.TryGetValue(runId, out var runCtx) ||
            !runCtx.TryGetValue(requestId, out var context) ||
            string.IsNullOrWhiteSpace(context.ToolName) ||
            OwnerOf(runId) is not { } owner)
        {
            return;
        }

        var policy = new ApprovalPolicy(
            owner,
            context.ToolName,
            ToolApprovalPolicySemantics.RiskFor(context.ToolName));
        var parentRunId = scope == ApprovalScope.Run &&
            _parentRuns.TryGetValue(runId, out var parentId) &&
            OwnerOf(parentId) is { } parentOwner &&
            string.Equals(parentOwner, owner, StringComparison.Ordinal)
            ? parentId
            : null;
        ScopeGrant? scopeGrant = null;
        if (!string.IsNullOrWhiteSpace(provisionalScopeGrantId))
        {
            scopeGrant = new ScopeGrant(
                provisionalScopeGrantId,
                parentRunId,
                provisionalExpiresAt ?? DateTimeOffset.UtcNow + ToolApprovalScopeProtocol.ProvisionalScopeLifetime);
            var scopeGrantKey = new ScopeGrantKey(runId, requestId);
            if (_scopeGrants.TryRemove(scopeGrantKey, out var priorGrant))
                RemoveScopePolicies(runId, priorGrant);
            if (_finalizedScopeGrants.TryRemove(scopeGrantKey, out priorGrant))
                RemoveScopePolicies(runId, priorGrant);
            _scopeGrants[scopeGrantKey] = scopeGrant;
        }

        if (scope is ApprovalScope.Run or ApprovalScope.Tool)
        {
            AddRunPolicy(runId, scopeGrant?.Id, policy);
            if (scope == ApprovalScope.Run && parentRunId is not null)
                AddRunPolicy(parentRunId, scopeGrant?.Id, policy);
        }
        else if (scope == ApprovalScope.Always &&
                 ToolApprovalPolicySemantics.IsAlwaysEligible(context.ToolName))
        {
            lock (_alwaysLock) _alwaysAllowedPolicies.Add(new ScopedApprovalPolicy(scopeGrant?.Id, policy));
        }
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

    private void AddRunPolicy(string runId, string? scopeGrantId, ApprovalPolicy policy)
    {
        var allowlist = _runScopedAllowlist.GetOrAdd(runId, _ => []);
        lock (allowlist) allowlist.Add(new ScopedApprovalPolicy(scopeGrantId, policy));
    }

    private bool IsInRunAllowlist(string runId, string owner, string toolName, string risk)
    {
        if (!_runScopedAllowlist.TryGetValue(runId, out var allowlist)) return false;
        lock (allowlist)
            return allowlist.Any(p => PolicyMatches(p.Policy, owner, toolName, risk));
    }

    private void RemoveRunPolicies(string runId, string scopeGrantId)
    {
        if (!_runScopedAllowlist.TryGetValue(runId, out var allowlist))
            return;

        lock (allowlist)
        {
            allowlist.RemoveWhere(policy => policy.ScopeGrantId == scopeGrantId);
            if (allowlist.Count == 0)
                _runScopedAllowlist.TryRemove(
                    new KeyValuePair<string, HashSet<ScopedApprovalPolicy>>(runId, allowlist));
        }
    }

    private void RemoveScopePolicies(string runId, ScopeGrant grant)
    {
        RemoveRunPolicies(runId, grant.Id);
        if (grant.ParentRunId is not null)
            RemoveRunPolicies(grant.ParentRunId, grant.Id);
        lock (_alwaysLock)
            _alwaysAllowedPolicies.RemoveWhere(policy => policy.ScopeGrantId == grant.Id);
    }

    private void ExpireProvisionalScopeGrants()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _scopeGrants)
        {
            if (pair.Value.ExpiresAt > now
                || !((ICollection<KeyValuePair<ScopeGrantKey, ScopeGrant>>)_scopeGrants).Remove(pair))
            {
                continue;
            }

            RemoveScopePolicies(pair.Key.RunId, pair.Value);
        }
    }

    private void RemoveLifecycleBoundScopePolicies(
        string runId,
        ConcurrentDictionary<ScopeGrantKey, ScopeGrant> scopeGrants)
    {
        foreach (var pair in scopeGrants)
        {
            if (!string.Equals(pair.Key.RunId, runId, StringComparison.Ordinal)
                || !((ICollection<KeyValuePair<ScopeGrantKey, ScopeGrant>>)scopeGrants).Remove(pair))
            {
                continue;
            }

            RemoveScopePolicies(runId, pair.Value);
        }
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

    private sealed class PendingApproval
    {
        private readonly object _sync = new();
        private readonly Action<ToolApprovalRequestState> _markResolved;
        private ToolApprovalRequestState? _resolutionState;

        internal PendingApproval(Action<ToolApprovalRequestState> markResolved) =>
            _markResolved = markResolved;

        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ToolApprovalRequestState? ResolutionState
        {
            get
            {
                lock (_sync)
                {
                    return _resolutionState;
                }
            }
        }

        internal bool TryResolve(
            ToolApprovalRequestState resolutionState,
            Action? beforeCompletion = null)
        {
            lock (_sync)
            {
                if (_resolutionState is not null)
                    return false;

                _resolutionState = resolutionState;
                beforeCompletion?.Invoke();
                _markResolved(resolutionState);
                Completion.TrySetResult(resolutionState == ToolApprovalRequestState.Approved);
                return true;
            }
        }
    }

    private sealed record ApprovalPolicy(string Owner, string ToolId, string RiskSemantics);
    private sealed record ScopedApprovalPolicy(string? ScopeGrantId, ApprovalPolicy Policy);
    private sealed record ScopeGrant(string Id, string? ParentRunId, DateTimeOffset ExpiresAt);
    private sealed record ScopeGrantKey(string RunId, string RequestId);
}
