using System.Text.Encodings.Web;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using k8s;
using LibGit2Sharp;
using Agentweaver.Api;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.SandboxExec;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Tools;
using Agentweaver.Api.Workflows;
using Agentweaver.Api.ReviewPolicies;

var builder = WebApplication.CreateBuilder(args);

// F1 release-blocker guard: refuse to start if a test-only auth bypass flag is enabled under
// Production. This runs before any service/pipeline setup so a misconfigured production deployment
// fails fast at boot instead of serving traffic with GitHub token / org authorization disabled.
Agentweaver.Api.Security.TestingBypassGuard.EnsureNotEnabledInProduction(
    builder.Environment, builder.Configuration);

// Fix 1 (Seraph T4–T7 review): OAuth issuer/audience must be pinned to the PUBLIC host in Production
// so MCP->API JWT validation (audience = https://<HOST>/mcp) succeeds on internal calls. Fail fast at
// boot if they are not configured, rather than serving traffic where every forwarded JWT 401s.
Agentweaver.Api.Security.OAuthConfigGuard.EnsureProductionIssuerAudiencePinned(
    builder.Environment, builder.Configuration);

var appRole = AppRole.Resolve(builder.Configuration);
var isWorker = appRole == AppRole.Worker;


builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// CORS
if (!isWorker)
{
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [];

    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()));
}

// Infrastructure
// For the Postgres provider, SQLite stores are replaced by EF-backed stores registered below.
// SqliteDb is still registered so SQLite-dependent singletons that aren't yet migrated compile fine;
// it is harmless when Postgres is used (the DB is simply never opened).
builder.Services.AddSingleton<SqliteDb>();
// Provider-aware run stores. In Postgres mode the EF-backed equivalents are registered in the
// Database:Provider block below; the concrete SQLite stores must NOT be registered or injected then,
// otherwise consumers binding the concrete type would open an empty ephemeral SQLite DB and crash.
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    var _isPostgres = _provider is "postgres" or "postgresql";
    if (!_isPostgres)
    {
        builder.Services.AddSingleton<SqliteRunStore>();
        builder.Services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<SqliteRunStore>());
        builder.Services.AddSingleton<SqliteRunRevisionStore>();
        builder.Services.AddSingleton<IRunRevisionStore>(sp => sp.GetRequiredService<SqliteRunRevisionStore>());
        builder.Services.AddSingleton<SqliteWorkflowRunStore>();
        builder.Services.AddSingleton<IWorkflowRunStore>(sp => sp.GetRequiredService<SqliteWorkflowRunStore>());
    }
}
builder.Services.AddSingleton<ISandboxPolicyStore, YamlSandboxPolicyStore>();
builder.Services.AddSingleton<RunStreamStore>();
builder.Services.AddSingleton<Agentweaver.Api.Sandbox.Preview.AgentPreviewGate>();
// IRunEventStream is registered conditionally in the Database:Provider block below.
// SQLite → SqliteRunEventStream (raw SQLite WAL); Postgres → EfRunEventStream (EF + serializable tx).
builder.Services.AddSingleton<WorktreeManager>();
builder.Services.AddSingleton<RepositoryMergeLock>();

// Workflow services
builder.Services.AddSingleton<RunWorkflowRegistry>();
builder.Services.AddSingleton<PendingRequestStore>();
builder.Services.AddSingleton<IWorktreeOperations, WorktreeOperationsAdapter>();
builder.Services.AddSingleton<IMergeCoordinator, MergeCoordinator>();
builder.Services.AddSingleton<RunWorkflowFactory>();
builder.Services.AddSingleton<RunWatchLoopService>();
builder.Services.AddSingleton<WorkflowRestartService>();

// Orchestration
builder.Services.AddSingleton<RunOrchestrator>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorAssemblyStore>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.AssemblyReviewGate>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICollectiveAssemblyPipeline,
    Agentweaver.Api.Coordinator.CollectiveAssemblyPipeline>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorAssemblyService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorAssembly>(
    sp => sp.GetRequiredService<Agentweaver.Api.Coordinator.CoordinatorAssemblyService>());
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorDispatchService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorAutopilot,
    Agentweaver.Api.Coordinator.CoordinatorAutopilot>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorDispatch>(
    sp => sp.GetRequiredService<Agentweaver.Api.Coordinator.CoordinatorDispatchService>());
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorSteeringQueue>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorSteeringService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorSpecDrafter,
    Agentweaver.Api.Coordinator.CopilotCoordinatorSpecDrafter>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.IWorkflowSelectionModel,
    Agentweaver.Api.Coordinator.CopilotWorkflowSelectionModel>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.IWorkflowSelector,
    Agentweaver.Api.Coordinator.WorkflowSelector>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorWorkflowFactory>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorRunService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorStatusReader>();

// GitHub auth (token store + scope provider + device flow service)
var tokenStoreProvider = builder.Configuration["Auth:TokenStore:Provider"];
var kvUri = builder.Configuration["Auth:TokenStore:KeyVaultUri"];
if (string.Equals(tokenStoreProvider, "keyvault", StringComparison.OrdinalIgnoreCase)
    && !string.IsNullOrWhiteSpace(kvUri))
{
    var secretClient = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
    var kvSecretStore = new KeyVaultSecretStore(secretClient);
    var diskFs = new FileSystemGitHubTokenStore(); // migration source only
    var kvTokenStore = new KeyVaultGitHubTokenStore(kvSecretStore, diskFallback: diskFs, diskMirror: null);
    var cachedTokenStore = new CachingGitHubTokenStore(kvTokenStore);
    builder.Services.AddSingleton<ISecretStore>(kvSecretStore);
    builder.Services.AddSingleton<IGitHubTokenStore>(cachedTokenStore);
    builder.Services.AddSingleton<IGitHubDeviceFlowStore>(new SecretStoreGitHubDeviceFlowStore(kvSecretStore));
    builder.Services.AddSingleton(secretClient); // exposed for SPC startup re-sync
}
else
{
    builder.Services.AddSingleton<IGitHubTokenStore, OsCredentialStoreGitHubTokenStore>();
    builder.Services.AddSingleton<IGitHubDeviceFlowStore, InMemoryGitHubDeviceFlowStore>();
}
var scopeProviderName = builder.Configuration["Auth:GitHub:ScopeProvider"] ?? "caller";
if (string.Equals(scopeProviderName, "installation", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IGitHubTokenScopeProvider, FixedInstallationScopeProvider>();
else
    builder.Services.AddSingleton<IGitHubTokenScopeProvider, CallerTokenScopeProvider>();
builder.Services.AddSingleton<IGitHubAccessTokenProvider, GitHubTokenRefreshService>();
builder.Services.AddSingleton<IGitHubAuthService, GitHubDeviceFlowAuthService>();
builder.Services.AddHttpClient<GitHubDeviceFlowAuthService>();
builder.Services.AddHttpClient("github-authz")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("github")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<GitHubOAuthRedirectService>();

// MCP OAuth 2.1 Authorization Server (Option C / Seraph design). T1-T3:
//  - McpTokenService: signs short-lived (15m) audience-bound JWT access tokens; key from
//    Auth:OAuth:SigningKey (Key Vault secret 'mcp-oauth-signing-key'), ephemeral dev fallback.
//  - McpOAuthBrokerService: brokers GitHub login (reusing GitHubOAuthRedirectService) + enforces
//    microsoft org membership, then issues PKCE-bound authorization codes.
builder.Services.AddSingleton<Agentweaver.Api.Auth.OAuth.McpTokenService>();
// Scoped: backed by the scoped MemoryDbContext so pending authorizations and issued authorization
// codes are persisted (Postgres in prod) and the OAuth flow is replica-safe.
builder.Services.AddScoped<Agentweaver.Api.Auth.OAuth.McpOAuthBrokerService>();
builder.Services.AddSingleton<Agentweaver.Api.Auth.WebSessionExchangeService>();
// T4: rotating refresh-token store + jti denylist (scoped: backed by the scoped MemoryDbContext).
builder.Services.AddScoped<Agentweaver.Api.Auth.OAuth.McpRefreshTokenStore>();
builder.Services.AddScoped<Agentweaver.Api.Auth.OAuth.McpClientStore>();

// F3: rate-limit the public OAuth flow endpoints. /oauth/authorize triggers a GitHub API call and
// /oauth/token can be probed at volume, so apply a fixed-window limiter (20 req/min per client IP).
// Scoped to the "oauth" policy below — the .well-known metadata and JWKS are intentionally NOT
// limited so discovery stays cheap and never throttles conformant clients.
if (!isWorker)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(OAuthServerEndpoints.RateLimitPolicy, httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                });
        });
    });
}

// Project infrastructure (must be before AddAgentRuntime)
// Provider-aware: Postgres uses EF stores; SQLite uses raw ADO.NET stores.
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    var _isPostgres = _provider is "postgres" or "postgresql";
    if (_isPostgres)
    {
        builder.Services.AddSingleton<EfProjectStore>();
        builder.Services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<EfProjectStore>());
    }
    else
    {
        builder.Services.AddSingleton<SqliteProjectStore>();
        builder.Services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
    }
}
builder.Services.AddSingleton<LocalFilesystemWorkspaceProvider>();
builder.Services.AddSingleton<PersistentVolumeWorkspaceProvider>();
builder.Services.AddSingleton<IProjectWorkspaceProvider>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var provider = configuration["Workspace:Provider"]?.ToLowerInvariant() ?? "local";
    return provider switch
    {
        "persistent-volume" or "kubernetes" => sp.GetRequiredService<PersistentVolumeWorkspaceProvider>(),
        _ => sp.GetRequiredService<LocalFilesystemWorkspaceProvider>()
    };
});
builder.Services.AddSingleton<ProjectGitInitializer>();
builder.Services.AddSingleton<ProjectService>();

// Backlog & Kanban board (Feature 009)
// Provider-aware: Postgres uses EfBacklogTaskStore; SQLite uses SqliteBacklogTaskStore.
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    var _isPostgres = _provider is "postgres" or "postgresql";
    if (_isPostgres)
    {
        builder.Services.AddSingleton<EfBacklogTaskStore>();
        builder.Services.AddSingleton<IBacklogTaskStore>(sp => sp.GetRequiredService<EfBacklogTaskStore>());
    }
    else
    {
        builder.Services.AddSingleton<SqliteBacklogTaskStore>();
        builder.Services.AddSingleton<IBacklogTaskStore>(sp => sp.GetRequiredService<SqliteBacklogTaskStore>());
    }
}
builder.Services.AddSingleton<Agentweaver.Api.Runs.WorkflowStageProjector>();
builder.Services.AddSingleton<Agentweaver.Api.Runs.IWorkflowStageProjector>(
    sp => sp.GetRequiredService<Agentweaver.Api.Runs.WorkflowStageProjector>());
builder.Services.AddSingleton<Agentweaver.Api.Runs.BoardProjectionService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorPickupService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorReconciler>();
builder.Services.AddSingleton<Agentweaver.Api.Diagnostics.HeartbeatStatusStore>();
builder.Services.AddHostedService<Agentweaver.Api.Coordinator.CoordinatorHeartbeatService>();

// Workflows (Feature 010) + Diagnostics (Feature 011)
builder.Services.AddSingleton<Agentweaver.Api.Workflows.WorkflowRegistry>();
builder.Services.AddSingleton<Agentweaver.Api.ReviewPolicies.ReviewPolicyRegistry>();
builder.Services.AddSingleton<Agentweaver.Api.Diagnostics.DiagnosticsService>();
builder.Services.AddSingleton<Agentweaver.Api.Metrics.MetricsService>();

// Feature 019: token usage store (provider-conditional like IRunStore)
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    if (_provider is "postgres" or "postgresql")
        builder.Services.AddSingleton<ITokenUsageStore, EfTokenUsageStore>();
    else
        builder.Services.AddSingleton<ITokenUsageStore, SqliteTokenUsageStore>();
}
builder.Services.AddHostedService<TokenUsageProjectionService>();

// Agent runtime
builder.Services.AddAgentRuntime();

// ISandboxExecutorRouter (017-US2): explicit router replaces fragile last-registration-wins pattern.
// Overrides the ISandboxExecutor registered by AddAgentRuntime() — last registration wins.
builder.Services.AddSingleton<IExecutionPodNameStore, RunEventExecutionPodNameStore>();
builder.Services.AddSingleton<IPodNameRegistry, PodNameRegistry>();
builder.Services.AddSingleton<IAgentHostTurnTokenRegistry>(sp =>
    (PodNameRegistry)sp.GetRequiredService<IPodNameRegistry>());
// Resolves a run's submitting user (from IRunStore) so the pod-per-run executor can inject
// AgentHost__UserId, scoping the in-pod GitHub Copilot auth to the user's Copilot-entitled token
// instead of the installation token (which fails the first model turn).
builder.Services.AddSingleton<IRunSubmittingUserResolver, RunStoreSubmittingUserResolver>();
builder.Services.AddSingleton<ISandboxExecutorRouter, SandboxExecutorRouter>();
builder.Services.AddSingleton<ISandboxExecutor>(sp =>
    sp.GetRequiredService<ISandboxExecutorRouter>().Resolve());

// spec-018 P1: AgentExecutionMode flag + A2A client seam (RemoteAgentProxy).
// Sandbox:AgentExecutionMode=in-api (default) keeps all agent turns in-process.
// Sandbox:AgentExecutionMode=pod-per-run routes turns to the per-run sandbox pod via A2A.
// IWorkflowAgentFactory override: must be registered AFTER AddAgentRuntime() to win.
{
    var agentMode = SandboxAgentOptions.ParseMode(
        builder.Configuration["Sandbox:AgentExecutionMode"]);

    var sandboxAgentOptions = new SandboxAgentOptions
    {
        AgentExecutionMode = agentMode,
        AgentHostPort = int.TryParse(builder.Configuration["Sandbox:AgentHost:Port"], out var p) ? p : 8088,
        // RequireMtls (default true) drives AgentHostScheme (https/http) via AgentHostEndpoint.
        // Set Sandbox:AgentHost:RequireMtls=false ONLY for the PoC env (plain http, no client cert).
        RequireMtls = !string.Equals(
            builder.Configuration["Sandbox:AgentHost:RequireMtls"], "false", StringComparison.OrdinalIgnoreCase),
        AgentHostA2APath = builder.Configuration["Sandbox:AgentHost:A2APath"] ?? "/a2a/agent",
    };
    builder.Services.AddSingleton(sandboxAgentOptions);

    // ISandboxAgentEndpointResolver: Kubernetes-native when in-cluster, no-op otherwise.
    // The no-op resolver causes a clear error if pod-per-run is attempted outside K8s.
    builder.Services.AddSingleton<ISandboxAgentEndpointResolver>(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        if (!SandboxExecutorFactory.IsInCluster)
            return new NoOpSandboxAgentEndpointResolver();

        try
        {
            var k8sConfig = KubernetesClientConfiguration.InClusterConfig();
            var k8sClient = new Kubernetes(k8sConfig);
            var podRegistry = sp.GetRequiredService<IPodNameRegistry>();
            var ns = builder.Configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
            // Optional: present in-cluster so the resolver can lazily launch the AgentHost
            // pod on first resolve (pod-per-run). Absent => resolver only reads the registry.
            var podLifecycle = sp.GetService<IAgentHostPodLifecycle>();
            // Optional: lets the resolver record a precise FailureReason (agent_quota_exceeded /
            // agent_pod_reconciler_error) on the run when a lazy pod launch fails.
            var runStore = sp.GetService<Agentweaver.Api.Infrastructure.IRunStore>();
            return new KubernetesPodAgentEndpointResolver(
                k8sClient, podRegistry, ns, sandboxAgentOptions,
                loggerFactory.CreateLogger<KubernetesPodAgentEndpointResolver>(),
                podLifecycle, runStore);
        }
        catch
        {
            return new NoOpSandboxAgentEndpointResolver();
        }
    });

    // Named HttpClient for A2A sandbox pod connections.
    // When RequireMtls=true (production, H1), attach the client-certificate handler here so the
    // worker presents its workload-bound cert on every pod connection (wiring owned by Link via
    // a mounted secret — left as the documented hook). When RequireMtls=false (PoC), no client
    // cert is configured and the worker connects over plain http.
    builder.Services.AddHttpClient("a2a-sandbox-pod")
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(30))
        // Defense-in-depth for the A2A cold-start race: retry ONLY connection-refused (the AgentHost
        // Kestrel listener has not bound :8088 yet). Safe for streaming sends — a refused connect
        // delivers no bytes, so there is no duplicate side effect. See ConnectRefusedRetryHandler.
        .AddHttpMessageHandler(sp => new ConnectRefusedRetryHandler(
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConnectRefusedRetryHandler>()));

    // RemoteWorkflowAgentFactory registered as an alternative to WorkflowAgentFactory.
    builder.Services.AddSingleton<RemoteWorkflowAgentFactory>();

    // Override IWorkflowAgentFactory if pod-per-run is requested.
    // AddAgentRuntime() registers WorkflowAgentFactory; this last-wins override replaces it.
    if (agentMode == AgentExecutionMode.PodPerRun)
    {
        builder.Services.AddSingleton<IWorkflowAgentFactory>(sp =>
            sp.GetRequiredService<RemoteWorkflowAgentFactory>());
    }
}

// spec-018 P1: IAgentHostPodLifecycle registration (pod-per-run launch/release lifecycle).
// KubernetesSandboxExecutor implements IAgentHostPodLifecycle when in-cluster. Register it
// as a non-nullable singleton that delegates to the ISandboxExecutor. When not in K8s the
// service is absent and RunWatchLoopService receives null (optional parameter).
if (SandboxExecutorFactory.IsInCluster)
{
    builder.Services.AddSingleton<IAgentHostPodLifecycle>(sp =>
    {
        var lifecycle = sp.GetRequiredService<ISandboxExecutor>() as IAgentHostPodLifecycle;
        return lifecycle ?? throw new InvalidOperationException(
            "ISandboxExecutor does not implement IAgentHostPodLifecycle in Kubernetes mode. " +
            "KubernetesSandboxExecutor is expected when KUBERNETES_SERVICE_HOST is set.");
    });
}

// spec-006: AgentHost orphaned-pod reaper. Each AgentHost pod reserves 2 CPU against the namespace
// quota (24 CPU); claims left behind by crashed/stalled runs exhaust it and make new runs fail with
// "exceeded quota". Register a shared in-cluster Kubernetes client + the reaper as a regular
// singleton (its cadence is driven by the coordinator heartbeat, NOT a standalone BackgroundService)
// when the sandbox provider is Kubernetes (Sandbox:Provider == "kubernetes") or we are in-cluster.
{
    var sandboxProvider = builder.Configuration["Sandbox:Provider"]?.ToLowerInvariant();
    var useKubernetesSandbox =
        sandboxProvider == "kubernetes" || SandboxExecutorFactory.IsInCluster;

    if (useKubernetesSandbox)
    {
        IKubernetes? sharedK8sClient = null;
        try
        {
            sharedK8sClient = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
        }
        catch (Exception ex)
        {
            // Best-effort: without a client the reaper and the diagnostics quota check are skipped,
            // but the API must still boot (e.g. provider=kubernetes outside a cluster during tooling).
            Console.Error.WriteLine(
                $"AgentHostReaper: in-cluster Kubernetes client init failed; reaper + quota diagnostics disabled. {ex.Message}");
        }

        if (sharedK8sClient is not null)
        {
            builder.Services.AddSingleton<IKubernetes>(sharedK8sClient);

            var reaperNamespace = builder.Configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
            builder.Services.AddSingleton<KubernetesSandboxOptions>(
                new KubernetesSandboxOptions { Namespace = reaperNamespace });
            builder.Services.AddSingleton<IAgentHostReaper, AgentHostReaperService>();
        }
    }
}

// SandboxRuntimeOptions: controls ReleasePodOnSuspend and AgentExecutionMode at runtime.
// Bound from the same "Sandbox" section; the IsPodPerRun computed prop used by RunWatchLoopService.
builder.Services.Configure<SandboxRuntimeOptions>(builder.Configuration.GetSection("Sandbox"));

// Port-forward service (017-preview): manages kubectl port-forward sessions per run.
builder.Services.AddSingleton<PortForwardService>();

// Gateway-direct browser preview (feat/sandbox-preview-proxy): Gateway -> per-preview HTTPRoute
// -> per-run ClusterIP Service -> sandbox pod. Replaces the replica-unsafe in-cluster kubectl
// port-forward leg. Ships DARK: when Sandbox:Preview:Enabled=false (default) the service reports
// Enabled=false and the reaper idles, so default behaviour is unchanged.
{
    var previewOptions = builder.Configuration.GetSection("Sandbox:Preview").Get<SandboxPreviewOptions>()
        ?? new SandboxPreviewOptions();
    builder.Services.AddSingleton(previewOptions);

    builder.Services.AddSingleton<ISandboxPreviewService>(sp =>
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SandboxPreviewService>();

        // Build an in-cluster client only when enabled AND in-cluster; otherwise pass null so the
        // service reports Enabled=false and every method short-circuits (no kubectl, no captive deps).
        IKubernetes? k8sClient = null;
        if (previewOptions.Enabled && SandboxExecutorFactory.IsInCluster)
        {
            try
            {
                k8sClient = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SandboxPreviewService: in-cluster Kubernetes client init failed; preview disabled.");
            }
        }

        return new SandboxPreviewService(k8sClient, previewOptions, logger);
    });

    // Replica-safe annotation-driven reaper. No-ops when preview disabled.
    builder.Services.AddHostedService<SandboxPreviewReaperService>();
}

// Kubernetes runtime environment detection (pod name, in-cluster flag).
builder.Services.AddSingleton<IKubernetesEnvironment, DefaultKubernetesEnvironment>();

// Authentication
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IGitHubOrgAuthorizationService, GitHubOrgAuthorizationService>();

// Repository path validation (A2 security fix)
builder.Services.AddSingleton<RepositoryRootValidator>();

// Memory database (EF Core, separate file from main SQLite DB).
// Database:Provider controls the backend: sqlite (default), sqlserver/azuresql, postgres/postgresql.
// For postgres: use AddDbContextFactory (thread-safe, per-call contexts) with migrations assembly
// Agentweaver.Api.Migrations.Postgres. For other providers: use AddDbContext (scoped).
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    var _isPostgres = _provider is "postgres" or "postgresql";

    void ConfigureOpts(DbContextOptionsBuilder opts)
    {
        switch (_provider)
        {
            case "sqlserver":
            case "azuresql":
                opts.UseSqlServer(builder.Configuration.GetConnectionString("MemoryDb")
                    ?? builder.Configuration["Database:ConnectionString"]
                    ?? throw new InvalidOperationException("Database:ConnectionString is required for SQL Server provider."));
                break;
            case "postgres":
            case "postgresql":
                opts.UseNpgsql(
                    builder.Configuration.GetConnectionString("Postgres")
                        ?? builder.Configuration.GetConnectionString("MemoryDb")
                        ?? builder.Configuration["Database:ConnectionString"]
                        ?? throw new InvalidOperationException("ConnectionStrings:Postgres (or MemoryDb / Database:ConnectionString) is required for PostgreSQL provider."),
                    npg => npg.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres"));
                break;
            default: // sqlite
                var basePath = builder.Configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
                    ? Path.GetDirectoryName(Path.GetFullPath(p))!
                    : AppPaths.DataDirectory;
                opts.UseSqlite($"Data Source={Path.Combine(basePath, "memory.db")}",
                    b => b.MigrationsAssembly("Agentweaver.Api"));
                break;
        }
    }

    if (_isPostgres)
    {
        // Factory pattern: each store call gets a fresh DbContext — required for concurrent access
        // from singleton stores (EfRunStore, EfProjectStore, etc.)
        builder.Services.AddDbContextFactory<MemoryDbContext>(ConfigureOpts);
        // Also register a scoped DbContext for scoped services (McpRefreshTokenStore etc.)
        builder.Services.AddDbContext<MemoryDbContext>(ConfigureOpts);

        // EF-backed singleton stores (provider-independent, use IDbContextFactory)
        builder.Services.AddSingleton<EfRunStore>();
        builder.Services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<EfRunStore>());
        builder.Services.AddSingleton<EfRunRevisionStore>();
        builder.Services.AddSingleton<IRunRevisionStore>(sp => sp.GetRequiredService<EfRunRevisionStore>());
        builder.Services.AddSingleton<EfWorkflowRunStore>();
        builder.Services.AddSingleton<IWorkflowRunStore>(sp => sp.GetRequiredService<EfWorkflowRunStore>());
        builder.Services.AddSingleton<EfCastProposalStore>();

        // Durable pub/sub event stream backed by EF + Postgres (two-layer: serializable tx + channel)
        builder.Services.AddSingleton<IRunEventStream, EfRunEventStream>();

        // Data migrator (SQLite → Postgres)
        builder.Services.AddSingleton<SqliteToPostgresMigrator>();
    }
    else
    {
        builder.Services.AddDbContext<MemoryDbContext>(ConfigureOpts);
        // Durable pub/sub event stream backed by raw SQLite WAL write-through + channel
        builder.Services.AddSingleton<IRunEventStream, SqliteRunEventStream>();
    }
}

// Checkpoint store backend: Postgres deployments use a shared, concurrency-safe store so both API
// replicas read/write the same checkpoints (no exclusive file lock, no shared-volume permission
// dependency, genuine cross-pod resume). SQLite/dev falls back to the per-pod file store.
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    if (_provider is "postgres" or "postgresql")
        builder.Services.AddSingleton<ICheckpointStoreFactory, PostgresCheckpointStoreFactory>();
    else
        builder.Services.AddSingleton<ICheckpointStoreFactory, FileCheckpointStoreFactory>();
}

// Run lease store: Postgres CAS-based for multi-replica; no-op for SQLite (single-replica safe).
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    if (_provider is "postgres" or "postgresql")
        builder.Services.AddSingleton<IRunLeaseStore, PostgresRunLeaseStore>();
    else
        builder.Services.AddSingleton<IRunLeaseStore, NoOpRunLeaseStore>();
}
builder.Services.AddScoped<MemoryContextCompiler>();
builder.Services.AddScoped<PostRunScribeService>();
builder.Services.AddSingleton<Agentweaver.Api.Projects.ProjectWorkspaceService>();

// Checkpoint GC background service (Guardrail 8)
builder.Services.AddHostedService<CheckpointGcService>();

// Casting
// Provider-aware: Postgres uses EfCastProposalStore; SQLite uses CastProposalStore.
// Both implement ICastProposalStore.
builder.Services.AddSingleton<CatalogReader>();
{
    var _provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    if (_provider is "postgres" or "postgresql")
    {
        // EfCastProposalStore already registered in the Postgres block above.
        builder.Services.AddSingleton<ICastProposalStore>(sp => sp.GetRequiredService<EfCastProposalStore>());
    }
    else
    {
        builder.Services.AddSingleton<CastProposalStore>();
        builder.Services.AddSingleton<ICastProposalStore>(sp => sp.GetRequiredService<CastProposalStore>());
    }
}
builder.Services.AddSingleton<ProjectSignalScanner>();
builder.Services.AddSingleton<CastingService>();

// Blueprints (Feature 012)
builder.Services.AddSingleton<IBlueprintGenerator, CopilotBlueprintGenerator>();
builder.Services.AddSingleton<BlueprintService>();

// Workflow generation (Feature 015 US10) — LLM → YAML draft, validated + one correction pass.
builder.Services.AddSingleton<Agentweaver.Api.Workflows.IWorkflowGenerator, Agentweaver.Api.Workflows.CopilotWorkflowGenerator>();

// Spec-to-backlog decomposition (Feature 014)
builder.Services.AddSingleton<Agentweaver.Api.Backlog.BacklogDecomposeService>();

var app = builder.Build();

// For Postgres, skip SqliteDb.EnsureCreatedAsync (agentweaver.db tables are in MemoryDbContext).
// For SQLite/other providers, run EnsureCreatedAsync as before.
var _startupProvider = app.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
if (_startupProvider is not ("postgres" or "postgresql"))
{
    await app.Services.GetRequiredService<SqliteDb>().EnsureCreatedAsync();
}

using (var scope = app.Services.CreateScope())
{
    var memoryDb = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

    // Transition guard: databases created before migrations were introduced
    // (via EnsureCreated) already have AgentMemory/Decisions/etc. but not RunEvents,
    // and have no __EFMigrationsHistory table. Detect this case and patch gracefully.
    // For fresh installs (no tables at all), MigrateAsync handles everything.
    if (memoryDb.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
    {
        var rawConn = (Microsoft.Data.Sqlite.SqliteConnection)memoryDb.Database.GetDbConnection();
        await rawConn.OpenAsync();
        using (var pragma = rawConn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync();
        }
        long historyExists, agentMemoryExists;
        using (var c1 = rawConn.CreateCommand())
        {
            c1.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
            historyExists = (long)(await c1.ExecuteScalarAsync())!;
        }
        using (var c2 = rawConn.CreateCommand())
        {
            c2.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AgentMemory'";
            agentMemoryExists = (long)(await c2.ExecuteScalarAsync())!;
        }
        if (historyExists == 0 && agentMemoryExists > 0)
        {
            // Pre-migration DB: seed __EFMigrationsHistory so MigrateAsync treats the
            // initial migration as already applied, then create only RunEvents (missing table).
            // MigrateAsync is called unconditionally below to apply any later migrations.
            await memoryDb.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260616063937_AddRunEvents', '9.0.0');
                CREATE TABLE IF NOT EXISTS "RunEvents" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                    "RunId" TEXT NOT NULL,
                    "Sequence" INTEGER NOT NULL,
                    "EventType" TEXT NOT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_RunEvents_RunId" ON "RunEvents" ("RunId");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
                """);
        }
    }

    // Always run MigrateAsync: on the pre-migration branch the history entries seeded above tell
    // EF that AddRunEvents is already applied, so only the subsequent migrations are executed.
    // On a fresh install or an already-migrated DB this is the normal migration path.
    await memoryDb.Database.MigrateAsync();
}

// --migrate-data: run SQLite → Postgres data migration then exit.
if (args.Contains("--migrate-data"))
{
    var migrator = app.Services.GetService<SqliteToPostgresMigrator>();
    if (migrator is null)
    {
        Console.Error.WriteLine("--migrate-data requires Database:Provider=postgres.");
        Environment.Exit(1);
        return;
    }
    await migrator.RunAsync(CancellationToken.None);
    Environment.Exit(0);
    return;
}
// Startup recovery — run on exactly one replica via a Postgres advisory lock so concurrent pod
// restarts do not race on orphaned-run recovery and trigger Postgres 40001 serialization failures.
// On SQLite (dev) the lock is always granted and the startup path is unchanged.
await using var recoveryLeader = await StartupRecoveryLeader.AcquireAsync(
    app.Configuration, app.Logger, CancellationToken.None);

if (recoveryLeader.IsLeader)
{
    await app.Services.GetRequiredService<WorkflowRestartService>().RecoverAsync(CancellationToken.None);
    // Coordinator (parent) runs are recovered AFTER the generic sweep (which has already failed any
    // stranded child runs) so a re-dispatched subtask always launches a fresh child. This re-arms the
    // dispatch / collective-assembly engine from the persisted work plan, or resumes the spec-phase MAF
    // workflow from checkpoint, instead of failing interrupted orchestrations.
    await app.Services.GetRequiredService<Agentweaver.Api.Coordinator.CoordinatorRunService>()
        .RecoverInterruptedRunsAsync(CancellationToken.None);
    // Immediate watchdog sweep at startup: re-arm any coordinator whose work plan is still dispatching
    // but has no active loop (orphaned after a crash/restart that the run-status recovery above did not
    // re-arm), so a restart recovers stuck dispatch fast instead of waiting for the first heartbeat tick.
    await app.Services.GetRequiredService<Agentweaver.Api.Coordinator.CoordinatorReconciler>()
        .SweepAsync(CancellationToken.None);
}
// recoveryLeader disposed here → Postgres advisory lock released so future restarts can acquire it.

// Startup mount-health warning: runs on ALL replicas so every pod logs its own volume state.
// The app continues — the /healthz/workspace readiness probe will keep unmounted pods out of the
// Service until the volume attaches, so traffic is not served. This log aids incident diagnosis.
{
    var workspaceProvider = app.Services.GetRequiredService<IProjectWorkspaceProvider>();
    if (!workspaceProvider.IsMountRootHealthy())
    {
        app.Logger.LogWarning(
            "Workspace mount-root health check failed at startup. " +
            "The workspace volume may not be mounted or may be read-only. " +
            "Pod will be excluded from the Service until /healthz/workspace returns 200.");
    }
}

if (isWorker)
{
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok", role = AppRole.Worker }));
    app.MapGet("/readyz", () => Results.Ok(new { status = "ready", role = AppRole.Worker }));
}
else
{
    app.UseExceptionHandler(err => err.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }));

    app.UseCors();
    app.UseRateLimiter();
    app.UseMiddleware<GitHubTokenAuthMiddleware>();
    app.UseMiddleware<GitHubOrgAuthorizationMiddleware>();

    app.MapRunEndpoints();
    app.MapProjectEndpoints();
    app.MapProjectWorkspaceEndpoints();
    app.MapBacklogEndpoints();
    app.MapBacklogDecomposeEndpoints();
    app.MapCoordinatorEndpoints();
    app.MapCastingEndpoints();
    app.MapBlueprintEndpoints();
    app.MapTeamEndpoints();
    app.MapAuthEndpoints();
    app.MapOAuthServerEndpoints();
    app.MapDecisionsEndpoints();
    app.MapMemoryEndpoints();
    app.MapWorkflowDefinitionEndpoints();
    app.MapReviewPolicyEndpoints();
    app.MapDiagnosticsEndpoints();
    app.MapMetricsEndpoints();
    app.MapUsageEndpoints();
    app.MapSandboxEndpoints();
    app.MapSystemEndpoints();
}

app.Run();

public partial class Program { }
