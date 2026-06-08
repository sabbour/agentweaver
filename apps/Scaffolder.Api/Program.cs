using System.Text.Encodings.Web;
using Scaffolder.AgentRuntime;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Security;
using Scaffolder.Domain;

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
builder.Services.AddSingleton<RunStreamStore>();

// Orchestration
builder.Services.AddSingleton<RunOrchestrator>();

// Agent runtime
builder.Services.AddAgentRuntime();

// Authentication
builder.Services.AddSingleton<ApiKeyRegistry>();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteDb>().EnsureCreatedAsync();
await app.Services.GetRequiredService<RunOrchestrator>().RestartRecoveryAsync(CancellationToken.None);

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapGet("/", () => "Scaffolder API");

app.MapPost("/api/runs", async (
    HttpContext httpContext,
    CreateRunRequest request,
    RunOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    if (string.IsNullOrWhiteSpace(request.Task) || string.IsNullOrWhiteSpace(request.ModelSource))
        return Results.BadRequest(new { error = "task and model_source are required." });

    ModelSource modelSource;
    try { modelSource = ModelSourceExtensions.FromApiString(request.ModelSource); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "model_source must be 'github-copilot' or 'microsoft-foundry'." }); }

    var run = new Run
    {
        Id = RunId.New(),
        RepositoryPath = request.RepositoryPath ?? string.Empty,
        OriginatingBranch = request.OriginatingBranch ?? string.Empty,
        ModelSource = modelSource,
        Task = request.Task,
        SubmittingUser = caller.User,
        Status = RunStatus.Pending,
        StartedAt = DateTimeOffset.UtcNow,
    };

    await orchestrator.StartRunAsync(run, ct);

    return Results.Accepted($"/api/runs/{run.Id}", new CreateRunResponse { RunId = run.Id.ToString(), Status = "in_progress" });
});

app.MapGet("/api/runs/{id}", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    var run = await runStore.GetAsync(runId, ct);
    if (run is null) return Results.NotFound();

    if (!IsOwner(httpContext, run)) return Results.Forbid();

    return Results.Json(new RunResponse
    {
        RunId = run.Id.ToString(),
        Status = run.Status.ToApiString(),
        ModelSource = run.ModelSource.ToApiString(),
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        Result = run.Result,
    });
});

app.MapGet("/api/runs/{id}/stream", async (
    HttpContext httpContext,
    string id,
    RunStreamStore streamStore,
    SqliteRunStore runStore,
    CancellationToken ct) =>
{
    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    var channel = streamStore.Get(id);
    if (channel is null)
    {
        if (RunId.TryParse(id, out var runId))
        {
            var run = await runStore.GetAsync(runId, ct);
            if (run?.Result is not null)
                await WriteChunkAsync(httpContext.Response, run.Result, ct);
        }
        await WriteDoneAsync(httpContext.Response, ct);
        return;
    }

    try
    {
        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            await WriteChunkAsync(httpContext.Response, chunk, ct);

        await WriteDoneAsync(httpContext.Response, ct);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal for SSE; suppress the exception.
    }
});

app.Run();

static bool IsOwner(HttpContext context, Run run) =>
    string.Equals(ApiKeyAuthMiddleware.GetCaller(context).User, run.SubmittingUser, StringComparison.Ordinal);

static async Task WriteChunkAsync(HttpResponse response, string chunk, CancellationToken ct)
{
    await response.WriteAsync($"data: {EscapeSse(chunk)}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

static async Task WriteDoneAsync(HttpResponse response, CancellationToken ct)
{
    await response.WriteAsync("event: done\ndata: \n\n", ct);
    await response.Body.FlushAsync(ct);
}

static string EscapeSse(string text) =>
    text.Replace("\n", "\ndata: ");

public partial class Program { }
