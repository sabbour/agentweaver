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
    if (!RunId.TryParse(id, out var runId))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid run id." }, ct);
        return;
    }

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var entry = streamStore.Get(id);

    // Authorize: for in-progress runs the entry carries the owner; for completed runs
    // (or when the entry has been evicted) fall back to the persistent run record.
    if (entry is not null)
    {
        if (!string.Equals(caller.User, entry.Owner, StringComparison.Ordinal))
        {
            httpContext.Response.StatusCode = 404;
            return;
        }
    }
    else
    {
        Run? run;
        try { run = await runStore.GetAsync(runId, ct); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch run {RunId} for stream", runId);
            httpContext.Response.StatusCode = 500;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Failed to retrieve the run." }, ct);
            return;
        }

        if (run is null || !string.Equals(caller.User, run.SubmittingUser, StringComparison.Ordinal))
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        // No retained event stream (for example after a process restart). Fall back to the
        // persisted result as a single message; the live delta sequence is not durable.
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        if (run.Result is not null)
        {
            var evt = new RunEvent(1, "agent.message", new { messageId = (string?)null, content = run.Result });
            await WriteSseEventAsync(httpContext.Response, evt, ct);
        }
        await WriteSseDoneAsync(httpContext.Response, ct);
        return;
    }

    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    var lastSeenHeader = httpContext.Request.Headers["Last-Event-ID"].FirstOrDefault();
    var lastSeen = int.TryParse(lastSeenHeader, out var ls) ? ls : 0;

    try
    {
        // Poll-based streaming: use the atomic GetSnapshotSince to get events + completion flag
        // under a single lock. This eliminates the race between reading events and checking
        // whether the run has completed — no events can be lost.
        while (!ct.IsCancellationRequested)
        {
            var snapshot = entry.GetSnapshotSince(lastSeen);

            foreach (var evt in snapshot.Events)
            {
                await WriteSseEventAsync(httpContext.Response, evt, ct);
                if (evt.Sequence > lastSeen)
                    lastSeen = evt.Sequence;
            }

            if (snapshot.IsCompleted)
                break;

            // Wait for new events or completion (with a short timeout to avoid indefinite hang).
            try { await entry.WaitForChangeAsync(ct); }
            catch (TimeoutException) { /* poll again */ }
        }

        await WriteSseDoneAsync(httpContext.Response, ct);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal for SSE.
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error streaming run {RunId}", runId);
        try { await httpContext.Response.WriteAsync("event: error\ndata: stream failure\n\n", CancellationToken.None); }
        catch { /* response may already be closed */ }
    }
});

app.Run();

static bool IsOwner(HttpContext context, Run run) =>
    string.Equals(ApiKeyAuthMiddleware.GetCaller(context).User, run.SubmittingUser, StringComparison.Ordinal);

static async Task WriteSseEventAsync(HttpResponse response, RunEvent evt, CancellationToken ct)
{
    var json = System.Text.Json.JsonSerializer.Serialize(evt.Payload,
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    await response.WriteAsync($"id: {evt.Sequence}\nevent: {evt.Type}\ndata: {json}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

static async Task WriteSseDoneAsync(HttpResponse response, CancellationToken ct)
{
    await response.WriteAsync("event: done\ndata: {}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

public partial class Program { }
