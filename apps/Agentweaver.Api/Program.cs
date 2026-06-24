using System.Text.Encodings.Web;
using k8s;
using LibGit2Sharp;
using Agentweaver.Api.Sandbox;
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
using Agentweaver.Api.Workflows;
using Agentweaver.Api.ReviewPolicies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// CORS
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()));

// Infrastructure
builder.Services.AddSingleton<SqliteDb>();
builder.Services.AddSingleton<SqliteRunStore>();
builder.Services.AddSingleton<SqliteRunRevisionStore>();
builder.Services.AddSingleton<SqliteWorkflowRunStore>();
builder.Services.AddSingleton<ISandboxPolicyStore, YamlSandboxPolicyStore>();
builder.Services.AddSingleton<RunStreamStore>();
// Durable, pub/sub run event log (016-run-event-stream). Two-layer: synchronous SQLite
// write-through for durability + an in-process Channel<RunEvent> per run for low-latency
// tailing. RunStreamStore is retained as the live fan-out path pending 016-us2/us3 migration.
builder.Services.AddSingleton<IRunEventStream, SqliteRunEventStream>();
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
builder.Services.AddSingleton<IGitHubTokenStore, OsCredentialStoreGitHubTokenStore>();
builder.Services.AddSingleton<IGitHubTokenScopeProvider, FixedInstallationScopeProvider>();
builder.Services.AddSingleton<IGitHubAccessTokenProvider, GitHubTokenRefreshService>();
builder.Services.AddSingleton<IGitHubAuthService, GitHubDeviceFlowAuthService>();
builder.Services.AddHttpClient<GitHubDeviceFlowAuthService>();
builder.Services.AddSingleton<GitHubOAuthRedirectService>();

// Project infrastructure (must be before AddAgentRuntime)
builder.Services.AddSingleton<SqliteProjectStore>();
builder.Services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
builder.Services.AddSingleton<IProjectWorkspaceProvider, LocalFilesystemWorkspaceProvider>();
builder.Services.AddSingleton<ProjectGitInitializer>();
builder.Services.AddSingleton<ProjectService>();

// Backlog & Kanban board (Feature 009)
builder.Services.AddSingleton<SqliteBacklogTaskStore>();
builder.Services.AddSingleton<IBacklogTaskStore>(sp => sp.GetRequiredService<SqliteBacklogTaskStore>());
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

// Agent runtime
builder.Services.AddAgentRuntime();

// ISandboxExecutorRouter (017-US2): explicit router replaces fragile last-registration-wins pattern.
// Overrides the ISandboxExecutor registered by AddAgentRuntime() — last registration wins.
builder.Services.AddSingleton<ISandboxExecutorRouter, SandboxExecutorRouter>();
builder.Services.AddSingleton<ISandboxExecutor>(sp =>
    sp.GetRequiredService<ISandboxExecutorRouter>().Resolve());

// Authentication
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ApiKeyRegistry>();
builder.Services.AddSingleton<GitHubOrgAuthorizationService>();

// Repository path validation (A2 security fix)
builder.Services.AddSingleton<RepositoryRootValidator>();

// Memory database (EF Core, separate file from main SQLite DB).
// Database:Provider controls the backend: sqlite (default), sqlserver/azuresql, postgres/postgresql.
builder.Services.AddDbContext<MemoryDbContext>(opts =>
{
    var provider = builder.Configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
    switch (provider)
    {
        case "sqlserver":
        case "azuresql":
            opts.UseSqlServer(builder.Configuration.GetConnectionString("MemoryDb")
                ?? builder.Configuration["Database:ConnectionString"]
                ?? throw new InvalidOperationException("Database:ConnectionString is required for SQL Server provider."));
            break;
        case "postgres":
        case "postgresql":
            opts.UseNpgsql(builder.Configuration.GetConnectionString("MemoryDb")
                ?? builder.Configuration["Database:ConnectionString"]
                ?? throw new InvalidOperationException("Database:ConnectionString is required for PostgreSQL provider."));
            break;
        default: // sqlite
            var basePath = builder.Configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
                ? Path.GetDirectoryName(Path.GetFullPath(p))!
                : AppPaths.DataDirectory;
            opts.UseSqlite($"Data Source={Path.Combine(basePath, "memory.db")}");
            break;
    }
});
builder.Services.AddScoped<MemoryContextCompiler>();
builder.Services.AddScoped<PostRunScribeService>();
builder.Services.AddSingleton<Agentweaver.Api.Projects.ProjectWorkspaceService>();

// Checkpoint GC background service (Guardrail 8)
builder.Services.AddHostedService<CheckpointGcService>();

// Casting
builder.Services.AddSingleton<CatalogReader>();
builder.Services.AddSingleton<CastProposalStore>();
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

await app.Services.GetRequiredService<SqliteDb>().EnsureCreatedAsync();

using (var scope = app.Services.CreateScope())
{
    var memoryDb = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

    // Transition guard: databases created before migrations were introduced
    // (via EnsureCreated) already have AgentMemory/Decisions/etc. but not RunEvents,
    // and have no __EFMigrationsHistory table. Detect this case and patch gracefully.
    // For fresh installs (no tables at all), MigrateAsync handles everything.
    var rawConn = (Microsoft.Data.Sqlite.SqliteConnection)memoryDb.Database.GetDbConnection();
    await rawConn.OpenAsync();
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
    await rawConn.CloseAsync();

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

    // Always run MigrateAsync: on the pre-migration branch the history entries seeded above tell
    // EF that AddRunEvents is already applied, so only the subsequent migrations are executed.
    // On a fresh install or an already-migrated DB this is the normal migration path.
    await memoryDb.Database.MigrateAsync();
}
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

app.UseExceptionHandler(err => err.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
}));

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();
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
app.MapDecisionsEndpoints();
app.MapMemoryEndpoints();
app.MapWorkflowDefinitionEndpoints();
app.MapReviewPolicyEndpoints();
app.MapDiagnosticsEndpoints();
app.MapMetricsEndpoints();

app.Run();

public partial class Program { }
