using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Runs;

public sealed class DurableToolApprovalGate(
    DurableRunControlState state,
    RunStreamStore? streams = null,
    ILogger<DurableToolApprovalGate>? logger = null,
    IRunStore? runStore = null) : IToolApprovalGate
{
    private const string OwnerPolicyBucketPrefix = "__agentweaver_tool_approvals_owner_sha256_v1__";
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
        {
            _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, false, true));
            logger?.LogWarning(
                "Tool approval timed out: runId={RunId} requestId={DisplayId}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId);
            EmitResolved(runId, requestId, approved: false, expired: true);
        }

        return false;
    }

    public async Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope)
    {
        var context = LatestContext(runId, requestId);
        if (context is null || LatestResolution(runId, requestId) is not null)
        {
            var reason = context is null ? "no_context" : "already_resolved";
            logger?.LogWarning(
                "GrantAsync rejected: runId={RunId} requestId={DisplayId} reason={Reason}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId, reason);
            return false;
        }

        if (scope != ApprovalScope.Once && !string.IsNullOrWhiteSpace(context.ToolName))
        {
            var owner = await OwnerOfAsync(runId).ConfigureAwait(false);
            if (owner is not null)
            {
                var policy = new PolicyGrant(
                    owner,
                    context.ToolName,
                    ToolApprovalPolicySemantics.RiskFor(context.ToolName));

                if (scope == ApprovalScope.Always)
                {
                    if (ToolApprovalPolicySemantics.IsAlwaysEligible(context.ToolName))
                        _state.Append(OwnerPolicyBucket(owner), PolicyGranted, policy);
                }
                else
                {
                    _state.Append(runId, PolicyGranted, policy);

                    if (ParentOf(runId) is { } parentId)
                    {
                        var parentOwner = await OwnerOfAsync(parentId).ConfigureAwait(false);
                        if (parentOwner is not null &&
                            string.Equals(parentOwner, owner, StringComparison.Ordinal))
                        {
                            _state.Append(parentId, PolicyGranted, policy);
                        }
                    }
                }
            }
        }

        _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, true, false));
        EmitResolved(runId, requestId, approved: true, expired: false);
        return true;
    }

    public bool Deny(string runId, string requestId)
    {
        if (LatestContext(runId, requestId) is null || LatestResolution(runId, requestId) is not null)
        {
            logger?.LogWarning(
                "Deny rejected: runId={RunId} requestId={DisplayId} reason={Reason}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId,
                LatestContext(runId, requestId) is null ? "no_context" : "already_resolved");
            return false;
        }

        _state.Append(runId, RequestResolved, new ApprovalResolution(requestId, false, false));
        EmitResolved(runId, requestId, approved: false, expired: false);
        return true;
    }

    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        var owner = OwnerOf(runId);
        if (owner is null)
            return false;

        var risk = ToolApprovalPolicySemantics.RiskFor(toolName);
        if (ToolApprovalPolicySemantics.IsAlwaysEligible(toolName) &&
            HasPolicy(OwnerPolicyBucket(owner), owner, toolName, risk))
            return true;

        if (HasPolicy(runId, owner, toolName, risk))
            return true;

        if (ParentOf(runId) is not { } parentId)
            return false;

        var parentOwner = OwnerOf(parentId);
        return parentOwner is not null
            && string.Equals(parentOwner, owner, StringComparison.Ordinal)
            && HasPolicy(parentId, parentOwner, toolName, risk);
    }

    public bool IsKnownRequest(string runId, string requestId) =>
        LatestContext(runId, requestId) is not null;

    public bool HasArmedApproval(string runId)
    {
        // A request is ARMED when its context was registered (after the last clear) but no resolution
        // has been recorded yet — i.e. the operator has not granted/denied and it has not expired.
        var pendingRequestIds = _state.Load(runId, RequestContext, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == RequestContext)
            .Select(e => JsonSerializer.Deserialize<ApprovalContext>(e.PayloadJson, JsonDefaults.Options)?.RequestId)
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal);

        return pendingRequestIds.Any(id => LatestResolution(runId, id!) is null);
    }

    public ToolApprovalRequestState GetRequestState(string runId, string requestId)
    {
        if (LatestResolutionRecord(runId, requestId) is { } resolution)
            return resolution.Expired
                ? ToolApprovalRequestState.Expired
                : resolution.Approved ? ToolApprovalRequestState.Approved : ToolApprovalRequestState.Denied;

        return LatestContext(runId, requestId) is not null
            ? ToolApprovalRequestState.Pending
            : ToolApprovalRequestState.Unknown;
    }

    public void Clear(string runId) =>
        _state.Append(runId, RunCleared, new { });

    public void RegisterParentRun(string childRunId, string parentRunId) =>
        _state.Append(childRunId, ParentRegistered, new ParentRegistration(parentRunId));

    private void EmitResolved(string runId, string requestId, bool approved, bool expired) =>
        streams?.Get(runId)?.RecordNext(EventTypes.ToolApprovalResolved, new
        {
            requestId,
            runId,
            approved,
            expired,
        });

    private ApprovalContext? LatestContext(string runId, string requestId) =>
        _state.Load(runId, RequestContext, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == RequestContext)
            .Select(e => JsonSerializer.Deserialize<ApprovalContext>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(c => c?.RequestId == requestId);

    private bool? LatestResolution(string runId, string requestId) =>
        LatestResolutionRecord(runId, requestId)?.Approved;

    private ApprovalResolution? LatestResolutionRecord(string runId, string requestId) =>
        _state.Load(runId, RequestResolved, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == RequestResolved)
            .Select(e => JsonSerializer.Deserialize<ApprovalResolution>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(r => r?.RequestId == requestId);

    private string? ParentOf(string runId) =>
        _state.Load(runId, ParentRegistered, RunCleared)
            .TakeLastAfterClear()
            .Where(e => e.EventType == ParentRegistered)
            .Select(e => JsonSerializer.Deserialize<ParentRegistration>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault()
            ?.ParentRunId;

    private bool HasPolicy(string bucketRunId, string owner, string toolName, string risk)
    {
        foreach (var record in _state.Load(bucketRunId, PolicyGranted, RunCleared)
                     .TakeLastAfterClear()
                     .Where(e => e.EventType == PolicyGranted))
        {
            try
            {
                var policy = JsonSerializer.Deserialize<PolicyGrant>(
                    record.PayloadJson,
                    JsonDefaults.Options);
                if (policy is not null
                    && string.Equals(policy.Owner, owner, StringComparison.Ordinal)
                    && string.Equals(policy.ToolId, toolName, StringComparison.Ordinal)
                    && string.Equals(policy.RiskSemantics, risk, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Malformed or legacy payloads fail closed.
            }
        }

        return false;
    }

    private async Task<string?> OwnerOfAsync(string runId)
    {
        if (runStore is null || !RunId.TryParse(runId, out var id))
            return null;

        try
        {
            var run = await runStore.GetAsync(id).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(run?.SubmittingUser) ? null : run.SubmittingUser;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to resolve tool-approval owner for run {RunId}", runId);
            return null;
        }
    }

    private string? OwnerOf(string runId)
    {
        if (runStore is null || !RunId.TryParse(runId, out var id))
            return null;

        try
        {
            var run = runStore.GetAsync(id).ConfigureAwait(false).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(run?.SubmittingUser) ? null : run.SubmittingUser;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to resolve tool-approval owner for run {RunId}", runId);
            return null;
        }
    }

    internal static string OwnerPolicyBucket(string owner)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(owner));
        return OwnerPolicyBucketPrefix + Convert.ToHexString(hash);
    }

    private sealed record ApprovalContext(string RequestId, string ToolName, string? Url);
    private sealed record ApprovalResolution(string RequestId, bool Approved, bool Expired = false);
    private sealed record PolicyGrant(string? Owner, string? ToolId, string? RiskSemantics);
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
