using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Streaming;

/// <summary>
/// T035: GET /runs/{runId}/stream — Server-Sent Events endpoint.
///
/// Protocol:
///   1. Parse Last-Event-ID header (or lastSeenSequence query param) as the
///      resumable cursor. 0 = start from the beginning.
///   2. Replay all historical events with sequence > cursor from the durable log.
///   3. Subscribe to EventBroadcaster for live events going forward.
///   4. Deliver events until a terminal event is received or the client disconnects.
///
/// At-least-once delivery: historical replay may re-send events the client has
/// already seen if the sequence cursor is at a coarse boundary. Clients must
/// deduplicate by sequence number.
///
/// The stream spans the full run lifecycle through review/merge (FR-023).
/// </summary>
public static class StreamEndpoints
{
    private static readonly HashSet<EventType> TerminalEventTypes =
    [
        EventType.RunFailed,
        EventType.RunBounded,
        EventType.ReviewDeclined,
        EventType.MergeCompleted,
        EventType.MergeFailed
    ];

    private static readonly HashSet<RunStatus> TerminalStatuses =
    [
        RunStatus.Failed,
        RunStatus.Bounded,
        RunStatus.Declined,
        RunStatus.Merged,
        RunStatus.MergeConflict
    ];

    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/runs/{runId:guid}/stream", HandleAsync)
            .WithName("StreamRunEvents")
            .WithSummary("Stream run events as Server-Sent Events (SSE)")
            .WithTags("Runs")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task HandleAsync(
        Guid runId,
        HttpContext httpContext,
        IRunRepository runRepository,
        EventReplayService replayService,
        EventBroadcaster broadcaster,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Verify the run exists
        var run = await runRepository.GetByIdAsync(runId, ct);
        if (run is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(
                new { title = "Run not found", detail = $"No run with id {runId} exists." }, ct);
            return;
        }

        // Parse the resumable cursor from Last-Event-ID header or query param
        var lastSeenSequence = ParseCursor(httpContext.Request);

        // Prepare SSE response — must be before writing any body bytes
        SseWriter.PrepareResponse(httpContext.Response);

        try
        {
            // --- Step 1: Replay historical events from the durable log ---
            var historical = await replayService.GetEventsAfterAsync(runId, lastSeenSequence, ct);
            foreach (var evt in historical)
            {
                await WriteEventAsync(httpContext.Response, evt, ct);
                lastSeenSequence = evt.Sequence;
            }

            // If the run is already in a terminal state after replay, close the stream
            if (TerminalStatuses.Contains(run.Status))
            {
                return;
            }

            // --- Step 2: Stream live events via EventBroadcaster ---
            var channel = broadcaster.Subscribe(runId);
            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                {
                    // At-least-once: skip any event already delivered in replay
                    if (evt.Sequence <= lastSeenSequence)
                    {
                        continue;
                    }

                    await WriteEventAsync(httpContext.Response, evt, ct);
                    lastSeenSequence = evt.Sequence;

                    // Stop streaming when a terminal event is received
                    if (TerminalEventTypes.Contains(evt.Type))
                    {
                        break;
                    }
                }
            }
            finally
            {
                broadcaster.Unsubscribe(runId, channel);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — normal SSE lifecycle; no error to log
            logger.LogDebug(
                "SSE client disconnected for run {RunId} at sequence {Seq}",
                runId, lastSeenSequence);
        }
    }

    private static Task WriteEventAsync(
        HttpResponse response, EventEntity evt, CancellationToken ct)
    {
        return SseWriter.WriteEventAsync(
            response,
            evt.Sequence,
            SseWriter.ToWireEventType(evt.Type),
            evt.Payload,
            ct);
    }

    private static long ParseCursor(HttpRequest request)
    {
        // Last-Event-ID header takes precedence (standard SSE reconnect header)
        if (request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && long.TryParse(lastEventId, out var fromHeader))
        {
            return fromHeader;
        }

        // lastSeenSequence query param as a fallback for non-browser clients
        if (request.Query.TryGetValue("lastSeenSequence", out var querySeq)
            && long.TryParse(querySeq, out var fromQuery))
        {
            return fromQuery;
        }

        return 0L;
    }
}
