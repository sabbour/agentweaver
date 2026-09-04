using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Single source of truth for "which of these runs currently have an unresolved tool-approval
/// gate", shared by <see cref="BoardProjectionService"/> (board card badges, #issue n/a) and
/// <see cref="Agentweaver.Api.Notifications.NotificationsService"/> (tool_approval notifications,
/// #321). A run is pending iff it has at least one public <c>tool.approval_required</c> event or
/// durable <c>tool.approval_context</c> event whose requestId has no matching
/// <c>tool.approval_resolved</c> requestId or <c>tool.result</c>/<c>tool.error</c> callId yet —
/// the same resolution logic used by the frontend's own pending-approval projection.
/// </summary>
public sealed class PendingToolApprovalRunsQuery
{
    private const string ToolApprovalContextEventType = "tool.approval_context";
    private readonly IServiceScopeFactory _scopeFactory;

    public PendingToolApprovalRunsQuery(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HashSet<string>> GetRunIdsWithPendingApprovalAsync(
        IReadOnlyCollection<string> runIds, CancellationToken ct)
    {
        var details = await GetPendingApprovalDetailsAsync(runIds, ct).ConfigureAwait(false);
        return new HashSet<string>(details.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Same pending-approval resolution as <see cref="GetRunIdsWithPendingApprovalAsync"/>, but also
    /// returns the requestId/toolName/timestamp of the most recent unresolved request per run — used
    /// by <see cref="Agentweaver.Api.Notifications.NotificationsService"/> to build a stable
    /// notification id and a "created" timestamp for the tool_approval notification (#321).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PendingToolApproval>> GetPendingApprovalDetailsAsync(
        IReadOnlyCollection<string> runIds, CancellationToken ct)
    {
        if (runIds.Count == 0) return new Dictionary<string, PendingToolApproval>(StringComparer.Ordinal);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var approvalEvents = await db.RunEvents.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId)
                && (e.EventType == EventTypes.ToolApprovalRequired
                    || e.EventType == ToolApprovalContextEventType))
            .Select(e => new { e.RunId, e.PayloadJson, e.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (approvalEvents.Count == 0) return new Dictionary<string, PendingToolApproval>(StringComparer.Ordinal);

        var resolvedEvents = await db.RunEvents.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId) &&
                        (e.EventType == EventTypes.ToolResult
                         || e.EventType == EventTypes.ToolError
                         || e.EventType == EventTypes.ToolApprovalResolved))
            .Select(e => new { e.RunId, e.EventType, e.PayloadJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Build per-run resolved callId sets.
        var resolvedByRun = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var evt in resolvedEvents)
        {
            if (!resolvedByRun.TryGetValue(evt.RunId, out var set))
                resolvedByRun[evt.RunId] = set = new HashSet<string>(StringComparer.Ordinal);
            var resolvedId = evt.EventType == EventTypes.ToolApprovalResolved
                ? ExtractStringField(evt.PayloadJson, "requestId")
                    ?? ExtractStringField(evt.PayloadJson, "request_id")
                : ExtractStringField(evt.PayloadJson, "callId")
                    ?? ExtractStringField(evt.PayloadJson, "call_id");
            if (resolvedId is not null) set.Add(resolvedId);
        }

        // Keep the most recent unresolved request per run (approval events are appended in order).
        var pending = new Dictionary<string, PendingToolApproval>(StringComparer.Ordinal);
        foreach (var evt in approvalEvents)
        {
            var requestId = ExtractStringField(evt.PayloadJson, "requestId")
                ?? ExtractStringField(evt.PayloadJson, "request_id")
                ?? ExtractStringField(evt.PayloadJson, "RequestId");
            if (requestId is null) continue;
            if (resolvedByRun.TryGetValue(evt.RunId, out var resolved) && resolved.Contains(requestId))
                continue;

            var toolName = ExtractStringField(evt.PayloadJson, "toolName")
                ?? ExtractStringField(evt.PayloadJson, "tool_name")
                ?? ExtractStringField(evt.PayloadJson, "ToolName");
            var createdUtc = new DateTimeOffset(DateTime.SpecifyKind(evt.CreatedAt, DateTimeKind.Utc));

            if (!pending.TryGetValue(evt.RunId, out var existing) || createdUtc > existing.CreatedUtc)
                pending[evt.RunId] = new PendingToolApproval(requestId, toolName, createdUtc);
        }
        return pending;
    }

    private static string? ExtractStringField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch { return null; }
    }
}

/// <summary>The most recent unresolved tool-approval request pending on a given run.</summary>
public sealed record PendingToolApproval(string RequestId, string? ToolName, DateTimeOffset CreatedUtc);
