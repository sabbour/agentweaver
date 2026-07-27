using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Coordinator.Preview;

/// <summary>Inputs for a single deterministic preview step.</summary>
public sealed record PreviewStepRequest(
    string RunId,
    int WorkPlanId,
    string TreeHash,
    string WorktreePath,
    string SubmittingUser,
    string? ExecutionWorkspacePath = null);

/// <summary>
/// Deterministic, platform-owned live-preview step (spec-006 decouple-preview). Runs AFTER Build&amp;Test
/// returns (ANY verdict) and BEFORE the authored gate decision, on the SAME retained coordinator pod.
/// Command discovery reads the API-visible tree while execution may use its mapped local workspace.
/// It drives the AgentHost <c>/preview-runner/*</c> lifecycle (start process →
/// observe ACTUAL bound port → register through <see cref="AgentPreviewGate"/>) with NO model turn, and
/// is the SINGLE emitter of the terminal <c>preview_ready</c>/<c>preview_failed</c> outcome per
/// <c>{runId, workPlanId, treeHash}</c>. A preview failure NEVER blocks human review — it emits
/// <c>preview_failed</c> and returns; the caller always proceeds.
/// </summary>
public sealed class PreviewStep
{
    // Vite can take longer than one minute while optimizing a cold dependency graph. Keep below the
    // two-minute AgentHost control-plane timeout while giving the pod-local observer a real retry window.
    private const int ObserveTimeoutSeconds = 105;
    private const string HealthPath = "/";

    private readonly ISandboxPreviewService _previewService;
    private readonly AgentPreviewGate _previewGate;
    private readonly IPreviewRunnerHttpClient _httpClient;
    private readonly PreviewCommandResolver _resolver;
    private readonly IPreviewCommandModel? _commandModel;
    private readonly IAgentHostTurnTokenRegistry _turnTokens;
    private readonly Agentweaver.Api.Auth.ISecretStore? _secretStore;
    private readonly RunStreamStore _streamStore;
    private readonly SandboxRuntimeOptions _sandboxRuntime;
    private readonly ILogger<PreviewStep> _logger;
    private readonly IPodNameRegistry? _podRegistry;

    public PreviewStep(
        ISandboxPreviewService previewService,
        AgentPreviewGate previewGate,
        IPreviewRunnerHttpClient httpClient,
        PreviewCommandResolver resolver,
        IAgentHostTurnTokenRegistry turnTokens,
        RunStreamStore streamStore,
        SandboxRuntimeOptions sandboxRuntime,
        ILogger<PreviewStep> logger,
        Agentweaver.Api.Auth.ISecretStore? secretStore = null,
        IPodNameRegistry? podRegistry = null,
        IPreviewCommandModel? commandModel = null)
    {
        _previewService = previewService;
        _previewGate = previewGate;
        _httpClient = httpClient;
        _resolver = resolver;
        _commandModel = commandModel;
        _turnTokens = turnTokens;
        _streamStore = streamStore;
        _sandboxRuntime = sandboxRuntime;
        _logger = logger;
        _secretStore = secretStore;
        _podRegistry = podRegistry;
    }

    /// <summary>
    /// Runs the deterministic preview state machine. Verdict-independent. Never throws for a preview
    /// failure — always emits an outcome and returns so the gate loop proceeds.
    /// </summary>
    public async Task RunAsync(PreviewStepRequest request, CancellationToken ct)
    {
        var runId = request.RunId;

        try
        {
            // 1. Idempotency + applicability short-circuit. A terminal outcome (ready/failed/skipped)
            //    already recorded for this tree ⇒ nothing to do (also covers docs-only "skipped").
            var latest = FindLatestTerminalKind(runId, request.WorkPlanId, request.TreeHash);
            if (latest is not null)
            {
                _logger.LogInformation(
                    "PreviewStep: run {RunId} tree already has terminal preview outcome '{Kind}'; skipping.",
                    runId, latest);
                return;
            }

            // 2. Infra-off degradation: no reachable preview_url is possible ⇒ SKIP (not failed).
            if (!_previewService.Enabled || !_sandboxRuntime.IsPodPerRun)
            {
                EmitSkipped(request, "preview_infra_unavailable",
                    "Live preview infrastructure is not configured (pod-per-run or Gateway preview disabled).");
                return;
            }

            // 3. Resolve the run command. Phase 1 = fast/free/deterministic heuristics (no model turn).
            //    Phase 2 (issue #541) = an LLM fallback that runs ONLY when the heuristics come up empty,
            //    giving a model a bounded worktree view to propose a command. The model-chosen command
            //    still runs through the IDENTICAL sandboxed start/observe/approval path below — only the
            //    command string's origin changes. If neither tier resolves, we preserve the terminal
            //    preview_command_unresolved outcome (this fallback is additive, never a forced success).
            var resolution = _resolver.Resolve(request.WorktreePath);
            if (!resolution.Resolved || string.IsNullOrWhiteSpace(resolution.Command))
            {
                resolution = await TryResolveViaModelAsync(request, ct).ConfigureAwait(false);
                if (resolution is null || !resolution.Resolved || string.IsNullOrWhiteSpace(resolution.Command))
                {
                    EmitFailed(request, "preview_command_unresolved",
                        "Could not determine how to run the app from the worktree (heuristics and model fallback).");
                    return;
                }
            }

            EmitStartRequested(request, resolution.Source);

            var sourceCwd = resolution.Cwd ?? request.WorktreePath;
            var executionWorkspacePath = string.IsNullOrWhiteSpace(request.ExecutionWorkspacePath)
                ? _podRegistry?.TryGetEffectiveWorkingDirectory(runId)
                : request.ExecutionWorkspacePath;
            var executionCwd = string.IsNullOrWhiteSpace(executionWorkspacePath)
                ? sourceCwd
                : PreviewCommandResolver.MapExecutionCwd(
                    request.WorktreePath,
                    sourceCwd,
                    executionWorkspacePath!);
            if (string.IsNullOrWhiteSpace(executionCwd))
            {
                EmitFailed(
                    request,
                    "preview_cwd_mapping_invalid",
                    "Resolved preview working directory was outside the API-visible source tree.");
                return;
            }

            // 4. Bearer: same-process affinity uses the run's turn token; fall back to the per-run
            //    preview-runner credential from the run secret store for a cross-replica reconcile.
            var bearer = await ResolveBearerAsync(runId, ct).ConfigureAwait(false);

            // 5. Start the supervised process (deterministic). Once started, EVERY non-success terminal
            //    exit below best-effort stops the process (which disposes the forwarder) so we never
            //    leak the app process + forwarder until the idle reaper — only a SUCCESSFUL
            //    registration keeps them alive.
            PreviewRunnerStartResult started;
            try
            {
                started = await _httpClient.StartProcessAsync(
                    runId, bearer, resolution.Command!, executionCwd,
                    request.WorkPlanId, request.TreeHash, ct).ConfigureAwait(false);
            }
            catch (PreviewRunnerHttpException ex) when (ex.Reason == "preview_runner_unauthorized")
            {
                EmitFailed(request, "preview_runner_unauthorized", "AgentHost rejected the preview-runner credential.");
                return;
            }
            catch (PreviewRunnerHttpException ex) when (ex.Reason == "preview_origin_lookup_timeout")
            {
                EmitFailed(request, ex.Reason, ex.Message);
                return;
            }
            catch (PreviewRunnerHttpException ex)
            {
                EmitFailed(request, "process_exited", $"Failed to start preview process: {ex.Message}");
                return;
            }

            // 6. Observe the ACTUAL bound port (deterministic; parses stdout/socket-diff + HTTP verify).
            //    Returns the forwarder PUBLIC port (pod-IP reachable) as Port; AppPort is the app's port.
            PreviewRunnerPortResult port;
            try
            {
                port = await _httpClient.ObserveBoundPortAsync(
                    runId, bearer, started.SessionId, ObserveTimeoutSeconds, HealthPath, ct).ConfigureAwait(false);
            }
            catch (PreviewRunnerHttpException ex) when (ex.Reason == "preview_runner_unauthorized")
            {
                await TryStopProcessAsync(runId, bearer, started.SessionId, "preview_runner_unauthorized", ct).ConfigureAwait(false);
                EmitFailed(request, "preview_runner_unauthorized", "AgentHost rejected the preview-runner credential.");
                return;
            }
            catch (PreviewRunnerHttpException ex)
            {
                await TryStopProcessAsync(runId, bearer, started.SessionId, "port_not_found", ct).ConfigureAwait(false);
                EmitFailed(request, "port_not_found", $"Could not observe a bound port: {ex.Message}");
                return;
            }

            // Reachability first: an unhealthy observation carries a DISTINCT reason (bound_unreachable,
            // no_public_port_available, …) that must win over the generic port-range check below.
            if (!port.Healthy)
            {
                var reason = string.IsNullOrWhiteSpace(port.Reason) ? "health_check_failed" : port.Reason!;
                await TryStopProcessAsync(runId, bearer, started.SessionId, reason, ct).ConfigureAwait(false);
                EmitFailed(request, reason,
                    $"The preview process on public port {port.Port} (app port {port.AppPort}) is not reachable. {port.Evidence}");
                return;
            }

            if (port.Port is <= 0 or > 65535)
            {
                await TryStopProcessAsync(runId, bearer, started.SessionId, "port_not_found", ct).ConfigureAwait(false);
                EmitFailed(request, "port_not_found", "The preview process did not bind a discoverable port.");
                return;
            }

            // 7. Register through the gate (honors Decision 1 — no auto-approve bypass).
            var approval = await _previewGate.RequestApprovalAsync(
                runId, port.Port, ct, request.WorkPlanId, request.TreeHash).ConfigureAwait(false);
            if (approval != PreviewApprovalOutcome.Approved)
            {
                var reason = approval == PreviewApprovalOutcome.TimedOut ? "approval_timed_out" : "approval_denied";
                await TryStopProcessAsync(runId, bearer, started.SessionId, reason, ct).ConfigureAwait(false);
                EmitFailed(request, reason, "Preview approval was denied or timed out.");
                return;
            }

            // 8. Gateway registration via the emit-nothing helper — single-owner emission below.
            var registration = await SandboxEndpoints.TryRegisterPreviewAsync(
                runId, port.Port, request.SubmittingUser, _previewService, ct,
                previewRunnerSessionId: started.SessionId).ConfigureAwait(false);

            if (registration.Status == PreviewRegistrationStatus.Success)
            {
                // SUCCESS: keep the process + forwarder alive to serve the preview.
                EmitReady(request, registration.Session!, started.SessionId);
                return;
            }

            var failReason = registration.Status == PreviewRegistrationStatus.PortNotAllowed
                ? "port_not_allowed"
                : "registration_failed";
            await TryStopProcessAsync(runId, bearer, started.SessionId, failReason, ct).ConfigureAwait(false);
            EmitFailed(request, failReason, registration.Message ?? "Preview registration failed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex,
                "PreviewStep: internal timeout for run {RunId}; emitting preview_failed", runId);
            EmitFailed(request, "preview_internal_timeout", "Preview step timed out internally.");
        }
        catch (Exception ex)
        {
            // Unhandled preview error is still a preview failure (never park, never block review).
            _logger.LogWarning(ex, "PreviewStep: unexpected error for run {RunId}; emitting preview_failed", runId);
            EmitFailed(request, "registration_failed", "Preview step failed unexpectedly.");
        }
    }

    /// <summary>
    /// Best-effort stop of the supervised preview process after a post-start terminal FAILURE, so the
    /// app process + pod-local forwarder are released immediately instead of lingering until the idle
    /// reaper. Never throws — cleanup must not mask the preview outcome.
    /// </summary>
    private async Task TryStopProcessAsync(string runId, string? bearer, string sessionId, string reason, CancellationToken ct)
    {
        try
        {
            await _httpClient.StopProcessAsync(runId, bearer, sessionId, $"preview_step_failed:{reason}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PreviewStep: best-effort stop of session {SessionId} for run {RunId} failed (ignored).", sessionId, runId);
        }
    }

    /// <summary>
    /// Phase-2 (issue #541) LLM fallback: only invoked when the deterministic heuristics returned
    /// <see cref="PreviewCommandResolution.Unresolved"/>. Asks the model for a run command, then
    /// defensively validates it (non-empty command + a working directory contained within the
    /// worktree that actually exists) before returning a resolution tagged <c>Source = "llm"</c>.
    /// Returns <see langword="null"/> when no model is wired, the model declines, or validation fails —
    /// callers then preserve the terminal <c>preview_command_unresolved</c> outcome.
    /// </summary>
    private async Task<PreviewCommandResolution?> TryResolveViaModelAsync(PreviewStepRequest request, CancellationToken ct)
    {
        if (_commandModel is null)
            return null;

        PreviewCommandProposal? proposal;
        try
        {
            proposal = await _commandModel.ProposeCommandAsync(
                new PreviewCommandModelContext(request.RunId, null, request.SubmittingUser, request.WorktreePath),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PreviewStep: LLM command fallback threw for run {RunId}; treating as unresolved.", request.RunId);
            return null;
        }

        if (proposal is null || !proposal.Previewable || string.IsNullOrWhiteSpace(proposal.Command))
            return null;

        var cwd = ResolveModelCwdWithinWorktree(request.WorktreePath, proposal.Cwd);
        if (cwd is null)
        {
            _logger.LogWarning(
                "PreviewStep: LLM-proposed cwd '{Cwd}' for run {RunId} escaped or does not exist in the worktree; treating as unresolved.",
                proposal.Cwd, request.RunId);
            return null;
        }

        _logger.LogInformation(
            "PreviewStep: LLM command fallback resolved a command for run {RunId}.", request.RunId);
        return new PreviewCommandResolution(true, proposal.Command, cwd, "llm", BindUncertain: true);
    }

    /// <summary>
    /// Resolves a model-proposed working directory (relative to the worktree root, or <c>"."</c>) to
    /// an absolute path INSIDE the worktree. Returns <see langword="null"/> for any escape, rooted
    /// path, or non-existent directory so a model can never steer execution outside the checkout.
    /// </summary>
    internal static string? ResolveModelCwdWithinWorktree(string worktreePath, string? proposedCwd)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
            var relative = string.IsNullOrWhiteSpace(proposedCwd) ? "." : proposedCwd.Trim();
            if (Path.IsPathRooted(relative))
                return null;

            var combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative)));
            var withSep = root + Path.DirectorySeparatorChar;
            if (!combined.Equals(root, StringComparison.Ordinal)
                && !combined.StartsWith(withSep, StringComparison.Ordinal))
                return null;

            return Directory.Exists(combined) ? combined : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ResolveBearerAsync(string runId, CancellationToken ct)
    {
        var turnToken = _turnTokens.TryGetTurnToken(runId);
        if (!string.IsNullOrEmpty(turnToken))
            return turnToken;

        if (_secretStore is not null)
        {
            try
            {
                var result = await _secretStore.GetSecretAsync(PreviewRunnerCredential.SecretKey(runId), ct)
                    .ConfigureAwait(false);
                if (result.Found && !string.IsNullOrEmpty(result.Value))
                    return result.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PreviewStep: failed to fetch preview-runner credential for run {RunId}", runId);
            }
        }

        return null;
    }

    // ── Emission (single terminal-outcome owner) ────────────────────────────────────

    private void EmitStartRequested(PreviewStepRequest r, string? source)
    {
        Record(r.RunId, EventTypes.SandboxPreviewStartRequested, new
        {
            run_id = r.RunId,
            work_plan_id = r.WorkPlanId,
            tree_hash = r.TreeHash,
            source = "preview-step",
            command_source = source,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });
        EmitWorkflowStep(r.RunId, "started", "Starting live preview.");
    }

    private void EmitReady(PreviewStepRequest r, PreviewSession preview, string previewRunnerSessionId)
    {
        var keepaliveUrl = $"/api/runs/{r.RunId}/sandbox/preview/{preview.Token}/keepalive";
        var payload = new
        {
            run_id = r.RunId,
            work_plan_id = r.WorkPlanId,
            tree_hash = r.TreeHash,
            source = "preview-step",
            target_port = preview.TargetPort,
            pod_name = preview.PodName,
            session_id = preview.Token,
            preview_runner_session_id = previewRunnerSessionId,
            preview_url = preview.PreviewUrl,
            keepalive_url = keepaliveUrl,
            started_at = preview.StartedAt,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        };
        Record(r.RunId, EventTypes.SandboxPreviewReady, payload);
        Record(r.RunId, EventTypes.CoordinatorPreviewReady, payload);
        EmitWorkflowStep(r.RunId, "completed", "Preview is ready.");
    }

    private void EmitFailed(PreviewStepRequest r, string reason, string message)
    {
        Record(r.RunId, EventTypes.SandboxPreviewFailed, new
        {
            run_id = r.RunId,
            work_plan_id = r.WorkPlanId,
            tree_hash = r.TreeHash,
            source = "preview-step",
            reason,
            message,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });
        EmitWorkflowStep(r.RunId, "failed", message);
    }

    private void EmitSkipped(PreviewStepRequest r, string reason, string message)
    {
        Record(r.RunId, EventTypes.SandboxPreviewSkippedNotApplicable, new
        {
            run_id = r.RunId,
            work_plan_id = r.WorkPlanId,
            tree_hash = r.TreeHash,
            source = "preview-step",
            reason,
            message,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });
        EmitWorkflowStep(r.RunId, "skipped", message);
    }

    private void EmitWorkflowStep(string runId, string status, string message) =>
        Record(runId, EventTypes.WorkflowStep, new
        {
            step = "preview",
            status,
            label = "Preview",
            message,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });

    private void Record(string runId, string type, object payload) =>
        _streamStore.Get(runId)?.RecordNext(type, payload);

    /// <summary>
    /// Latest terminal preview outcome for the tree (<c>ready</c>/<c>failed</c>/<c>skipped</c>), or
    /// <see langword="null"/> when none yet. Mirrors the coordinator guard's authoritative
    /// latest-state logic so the two never disagree on "already has an outcome".
    /// </summary>
    private string? FindLatestTerminalKind(string runId, int workPlanId, string treeHash)
    {
        var events = _streamStore.Get(runId)?.GetSnapshotSince(0).Events;
        if (events is null || events.Count == 0)
            return null;

        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            var isTerminal = evt.Type is EventTypes.SandboxPreviewReady
                or EventTypes.SandboxPreviewFailed
                or EventTypes.SandboxPreviewSkippedNotApplicable;
            var isApplicability = evt.Type == EventTypes.SandboxPreviewApplicability;
            if (!isTerminal && !isApplicability)
                continue;

            var node = System.Text.Json.JsonSerializer.SerializeToNode(evt.Payload)
                as System.Text.Json.Nodes.JsonObject;
            if (node is null || !TreeMatches(node, workPlanId, treeHash))
                continue;

            if (evt.Type == EventTypes.SandboxPreviewReady) return "ready";
            if (evt.Type == EventTypes.SandboxPreviewFailed) return "failed";
            if (evt.Type == EventTypes.SandboxPreviewSkippedNotApplicable) return "skipped";
            if (evt.Type == EventTypes.SandboxPreviewApplicability
                && string.Equals(GetString(node, "state"), "preview_skipped_not_applicable", StringComparison.Ordinal))
                return "skipped";
        }

        return null;
    }

    private static bool TreeMatches(System.Text.Json.Nodes.JsonObject node, int workPlanId, string treeHash)
    {
        var payloadWorkPlanId = GetInt(node, "work_plan_id") ?? GetInt(node, "workPlanId");
        var payloadTreeHash = GetString(node, "tree_hash") ?? GetString(node, "treeHash");
        return (payloadWorkPlanId is null || payloadWorkPlanId == workPlanId)
            && (string.IsNullOrWhiteSpace(payloadTreeHash)
                || string.IsNullOrWhiteSpace(treeHash)
                || string.Equals(payloadTreeHash, treeHash, StringComparison.Ordinal));
    }

    private static int? GetInt(System.Text.Json.Nodes.JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out var v) || v is null) return null;
        try { return v.GetValue<int>(); } catch { return null; }
    }

    private static string? GetString(System.Text.Json.Nodes.JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out var v) || v is null) return null;
        try { return v.GetValue<string>(); } catch { return null; }
    }
}
