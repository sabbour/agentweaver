using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
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
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorDispatch>(
    sp => sp.GetRequiredService<Agentweaver.Api.Coordinator.CoordinatorDispatchService>());
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorSteeringQueue>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorSteeringService>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorWorkflowFactory>();
builder.Services.AddSingleton<Agentweaver.Api.Coordinator.CoordinatorRunService>();

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

// Agent runtime
builder.Services.AddAgentRuntime();

// Authentication
builder.Services.AddSingleton<ApiKeyRegistry>();

// Repository path validation (A2 security fix)
builder.Services.AddSingleton<RepositoryRootValidator>();

// Memory database (EF Core, separate file from main SQLite DB)
builder.Services.AddDbContext<MemoryDbContext>(opts =>
{
    var basePath = builder.Configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
        ? Path.GetDirectoryName(Path.GetFullPath(p))!
        : AppPaths.DataDirectory;
    var memoryDbPath = Path.Combine(basePath, "memory.db");
    opts.UseSqlite($"Data Source={memoryDbPath}");
});
builder.Services.AddScoped<MemoryContextCompiler>();
builder.Services.AddScoped<PostRunScribeService>();

// Checkpoint GC background service (Guardrail 8)
builder.Services.AddHostedService<CheckpointGcService>();

// Casting
builder.Services.AddSingleton<CatalogReader>();
builder.Services.AddSingleton<CastProposalStore>();
builder.Services.AddSingleton<ProjectSignalScanner>();
builder.Services.AddSingleton<CastingService>();

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
    else
    {
        // Fresh install or already-migrated DB: normal path.
        await memoryDb.Database.MigrateAsync();
    }
}
await app.Services.GetRequiredService<WorkflowRestartService>().RecoverAsync(CancellationToken.None);

app.UseExceptionHandler(err => err.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
}));

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapRunEndpoints();
app.MapProjectEndpoints();
app.MapCoordinatorEndpoints();
app.MapCastingEndpoints();
app.MapTeamEndpoints();
app.MapAuthEndpoints();
app.MapDecisionsEndpoints();
app.MapMemoryEndpoints();

app.Run();

public partial class Program { }
