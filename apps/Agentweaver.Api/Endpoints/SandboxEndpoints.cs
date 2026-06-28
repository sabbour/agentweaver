using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Security;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Api.Endpoints;

public static class SandboxEndpoints
{
    public static void MapSandboxEndpoints(this WebApplication app)
    {
        // POST /api/runs/{runId}/sandbox/port-forward
        // Starts a browser preview for the run's sandbox pod on the requested target port.
        //
        // When Sandbox:Preview:Enabled=true (in-cluster), this provisions a Gateway-direct preview
        // (per-preview HTTPRoute -> per-run ClusterIP Service -> sandbox pod) and returns
        // preview_url + keepalive_url. Otherwise it falls back to the legacy kubectl port-forward
        // (local-dev) path. Body: { "target_port": 3000 }.
        app.MapPost("/api/runs/{runId}/sandbox/port-forward", async (
            HttpContext httpContext,
            string runId,
            PortForwardRequest request,
            PortForwardService portForwardService,
            ISandboxPreviewService previewService,
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

            // ── Gateway-direct preview path (replica-safe) ───────────────────────────────
            if (previewService.Enabled)
            {
                try
                {
                    var preview = await previewService.StartPreviewAsync(
                        runId, request.TargetPort, run.SubmittingUser, ct);

                    return Results.Ok(new
                    {
                        session_id    = preview.Token,
                        local_port    = 0,
                        target_port   = preview.TargetPort,
                        pod_name      = preview.PodName,
                        started_at    = preview.StartedAt,
                        preview_url   = preview.PreviewUrl,
                        keepalive_url = $"/api/runs/{runId}/sandbox/preview/{preview.Token}/keepalive",
                    });
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Preview start failed for run {RunId}", runId);
                    return Results.Conflict(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Preview start error for run {RunId}", runId);
                    return Results.Problem("Failed to start preview.", statusCode: 500);
                }
            }

            // ── Legacy kubectl port-forward fallback (local dev) ─────────────────────────
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

        // POST /api/runs/{runId}/sandbox/preview/{token}/keepalive
        // Bumps the preview's idle expiry (Sandbox:Preview path only). The keepalive_url returned
        // by the start endpoint points here. Ownership-checked.
        app.MapPost("/api/runs/{runId}/sandbox/preview/{token}/keepalive", async (
            HttpContext httpContext,
            string runId,
            string token,
            ISandboxPreviewService previewService,
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

            if (!previewService.Enabled)
                return Results.Conflict(new { error = "Gateway preview is not enabled." });

            try
            {
                await previewService.KeepAliveAsync(token, ct);
                return Results.Ok(new { token, kept_alive = true });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Preview keepalive error for run {RunId}", runId);
                return Results.Problem("Failed to keep preview alive.", statusCode: 500);
            }
        });

        // DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}
        // Explicit user stop. For the preview path, sessionId is the capability token; this is the
        // ONLY place a preview is deleted on demand (keep_after_run=true means run-end / pod-release
        // do NOT delete it — the reaper handles expiry). Local-dev path stops the kubectl tunnel.
        app.MapDelete("/api/runs/{runId}/sandbox/port-forward/{sessionId}", async (
            HttpContext httpContext,
            string runId,
            string sessionId,
            PortForwardService portForwardService,
            ISandboxPreviewService previewService,
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

            if (previewService.Enabled)
            {
                // Idempotent (404 ignored inside the service); treat sessionId as the preview token.
                await previewService.StopPreviewAsync(sessionId, ct);
                return Results.Ok(new { session_id = sessionId, stopped = true });
            }

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
