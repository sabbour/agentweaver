using System.Net;
using Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
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
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(a2aPort));
}

// ── GitHub credential chain ────────────────────────────────────────────────────
// The token is expected in Providers:GitHubCopilot:GitHubToken (set at pod launch).
// PodGitHubTokenStore is seeded below from the factory's config-fallback path; the
// factory itself reads GitHubToken from config directly, so the token store is a
// backstop for the scope-provider resolution path.
var podTokenStore = new PodGitHubTokenStore();
builder.Services.AddSingleton<IGitHubTokenStore>(podTokenStore);
builder.Services.AddSingleton<IGitHubTokenScopeProvider, PodInstallationScopeProvider>();
// No IGitHubAccessTokenProvider (token is static at pod lifetime).

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
// Return 503 until AgentHostStartupService has completed SetupAsync.
app.Use(async (ctx, next) =>
{
    var startup = ctx.RequestServices.GetRequiredService<AgentHostStartupService>();
    if (!startup.IsReady &&
        !ctx.Request.Path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Agent not ready — SetupAsync in progress.").ConfigureAwait(false);
        return;
    }
    await next(ctx).ConfigureAwait(false);
});

// ── H3: card endpoint bearer auth gate ────────────────────────────────────────
// Rejects unauthenticated discovery of the agent card unless CardBearerToken is empty
// (dev/test only). Evaluated before the A2A route so it cannot be bypassed.
app.Use(async (ctx, next) =>
{
    var opts = ctx.RequestServices.GetRequiredService<IOptions<AgentHostOptions>>().Value;
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
    await next(ctx).ConfigureAwait(false);
});

// ── Liveness probe ─────────────────────────────────────────────────────────────
app.MapGet("/healthz", (AgentHostStartupService startup) =>
    startup.IsReady ? Results.Ok("ready") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

// ── A2A endpoints ──────────────────────────────────────────────────────────────
// Mounts:
//   POST  {A2APath}/v1/message:stream  — streaming agent turn (SSE)
//   GET   {A2APath}/v1/card            — agent card discovery (authz-gated above)
var opts0 = app.Services.GetRequiredService<IOptions<AgentHostOptions>>().Value;
app.MapA2AHttpJson(agentHostedBuilder, opts0.A2APath);

await app.RunAsync().ConfigureAwait(false);
