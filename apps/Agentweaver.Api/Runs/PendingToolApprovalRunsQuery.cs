using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Canonical read model for actionable approvals. It folds durable gate contexts, public cards,
/// coordinator child projections, resolutions, expiry, clears, and run lifecycle into one set.
/// Board badges, notifications, and the run review UI must consume this projection instead of
/// independently interpreting the event stream. The projection is launch-path and policy agnostic:
/// auto-approval affects whether the gate creates a pending request, not whether an already-persisted
/// request is actionable. It must not be inferred from backlog heartbeat pickup settings.
/// </summary>
public sealed class PendingToolApprovalRunsQuery
{
    private const string ToolApprovalContext = "tool.approval_context";
    private const string ToolApprovalRunCleared = "tool.approval_run_cleared";
    private const string ShellApprovalRequired = "shell.approval_required";
    private const string ShellApproved = "shell.approved";
    private const string ShellDenied = "shell.denied";
    private const string ShellConsumed = "shell.approval_consumed";
    private const string ShellCleared = "shell.approvals_cleared";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunStore _runStore;

    public PendingToolApprovalRunsQuery(IServiceScopeFactory scopeFactory, IRunStore runStore)
    {
        _scopeFactory = scopeFactory;
        _runStore = runStore;
    }

    public async Task<HashSet<string>> GetRunIdsWithPendingApprovalAsync(
        IReadOnlyCollection<string> runIds, CancellationToken ct)
    {
        var approvals = await GetPendingApprovalsAsync(runIds, ct).ConfigureAwait(false);
        return approvals.Select(item => item.RootRunId).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, PendingToolApproval>> GetPendingApprovalDetailsAsync(
        IReadOnlyCollection<string> runIds, CancellationToken ct)
    {
        var approvals = await GetPendingApprovalsAsync(runIds, ct).ConfigureAwait(false);
        return approvals
            .GroupBy(item => item.RootRunId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var latest = group.OrderByDescending(item => item.RequestedUtc).First();
                    return new PendingToolApproval(
                        latest.RequestId,
                        latest.ToolName,
                        latest.RequestedUtc,
                        group.Count());
                },
                StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ActionableApproval>> GetPendingApprovalsAsync(
        IReadOnlyCollection<string> requestedRunIds, CancellationToken ct)
    {
        var scopes = await BuildScopesAsync(requestedRunIds, ct).ConfigureAwait(false);
        if (scopes.StreamToOwner.Count == 0)
            return Array.Empty<ActionableApproval>();

        var streamIds = scopes.StreamToOwner.Keys.ToList();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = await db.RunEvents.AsNoTracking()
            .Where(e => streamIds.Contains(e.RunId) && (
                e.EventType == EventTypes.ToolApprovalRequired
                || e.EventType == ToolApprovalContext
                || e.EventType == EventTypes.CoordinatorChildApprovalRequired
                || e.EventType == ShellApprovalRequired
                || e.EventType == EventTypes.ToolApprovalResolved
                || e.EventType == EventTypes.CoordinatorChildApprovalResolved
                || e.EventType == EventTypes.ToolResult
                || e.EventType == EventTypes.ToolError
                || e.EventType == ToolApprovalRunCleared
                || e.EventType == ShellApproved
                || e.EventType == ShellDenied
                || e.EventType == ShellConsumed
                || e.EventType == ShellCleared))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var lastToolClear = events
            .Where(e => e.EventType == ToolApprovalRunCleared)
            .GroupBy(e => e.RunId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(e => e.Sequence), StringComparer.Ordinal);
        var lastShellClear = events
            .Where(e => e.EventType == ShellCleared)
            .GroupBy(e => e.RunId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(e => e.Sequence), StringComparer.Ordinal);

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            if (!TryResolutionIdentity(evt, scopes, out var ownerStreamId, out var requestId))
                continue;
            resolved.Add(Key(ownerStreamId, requestId));
        }

        var requests = new Dictionary<string, ActionableApproval>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            if (!TryRequest(evt, scopes, lastToolClear, lastShellClear, out var approval))
                continue;

            var key = Key(approval.OwningStreamId, approval.RequestId);
            if (resolved.Contains(key) || approval.ExpiresUtc is { } expires && expires <= DateTimeOffset.UtcNow)
                continue;

            if (!scopes.ActiveOwnerRunIds.Contains(approval.ActionRunId))
                continue;

            if (!requests.TryGetValue(key, out var existing)
                || IsBetterRequestSource(approval, existing))
            {
                requests[key] = approval;
            }
        }

        return requests.Values
            .OrderBy(item => item.RequestedUtc)
            .ThenBy(item => item.RequestId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<ApprovalScopes> BuildScopesAsync(
        IReadOnlyCollection<string> requestedRunIds,
        CancellationToken ct)
    {
        var streamToOwner = new Dictionary<string, ApprovalOwner>(StringComparer.Ordinal);
        var activeOwnerRunIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedRoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var requestedId in requestedRunIds.Distinct(StringComparer.Ordinal))
        {
            if (!RunId.TryParse(requestedId, out var parsed))
                continue;

            var requested = await _runStore.GetAsync(parsed, ct).ConfigureAwait(false);
            if (requested is null)
                continue;

            var rootId = requested.ParentRunId ?? requested.Id.ToString();
            if (!visitedRoots.Add(rootId) || !RunId.TryParse(rootId, out var parsedRoot))
                continue;

            var root = await _runStore.GetAsync(parsedRoot, ct).ConfigureAwait(false);
            if (root is null)
                continue;

            AddOwner(root, rootId, streamToOwner, activeOwnerRunIds);
            foreach (var suffix in CoordinatorSubRunIds.Suffixes)
            {
                streamToOwner[rootId + suffix] = new ApprovalOwner(
                    rootId + suffix,
                    rootId,
                    rootId,
                    root.Status == RunStatus.InProgress);
            }

            var children = await _runStore.GetRunsByParentAsync(rootId, ct).ConfigureAwait(false);
            foreach (var child in children)
                AddOwner(child, rootId, streamToOwner, activeOwnerRunIds);
        }

        return new ApprovalScopes(streamToOwner, activeOwnerRunIds);
    }

    private static void AddOwner(
        Run run,
        string rootRunId,
        IDictionary<string, ApprovalOwner> streamToOwner,
        ISet<string> activeOwnerRunIds)
    {
        var runId = run.Id.ToString();
        var active = run.Status == RunStatus.InProgress && run.ArchivedAt is null;
        streamToOwner[runId] = new ApprovalOwner(runId, runId, rootRunId, active);
        if (active)
            activeOwnerRunIds.Add(runId);
    }

    private static bool TryRequest(
        RunEventRecord evt,
        ApprovalScopes scopes,
        IReadOnlyDictionary<string, int> lastToolClear,
        IReadOnlyDictionary<string, int> lastShellClear,
        out ActionableApproval approval)
    {
        approval = default!;
        var shell = evt.EventType == ShellApprovalRequired;
        var requestEvent = evt.EventType == EventTypes.ToolApprovalRequired
            || evt.EventType == ToolApprovalContext
            || evt.EventType == EventTypes.CoordinatorChildApprovalRequired
            || shell;
        if (!requestEvent || !scopes.StreamToOwner.TryGetValue(evt.RunId, out var streamOwner))
            return false;

        if (shell
            ? lastShellClear.TryGetValue(evt.RunId, out var shellClear) && evt.Sequence <= shellClear
            : lastToolClear.TryGetValue(evt.RunId, out var toolClear) && evt.Sequence <= toolClear)
        {
            return false;
        }

        using var payload = Parse(evt.PayloadJson);
        if (payload is null)
            return false;
        var root = payload.RootElement;
        var requestId = shell
            ? GetString(root, "commandHash", "command_hash", "CommandHash")
            : GetString(root, "requestId", "request_id", "RequestId");
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        var owner = streamOwner;
        if (evt.EventType == EventTypes.CoordinatorChildApprovalRequired)
        {
            var childRunId = GetString(root, "childRunId", "child_run_id");
            if (childRunId is null || !scopes.StreamToOwner.TryGetValue(childRunId, out owner))
                return false;
        }

        var requestedUtc = GetDate(root, "requestedAt", "requested_at")
            ?? new DateTimeOffset(DateTime.SpecifyKind(evt.CreatedAt, DateTimeKind.Utc));
        approval = new ActionableApproval(
            owner.RootRunId,
            owner.StreamId,
            owner.ActionRunId,
            requestId,
            shell ? "run_command" : GetString(root, "toolName", "tool_name", "ToolName"),
            GetString(root, "url", "Url"),
            shell ? GetString(root, "command") : null,
            GetString(root, "message", "intention", "command"),
            requestedUtc,
            GetDate(root, "expiresAt", "expires_at"),
            shell,
            evt.Sequence,
            evt.EventType != ToolApprovalContext);
        return true;
    }

    private static bool TryResolutionIdentity(
        RunEventRecord evt,
        ApprovalScopes scopes,
        out string ownerStreamId,
        out string requestId)
    {
        ownerStreamId = string.Empty;
        requestId = string.Empty;
        if (!scopes.StreamToOwner.TryGetValue(evt.RunId, out var owner))
            return false;

        using var payload = Parse(evt.PayloadJson);
        if (payload is null)
            return false;
        var root = payload.RootElement;

        if (evt.EventType == EventTypes.CoordinatorChildApprovalResolved)
        {
            var childRunId = GetString(root, "childRunId", "child_run_id");
            if (childRunId is null || !scopes.StreamToOwner.TryGetValue(childRunId, out owner))
                return false;
        }

        requestId = evt.EventType switch
        {
            EventTypes.ToolApprovalResolved or EventTypes.CoordinatorChildApprovalResolved =>
                GetString(root, "requestId", "request_id") ?? string.Empty,
            EventTypes.ToolResult or EventTypes.ToolError =>
                GetString(root, "callId", "call_id") ?? string.Empty,
            ShellApproved or ShellDenied or ShellConsumed =>
                GetString(root, "commandHash", "command_hash", "CommandHash") ?? string.Empty,
            _ => string.Empty,
        };
        ownerStreamId = owner.StreamId;
        return requestId.Length > 0;
    }

    private static bool IsBetterRequestSource(ActionableApproval candidate, ActionableApproval existing) =>
        candidate.HasPublicDetails != existing.HasPublicDetails
            ? candidate.HasPublicDetails
            : candidate.SourceSequence > existing.SourceSequence;

    private static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static DateTimeOffset? GetDate(JsonElement root, params string[] names)
    {
        var value = GetString(root, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string Key(string ownerStreamId, string requestId) => $"{ownerStreamId}\0{requestId}";

    private sealed record ApprovalOwner(
        string StreamId,
        string ActionRunId,
        string RootRunId,
        bool Active);

    private sealed record ApprovalScopes(
        IReadOnlyDictionary<string, ApprovalOwner> StreamToOwner,
        IReadOnlySet<string> ActiveOwnerRunIds);
}

public sealed record ActionableApproval(
    string RootRunId,
    string OwningStreamId,
    string ActionRunId,
    string RequestId,
    string? ToolName,
    string? Url,
    string? Command,
    string? Message,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? ExpiresUtc,
    bool IsShell,
    int SourceSequence,
    bool HasPublicDetails);

public sealed record PendingToolApproval(
    string RequestId,
    string? ToolName,
    DateTimeOffset CreatedUtc,
    int Count);
