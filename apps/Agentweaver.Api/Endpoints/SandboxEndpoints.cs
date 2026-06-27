using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Security;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Api.Endpoints;

public static class SandboxEndpoints
{
    public static void MapSandboxEndpoints(this WebApplication app)
    {
        // POST /api/runs/{runId}/sandbox/port-forward
        // Starts a kubectl port-forward to the sandbox pod for the given run.
        // Body: { "target_port": 3000 }
        // Returns: { "session_id": "...", "local_port": 54321, "pod_name": "...", "started_at": "..." }
        app.MapPost("/api/runs/{runId}/sandbox/port-forward", async (
            HttpContext httpContext,
            string runId,
            PortForwardRequest request,
            PortForwardService portForwardService,
            IRunStore runStore,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.TargetPort is <= 0 or > 65535)
                return Results.BadRequest(new { error = "target_port must be between 1 and 65535." });

            // Verify the run exists and the caller owns it.
            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            PortForwardSession session;
            try
            {
                session = await portForwardService.StartAsync(runId, request.TargetPort, ct);
            }
            catch (PortForwardLimitExceededException ex)
            {
                logger.LogWarning(ex, "PortForward session limit exceeded for run {RunId}", runId);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "PortForward start failed for run {RunId}", runId);
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PortForward start error for run {RunId}", runId);
                return Results.Problem("Failed to start port-forward.", statusCode: 500);
            }

            return Results.Ok(new
            {
                session_id  = session.SessionId,
                local_port  = session.LocalPort,
                target_port = session.TargetPort,
                pod_name    = session.PodName,
                started_at  = session.StartedAt,
            });
        });

        // DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}
        // Stops a running port-forward session.
        app.MapDelete("/api/runs/{runId}/sandbox/port-forward/{sessionId}", async (
            HttpContext httpContext,
            string runId,
            string sessionId,
            PortForwardService portForwardService,
            IRunStore runStore,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var stopped = portForwardService.Stop(runId, sessionId);
            if (!stopped)
                return Results.NotFound(new { error = "Port-forward session not found." });

            return Results.Ok(new { session_id = sessionId, stopped = true });
        });

        // GET /api/runs/{runId}/sandbox/port-forward
        // Lists active port-forward sessions for the given run.
        app.MapGet("/api/runs/{runId}/sandbox/port-forward", async (
            HttpContext httpContext,
            string runId,
            PortForwardService portForwardService,
            IRunStore runStore,
            CancellationToken ct) =>
        {
            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var sessions = portForwardService.ListForRun(runId)
                .Select(s => new
                {
                    session_id  = s.SessionId,
                    local_port  = s.LocalPort,
                    target_port = s.TargetPort,
                    pod_name    = s.PodName,
                    started_at  = s.StartedAt,
                });

            return Results.Ok(sessions);
        });
    }
}

/// <summary>Request body for starting a port-forward session.</summary>
public sealed record PortForwardRequest(int TargetPort);
