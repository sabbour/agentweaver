using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableToolApprovalGate(DurableRunControlState state) : IToolApprovalGate
{
    private const string GlobalRunId = "__agentweaver_tool_approvals__";
    private const string RequestContext = "tool.approval_context";
    private const string RequestResolved = "tool.approval_resolved";
    private const string PolicyGranted = "tool.approval_policy_granted";
    private const string ParentRegistered = "tool.approval_parent_registered";
    private const string RunCleared = "tool.approval_run_cleared";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly DurableRunControlState _state = state;

    public async Task<bool> WaitForApprovalAsync(
        string runId, string requestId, string toolName, string? url, TimeSpan timeout, CancellationToken ct)
    {
        _state.Append(runId, RequestContext, new ApprovalContext(requestId, toolName, url));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (IsAutoApproved(runId, toolName, url))
                return true;

            if (LatestContext(runId, requestId) is null)
                return false;

            var resolved = LatestResolution(runId, requestId);
            if (resolved is not null)
                return resolved.Value;

            try { await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        if (LatestContext(runId, requestId) is not null && LatestResolution(runId, requestId) is null)
            _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, false));

        return false;
    }

    public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope)
    {
        var context = LatestContext(runId, requestId);
        if (context is null || LatestResolution(runId, requestId) is not null)
            return Task.FromResult(false);

        if (scope != ApprovalScope.Once)
        {
            var policy = scope == ApprovalScope.Tool
                ? PolicyKey(context.ToolName, null)
                : PolicyKey(context.ToolName, context.Url);
            var targetRunId = scope == ApprovalScope.Always ? GlobalRunId : runId;
            _state.Append(targetRunId, PolicyGranted, new PolicyGrant(policy));

            if (scope is ApprovalScope.Run or ApprovalScope.Tool && ParentOf(runId) is { } parentId)
                _state.Append(parentId, PolicyGranted, new PolicyGrant(policy));
        }

        _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, true));
        return Task.FromResult(true);
    }

    public bool Deny(string runId, string requestId)
    {
        if (LatestContext(runId, requestId) is null || LatestResolution(runId, requestId) is not null)
            return false;

        _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, false));
        return true;
    }

    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        var key = PolicyKey(toolName, url);
        var toolKey = PolicyKey(toolName, null);
        if (HasPolicy(GlobalRunId, key, toolKey) || HasPolicy(runId, key, toolKey))
            return true;

        return ParentOf(runId) is { } parentId && HasPolicy(parentId, key, toolKey);
    }

    public void Clear(string runId) =>
        _state.Append(runId, RunCleared, new { });

    public void RegisterParentRun(string childRunId, string parentRunId) =>
        _state.Append(childRunId, ParentRegistered, new ParentRegistration(parentRunId));

    private ApprovalContext? LatestContext(string runId, string requestId) =>
        _state.Load(runId, RequestContext, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == RequestContext)
            .Select(e => JsonSerializer.Deserialize<ApprovalContext>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(c => c?.RequestId == requestId);

    private bool? LatestResolution(string runId, string requestId) =>
        _state.Load(runId, RequestResolved, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == RequestResolved)
            .Select(e => JsonSerializer.Deserialize<ApprovalResolution>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(r => r?.RequestId == requestId)
            ?.Approved;

    private string? ParentOf(string runId) =>
        _state.Load(runId, ParentRegistered, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == ParentRegistered)
            .Select(e => JsonSerializer.Deserialize<ParentRegistration>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault()
            ?.ParentRunId;

    private bool HasPolicy(string runId, string key, string toolKey) =>
        _state.Load(runId, PolicyGranted, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == PolicyGranted)
            .Select(e => JsonSerializer.Deserialize<PolicyGrant>(e.PayloadJson, JsonDefaults.Options))
            .Any(p => p?.PolicyKey == key || p?.PolicyKey == toolKey);

    private static string PolicyKey(string toolName, string? url) =>
        $"{toolName}:{url ?? ""}";

    private sealed record ApprovalContext(string RequestId, string ToolName, string? Url);
    private sealed record ApprovalResolution(string RequestId, bool Approved);
    private sealed record PolicyGrant(string PolicyKey);
    private sealed record ParentRegistration(string ParentRunId);
}

file static class DurableRunControlEventExtensions
{
    public static IEnumerable<RunEventRecord> TakeLastAfterClear(this IReadOnlyList<RunEventRecord> events)
    {
        var lastClear = events.LastOrDefault(e => e.EventType.EndsWith("_cleared", StringComparison.Ordinal));
        return lastClear is null ? events : events.Where(e => e.Sequence > lastClear.Sequence);
    }
}
