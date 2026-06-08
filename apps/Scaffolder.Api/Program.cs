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

app.UseExceptionHandler(err => err.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
}));

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapGet("/", () => "Scaffolder API");

app.MapPost("/api/runs", async (
    HttpContext httpContext,
    CreateRunRequest request,
    RunOrchestrator orchestrator,
    ILogger<Program> logger,
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

    try
    {
        await orchestrator.StartRunAsync(run, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to start run {RunId}", run.Id);
        return Results.Problem("Failed to start the run.", statusCode: 500);
    }

    return Results.Accepted($"/api/runs/{run.Id}", new CreateRunResponse { RunId = run.Id.ToString(), Status = "in_progress" });
});

app.MapGet("/api/runs/{id}", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try
    {
        run = await runStore.GetAsync(runId, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId}", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

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
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    // Validate ID and ownership before committing to an SSE response.
    if (!RunId.TryParse(id, out var runId))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid run id." }, ct);
        return;
    }

    var channel = streamStore.Get(id);

    // If no live channel, check the DB for a completed run.
    Run? completedRun = null;
    if (channel is null)
    {
        try { completedRun = await runStore.GetAsync(runId, ct); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch run {RunId} for stream", runId);
            httpContext.Response.StatusCode = 500;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Failed to retrieve the run." }, ct);
            return;
        }

        if (completedRun is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }
    }

    // Headers must be set before any body writes.
    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    try
    {
        if (channel is null)
        {
            // Run already finished — replay stored result as a single chunk.
            if (completedRun?.Result is not null)
                await WriteChunkAsync(httpContext.Response, completedRun.Result, ct);
            await WriteDoneAsync(httpContext.Response, ct);
            return;
        }

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            await WriteChunkAsync(httpContext.Response, chunk, ct);

        await WriteDoneAsync(httpContext.Response, ct);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal for SSE.
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error streaming run {RunId}", runId);
        // Headers already sent; can't change status code. Send an error event so the client knows.
        try { await httpContext.Response.WriteAsync("event: error\ndata: stream failure\n\n", CancellationToken.None); }
        catch { /* response may already be closed */ }
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
