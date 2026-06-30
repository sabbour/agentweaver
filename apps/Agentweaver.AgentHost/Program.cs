using System.Net;
using Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
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

// ── Bootstrap ──────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Load AgentHost options (per-run config injected as env vars / config at pod launch).
builder.Services.Configure<AgentHostOptions>(builder.Configuration.GetSection("AgentHost"));

// Mutable per-run runtime state. Immutable AgentHostOptions carries static config; this holder
// carries RunId/UserId/TurnBearerToken/KvUserSecretName delivered later via POST /configure
// (warm pool) or seeded from options at startup (env-var launch).
builder.Services.AddSingleton<AgentHostRuntimeState>();

// ── A2A listener: mTLS (production default) vs plain HTTP (PoC) ─────────────────
// Sandbox:AgentHost:RequireMtls maps here as AgentHost:RequireMtls. Default TRUE keeps the
// secure path (H1): the mounted appsettings.k8s.json (at /app/config) drives
// Kestrel:Endpoints:A2A with the workload-bound server cert + RequireCertificate. When FALSE
// (PoC only), no Kestrel:Endpoints are configured and the listener falls back to plain HTTP on
// AgentHost:Port. The SandboxTemplate sets envVarsInjectionPolicy=Disallowed, so this config is
// read from the mounted ConfigMap, not per-run env vars.
builder.Configuration.AddJsonFile("/app/config/appsettings.k8s.json", optional: true);

var requireMtls = !string.Equals(
    builder.Configuration["AgentHost:RequireMtls"], "false", StringComparison.OrdinalIgnoreCase);
var a2aPort = int.TryParse(builder.Configuration["AgentHost:Port"], out var parsedPort)
    ? parsedPort
    : 8088;
var kestrelEndpointsConfigured = builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();

if (!requireMtls && !kestrelEndpointsConfigured)
{
    // PoC path: no explicit Kestrel endpoint config → bind plain HTTP on the A2A port.
    // MUST NOT be used in production (set AgentHost:RequireMtls=true + provide Kestrel:Endpoints).
    // Use IPAddress.Any (0.0.0.0) rather than ListenAnyIP ([::]) so the pod is reachable on
    // single-stack IPv4 clusters where the IPv4 podIP is dialled directly by the API.
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Any, a2aPort));
}

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

// ── Agent runtime (in-memory approvals, local executor — Kata VM IS the sandbox) ─
builder.Services.AddAgentRuntime();

// ── CopilotAIAgent (singleton per pod — one run per pod lifetime) ──────────────
builder.Services.AddSingleton<CopilotAIAgent>();

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
    (sp, _) => new A2ATurnBridgeAgent(
        sp.GetRequiredService<CopilotAIAgent>(),
        sp.GetRequiredService<ILogger<A2ATurnBridgeAgent>>()),
    Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);

agentHostedBuilder.AddA2AServer(options =>
{
    options.AgentRunMode = AgentRunMode.DisallowBackground;
});

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

    // Interlocked one-time gate (inside TryConfigure): first caller wins, the rest get 409.
    if (!runtimeState.TryConfigure(
            body.RunId, body.UserId ?? string.Empty, body.TurnBearerToken ?? string.Empty, body.KvUserSecretName))
        return Results.Conflict("Already configured");

    await startup.ConfigureAsync(
        body.RunId, body.UserId ?? string.Empty, body.TurnBearerToken ?? string.Empty,
        body.KvUserSecretName, ctx.RequestAborted).ConfigureAwait(false);

    return Results.Ok(new { configured = true, runId = body.RunId });
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

// ── A2A endpoints ──────────────────────────────────────────────────────────────
// Mounts:
//   POST  {A2APath}/v1/message:stream  — streaming agent turn (SSE)
//   GET   {A2APath}/v1/card            — agent card discovery (authz-gated above)
var opts0 = app.Services.GetRequiredService<IOptions<AgentHostOptions>>().Value;
app.MapA2AHttpJson(agentHostedBuilder, opts0.A2APath);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Request body for the warm-pool <c>POST /configure</c> endpoint.</summary>
internal sealed record ConfigureRequest
{
    public string? RunId { get; init; }
    public string? UserId { get; init; }
    public string? TurnBearerToken { get; init; }
    public string? KvUserSecretName { get; init; }
}
