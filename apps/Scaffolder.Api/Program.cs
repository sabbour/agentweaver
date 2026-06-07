using System.Text;
using System.Text.Encodings.Web;
using Scaffolder.AgentRuntime;
using Scaffolder.Api;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Security;
using Scaffolder.Api.Streaming;
using Scaffolder.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// Infrastructure and stores.
builder.Services.AddSingleton<SqliteDb>();
builder.Services.AddSingleton<SqliteEventStore>();
builder.Services.AddSingleton<IRunEventStore>(sp => sp.GetRequiredService<SqliteEventStore>());
builder.Services.AddSingleton<SqliteOperationalStore>();
builder.Services.AddSingleton<IOperationalStore>(sp => sp.GetRequiredService<SqliteOperationalStore>());
builder.Services.AddSingleton<SqliteRunStore>();

// Streaming fan-out.
builder.Services.AddSingleton<RunEventBroadcaster>();
builder.Services.AddSingleton<IRunEventPublisher>(sp => sp.GetRequiredService<RunEventBroadcaster>());
builder.Services.AddSingleton<RunEventEmitter>();

// Git worktree lifecycle.
builder.Services.AddSingleton<WorktreeManager>();

// Run orchestration.
builder.Services.AddSingleton<RunOrchestrator>();

// Agent runtime — GitHub Copilot SDK + Microsoft Foundry providers, MAF loop, governance.
builder.Services.AddAgentRuntime(builder.Configuration);

// Authentication.
builder.Services.AddSingleton<ApiKeyRegistry>();

var app = builder.Build();

// Initialize the database and recover any runs stranded by a previous process
// before accepting requests.
await app.Services.GetRequiredService<SqliteDb>().EnsureCreatedAsync();
await app.Services.GetRequiredService<RunOrchestrator>().RestartRecoveryAsync(CancellationToken.None);

app.UseMiddleware<EmojiResponseGuardMiddleware>();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapGet("/", () => "Scaffolder API");

app.MapPost("/api/runs", async (
    HttpContext httpContext,
    CreateRunRequest request,
    RunOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    if (string.IsNullOrWhiteSpace(request.RepositoryPath) ||
        string.IsNullOrWhiteSpace(request.OriginatingBranch) ||
        string.IsNullOrWhiteSpace(request.Task) ||
        string.IsNullOrWhiteSpace(request.ModelSource))
    {
        return Results.BadRequest(new { error = "repository_path, originating_branch, task, and model_source are required." });
    }

    ModelSource modelSource;
    try
    {
        modelSource = ModelSourceExtensions.FromApiString(request.ModelSource);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "model_source must be 'github-copilot' or 'microsoft-foundry'." });
    }

    var run = new Run
    {
        Id = RunId.New(),
        RepositoryPath = request.RepositoryPath,
        OriginatingBranch = request.OriginatingBranch,
        ModelSource = modelSource,
        Task = request.Task,
        SubmittingUser = caller.User,
        Status = RunStatus.Pending,
        StartedAt = DateTimeOffset.UtcNow
    };

    try
    {
        await orchestrator.StartRunAsync(run, ct);
    }
    catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or InvalidOperationException or DirectoryNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var response = new CreateRunResponse { RunId = run.Id.ToString(), Status = "pending" };
    return Results.Accepted($"/api/runs/{run.Id}", response);
});

app.MapGet("/api/runs/{id}", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    WorktreeManager worktree,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
    {
        return Results.BadRequest(new { error = "Invalid run id." });
    }

    var run = await runStore.GetAsync(runId, ct);
    if (run is null)
    {
        return Results.NotFound();
    }

    if (!IsOwner(httpContext, run))
    {
        return Results.Forbid();
    }

    string? diff = null;
    if (run.WorktreeBranch is not null &&
        run.Status is RunStatus.Completed or RunStatus.Approved or RunStatus.Declined)
    {
        diff = worktree.GetDiff(run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch);
    }

    var response = new RunResponse
    {
        RunId = run.Id.ToString(),
        Status = run.Status.ToApiString(),
        ModelSource = run.ModelSource.ToApiString(),
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        StepCount = run.StepCount,
        Diff = diff
    };

    return Results.Json(response);
});

app.MapGet("/api/runs/{id}/stream", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    IRunEventPublisher publisher,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var run = await runStore.GetAsync(runId, ct);
    if (run is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!IsOwner(httpContext, run))
    {
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var afterSequence = ParseLastEventId(httpContext.Request.Headers["Last-Event-ID"]);

    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";
    httpContext.Response.ContentType = "text/event-stream";

    await foreach (var evt in publisher.SubscribeAsync(runId, afterSequence, ct))
    {
        var json = EventEnvelope.ToClientJson(evt);
        var frame = $"id: {evt.Sequence}\ndata: {json}\n\n";
        await httpContext.Response.WriteAsync(frame, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }
});

app.MapGet("/api/runs/{id}/events", async (
    HttpContext httpContext,
    string id,
    int? afterSequence,
    SqliteRunStore runStore,
    IRunEventStore eventStore,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
    {
        return Results.BadRequest(new { error = "Invalid run id." });
    }

    var run = await runStore.GetAsync(runId, ct);
    if (run is null)
    {
        return Results.NotFound();
    }

    if (!IsOwner(httpContext, run))
    {
        return Results.Forbid();
    }

    var cursor = afterSequence ?? -1;
    var arrayBuilder = new StringBuilder();
    arrayBuilder.Append('[');
    var first = true;
    await foreach (var evt in eventStore.ReadFromAsync(runId, cursor, ct))
    {
        if (!first)
        {
            arrayBuilder.Append(',');
        }

        arrayBuilder.Append(EventEnvelope.ToClientJson(evt));
        first = false;
    }

    arrayBuilder.Append(']');
    return Results.Text(arrayBuilder.ToString(), "application/json");
});

app.MapPost("/api/runs/{id}/review", async (
    HttpContext httpContext,
    string id,
    ReviewRequest request,
    SqliteRunStore runStore,
    RunOrchestrator orchestrator,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
    {
        return Results.BadRequest(new { error = "Invalid run id." });
    }

    if (request.Approved is null)
    {
        return Results.BadRequest(new { error = "approved is required." });
    }

    var run = await runStore.GetAsync(runId, ct);
    if (run is null)
    {
        return Results.NotFound();
    }

    if (!IsOwner(httpContext, run))
    {
        return Results.Forbid();
    }

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var result = await orchestrator.SubmitReviewAsync(runId, request.Approved.Value, caller.User, ct);

    if (result.Outcome == ReviewDecisionOutcome.Rejected)
    {
        return Results.Conflict(new { error = result.RejectionReason });
    }

    var response = new ReviewResponse
    {
        RunId = runId.ToString(),
        Status = result.Status.ToApiString(),
        MergeResult = result.MergeResult
    };

    return Results.Json(response);
});

app.Run();

static bool IsOwner(HttpContext context, Run run) =>
    string.Equals(ApiKeyAuthMiddleware.GetCaller(context).User, run.SubmittingUser, StringComparison.Ordinal);

static int ParseLastEventId(string? value) =>
    int.TryParse(value, out var sequence) ? sequence : -1;

// Exposed for WebApplicationFactory<Program> in tests.
public partial class Program { }
