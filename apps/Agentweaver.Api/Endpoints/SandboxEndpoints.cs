using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Security;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

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

            return await StartPreviewForRunAsync(
                runId, request.TargetPort, run, previewService, portForwardService, logger, ct);
        });

        // POST /api/runs/{runId}/sandbox/preview
        // Agent-initiated preview. A running agent (inside its sandbox, mid-workflow) calls the
        // start_preview(port) tool which POSTs here. The request routes through a human-in-the-loop
        // approval gate (AgentPreviewGate); on approval it runs the SAME preview-start path as the
        // operator-driven port-forward endpoint above and returns preview_url to the agent.
        //
        // Authorization accepts the run's OWN agent callback (service identity) OR a human owner.
        // The runId is server-bound in the agent's tool closure, so the agent can only ever target
        // its own run. Approval is auto-grantable via SANDBOX_PREVIEW_AUTO_APPROVE / the per-run
        // AutoApproveTools option so an automated demo can run unattended; prod stays human-gated.
        // Body: { "target_port": 3000 } (snake_case).
        app.MapPost("/api/runs/{runId}/sandbox/preview", async (
            HttpContext httpContext,
            string runId,
            StartPreviewRequest request,
            AgentPreviewGate previewGate,
            PortForwardService portForwardService,
            ISandboxPreviewService previewService,
            IRunStore runStore,
            IConfiguration configuration,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.TargetPort is <= 0 or > 65535)
                return Results.BadRequest(new { error = "target_port must be between 1 and 65535." });

            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwnerOrServiceCaller(httpContext, run, configuration))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var outcome = await previewGate.RequestApprovalAsync(runId, request.TargetPort, ct);
            if (outcome != PreviewApprovalOutcome.Approved)
            {
                return Results.Json(
                    new { error = "Preview approval was denied or timed out." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await StartPreviewForRunAsync(
                runId, request.TargetPort, run, previewService, portForwardService, logger, ct);
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

            // M1: verify the capability token actually belongs to THIS run (replica-safe — reads the
            // HTTPRoute's run annotation from the cluster) so a caller cannot keep alive another
            // run's preview by presenting its own runId with a guessed/foreign token.
            if (!await previewService.VerifyTokenForRunAsync(token, runId, ct))
                return Results.NotFound(new { error = "Preview not found for this run." });

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
                // M1: bind the token to THIS run before deleting so one run cannot delete another
                // run's preview by presenting a foreign token (replica-safe annotation check).
                if (!await previewService.VerifyTokenForRunAsync(sessionId, runId, ct))
                    return Results.NotFound(new { error = "Preview not found for this run." });

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
            ISandboxPreviewService previewService,
            IRunStore runStore,
            CancellationToken ct) =>
        {
            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var sessions = previewService.Enabled
                ? (await previewService.ListForRunAsync(runId, ct)).Select(s => new
                {
                    session_id  = s.Token,
                    local_port  = 0,
                    target_port = s.TargetPort,
                    pod_name    = s.PodName,
                    started_at  = s.StartedAt,
                })
                : portForwardService.ListForRun(runId).Select(s => new
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

    /// <summary>
    /// Shared preview-start path used by BOTH the operator port-forward endpoint and the
    /// agent-initiated preview endpoint. When the Gateway-direct preview service is enabled
    /// (in-cluster) it provisions a replica-safe preview and returns preview_url; otherwise it
    /// falls back to the legacy kubectl port-forward (local-dev). Authorization and the HITL
    /// approval gate are the caller's responsibility — by the time this runs the request is
    /// already authorized/approved.
    /// </summary>
    private static async Task<IResult> StartPreviewForRunAsync(
        string runId,
        int targetPort,
        Run run,
        ISandboxPreviewService previewService,
        PortForwardService portForwardService,
        ILogger logger,
        CancellationToken ct)
    {
        // ── Gateway-direct preview path (replica-safe) ───────────────────────────────
        if (previewService.Enabled)
        {
            // Preview ports are constrained to the gateway-only ingress range allowed by
            // k8s/networkpolicy-sandbox.yaml (Sandbox:Preview:AllowedPortMin/Max) so we never
            // provision a preview the NetworkPolicy would black-hole. The legacy kubectl
            // fallback below is unaffected (still 1-65535).
            if (!Agentweaver.Api.Sandbox.Preview.SandboxPreviewOptions.IsPortInRange(
                    targetPort, previewService.AllowedPortMin, previewService.AllowedPortMax))
            {
                return Results.BadRequest(new
                {
                    error = $"preview port must be between {previewService.AllowedPortMin} and {previewService.AllowedPortMax}.",
                });
            }

            try
            {
                var preview = await previewService.StartPreviewAsync(
                    runId, targetPort, run.SubmittingUser, ct);

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
            catch (PortForwardLimitExceededException ex)
            {
                logger.LogWarning(ex, "Preview session limit exceeded for run {RunId}", runId);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
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
            session = await portForwardService.StartAsync(runId, targetPort, ct);
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
    }
}

/// <summary>Request body for starting a port-forward session.</summary>
public sealed record PortForwardRequest(int TargetPort);

/// <summary>
/// Request body for the agent-initiated preview endpoint. Uses the snake_case DTO convention
/// (explicit <see cref="JsonPropertyNameAttribute"/>) — unlike the legacy <see cref="PortForwardRequest"/>
/// which binds camelCase <c>targetPort</c>.
/// </summary>
public sealed record StartPreviewRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("target_port")]
    public int TargetPort { get; init; }
}
