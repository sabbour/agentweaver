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

    // Run-scoped allowlist: runId → set of "toolName:url" policy keys
    private readonly ConcurrentDictionary<string, HashSet<string>> _runScopedAllowlist = new();

    // Always-allowed policies: set of "toolName:url" keys that survive across runs.
    // TODO: persist this to the database so always-allowed policies survive process restarts.
    private readonly HashSet<string> _alwaysAllowedPolicies = [];
    private readonly object _alwaysLock = new();

    // childRunId → parentRunId — populated by RegisterParentRun
    private readonly ConcurrentDictionary<string, string> _parentRuns = new();

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

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
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task.ConfigureAwait(false);
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
            // Look up the tool+url context that was stored by SetRequestContext.
            if (_requestContext.TryGetValue(runId, out var runCtx) &&
                runCtx.TryGetValue(requestId, out var ctx))
            {
                var policyKey = PolicyKey(ctx.ToolName, ctx.Url);

                if (scope == ApprovalScope.Run)
                {
                    AddRunPolicy(runId, policyKey);
                    // Propagate to parent so siblings see the policy via IsAutoApproved.
                    if (_parentRuns.TryGetValue(runId, out var parentId))
                        AddRunPolicy(parentId, policyKey);
                }
                else if (scope == ApprovalScope.Tool)
                {
                    // Tool-scoped: approve this tool for any URL this run.
                    var toolKey = PolicyKey(ctx.ToolName, null);
                    AddRunPolicy(runId, toolKey);
                    if (_parentRuns.TryGetValue(runId, out var parentId))
                        AddRunPolicy(parentId, toolKey);
                }
                else if (scope == ApprovalScope.Always)
                {
                    lock (_alwaysLock) _alwaysAllowedPolicies.Add(policyKey);
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
        var key = PolicyKey(toolName, url);
        var toolKey = PolicyKey(toolName, null); // tool-scoped wildcard (any URL)

        // Check always-allowed first (cheaper lookup with a small set).
        lock (_alwaysLock)
        {
            if (_alwaysAllowedPolicies.Contains(key)) return true;
            if (_alwaysAllowedPolicies.Contains(toolKey)) return true;
        }

        // Check run-scoped allowlist for this run.
        if (IsInRunAllowlist(runId, key, toolKey)) return true;

        // Check parent run's allowlist — a sibling child may have already been approved.
        if (_parentRuns.TryGetValue(runId, out var parentId) &&
            IsInRunAllowlist(parentId, key, toolKey)) return true;

        return false;
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
        _runScopedAllowlist.TryRemove(runId, out _);
        _parentRuns.TryRemove(runId, out _);
        // Always-allowed policies are intentionally not cleared — they survive run boundaries.
    }

    private bool Resolve(string runId, string requestId, bool result)
    {
        if (!_pending.TryGetValue(runId, out var runPending)) return false;
        if (!runPending.TryGetValue(requestId, out var tcs)) return false;
        return tcs.TrySetResult(result);
    }

    private void AddRunPolicy(string runId, string policyKey)
    {
        var allowlist = _runScopedAllowlist.GetOrAdd(runId, _ => []);
        lock (allowlist) allowlist.Add(policyKey);
    }

    private bool IsInRunAllowlist(string runId, string key, string toolKey)
    {
        if (!_runScopedAllowlist.TryGetValue(runId, out var allowlist)) return false;
        lock (allowlist) return allowlist.Contains(key) || allowlist.Contains(toolKey);
    }

    private static string PolicyKey(string toolName, string? url) =>
        $"{toolName}:{url ?? ""}";
}
