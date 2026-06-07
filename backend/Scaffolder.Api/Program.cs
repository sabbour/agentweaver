using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Agent;
using Scaffolder.Api.Agent.Governance;
using Scaffolder.Api.Agent.ModelSources;
using Scaffolder.Api.Agent.Tools;
using Scaffolder.Api.Configuration;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Streaming;
using Scaffolder.Api.Worktrees;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<ScaffolderOptions>(
    builder.Configuration.GetSection(ScaffolderOptions.SectionName));

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=scaffolder.db";
builder.Services.AddDbContext<ScaffolderDbContext>(options =>
    options.UseSqlite(connectionString));

// T050: NFR-001 deployment parity — register IDbContextFactory<ScaffolderDbContext>
// so that long-lived services (e.g. integration tests, background services) can
// create their own scoped DbContext instances without coupling to the request scope.
// The factory uses the same options as AddDbContext above.
builder.Services.AddDbContextFactory<ScaffolderDbContext>(options =>
    options.UseSqlite(connectionString), ServiceLifetime.Scoped);

// Repositories
builder.Services.AddScoped<IRunRepository, RunRepository>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IOperationalRecordRepository, OperationalRecordRepository>();

// Worktree services
builder.Services.AddScoped<IWorktreeService, WorktreeService>();
builder.Services.AddScoped<IDiffService, DiffService>();
builder.Services.AddScoped<MergeService>(); // T041: merge worktree into originating branch

// Agent loop
builder.Services.AddScoped<IAgentLoopHost, AgentLoopHost>();

// T038/T039: Model source adapters (keyed so AgentLoopHost can select by ModelSource)
builder.Services.AddScoped<CopilotSdkAdapter>();
builder.Services.AddScoped<MicrosoftFoundryAdapter>();

// T040/T045: Central governance policy enforcer
builder.Services.AddScoped<GovernancePolicyEngine>();

// T046: Content-safety interceptor
builder.Services.AddScoped<ContentSafetyInterceptor>();

// T047: Secrets-scrubbing filter (singleton — stateless, thread-safe)
builder.Services.AddSingleton<SecretsScrubbingFilter>();

// T048: Run bounds enforcer
builder.Services.AddScoped<RunBoundsEnforcer>();

// Event log
builder.Services.AddScoped<EventLogService>();

// T033/T036: EventBroadcaster registered as Singleton so it survives across
// request scopes. It is injected into EventLogService (Scoped) via IEventBroadcaster.
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<IEventBroadcaster>(sp => sp.GetRequiredService<EventBroadcaster>());

// T034: EventReplayService reads historical events for SSE reconnect/replay
builder.Services.AddScoped<EventReplayService>();

// Sandbox tools
builder.Services.AddSingleton<SandboxPathResolver>();
builder.Services.AddScoped<ReadFileTool>();
builder.Services.AddScoped<WriteFileTool>();

// Runs orchestration
builder.Services.AddScoped<RunStateMachine>();
builder.Services.AddScoped<OperationalRecordWriter>();
builder.Services.AddScoped<RunExecutionService>();

// ProblemDetails for RFC 7807 error responses
builder.Services.AddProblemDetails();

// Configure JSON to serialize/deserialize enums as strings (e.g. "CopilotSdk" not 0)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// OpenAPI / Swagger (Swashbuckle for net8.0 - native OpenApi is net9.0+)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Scaffolder API",
        Version = "v1",
        Description = "Single-agent file-editing run management API"
    });
});

var app = builder.Build();

// Ensure database is created and migrations applied on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ScaffolderDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        // Tests use EnsureCreated (no migrations) for speed and isolation
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

// Error handling
app.UseExceptionHandler();
app.UseStatusCodePages();

// OpenAPI - always enable Swagger for local use
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scaffolder API v1"));

app.MapRunsEndpoints();
app.MapStreamEndpoints(); // T035: SSE stream endpoint

app.Run();

// Make Program accessible for integration tests (Microsoft.AspNetCore.Mvc.Testing)
public partial class Program { }
