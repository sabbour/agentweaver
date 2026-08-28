using System.Text.Json.Serialization;
using Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxExec.PodExec;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ── Executor sidecar entrypoints ───────────────────────────────────────────────
// The same image serves three roles so the sandbox toolchain is byte-identical on both sides of the
// pod boundary and no second image has to be built, scanned, or version-matched:
//   * default            — the AgentHost A2A server (this file, below);
//   * --exec-agent       — the executor sidecar daemon that owns every model-controlled process;
//   * --exec-relay       — a short-lived supervisor for one long-running sandboxed process.
// Both auxiliary modes exit the process when done and never construct the web host.
if (args.Contains(AgentHostExecutorEntrypoints.ExecAgentArgument, StringComparer.Ordinal))
{
    return await AgentHostExecutorEntrypoints.RunExecutorSidecarAsync(args).ConfigureAwait(false);
}
if (args.Contains(PodExecRelay.RelayArgument, StringComparer.Ordinal))
{
    return await PodExecRelay
        .RunAsync(AgentHostExecutorEntrypoints.ResolveSocketArgument(args, PodExecRelay.RelayArgument))
        .ConfigureAwait(false);
}

// ── Bootstrap ──────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Load AgentHost options (per-run config injected as env vars / config at pod launch).
builder.Services.Configure<AgentHostOptions>(builder.Configuration.GetSection("AgentHost"));

builder.Services.AddSingleton<PodLocalWorkspaceManager>();
builder.Services.Configure<PreviewRunnerOptions>(builder.Configuration.GetSection("AgentHost:PreviewRunner"));

// ── A2A listener: mTLS (production default) vs plain HTTP (PoC) ─────────────────
// Sandbox:AgentHost:RequireMtls maps here as AgentHost:RequireMtls. Default TRUE keeps the
// secure path (H1): the mounted appsettings.k8s.json (at /app/config) drives
// Kestrel:Endpoints:A2A, while this bootstrap layer applies the mTLS client-certificate policy and
// fail-closed HTTPS fallback if the endpoint block is absent. When FALSE (PoC only), the listener
// falls back to plain HTTP on AgentHost:Port. The SandboxTemplate sets
// envVarsInjectionPolicy=Disallowed, so this config is read from the mounted ConfigMap, not per-run
// env vars.
builder.Configuration.AddJsonFile("/app/config/appsettings.k8s.json", optional: true);
builder.WebHost.ConfigureKestrel(kestrel =>
    AgentHostKestrelConfigurator.Configure(kestrel, builder.Configuration));

// ── GitHub credential chain ────────────────────────────────────────────────────
// Three paths, selected in priority order:
//
//  (A) CSI-mounted Key Vault token files (Option B, KvTokenMountPath set):
//      A per-run SecretProviderClass mounts only the run owner's token file from Key Vault at
//      /mnt/user-tokens/user_{userId}.json — same StoredCredential JSON as the shared store.
//      CsiMountedGitHubTokenStore adds cold-start retry (6×5s) in case the CSI driver hasn't
//      written the file yet at pod startup. Takes precedence over UseSharedTokenStore.
//
//  (B) Shared file store (spec-018 P1.5 live PoC): the cluster mounts the agentweaver-workspace
//      RWX volume at /workspace with HOME=/workspace/.home, and the API persists the user's GitHub
//      token to {HOME}/.local/share/agentweaver/auth/user_<id>.json. When UseSharedTokenStore=true
//      the pod READS that same shared store directly — the token never moves, no secret is created.
//      Pairs with a per-user scope provider so the correct user_<id>.json is read.
//
//  (C) Default: PodGitHubTokenStore (NeverSignedIn) + installation scope. The factory then falls
//      back to Providers:GitHubCopilot:GitHubToken from config (e.g. an injected env/secret).
//
// No IGitHubAccessTokenProvider is wired (token is static at pod lifetime; the shared store already
// holds a freshly-issued user token).
var kvUri = builder.Configuration["AgentHost:KeyVaultUri"];
var kvMountPath = builder.Configuration["AgentHost:KvTokenMountPath"];
// Guard: reject empty, whitespace, or unsubstituted envsubst placeholders (e.g. "${AGENTHOST_KEYVAULT_URI}")
Uri? kvUriParsed = null;
var kvUriValid = !string.IsNullOrWhiteSpace(kvUri)
    && Uri.TryCreate(kvUri, UriKind.Absolute, out kvUriParsed)
    && (kvUriParsed.Scheme == "https" || kvUriParsed.Scheme == "http");
if (kvUriValid)
{
    // Option C (warm pool): fetch the run owner's token from Key Vault at /configure-time via the
    // pod's workload identity (DefaultAzureCredential). No CSI volume, no per-run SPC — the secret
    // name (ghtok-user--{base32(userId)}) arrives in the /configure call and lands on
    // AgentHostRuntimeState.KvUserSecretName. KeyVaultUserTokenProvider fetches ONLY that one secret
    // and caches it for the pod lifetime. Takes precedence over the file-mount paths.
    builder.Services.AddSingleton(new SecretClient(kvUriParsed!, new DefaultAzureCredential()));
    builder.Services.AddSingleton<KeyVaultUserTokenProvider>();
    builder.Services.AddSingleton<IGitHubTokenStore>(sp =>
        new KeyVaultGitHubTokenStore(sp.GetRequiredService<KeyVaultUserTokenProvider>()));
    builder.Services.AddSingleton<IGitHubTokenScopeProvider>(sp =>
        new RuntimeUserScopeProvider(sp.GetRequiredService<AgentHostRuntimeState>()));
}
else if (!string.IsNullOrWhiteSpace(kvMountPath))
{
    // Option A: CSI-mounted Key Vault token files.
    // File per user: {kvMountPath}/user_{sanitizedUserId}.json — same StoredCredential JSON.
    var configuredUserId = builder.Configuration["AgentHost:UserId"];
    builder.Services.AddSingleton<IGitHubTokenStore>(
        new CsiMountedGitHubTokenStore(kvMountPath));
    builder.Services.AddSingleton<IGitHubTokenScopeProvider>(sp =>
        new SharedUserScopeProvider(
            kvMountPath,
            configuredUserId,
            sp.GetRequiredService<ILogger<SharedUserScopeProvider>>()));
}
else if (builder.Configuration.GetValue("AgentHost:UseSharedTokenStore", false))
{
    var authDir = SharedTokenStorePaths.ResolveAuthDir(
        builder.Configuration["AgentHost:SharedTokenStorePath"]);
    var configuredUserId = builder.Configuration["AgentHost:UserId"];
    builder.Services.AddSingleton<IGitHubTokenStore>(new SharedHomeGitHubTokenStore(authDir));
    builder.Services.AddSingleton<IGitHubTokenScopeProvider>(sp =>
        new SharedUserScopeProvider(
            authDir,
            configuredUserId,
            sp.GetRequiredService<ILogger<SharedUserScopeProvider>>()));
}
else
{
    var podTokenStore = new PodGitHubTokenStore();
    builder.Services.AddSingleton<IGitHubTokenStore>(podTokenStore);
    builder.Services.AddSingleton<IGitHubTokenScopeProvider, PodInstallationScopeProvider>();
}

// ── Sandbox policy (no DB in pod) ─────────────────────────────────────────────
builder.Services.AddSingleton<ISandboxPolicyStore, PodSandboxPolicyStore>();
builder.Services.AddSingleton<ISandboxRepositoryCredentialProvider, RunScopedRepositoryCredentialProvider>();

// ── Agent runtime (in-memory approvals, local executor — Kata VM IS the sandbox) ─
builder.Services.AddSingleton<PreviewRunner>();
builder.Services.AddSingleton<IPreviewRunner>(sp => sp.GetRequiredService<PreviewRunner>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PreviewRunner>());
builder.Services.AddSingleton<IAgentRuntimeToolProvider, PreviewRunnerToolProvider>();
builder.Services.AddAgentHostRuntime();

// The production AgentHost runs inside a per-run Kata VM, but the shared /workspace PVC is outside
// the VM image and is visible to every pod. Model-controlled commands therefore run in a dedicated
// executor sidecar container of the same pod: the container boundary supplies the PID namespace and
// the runtime-provided procfs (a nested procfs mount is refused by the kernel in any masked-procfs
// container — the failure that broke the Kata warm pool), while bubblewrap inside that container
// binds only the current run's roots. Startup fails closed when the sidecar is unreachable or not
// actually isolated.
var useKataSidecarExecutor =
    SandboxExecutorFactory.IsInCluster &&
    string.Equals(
        builder.Configuration["AgentHost:SandboxMode"],
        "kata",
        StringComparison.OrdinalIgnoreCase);
if (useKataSidecarExecutor)
{
    using var probeLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    var executorClient = new PodExecSandboxClient(
        builder.Configuration["AgentHost:ExecSocketPath"],
        probeLoggerFactory.CreateLogger(nameof(PodExecSandboxClient)));
    var probeTimeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("AgentHost:ExecSidecarProbeTimeoutSeconds", 120));
    var (isolationReady, isolationDetail) = executorClient
        .ProbeAsync(probeTimeout)
        .GetAwaiter()
        .GetResult();
    if (!isolationReady)
        throw new InvalidOperationException(
            $"AgentHost Kata filesystem isolation is unavailable; refusing to start: {isolationDetail}");

    builder.Services.AddSingleton<ISandboxExecutor>(sp =>
        new PodExecSandboxClient(
            builder.Configuration["AgentHost:ExecSocketPath"],
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(PodExecSandboxClient))));
}

// ── CopilotAIAgent (singleton per pod — one run per pod lifetime) ──────────────
builder.Services.AddSingleton<CopilotAIAgent>();

// ── Operator assistant (narrow AgentHost cutover, #346/#347) ──────────────────
// Same MCP-driven Copilot chat loop that used to run in the API pod, now hosted here when a pod is
// configured with AgentHostPurpose.OperatorAssistant. GitHubCopilotClientFactory and the pod's
// IGitHubTokenScopeProvider (registered above per credential path) are reused as-is — the operator
// assistant needs no infrastructure this pod doesn't already provision for CopilotAIAgent.
builder.Services.AddSingleton<IAgentweaverMcpToolProvider>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentHostOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.McpEndpoint))
        throw new InvalidOperationException(
            "AgentHost:McpEndpoint must be configured (the in-cluster AgentweaverMCP /mcp URL) to run the operator assistant purpose.");
    var mcpOptions = new AgentweaverMcpConnectionOptions { Endpoint = new Uri(opts.McpEndpoint) };
    return new AgentweaverMcpToolProvider(mcpOptions, sp.GetService<ILoggerFactory>());
});
builder.Services.AddSingleton<IOperatorAssistantAgent, OperatorAssistantAgent>();
builder.Services.AddSingleton<OperatorPodTurnRunner>();

// ── Startup service calls SetupAsync before the server begins serving requests ──
// Registered as singleton first so the readiness middleware can resolve it by type.
builder.Services.AddSingleton<AgentHostStartupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentHostStartupService>());

// ── A2A server registration ────────────────────────────────────────────────────
// AddAIAgent registers the MAF-exposed agent with the hosting infrastructure (returns
// IHostedAgentBuilder); AddA2AServer adds the A2A HTTP streaming server layer on top.
// MapA2AHttpJson (below, after Build) mounts the actual SSE/card endpoints.
// We expose A2ATurnBridgeAgent (NOT CopilotAIAgent directly) so the standard MAF streaming
// entrypoint decodes per-turn AgentSetupParams (isRevision) and forwards CopilotAIAgent's
// RunEvents back over A2A as DataParts (spec-018 P1.5). The bridge wraps the same
// CopilotAIAgent singleton that AgentHostStartupService runs SetupAsync on.
// Preview packages pinned to 1.9.0-preview.260603.1 per spec H7.
var agentHostedBuilder = builder.AddAIAgent(
    A2ATurnBridgeAgent.AgentName,
    (sp, _) =>
    {
        var copilotAgent = sp.GetRequiredService<CopilotAIAgent>();
        var runtimeState = sp.GetRequiredService<AgentHostRuntimeState>();
        // RoutingPodTurnRunner (narrow AgentHost cutover, #346/#347) picks per-turn between the
        // sandboxed CopilotAIAgent path (Coordinator/workflow purposes) and the operator assistant's
        // MCP chat loop, based on the pod's configured AgentHostPurpose — the bridge itself is built
        // once at startup, before /configure has told the pod which purpose it serves.
        var runner = new RoutingPodTurnRunner(
            new CopilotPodTurnRunner(copilotAgent),
            sp.GetRequiredService<OperatorPodTurnRunner>(),
            runtimeState);
        return new A2ATurnBridgeAgent(
            copilotAgent,
            runner,
            sp.GetRequiredService<PodLocalWorkspaceManager>(),
            runtimeState,
            sp.GetRequiredService<ILogger<A2ATurnBridgeAgent>>());
    },
    Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);

agentHostedBuilder.AddA2AServer(options =>
{
    options.AgentRunMode = AgentRunMode.DisallowBackground;
});

// Azure Monitor OpenTelemetry (Application Insights) — enabled only when connection string is set.
var appInsightsConnStr = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(appInsightsConnStr))
{
    Agentweaver.AgentHost.AzureMonitorBootstrap.Configure(builder.Services, builder.Logging);
}

// ── Build app ─────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Startup readiness gate ─────────────────────────────────────────────────────
// Return 503 until AgentHostStartupService has completed SetupAsync. /healthz (liveness) and
// /configure (warm-pool injection) are exempt: a warm pod must accept /configure while not yet ready.
app.Use(async (ctx, next) =>
{
    var startup = ctx.RequestServices.GetRequiredService<AgentHostStartupService>();
    if (!startup.IsReady &&
        !ctx.Request.Path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/configure", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Agent not ready — SetupAsync in progress.").ConfigureAwait(false);
        return;
    }
    await next(ctx).ConfigureAwait(false);
});

// ── Warm-pool one-time /configure endpoint (Option C) ───────────────────────────
// Injects the per-run RunId/UserId/TurnBearerToken (and the KV secret name) into an already-warm
// pod, then runs the deferred SetupAsync. Placed BEFORE the A2A bearer-auth middleware: it cannot be
// protected by the TurnBearerToken (chicken-and-egg — the token is delivered HERE). NetworkPolicy
// (ingress to AgentHost pods restricted to API/worker) is the guard. One-time: a second call (or a
// pod launched with env vars) is rejected with 409.
app.MapPost("/configure", async (HttpContext ctx) =>
{
    var runtimeState = ctx.RequestServices.GetRequiredService<AgentHostRuntimeState>();
    var startup = ctx.RequestServices.GetRequiredService<AgentHostStartupService>();
    var options = ctx.RequestServices.GetRequiredService<IOptions<AgentHostOptions>>().Value;

    // Pod was launched with a RunId via env vars (non-warm deployment) — already provisioned.
    if (!string.IsNullOrWhiteSpace(options.RunId))
        return Results.Conflict("Already configured via env");

    ConfigureRequest? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<ConfigureRequest>(ctx.RequestAborted).ConfigureAwait(false);
    }
    catch (Exception)
    {
        return Results.BadRequest("Malformed /configure body");
    }

    if (body is null || string.IsNullOrWhiteSpace(body.RunId))
        return Results.BadRequest("runId is required");

    var configuration = body.ToRunConfiguration();
    try
    {
        PodLocalWorkspaceManager.ValidateConfiguration(configuration);
    }
    catch (AgentHostConfigurationException ex)
    {
        return Results.Json(
            new { error = ex.Reason, message = ex.Message },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // Interlocked one-time gate (inside TryConfigure): first caller wins, the rest get 409.
    if (!runtimeState.TryConfigure(configuration))
        return Results.Conflict("Already configured");

    try
    {
        await startup.ConfigureAsync(configuration, body.AutoApproveTools, ctx.RequestAborted)
            .ConfigureAwait(false);
    }
    catch (AgentHostConfigurationException ex)
    {
        return Results.Json(
            new { error = ex.Reason, message = ex.Message },
            statusCode: ex.Reason == "insufficient_ephemeral_storage"
                ? StatusCodes.Status507InsufficientStorage
                : StatusCodes.Status409Conflict);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        throw;
    }
    catch (GitHubCopilotUnauthorizedException ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<AgentHostRuntimeState>>();
        logger.LogWarning(
            ex,
            "AgentHost /configure: GitHub Copilot rejected the run credential for run {RunId}; the API may refresh and recreate this pod once.",
            configuration.RunId);
        return Results.Json(
            new
            {
                error = "agenthost_configure_copilot_unauthorized",
                message = ex.Message,
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (Exception ex)
    {
        // Any exception here that is NOT an AgentHostConfigurationException previously escaped this
        // handler uncaught, producing ASP.NET Core's default empty-body HTTP 500 in Production — an
        // opaque failure the caller (KubernetesSandboxExecutor.CallAgentHostConfigureAsync) could only
        // report as "agenthost_configure_failed ... HTTP 500 " with no detail, and whose root cause
        // was lost the moment the warm pod got recycled. Log the real exception here — where it is
        // still attributable to this specific pod/run — and surface a structured, typed reason so a
        // recurrence is diagnosable from the caller's log line alone, without needing to catch a
        // short-lived pod's logs before the warm pool recycles it.
        var logger = ctx.RequestServices.GetRequiredService<ILogger<AgentHostRuntimeState>>();
        logger.LogError(
            ex,
            "AgentHost /configure: unexpected exception during ConfigureAsync for run {RunId} (ready={Ready})",
            configuration.RunId, startup.IsReady);
        return Results.Json(
            new { error = "agenthost_configure_unexpected_exception", message = $"{ex.GetType().Name}: {ex.Message}" },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(new
    {
        configured = true,
        runId = body.RunId,
        effectiveWorkingDirectory = runtimeState.EffectiveWorkingDirectory,
    });
});

// ── A2A bearer auth gates ─────────────────────────────────────────────────────
// Rejects unauthenticated card discovery / turn submission unless the corresponding
// bearer token is empty (dev/test only). Evaluated before the A2A route so it
// cannot be bypassed. TurnBearerToken is read from AgentHostRuntimeState (delivered via
// /configure on the warm-pool path, or seeded from options on the env-var path) — NOT from the
// immutable AgentHostOptions — so the configured token is the one enforced on message:stream.
app.Use(async (ctx, next) =>
{
    var opts = ctx.RequestServices.GetRequiredService<IOptions<AgentHostOptions>>().Value;
    var runtimeState = ctx.RequestServices.GetRequiredService<AgentHostRuntimeState>();
    if (!string.IsNullOrEmpty(opts.CardBearerToken) &&
        ctx.Request.Path.StartsWithSegments(opts.A2APath + "/v1/card", StringComparison.OrdinalIgnoreCase))
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + opts.CardBearerToken;
        if (!string.Equals(authHeader, expected, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"agentweaver-agent-host\"";
            await ctx.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
            return;
        }
    }

    var turnBearerToken = runtimeState.TurnBearerToken;
    if (!string.IsNullOrEmpty(turnBearerToken) &&
        HttpMethods.IsPost(ctx.Request.Method) &&
        ctx.Request.Path.StartsWithSegments(opts.A2APath + "/v1/message:stream", StringComparison.OrdinalIgnoreCase))
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + turnBearerToken;
        if (!string.Equals(authHeader, expected, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"agentweaver-agent-host\"";
            await ctx.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
            return;
        }
    }

    await next(ctx).ConfigureAwait(false);
});

// ── Liveness probe ─────────────────────────────────────────────────────────────
// Always 200 once Kestrel is up — signals "pod is reachable, safe to POST /configure".
// "standby" = warm pool pod waiting for /configure; "ready" = SetupAsync complete.
// The API executor must NOT wait for "ready" before calling /configure (that is a deadlock:
// IsReady is only set after /configure → SetupAsync, but /configure is gated on healthz 200).
app.MapGet("/healthz", (AgentHostStartupService startup) =>
    Results.Ok(startup.IsReady ? "ready" : "standby"));

// ── Tool approval endpoints ───────────────────────────────────────────────────
app.MapPost("/tool-approvals", ToolApprovalEndpointHandlers.GrantAsync);
app.MapGet("/tool-approvals/{requestId}", ToolApprovalEndpointHandlers.GetPendingContextAsync);
app.MapPost("/tool-denials", ToolApprovalEndpointHandlers.DenyAsync);

// ── PreviewRunner endpoints ───────────────────────────────────────────────────
// API/Coordinator uses these to manage the pod-local preview process lifecycle. The model-facing
// tools call the same PreviewRunner service in-process; these HTTP endpoints are for platform
// cleanup/reconciliation (terminal assembly, explicit stop, stale-run repair). They are protected
// with the same per-run TurnBearerToken used for A2A turns when one is configured.
app.MapPost("/preview-runner/processes", async (
    HttpContext ctx,
    PreviewProcessStartRequest request,
    IPreviewRunner previewRunner,
    AgentHostRuntimeState runtimeState) =>
{
    if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Command))
        return Results.BadRequest(new { error = "command is required" });
    if (string.IsNullOrWhiteSpace(request.Cwd))
        return Results.BadRequest(new { error = "cwd is required" });

    var result = await previewRunner.StartPreviewProcessAsync(
        request.Command,
        request.Cwd,
        string.IsNullOrWhiteSpace(request.RunId) ? runtimeState.RunId : request.RunId,
        request.WorkPlanId,
        request.TreeHash,
        ctx.RequestAborted).ConfigureAwait(false);

    return Results.Ok(new
    {
        session_id = result.SessionId,
        pid = result.Pid,
        started_at = result.StartedAt,
        working_directory = result.WorkingDirectory,
    });
});

app.MapPost("/preview-runner/processes/{sessionId}/observe-bound-port", async (
    HttpContext ctx,
    string sessionId,
    PreviewObservePortRequest request,
    IPreviewRunner previewRunner,
    AgentHostRuntimeState runtimeState) =>
{
    if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
        return Results.Unauthorized();

    PreviewPortObservation result;
    try
    {
        result = await previewRunner.ObserveBoundPortAsync(
            sessionId,
            TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds ?? 60)),
            request.HealthPath ?? "/",
            ctx.RequestAborted).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // Never surface an opaque HTTP 500 to PreviewStep: map any unexpected failure to a structured
        // 200 unhealthy-with-reason so the run shows a legible cause (e.g. "observe_error"), not "500".
        return Results.Ok(new
        {
            session_id = sessionId,
            port = 0,
            app_port = 0,
            evidence = $"observe_error: {ex.Message}",
            healthy = false,
            health_evidence = ex.ToString(),
            reason = "observe_error",
        });
    }

    return Results.Ok(new
    {
        session_id = result.SessionId,
        port = result.Port,
        app_port = result.AppPort,
        evidence = result.Evidence,
        healthy = result.Healthy,
        health_evidence = result.HealthEvidence,
        reason = result.Reason,
    });
});

app.MapPost("/preview-runner/processes/{sessionId}/health-check", async (
    HttpContext ctx,
    string sessionId,
    PreviewHealthCheckRequest request,
    IPreviewRunner previewRunner,
    AgentHostRuntimeState runtimeState) =>
{
    if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
        return Results.Unauthorized();
    if (request.Port is <= 0 or > 65535)
        return Results.BadRequest(new { error = "port must be between 1 and 65535" });

    var result = await previewRunner.HealthCheckAsync(
        sessionId, request.Port, request.Path ?? "/", ctx.RequestAborted).ConfigureAwait(false);

    return Results.Ok(new
    {
        session_id = result.SessionId,
        port = result.Port,
        path = result.Path,
        healthy = result.Healthy,
        status_code = result.StatusCode,
        evidence = result.Evidence,
    });
});

app.MapDelete("/preview-runner/processes/{sessionId}", async (
    HttpContext ctx,
    string sessionId,
    string? reason,
    IPreviewRunner previewRunner,
    AgentHostRuntimeState runtimeState) =>
{
    if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
        return Results.Unauthorized();

    var result = await previewRunner.StopPreviewProcessAsync(
        sessionId,
        string.IsNullOrWhiteSpace(reason) ? "api_stop" : reason!,
        ctx.RequestAborted).ConfigureAwait(false);

    return Results.Ok(new
    {
        session_id = result.SessionId,
        stopped = result.Stopped,
        reason = result.Reason,
    });
});

// ── A2A endpoints ──────────────────────────────────────────────────────────────
// Mounts:
//   POST  {A2APath}/v1/message:stream  — streaming agent turn (SSE)
//   GET   {A2APath}/v1/card            — agent card discovery (authz-gated above)
var opts0 = app.Services.GetRequiredService<IOptions<AgentHostOptions>>().Value;
app.MapA2AHttpJson(agentHostedBuilder, opts0.A2APath);

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Request body for the warm-pool <c>POST /configure</c> endpoint.</summary>
internal sealed record ConfigureRequest
{
    public string? RunId { get; init; }
    public string? UserId { get; init; }
    public string? TurnBearerToken { get; init; }
    public string? KvUserSecretName { get; init; }

    /// <summary>
    /// Per-run preview-runner credential (spec-006 decouple-preview, BLOCKER A). Delivered in-memory
    /// only — never placed in pod env/config/file — so the untrusted preview process cannot inherit it.
    /// <c>PreviewRunnerEndpointAuth</c> accepts this OR <see cref="TurnBearerToken"/>.
    /// </summary>
    public string? PreviewRunnerCredential { get; init; }

    /// <summary>
    /// GitHub OAuth access token pre-resolved by the API (which has KV access).
    /// When present, the pod skips the Key Vault fetch entirely — no OIDC or KV egress needed.
    /// </summary>
    public string? GitHubAccessToken { get; init; }

    /// <summary>
    /// Short-lived credential for the configured run and repository. The runtime gives this value
    /// only to a single <c>git</c> or <c>gh</c> shell command.
    /// </summary>
    public string? RepositoryAccessToken { get; init; }

    /// <summary>
    /// Authenticated platform caller token used by the operator assistant's MCP connection. Kept
    /// separate from <see cref="GitHubAccessToken"/> because Entra deployments use different
    /// credentials for Agentweaver API authorization and the linked GitHub/Copilot account.
    /// </summary>
    public string? CallerBearerToken { get; init; }

    /// <summary>
    /// The run's shared orchestration worktree path (e.g. <c>/workspace/{worktree}</c>). When present,
    /// the pod runs <c>SetupAsync</c> with this as its working directory — and therefore its file-tool
    /// root — instead of the static <c>AgentHost__WorkingDirectory</c> env default. This keeps every
    /// warm pod serving a run of the same parent rooted at the SAME directory the run's system prompt
    /// references, so files produced by one stage are visible to the next.
    /// </summary>
    public string? SharedWorkingDirectory { get; init; }

    /// <summary>
    /// Backward-compatible alias for callers predating the explicit workspace descriptor.
    /// New callers should send <see cref="SharedWorkingDirectory"/>.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Per-run <c>AutoApproveTools</c> run option (bug #221). When true, the pod auto-grants the
    /// allow-with-approval HITL gate (e.g. <c>web_fetch</c>) instead of stalling for an operator.
    /// The API resolves this from its own run-options store and the pod seeds its in-pod
    /// <c>IRunOptionsStore</c> from it — otherwise the fresh pod store defaults to false and every
    /// <c>web_fetch</c> waits out the HITL timeout under autopilot.
    /// </summary>
    public bool AutoApproveTools { get; init; }

    /// <summary>Explicit run purpose. Omitted payloads preserve the existing shared-worktree behavior.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AgentHostPurpose>))]
    public AgentHostPurpose Purpose { get; init; } = AgentHostPurpose.Default;

    /// <summary>API-visible shared repository used only as the immutable git fetch source.</summary>
    public string? SourceRepositoryPath { get; init; }

    /// <summary>Branch/ref shallow-fetched from <see cref="SourceRepositoryPath"/>.</summary>
    public string? SourceRef { get; init; }

    /// <summary>Immutable commit expected at <see cref="SourceRef"/>.</summary>
    public string? BaseCommitSha { get; init; }

    /// <summary>Immutable tree object expected for <see cref="BaseCommitSha"/>.</summary>
    public string? ExpectedTreeHash { get; init; }

    /// <summary>Shared versus pod-local execution and write-back policy.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ExecutionWorkspaceMode>))]
    public ExecutionWorkspaceMode WorkspaceMode { get; init; } = ExecutionWorkspaceMode.Shared;

    /// <summary>Root of the execution-scratch emptyDir where the pod creates local workspaces.</summary>
    public string? ScratchRoot { get; init; }

    /// <summary>Platform-owned identity used for the generated implementation commit.</summary>
    public string? CommitAuthorName { get; init; }

    /// <summary>Platform-owned email used for the generated implementation commit.</summary>
    public string? CommitAuthorEmail { get; init; }

    /// <summary>
    /// Project ID for the run (#335). Delivered per-run so the in-pod agent's tool schema includes
    /// the Agentweaver API tools (record_memory, get_memory, submit_decision, list_decisions,
    /// list_inbox). Warm pods boot with an empty static <c>AgentHost__ProjectId</c>, so without this
    /// the memory/decision tools are silently absent from the agent's callable functions.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>Agent persona name for the run (#335). Paired with <see cref="ProjectId"/> to gate
    /// Agentweaver API tool injection in <c>CopilotAIAgent.BuildSessionConfigTools</c>.</summary>
    public string? AgentName { get; init; }

    internal AgentHostRunConfiguration ToRunConfiguration() => new(
        RunId ?? string.Empty,
        UserId ?? string.Empty,
        TurnBearerToken ?? string.Empty,
        KvUserSecretName,
        GitHubAccessToken,
        PreviewRunnerCredential,
        SharedWorkingDirectory ?? WorkingDirectory,
        Purpose,
        SourceRepositoryPath,
        SourceRef,
        BaseCommitSha,
        ExpectedTreeHash,
        WorkspaceMode,
        ScratchRoot,
        CommitAuthorName,
        CommitAuthorEmail,
        ProjectId,
        AgentName,
        CallerBearerToken,
        RepositoryAccessToken);
}

internal sealed record PreviewProcessStartRequest
{
    public string Command { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string? RunId { get; init; }
    public string? WorkPlanId { get; init; }
    public string? TreeHash { get; init; }
}

internal sealed record PreviewObservePortRequest
{
    public int? TimeoutSeconds { get; init; }
    public string? HealthPath { get; init; }
}

internal sealed record PreviewHealthCheckRequest
{
    public int Port { get; init; }
    public string? Path { get; init; }
}

internal sealed record AgentHostToolApprovalRequest
{
    public string? RunId { get; init; }
    public string? RequestId { get; init; }
    public string Scope { get; init; } = "once";
}

internal static class ToolApprovalEndpointHandlers
{
    public static Task<IResult> GetPendingContextAsync(
        HttpContext ctx,
        string requestId,
        IToolApprovalGate gate,
        AgentHostRuntimeState runtimeState)
    {
        if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
            return Task.FromResult<IResult>(Results.Unauthorized());
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "requestId is required" }));

        var context = gate.GetRequestContext(runtimeState.RunId, requestId);
        var state = gate.GetRequestState(runtimeState.RunId, requestId);
        return Task.FromResult<IResult>(
            state == ToolApprovalRequestState.Pending && context is not null
                ? Results.Ok(new
                {
                    resolved = false,
                    applied = false,
                    state = "pending",
                    toolName = context.ToolName,
                    url = context.Url,
                })
                : ResultFor(state));
    }

    public static async Task<IResult> GrantAsync(
        HttpContext ctx,
        AgentHostToolApprovalRequest request,
        IToolApprovalGate gate,
        AgentHostRuntimeState runtimeState)
    {
        if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Results.BadRequest(new { error = "requestId is required" });
        if (IsRunMismatch(request.RunId, runtimeState.RunId))
            return Results.Conflict(new { error = "run mismatch", state = "run_mismatch" });

        var scope = request.Scope switch
        {
            "run" => ApprovalScope.Run,
            "always" => ApprovalScope.Always,
            "tool" => ApprovalScope.Tool,
            _ => ApprovalScope.Once,
        };

        var context = gate.GetRequestContext(runtimeState.RunId, request.RequestId);
        var applied = await gate.GrantAsync(runtimeState.RunId, request.RequestId, scope).ConfigureAwait(false);
        return ResultFor(
            gate.GetRequestState(runtimeState.RunId, request.RequestId),
            applied,
            context?.ToolName,
            context?.Url);
    }

    public static Task<IResult> DenyAsync(
        HttpContext ctx,
        AgentHostToolApprovalRequest request,
        IToolApprovalGate gate,
        AgentHostRuntimeState runtimeState)
    {
        if (!PreviewRunnerEndpointAuth.Authorize(ctx, runtimeState))
            return Task.FromResult<IResult>(Results.Unauthorized());
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "requestId is required" }));
        if (IsRunMismatch(request.RunId, runtimeState.RunId))
            return Task.FromResult<IResult>(Results.Conflict(new { error = "run mismatch", state = "run_mismatch" }));

        var applied = gate.Deny(runtimeState.RunId, request.RequestId);
        return Task.FromResult(ResultFor(gate.GetRequestState(runtimeState.RunId, request.RequestId), applied));
    }

    private static bool IsRunMismatch(string? requestedRunId, string runtimeRunId) =>
        !string.IsNullOrWhiteSpace(requestedRunId)
        && !string.IsNullOrWhiteSpace(runtimeRunId)
        && !string.Equals(requestedRunId, runtimeRunId, StringComparison.Ordinal);

    private static IResult ResultFor(
        ToolApprovalRequestState state,
        bool applied = false,
        string? toolName = null,
        string? url = null) =>
        state switch
        {
            ToolApprovalRequestState.Approved or
            ToolApprovalRequestState.Denied or
            ToolApprovalRequestState.Expired =>
                Results.Ok(new { resolved = true, applied, state = FormatState(state), toolName, url }),
            ToolApprovalRequestState.Pending =>
                Results.Conflict(new { resolved = false, applied = false, state = "pending" }),
            _ => Results.NotFound(new { resolved = false, applied = false, state = "unknown" }),
        };

    private static string FormatState(ToolApprovalRequestState state) =>
        state switch
        {
            ToolApprovalRequestState.Approved => "approved",
            ToolApprovalRequestState.Denied => "denied",
            ToolApprovalRequestState.Expired => "expired",
            ToolApprovalRequestState.Pending => "pending",
            _ => "unknown",
        };
}

internal static class PreviewRunnerEndpointAuth
{
    /// <summary>
    /// Authorizes a <c>/preview-runner/*</c> call (spec-006 decouple-preview, BLOCKER 2/A). Accepts
    /// EITHER the per-run <see cref="AgentHostRuntimeState.TurnBearerToken"/> OR the per-run
    /// <see cref="AgentHostRuntimeState.PreviewRunnerCredential"/> (delivered in-memory via
    /// <c>/configure</c>). Fail-closed: when EITHER credential is configured, a caller presenting
    /// none/an invalid one is rejected. The dev "no credential configured ⇒ allow" branch applies
    /// ONLY when neither credential is set (local/dev where preview infra is not active).
    /// </summary>
    public static bool Authorize(HttpContext ctx, AgentHostRuntimeState runtimeState)
    {
        var turnBearerToken = runtimeState.TurnBearerToken;
        var previewCredential = runtimeState.PreviewRunnerCredential;

        var hasTurn = !string.IsNullOrEmpty(turnBearerToken);
        var hasCredential = !string.IsNullOrEmpty(previewCredential);

        // Dev/local: no credential configured at all ⇒ allow (preview infra inactive).
        if (!hasTurn && !hasCredential)
            return true;

        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (hasTurn && string.Equals(authHeader, "Bearer " + turnBearerToken, StringComparison.Ordinal))
            return true;
        if (hasCredential && string.Equals(authHeader, "Bearer " + previewCredential, StringComparison.Ordinal))
            return true;

        // Fail-closed: a credential is configured but the caller presented none/an invalid one.
        return false;
    }
}
