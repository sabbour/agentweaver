using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Security;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

public static class SandboxEndpoints
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        PreviewApprovalRetryGates = new(StringComparer.Ordinal);

    public static void MapSandboxEndpoints(this IEndpointRouteBuilder app)
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
            RunStreamStore streamStore,
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
            if (await EndpointHelpers.RequireRunAccessAsync(httpContext, run, ProjectRole.Contributor, ct) is { } denied)
                return denied;

            return await StartPreviewForRunAsync(
                runId, request.TargetPort, run, previewService, portForwardService, streamStore, logger, ct);
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
        // AutoApproveTools option so an automated demo can run unattended; otherwise the wait window
        // defaults to the project's 30-minute setting. Prod stays human-gated.
        // Body: { "target_port": 3000 } (snake_case).
        app.MapPost("/api/runs/{runId}/sandbox/preview", async (
            HttpContext httpContext,
            string runId,
            StartPreviewRequest request,
            AgentPreviewGate previewGate,
            PortForwardService portForwardService,
            ISandboxPreviewService previewService,
            RunStreamStore streamStore,
            IRunStore runStore,
            IPreviewRunnerHttpClient previewRunnerClient,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.TargetPort is <= 0 or > 65535)
                return Results.BadRequest(new { error = "target_port must be between 1 and 65535." });

            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (await EndpointHelpers.RequireRunAccessAsync(
                    httpContext,
                    run,
                    ProjectRole.Contributor,
                    ct,
                    allowInternalService: true) is { } denied)
                return denied;
            var previewContext = LatestPreviewContext(streamStore, runId);
            var outcome = await previewGate.RequestApprovalAsync(
                runId,
                request.TargetPort,
                ct,
                previewContext.WorkPlanId,
                previewContext.TreeHash);
            if (outcome.Outcome != PreviewApprovalOutcome.Approved)
            {
                var timedOut = outcome.Outcome == PreviewApprovalOutcome.TimedOut;
                var reason = timedOut ? "approval_timed_out" : "approval_denied";
                EmitPreviewFailure(
                    streamStore,
                    runId,
                    request.TargetPort,
                    reason,
                    timedOut
                        ? "Preview approval expired. Retry approval without restarting the run."
                        : "Preview approval was denied.",
                    approvalRequestId: outcome.RequestId,
                    retryAvailable: timedOut,
                    expiredAt: outcome.ExpiresAt);
                return Results.Json(
                    new { error = timedOut ? "Preview approval expired." : "Preview approval was denied." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Approval can outlive both the run and its preview process. Check the authoritative
            // AgentHost session immediately before creating Gateway resources, so no dead URL is published.
            var currentRun = await runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            if (currentRun is null) return Results.NotFound();
            if (EndpointHelpers.IsTerminal(currentRun.Status))
            {
                const string message = "Preview session has exited; a preview URL cannot be published for a terminal run.";
                EmitPreviewFailure(streamStore, runId, request.TargetPort, "preview_session_exited", message,
                    previewRunnerSessionId: request.PreviewRunnerSessionId);
                return Results.Conflict(new { error = message });
            }

            if (!string.IsNullOrWhiteSpace(request.PreviewRunnerSessionId))
            {
                PreviewRunnerHealthResult health;
                try
                {
                    health = await previewRunnerClient.HealthCheckAsync(
                        runId,
                        BearerToken(httpContext),
                        request.PreviewRunnerSessionId,
                        request.TargetPort,
                        "/",
                        ct).ConfigureAwait(false);
                }
                catch (PreviewRunnerHttpException)
                {
                    const string message = "Preview session has exited or is unreachable; a preview URL cannot be published.";
                    try
                    {
                        await previewRunnerClient.StopProcessAsync(
                            runId, BearerToken(httpContext), request.PreviewRunnerSessionId, "preview_session_exited", ct)
                            .ConfigureAwait(false);
                    }
                    catch (PreviewRunnerHttpException) { }
                    EmitPreviewFailure(streamStore, runId, request.TargetPort, "preview_session_exited", message,
                        previewRunnerSessionId: request.PreviewRunnerSessionId);
                    return Results.Conflict(new { error = message });
                }

                if (!health.Healthy)
                {
                    const string message = "Preview session is no longer healthy; a preview URL cannot be published.";
                    try
                    {
                        await previewRunnerClient.StopProcessAsync(
                            runId, BearerToken(httpContext), request.PreviewRunnerSessionId, "preview_session_exited", ct)
                            .ConfigureAwait(false);
                    }
                    catch (PreviewRunnerHttpException) { }
                    EmitPreviewFailure(streamStore, runId, request.TargetPort, "preview_session_exited", message,
                        previewRunnerSessionId: request.PreviewRunnerSessionId);
                    return Results.Conflict(new { error = message });
                }
            }

            return await StartPreviewForRunAsync(
                runId, request.TargetPort, currentRun, previewService, portForwardService, streamStore, logger, ct,
                request.PreviewRunnerSessionId);
        });

        // POST /api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry
        // Re-arms an expired start_preview request. A new request id is emitted immediately; if the
        // deterministic PreviewStep already started a process, approval registers that same process
        // rather than executing the preview command again.
        app.MapPost("/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry", async (
            HttpContext httpContext,
            string runId,
            string requestId,
            AgentPreviewGate previewGate,
            IToolApprovalGate approvalGate,
            PortForwardService portForwardService,
            ISandboxPreviewService previewService,
            RunStreamStore streamStore,
            IRunStore runStore,
            IPreviewRunnerHttpClient previewRunnerClient,
            Agentweaver.AgentRuntime.Workflow.IAgentHostTurnTokenRegistry turnTokens,
            Agentweaver.Api.Auth.ISecretStore secretStore,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Agentweaver.Domain.RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct);
            if (run is null) return Results.NotFound();
            if (await EndpointHelpers.RequireRunAccessAsync(httpContext, run, ProjectRole.Contributor, ct) is { } denied)
                return denied;
            var retryGateKey = $"{runId}:{requestId}";
            var retryGate = PreviewApprovalRetryGates.GetOrAdd(
                retryGateKey,
                static _ => new SemaphoreSlim(1, 1));
            await retryGate.WaitAsync(ct).ConfigureAwait(false);

            RetryablePreviewContext retry;
            PreviewApprovalAttempt attempt;
            try
            {
                var currentRun = await runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
                if (currentRun is null)
                    return Results.NotFound();
                if (EndpointHelpers.IsTerminal(currentRun.Status))
                    return Results.Conflict(new { error = "Run is terminal; preview approval cannot be retried." });
                if (approvalGate.GetRequestState(runId, requestId) != ToolApprovalRequestState.Expired)
                    return Results.Conflict(new { error = "Only an expired preview approval can be retried." });

                var retryCandidate = LatestRetryablePreview(streamStore, runId, requestId);
                if (retryCandidate is null)
                    return Results.Conflict(new
                    {
                        error = "This preview approval is no longer the latest retryable preview state.",
                    });
                retry = retryCandidate;

                attempt = await previewGate.BeginApprovalAsync(
                    runId,
                    retry.TargetPort,
                    CancellationToken.None,
                    retry.WorkPlanId,
                    retry.TreeHash,
                    retryOfRequestId: requestId).ConfigureAwait(false);
            }
            finally
            {
                retryGate.Release();
                PreviewApprovalRetryGates.TryRemove(retryGateKey, out _);
            }

            _ = CompletePreviewRetryAsync(
                runId,
                run,
                retry,
                attempt,
                previewService,
                portForwardService,
                streamStore,
                previewRunnerClient,
                turnTokens,
                secretStore,
                runStore,
                logger);

            return Results.Accepted(
                $"/api/runs/{runId}/events",
                new
                {
                    run_id = runId,
                    request_id = attempt.RequestId,
                    retry_of_request_id = requestId,
                    expires_at = attempt.ExpiresAt,
                    state = "pending",
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
            if (await EndpointHelpers.RequireRunAccessAsync(httpContext, run, ProjectRole.Contributor, ct) is { } denied)
                return denied;

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
            if (await EndpointHelpers.RequireRunAccessAsync(httpContext, run, ProjectRole.Contributor, ct) is { } denied)
                return denied;

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
            if (await EndpointHelpers.RequireRunAccessAsync(httpContext, run, ProjectRole.Viewer, ct) is { } denied)
                return denied;

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
    internal static async Task<IResult> StartPreviewForRunAsync(
        string runId,
        int targetPort,
        Run run,
        ISandboxPreviewService previewService,
        PortForwardService portForwardService,
        RunStreamStore streamStore,
        ILogger logger,
        CancellationToken ct,
        string? previewRunnerSessionId = null)
    {
        // ── Gateway-direct preview path (replica-safe) ───────────────────────────────
        if (previewService.Enabled)
        {
            var registration = await TryRegisterPreviewAsync(
                runId, targetPort, run.SubmittingUser, previewService, ct, previewRunnerSessionId);

            if (registration.Status == PreviewRegistrationStatus.Success)
            {
                var preview = registration.Session!;
                var keepaliveUrl = $"/api/runs/{runId}/sandbox/preview/{preview.Token}/keepalive";
                var context = LatestPreviewContext(streamStore, runId);
                var readyPayload = new
                {
                    run_id = runId,
                    work_plan_id = context.WorkPlanId,
                    tree_hash = context.TreeHash,
                    source = "preview-api",
                    target_port = preview.TargetPort,
                    pod_name = preview.PodName,
                    session_id = preview.Token,
                    preview_runner_session_id = registration.PreviewRunnerSessionId,
                    preview_url = preview.PreviewUrl,
                    keepalive_url = keepaliveUrl,
                    started_at = preview.StartedAt,
                    timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                };
                streamStore.Get(runId)?.RecordNext(EventTypes.SandboxPreviewReady, readyPayload);
                streamStore.Get(runId)?.RecordNext(EventTypes.CoordinatorPreviewReady, readyPayload);
                EmitPreviewWorkflowStep(streamStore, runId, "completed", "Preview is ready.");

                return Results.Ok(new
                {
                    session_id    = preview.Token,
                    local_port    = 0,
                    target_port   = preview.TargetPort,
                    pod_name      = preview.PodName,
                    started_at    = preview.StartedAt,
                    preview_url   = preview.PreviewUrl,
                    keepalive_url = keepaliveUrl,
                });
            }

            // Single-owner emission: the helper emitted nothing — this caller emits exactly one
            // preview_failed for the typed error and returns the matching HTTP status.
            EmitPreviewFailure(streamStore, runId, targetPort, registration.Reason!, registration.Message!);
            return registration.Status switch
            {
                PreviewRegistrationStatus.PortNotAllowed => Results.BadRequest(new { error = registration.Message }),
                PreviewRegistrationStatus.Capacity =>
                    Results.Json(new { error = registration.Message }, statusCode: StatusCodes.Status429TooManyRequests),
                PreviewRegistrationStatus.Conflict => Results.Conflict(new { error = registration.Message }),
                _ => Results.Problem("Failed to start preview.", statusCode: 500),
            };
        }

        // ── Legacy kubectl port-forward fallback (local dev) ─────────────────────────
        // A reachable preview_url requires the Gateway-direct path (Sandbox:Preview:Enabled=true).
        // The legacy port-forward fallback below is a developer-local diagnostic and does not
        // satisfy the software-delivery live-preview contract for preview-required projects.
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

    /// <summary>
    /// Lower-level Gateway-direct preview registration (spec-006 decouple-preview, BLOCKER 3). Runs
    /// the port-range guard + <see cref="ISandboxPreviewService.StartPreviewAsync"/> and returns a
    /// TYPED result. It <b>emits NOTHING</b> onto the run stream — the single caller
    /// (<see cref="StartPreviewForRunAsync"/> or the deterministic <c>PreviewStep</c>) owns emission,
    /// so there is exactly one terminal preview outcome per tree. The port-range rejection is
    /// surfaced as <see cref="PreviewRegistrationStatus.PortNotAllowed"/> instead of a silent
    /// <c>BadRequest</c>.
    /// </summary>
    internal static async Task<PreviewRegistrationResult> TryRegisterPreviewAsync(
        string runId,
        int targetPort,
        string ownerUserId,
        ISandboxPreviewService previewService,
        CancellationToken ct,
        string? previewRunnerSessionId = null)
    {
        if (!Agentweaver.Api.Sandbox.Preview.SandboxPreviewOptions.IsPortInRange(
                targetPort, previewService.AllowedPortMin, previewService.AllowedPortMax))
        {
            return PreviewRegistrationResult.Error(
                PreviewRegistrationStatus.PortNotAllowed,
                "port_not_allowed",
                $"preview port must be between {previewService.AllowedPortMin} and {previewService.AllowedPortMax}.");
        }

        try
        {
            var preview = await previewService.StartPreviewAsync(
                runId, targetPort, ownerUserId, ct, previewRunnerSessionId);
            return PreviewRegistrationResult.Ok(preview, previewRunnerSessionId);
        }
        catch (PortForwardLimitExceededException ex)
        {
            return PreviewRegistrationResult.Error(PreviewRegistrationStatus.Capacity, "capacity", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return PreviewRegistrationResult.Error(PreviewRegistrationStatus.Conflict, PreviewFailureReason(ex), ex.Message);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PreviewRegistrationResult.Error(
                PreviewRegistrationStatus.GatewayFailed, "gateway_failed", "Failed to start preview.");
        }
    }

    private static string? BearerToken(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
    }

    private static void EmitPreviewFailure(
        RunStreamStore streamStore,
        string runId,
        int targetPort,
        string reason,
        string message,
        string? previewRunnerSessionId = null,
        string? approvalRequestId = null,
        bool retryAvailable = false,
        DateTimeOffset? expiredAt = null)
    {
        var context = LatestPreviewContext(streamStore, runId);
        streamStore.Get(runId)?.RecordNext(EventTypes.SandboxPreviewFailed, new
        {
            run_id = runId,
            work_plan_id = context.WorkPlanId,
            tree_hash = context.TreeHash,
            source = "preview-api",
            target_port = targetPort,
            reason,
            message,
            preview_runner_session_id = previewRunnerSessionId,
            approval_request_id = approvalRequestId,
            retry_available = retryAvailable,
            expired_at = expiredAt?.ToString("O"),
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });
        EmitPreviewWorkflowStep(streamStore, runId, "failed", message);
    }

    private static async Task CompletePreviewRetryAsync(
        string runId,
        Run run,
        RetryablePreviewContext retry,
        PreviewApprovalAttempt attempt,
        ISandboxPreviewService previewService,
        PortForwardService portForwardService,
        RunStreamStore streamStore,
        IPreviewRunnerHttpClient previewRunnerClient,
        Agentweaver.AgentRuntime.Workflow.IAgentHostTurnTokenRegistry turnTokens,
        Agentweaver.Api.Auth.ISecretStore secretStore,
        IRunStore runStore,
        ILogger logger)
    {
        try
        {
            var result = await attempt.Completion.ConfigureAwait(false);
            var currentRun = await runStore.GetAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            if (currentRun is null || EndpointHelpers.IsTerminal(currentRun.Status))
            {
                await TryStopRetainedProcessAsync(
                    runId,
                    retry.PreviewRunnerSessionId,
                    "run_terminal",
                    previewRunnerClient,
                    turnTokens,
                    secretStore,
                    logger).ConfigureAwait(false);
                EmitPreviewFailure(
                    streamStore,
                    runId,
                    retry.TargetPort,
                    "registration_failed",
                    "The run became terminal before preview approval completed.");
                return;
            }

            if (result.Outcome == PreviewApprovalOutcome.Approved)
            {
                var registrationResult = await StartPreviewForRunAsync(
                    runId,
                    retry.TargetPort,
                    currentRun,
                    previewService,
                    portForwardService,
                    streamStore,
                    logger,
                    CancellationToken.None,
                    retry.PreviewRunnerSessionId).ConfigureAwait(false);
                if (registrationResult is not Microsoft.AspNetCore.Http.IStatusCodeHttpResult
                    { StatusCode: StatusCodes.Status200OK })
                {
                    await TryStopRetainedProcessAsync(
                        runId,
                        retry.PreviewRunnerSessionId,
                        "registration_failed",
                        previewRunnerClient,
                        turnTokens,
                        secretStore,
                        logger).ConfigureAwait(false);
                }
                return;
            }

            var timedOut = result.Outcome == PreviewApprovalOutcome.TimedOut;
            if (!timedOut && retry.PreviewRunnerSessionId is not null)
            {
                await TryStopRetainedProcessAsync(
                    runId,
                    retry.PreviewRunnerSessionId,
                    "preview_approval_denied",
                    previewRunnerClient,
                    turnTokens,
                    secretStore,
                    logger).ConfigureAwait(false);
            }

            EmitPreviewFailure(
                streamStore,
                runId,
                retry.TargetPort,
                timedOut ? "approval_timed_out" : "approval_denied",
                timedOut
                    ? "Preview approval expired. Retry approval without restarting the run."
                    : "Preview approval was denied.",
                retry.PreviewRunnerSessionId,
                result.RequestId,
                retryAvailable: timedOut,
                expiredAt: result.ExpiresAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Preview approval retry failed for run {RunId}", runId);
            EmitPreviewFailure(
                streamStore,
                runId,
                retry.TargetPort,
                "registration_failed",
                "Preview approval retry failed unexpectedly.");
        }
    }

    private static async Task TryStopRetainedProcessAsync(
        string runId,
        string? previewRunnerSessionId,
        string reason,
        IPreviewRunnerHttpClient previewRunnerClient,
        Agentweaver.AgentRuntime.Workflow.IAgentHostTurnTokenRegistry turnTokens,
        Agentweaver.Api.Auth.ISecretStore secretStore,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(previewRunnerSessionId)) return;

        try
        {
            var bearer = turnTokens.TryGetTurnToken(runId);
            if (string.IsNullOrWhiteSpace(bearer))
            {
                var secret = await secretStore.GetSecretAsync(
                    PreviewRunnerCredential.SecretKey(runId),
                    CancellationToken.None).ConfigureAwait(false);
                bearer = secret.Found ? secret.Value : null;
            }

            await previewRunnerClient.StopProcessAsync(
                runId,
                bearer,
                previewRunnerSessionId,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to stop retained preview process {SessionId} for run {RunId}",
                previewRunnerSessionId,
                runId);
        }
    }

    private static RetryablePreviewContext? LatestRetryablePreview(
        RunStreamStore streamStore,
        string runId,
        string approvalRequestId)
    {
        var events = streamStore.Get(runId)?.GetSnapshotSince(0).Events;
        if (events is null) return null;

        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type == EventTypes.SandboxPreviewPending)
                return null;
            if (events[i].Type is EventTypes.SandboxPreviewReady or EventTypes.SandboxPreviewSkippedNotApplicable)
                return null;
            if (events[i].Type != EventTypes.SandboxPreviewFailed)
                continue;

            var node = System.Text.Json.JsonSerializer.SerializeToNode(events[i].Payload)
                as System.Text.Json.Nodes.JsonObject;
            if (node is null
                || !GetBool(node, "retry_available")
                || !string.Equals(GetString(node, "approval_request_id"), approvalRequestId, StringComparison.Ordinal))
                return null;

            var targetPort = GetInt(node, "target_port");
            if (targetPort is null) return null;
            return new RetryablePreviewContext(
                targetPort.Value,
                GetInt(node, "work_plan_id"),
                GetString(node, "tree_hash"),
                GetString(node, "preview_runner_session_id"));
        }

        return null;
    }

    private static int? GetInt(System.Text.Json.Nodes.JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is not null
            ? GetNullableInt(value)
            : null;

    private static string? GetString(System.Text.Json.Nodes.JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out var value) || value is null) return null;
        try { return value.GetValue<string>(); }
        catch { return null; }
    }

    private static bool GetBool(System.Text.Json.Nodes.JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out var value) || value is null) return false;
        try { return value.GetValue<bool>(); }
        catch { return false; }
    }

    private static void EmitPreviewWorkflowStep(
        RunStreamStore streamStore,
        string runId,
        string status,
        string message) =>
        streamStore.Get(runId)?.RecordNext(EventTypes.WorkflowStep, new
        {
            step = "preview",
            status,
            label = "Preview",
            message,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });

    private static (int? WorkPlanId, string? TreeHash) LatestPreviewContext(RunStreamStore streamStore, string runId)
    {
        var events = streamStore.Get(runId)?.GetSnapshotSince(0).Events;
        if (events is null) return (null, null);

        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type != EventTypes.SandboxPreviewApplicability
                && events[i].Type != EventTypes.SandboxPreviewReady
                && events[i].Type != EventTypes.SandboxPreviewFailed
                && events[i].Type != EventTypes.SandboxPreviewSkippedNotApplicable)
                continue;

            var node = System.Text.Json.JsonSerializer.SerializeToNode(events[i].Payload) as System.Text.Json.Nodes.JsonObject;
            if (node is null) continue;
            int? workPlanId = null;
            if (node.TryGetPropertyValue("work_plan_id", out var snakeWorkPlan) && snakeWorkPlan is not null)
                workPlanId = GetNullableInt(snakeWorkPlan);
            else if (node.TryGetPropertyValue("workPlanId", out var camelWorkPlan) && camelWorkPlan is not null)
                workPlanId = GetNullableInt(camelWorkPlan);
            var treeHash = node.TryGetPropertyValue("tree_hash", out var snakeTree) ? snakeTree?.GetValue<string>() : null;
            treeHash ??= node.TryGetPropertyValue("treeHash", out var camelTree) ? camelTree?.GetValue<string>() : null;
            if (workPlanId is not null || !string.IsNullOrWhiteSpace(treeHash))
                return (workPlanId, treeHash);
        }

        return (null, null);
    }

    private static int? GetNullableInt(System.Text.Json.Nodes.JsonNode node)
    {
        try { return node.GetValue<int>(); }
        catch { return null; }
    }

    private static string PreviewFailureReason(InvalidOperationException ex) =>
        ex.Message.Contains("pod", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("claim", StringComparison.OrdinalIgnoreCase)
            ? "no_bound_pod"
            : "gateway_failed";

    private sealed record RetryablePreviewContext(
        int TargetPort,
        int? WorkPlanId,
        string? TreeHash,
        string? PreviewRunnerSessionId);
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

    [System.Text.Json.Serialization.JsonPropertyName("preview_runner_session_id")]
    public string? PreviewRunnerSessionId { get; init; }
}

/// <summary>Typed status of a Gateway-direct preview registration (spec-006 decouple-preview).</summary>
public enum PreviewRegistrationStatus
{
    Success,
    PortNotAllowed,
    Capacity,
    Conflict,
    NoBoundPod,
    GatewayFailed,
}

/// <summary>
/// Emit-nothing typed result of <see cref="SandboxEndpoints.TryRegisterPreviewAsync"/>. On success
/// carries the <see cref="Agentweaver.Api.Sandbox.Preview.PreviewSession"/> and the distinct
/// PreviewRunner process session id (BLOCKER B); on failure carries a closed-set reason + message.
/// </summary>
public sealed record PreviewRegistrationResult(
    PreviewRegistrationStatus Status,
    Agentweaver.Api.Sandbox.Preview.PreviewSession? Session,
    string? Reason,
    string? Message,
    string? PreviewRunnerSessionId = null)
{
    public static PreviewRegistrationResult Ok(
        Agentweaver.Api.Sandbox.Preview.PreviewSession session, string? previewRunnerSessionId) =>
        new(PreviewRegistrationStatus.Success, session, null, null, previewRunnerSessionId);

    public static PreviewRegistrationResult Error(PreviewRegistrationStatus status, string reason, string message) =>
        new(status, null, reason, message);
}
