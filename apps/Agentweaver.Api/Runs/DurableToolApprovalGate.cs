using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Runs;

public interface IAgentHostToolApprovalPersistence
{
    Task<bool> PersistAgentHostApprovalAsync(
        string runId,
        string requestId,
        string toolName,
        string? url,
        ApprovalScope scope);
}

public sealed class DurableToolApprovalGate(
    DurableRunControlState state,
    RunStreamStore? streams = null,
    ILogger<DurableToolApprovalGate>? logger = null,
    IRunStore? runStore = null,
    RunActiveClaimGuard? runActiveClaimGuard = null) : IToolApprovalGate, IAgentHostToolApprovalPersistence
{
    private const string ProjectPolicyBucketPrefix = "__agentweaver_tool_approvals_project_owner_sha256_v1__";
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

        if (await ResolveDenialAsync(runId, requestId, expired: true).ConfigureAwait(false))
        {
            logger?.LogWarning(
                "Tool approval timed out: runId={RunId} requestId={DisplayId}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId);
            EmitResolved(runId, requestId, approved: false, expired: true);
            return false;
        }

        // A concurrent grant or denial won the durable claim while this timeout was pending.
        // Read its terminal resolution so an approved request is not denied after timing out.
        return LatestResolution(runId, requestId) ?? false;
    }

    public async Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope)
    {
        var resolved = await ResolveAndPersistAsync(runId, requestId, scope, context: null)
            .ConfigureAwait(false);
        if (!resolved)
        {
            var reason = LatestContext(runId, requestId) is null ? "no_context" : "already_resolved";
            logger?.LogWarning(
                "GrantAsync rejected: runId={RunId} requestId={DisplayId} reason={Reason}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId, reason);
            return false;
        }

        EmitResolved(runId, requestId, approved: true, expired: false);
        return true;
    }

    /// <summary>
    /// Persists a scope selected for an AgentHost-owned request after the pod has successfully
    /// applied that request's local approval. The AgentHost supplies its server-captured tool
    /// context; the API never trusts UI metadata when creating a durable policy.
    /// </summary>
    public async Task<bool> PersistAgentHostApprovalAsync(
        string runId,
        string requestId,
        string toolName,
        string? url,
        ApprovalScope scope)
    {
        if (scope == ApprovalScope.Once || string.IsNullOrWhiteSpace(toolName))
            return false;

        return await ResolveAndPersistAsync(
                runId,
                requestId,
                scope,
                new ApprovalContext(requestId, toolName, url))
            .ConfigureAwait(false);
    }

    public bool Deny(string runId, string requestId)
    {
        var resolved = ResolveDenialAsync(runId, requestId, expired: false).GetAwaiter().GetResult();
        if (!resolved)
        {
            logger?.LogWarning(
                "Deny rejected: runId={RunId} requestId={DisplayId} reason={Reason}",
                runId, requestId.Length >= 8 ? requestId[..8] : requestId,
                LatestContext(runId, requestId) is null ? "no_context" : "already_resolved");
            return false;
        }

        EmitResolved(runId, requestId, approved: false, expired: false);
        return true;
    }

    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        var subject = SubjectOf(runId);
        if (subject is null)
            return false;

        var risk = ToolApprovalPolicySemantics.RiskFor(toolName);
        if (ToolApprovalPolicySemantics.IsAlwaysEligible(toolName) &&
            subject.ProjectId is not null &&
            HasPolicy(ProjectPolicyBucket(subject.ProjectId.Value, subject.Owner), subject, toolName, risk))
            return true;

        if (HasPolicy(runId, subject, toolName, risk))
            return true;

        if (ParentOf(runId) is not { } parentId)
            return false;

        var parentSubject = SubjectOf(parentId);
        return parentSubject == subject
            && HasPolicy(parentId, parentSubject, toolName, risk);
    }

    public bool IsKnownRequest(string runId, string requestId) =>
        LatestContext(runId, requestId) is not null;

    public ToolApprovalRequestContext? GetRequestContext(string runId, string requestId) =>
        LatestContext(runId, requestId) is { } context
            ? new ToolApprovalRequestContext(context.ToolName, context.Url)
            : null;

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

    private async Task<bool> ResolveAndPersistAsync(
        string runId,
        string requestId,
        ApprovalScope scope,
        ApprovalContext? context)
    {
        var subject = scope == ApprovalScope.Once
            ? null
            : await SubjectOfAsync(runId).ConfigureAwait(false);
        var parentId = scope == ApprovalScope.Once ? null : ParentOf(runId);
        var parentSubject = parentId is null
            ? null
            : await SubjectOfAsync(parentId).ConfigureAwait(false);
        var lockIds = PolicyLockStreamIds(runId, parentId, subject, parentSubject, scope);

        // A non-once scope both reads a run's active status and commits a durable policy grant.
        // Postgres closes that interval with FOR UPDATE inside the same transaction below. SQLite
        // (and any other non-Npgsql provider) keeps run records and RunEvents in separate stores
        // that cannot share one ACID transaction, so an in-process claim brackets the whole
        // read-then-commit critical section below: no guarded run-store status transition can
        // complete while a grant for the same run is mid-flight, and vice versa. This applies to
        // EVERY non-once scope from EVERY caller -- standard API GrantAsync (context: null) and
        // AgentHost-context PersistAgentHostApprovalAsync alike -- there is no context-based
        // carve-out for the active-run requirement.
        var activeClaims = new List<IAsyncDisposable>();
        if (scope != ApprovalScope.Once
            && runActiveClaimGuard is not null)
        {
            foreach (var claimRunId in ActiveClaimRunIds(runId, parentId, subject, parentSubject, scope)
                         .Distinct()
                         .OrderBy(id => id.ToString(), StringComparer.Ordinal))
            {
                activeClaims.Add(await runActiveClaimGuard.AcquireAsync(claimRunId, CancellationToken.None)
                    .ConfigureAwait(false));
            }
        }

        try
        {
            return await _state.ExecuteExclusivelyAsync(
                lockIds,
                async (db, ct) =>
                {
                    var records = await db.RunEvents
                        .AsNoTracking()
                        .Where(e => e.RunId == runId
                            && (e.EventType == RequestContext
                                || e.EventType == RequestResolved
                                || e.EventType == RunCleared))
                        .OrderBy(e => e.Sequence)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var persistedContext = LatestContext(records, requestId);
                    var resolvedContext = persistedContext ?? context;
                    if (resolvedContext is null || LatestResolutionRecord(records, requestId) is not null)
                        return false;
                    var policyDestinations = BuildPolicyDestinations(
                        runId, parentId, subject, parentSubject, resolvedContext.ToolName, scope);

                    // Persisting a scoped policy claims the same run rows status transitions
                    // update. The requesting child and every parent run that would receive a
                    // policy must still be active. Postgres locks and checks all of those rows
                    // in this transaction; SQLite holds the corresponding in-process claims
                    // across the separate run-store reads and this policy commit.
                    if (scope != ApprovalScope.Once
                        && !await LockAndRequireActiveRunsAsync(
                            db, ActivePolicyRunIds(runId, policyDestinations), ct).ConfigureAwait(false))
                    {
                        return false;
                    }

                    // Claiming the pending request and writing every selected policy occur in this
                    // transaction under the same sorted advisory locks. A losing once/always race
                    // observes this resolution and cannot create a broader policy.
                    var events = new List<PendingEvent>();
                    if (persistedContext is null)
                        events.Add(new PendingEvent(runId, RequestContext, resolvedContext));
                    events.Add(new PendingEvent(
                        runId,
                        RequestResolved,
                        new ApprovalResolution(requestId, true, false)));
                    events.AddRange(policyDestinations.Select(destination =>
                        new PendingEvent(destination.StreamId, PolicyGranted, destination.Policy)));

                    var nextSequences = await NextSequencesAsync(
                            db,
                            events.Select(e => e.StreamId).Distinct(StringComparer.Ordinal),
                            ct)
                        .ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    foreach (var entry in events)
                    {
                        db.RunEvents.Add(new RunEventRecord
                        {
                            RunId = entry.StreamId,
                            Sequence = nextSequences[entry.StreamId]++,
                            EventType = entry.EventType,
                            PayloadJson = JsonSerializer.Serialize(entry.Payload, JsonDefaults.Options),
                            CreatedAt = now,
                        });
                    }

                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (var activeClaim in activeClaims.AsEnumerable().Reverse())
                await activeClaim.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> LockAndRequireActiveRunsAsync(
        MemoryDbContext db,
        IEnumerable<string> runIds,
        CancellationToken ct)
    {
        foreach (var runId in runIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!await LockAndRequireActiveRunAsync(db, runId, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    private async Task<bool> LockAndRequireActiveRunAsync(
        MemoryDbContext db,
        string runId,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var lockedRun = await db.Runs
                .FromSqlInterpolated($"SELECT * FROM runs WHERE run_id = {runId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            return lockedRun?.Status == RunStatus.InProgress.ToApiString();
        }

        if (runStore is null || !RunId.TryParse(runId, out var id))
            return false;

        var run = await runStore.GetAsync(id, ct).ConfigureAwait(false);
        return run?.Status == RunStatus.InProgress;
    }

    private static IEnumerable<RunId> ActiveClaimRunIds(
        string runId,
        string? parentId,
        ApprovalSubject? subject,
        ApprovalSubject? parentSubject,
        ApprovalScope scope)
    {
        if (RunId.TryParse(runId, out var sourceRunId))
            yield return sourceRunId;

        if (scope != ApprovalScope.Always
            && parentId is not null
            && subject is not null
            && parentSubject == subject
            && RunId.TryParse(parentId, out var parentRunId))
        {
            yield return parentRunId;
        }
    }

    private static IEnumerable<string> ActivePolicyRunIds(
        string runId,
        IReadOnlyList<PolicyDestination> policyDestinations)
    {
        yield return runId;
        foreach (var destination in policyDestinations)
        {
            if (RunId.TryParse(destination.StreamId, out _))
                yield return destination.StreamId;
        }
    }

    private Task<bool> ResolveDenialAsync(string runId, string requestId, bool expired) =>
        _state.ExecuteExclusivelyAsync(
            [runId],
            async (db, ct) =>
            {
                var records = await db.RunEvents
                    .AsNoTracking()
                    .Where(e => e.RunId == runId
                        && (e.EventType == RequestContext
                            || e.EventType == RequestResolved
                            || e.EventType == RunCleared))
                    .OrderBy(e => e.Sequence)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                if (LatestContext(records, requestId) is null ||
                    LatestResolutionRecord(records, requestId) is not null)
                {
                    return false;
                }

                var nextSequences = await NextSequencesAsync(db, [runId], ct).ConfigureAwait(false);
                db.RunEvents.Add(new RunEventRecord
                {
                    RunId = runId,
                    Sequence = nextSequences[runId],
                    EventType = RequestResolved,
                    PayloadJson = JsonSerializer.Serialize(
                        new ApprovalResolution(requestId, false, expired),
                        JsonDefaults.Options),
                    CreatedAt = DateTime.UtcNow,
                });
                return true;
            },
            CancellationToken.None);

    private static IEnumerable<string> PolicyLockStreamIds(
        string runId,
        string? parentId,
        ApprovalSubject? subject,
        ApprovalSubject? parentSubject,
        ApprovalScope scope)
    {
        yield return runId;
        if (subject is null || scope == ApprovalScope.Once)
            yield break;

        if (scope == ApprovalScope.Always)
        {
            if (subject.ProjectId is not null)
                yield return ProjectPolicyBucket(subject.ProjectId.Value, subject.Owner);
            yield break;
        }

        if (parentId is not null && parentSubject == subject)
            yield return parentId;
    }

    private static IReadOnlyList<PolicyDestination> BuildPolicyDestinations(
        string runId,
        string? parentId,
        ApprovalSubject? subject,
        ApprovalSubject? parentSubject,
        string? toolName,
        ApprovalScope scope)
    {
        if (subject is null || string.IsNullOrWhiteSpace(toolName) || scope == ApprovalScope.Once)
            return [];

        var policy = new PolicyGrant(
            subject.ProjectId?.ToString(),
            subject.Owner,
            toolName,
            ToolApprovalPolicySemantics.RiskFor(toolName));

        if (scope == ApprovalScope.Always)
        {
            // A durable "always" grant must have a real project boundary. Legacy runs still
            // resolve their current request, but cannot create a policy outside that project.
            return subject.ProjectId is not null
                && ToolApprovalPolicySemantics.IsAlwaysEligible(toolName)
                ? [new PolicyDestination(ProjectPolicyBucket(subject.ProjectId.Value, subject.Owner), policy)]
                : [];
        }

        var destinations = new List<PolicyDestination> { new(runId, policy) };
        if (parentId is not null && parentSubject == subject)
            destinations.Add(new PolicyDestination(parentId, policy));
        return destinations;
    }

    private static async Task<Dictionary<string, int>> NextSequencesAsync(
        MemoryDbContext db,
        IEnumerable<string> streamIds,
        CancellationToken ct)
    {
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var streamId in streamIds)
        {
            var max = await db.RunEvents
                .Where(e => e.RunId == streamId)
                .Select(e => (int?)e.Sequence)
                .MaxAsync(ct)
                .ConfigureAwait(false);
            next[streamId] = (max ?? 0) + 1;
        }

        return next;
    }

    private void EmitResolved(string runId, string requestId, bool approved, bool expired) =>
        streams?.Get(runId)?.RecordNext(EventTypes.ToolApprovalResolved, new
        {
            requestId,
            runId,
            approved,
            expired,
        });

    private ApprovalContext? LatestContext(string runId, string requestId) =>
        LatestContext(_state.Load(runId, RequestContext, RunCleared), requestId);

    private static ApprovalContext? LatestContext(
        IReadOnlyList<RunEventRecord> records,
        string requestId) =>
        records.TakeLastAfterClear()
            .Where(e => e.EventType == RequestContext)
            .Select(e => JsonSerializer.Deserialize<ApprovalContext>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(c => c?.RequestId == requestId);

    private bool? LatestResolution(string runId, string requestId) =>
        LatestResolutionRecord(runId, requestId)?.Approved;

    private ApprovalResolution? LatestResolutionRecord(string runId, string requestId) =>
        LatestResolutionRecord(_state.Load(runId, RequestResolved, RunCleared), requestId);

    private static ApprovalResolution? LatestResolutionRecord(
        IReadOnlyList<RunEventRecord> records,
        string requestId) =>
        records.TakeLastAfterClear()
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

    private bool HasPolicy(string bucketRunId, ApprovalSubject subject, string toolName, string risk)
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
                    && string.Equals(policy.ProjectId, subject.ProjectId?.ToString(), StringComparison.Ordinal)
                    && string.Equals(policy.Owner, subject.Owner, StringComparison.Ordinal)
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

    private async Task<ApprovalSubject?> SubjectOfAsync(string runId)
    {
        if (runStore is null || !RunId.TryParse(runId, out var id))
            return null;

        try
        {
            var run = await runStore.GetAsync(id).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(run?.SubmittingUser)
                ? null
                : new ApprovalSubject(run.SubmittingUser, run.ProjectId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to resolve tool-approval subject for run {RunId}", runId);
            return null;
        }
    }

    private ApprovalSubject? SubjectOf(string runId)
    {
        if (runStore is null || !RunId.TryParse(runId, out var id))
            return null;

        try
        {
            var run = runStore.GetAsync(id).ConfigureAwait(false).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(run?.SubmittingUser)
                ? null
                : new ApprovalSubject(run.SubmittingUser, run.ProjectId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to resolve tool-approval subject for run {RunId}", runId);
            return null;
        }
    }

    internal static string ProjectPolicyBucket(ProjectId projectId, string owner)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId}:{owner}"));
        return ProjectPolicyBucketPrefix + Convert.ToHexString(hash);
    }

    private sealed record ApprovalContext(string RequestId, string ToolName, string? Url);
    private sealed record ApprovalResolution(string RequestId, bool Approved, bool Expired = false);
    private sealed record PolicyGrant(string? ProjectId, string? Owner, string? ToolId, string? RiskSemantics);
    private sealed record ParentRegistration(string ParentRunId);
    private sealed record ApprovalSubject(string Owner, ProjectId? ProjectId);
    private sealed record PolicyDestination(string StreamId, PolicyGrant Policy);
    private sealed record PendingEvent(string StreamId, string EventType, object Payload);
}

file static class DurableRunControlEventExtensions
{
    public static IEnumerable<RunEventRecord> TakeLastAfterClear(this IReadOnlyList<RunEventRecord> events)
    {
        var lastClear = events.LastOrDefault(e => e.EventType.EndsWith("_cleared", StringComparison.Ordinal));
        return lastClear is null ? events : events.Where(e => e.Sequence > lastClear.Sequence);
    }
}
