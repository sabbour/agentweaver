using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Scaffolder.Api.Persistence;

namespace Scaffolder.Api.Streaming;

/// <summary>
/// T032: Writes Server-Sent Events (SSE) frames to the HTTP response.
/// Frame format per spec:
///   id:&lt;sequence&gt;\n
///   event:&lt;type&gt;\n
///   data:&lt;json&gt;\n
///   \n
/// Content-Type: text/event-stream; response buffering disabled.
/// </summary>
public static class SseWriter
{
    /// <summary>
    /// Prepares the HTTP response for SSE streaming.
    /// Must be called before any WriteEventAsync calls.
    /// </summary>
    public static void PrepareResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        // Nginx: disable proxy buffering so frames reach the client immediately
        response.Headers["X-Accel-Buffering"] = "no";

        // Disable ASP.NET Core response buffering for true streaming
        var bufferingFeature = response.HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
    }

    /// <summary>
    /// Writes a single SSE frame and flushes the response body.
    /// </summary>
    /// <param name="response">The active HTTP response.</param>
    /// <param name="id">Event id — mapped from the event sequence number.</param>
    /// <param name="eventType">Wire event type string (e.g. "run.started", "tool.call").</param>
    /// <param name="data">JSON payload string.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteEventAsync(
        HttpResponse response,
        long id,
        string eventType,
        string data,
        CancellationToken ct = default)
    {
        var frame = new StringBuilder();
        frame.Append("id:").Append(id).Append('\n');
        frame.Append("event:").Append(eventType).Append('\n');
        frame.Append("data:").Append(data).Append('\n');
        frame.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(frame.ToString());
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Converts the <see cref="EventType"/> enum to the wire SSE event name.
    /// Values match run-step-event.schema.json type discriminators.
    /// </summary>
    public static string ToWireEventType(EventType type) => type switch
    {
        EventType.RunStarted => "run.started",
        EventType.RunCompleted => "run.completed",
        EventType.RunFailed => "run.failed",
        EventType.RunBounded => "run.bounded",
        EventType.AgentMessage => "agent.message",
        EventType.ToolCall => "tool.call",
        EventType.ToolResult => "tool.result",
        EventType.ToolRejected => "tool.rejected",
        EventType.ToolError => "tool.error",
        EventType.ReviewRequested => "review.requested",
        EventType.ReviewApproved => "review.approved",
        EventType.ReviewDeclined => "review.declined",
        EventType.MergeCompleted => "merge.completed",
        EventType.MergeFailed => "merge.failed",
        _ => type.ToString().ToLowerInvariant()
    };
}
