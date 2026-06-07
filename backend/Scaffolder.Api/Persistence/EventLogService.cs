using System.Text.Json;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

/// <summary>
/// Centralized service for appending events to the per-run append-only event log.
/// This is the ONLY authorized path for writing events. Direct use of IEventRepository
/// from outside this service is prohibited.
/// 
/// Sequence assignment is handled by IEventRepository.AppendAsync atomically.
/// EventBroadcaster is injected optionally and called after successful persistence
/// to push events to live SSE subscribers (wired in T036).
/// </summary>
public sealed class EventLogService
{
    private readonly IEventRepository _eventRepository;
    private readonly ILogger<EventLogService> _logger;

    // T036: IEventBroadcaster is a Singleton injected into this Scoped service.
    // Optional (nullable) so tests and early phases can run without a broadcaster.
    private readonly IEventBroadcaster? _broadcaster;

    public EventLogService(
        IEventRepository eventRepository,
        ILogger<EventLogService> logger,
        IEventBroadcaster? broadcaster = null)
    {
        _eventRepository = eventRepository;
        _logger = logger;
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Appends a run lifecycle event (run.started, run.completed, run.failed, run.bounded).
    /// </summary>
    public Task<EventEntity> AppendLifecycleEventAsync(
        Guid runId,
        EventType type,
        object payload,
        CancellationToken ct = default)
    {
        return AppendAsync(runId, type, callId: null, payload, ct);
    }

    /// <summary>
    /// Appends an agent message event (agent.message).
    /// </summary>
    public Task<EventEntity> AppendAgentMessageAsync(
        Guid runId,
        string messageContent,
        CancellationToken ct = default)
    {
        var payload = new { content = messageContent };
        return AppendAsync(runId, EventType.AgentMessage, callId: null, payload, ct);
    }

    /// <summary>
    /// Appends a tool call event (tool.call). Returns the callId for correlation.
    /// </summary>
    public async Task<(EventEntity Event, Guid CallId)> AppendToolCallAsync(
        Guid runId,
        string toolName,
        string requestedPath,
        CancellationToken ct = default)
    {
        var callId = Guid.NewGuid();
        var payload = new { toolName, requestedPath };
        var entity = await AppendAsync(runId, EventType.ToolCall, callId, payload, ct);
        return (entity, callId);
    }

    /// <summary>
    /// Appends a tool result event (tool.result) for a successful tool invocation.
    /// </summary>
    public Task<EventEntity> AppendToolResultAsync(
        Guid runId,
        Guid callId,
        string? content,
        CancellationToken ct = default)
    {
        var payload = new { content };
        return AppendAsync(runId, EventType.ToolResult, callId, payload, ct);
    }

    /// <summary>
    /// Appends a tool rejected event (tool.rejected) for a PATH_ESCAPE rejection.
    /// </summary>
    public Task<EventEntity> AppendToolRejectedAsync(
        Guid runId,
        Guid callId,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default)
    {
        var payload = new { errorCode, errorMessage };
        return AppendAsync(runId, EventType.ToolRejected, callId, payload, ct);
    }

    /// <summary>
    /// Appends a tool error event (tool.error) for NOT_FOUND, PERMISSION, or UNKNOWN errors.
    /// </summary>
    public Task<EventEntity> AppendToolErrorAsync(
        Guid runId,
        Guid callId,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default)
    {
        var payload = new { errorCode, errorMessage };
        return AppendAsync(runId, EventType.ToolError, callId, payload, ct);
    }

    /// <summary>
    /// Appends a review/merge event (review.requested, review.approved, etc).
    /// </summary>
    public Task<EventEntity> AppendReviewEventAsync(
        Guid runId,
        EventType type,
        object payload,
        CancellationToken ct = default)
    {
        return AppendAsync(runId, type, callId: null, payload, ct);
    }

    private async Task<EventEntity> AppendAsync(
        Guid runId,
        EventType type,
        Guid? callId,
        object payload,
        CancellationToken ct)
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Type = type,
            CallId = callId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(payload)
        };

        var appended = await _eventRepository.AppendAsync(entity, ct);

        _logger.LogDebug(
            "Event appended: Run={RunId} Seq={Seq} Type={Type}",
            runId, appended.Sequence, type);

        // Broadcast to live SSE subscribers (no-op until T036 wires broadcaster)
        if (_broadcaster is not null)
        {
            await _broadcaster.BroadcastAsync(appended, ct);
        }

        return appended;
    }
}

/// <summary>
/// Placeholder interface for EventBroadcaster (implemented in T033).
/// Allows EventLogService to be compiled without the broadcaster dependency.
/// </summary>
public interface IEventBroadcaster
{
    Task BroadcastAsync(EventEntity eventEntity, CancellationToken ct = default);
}
