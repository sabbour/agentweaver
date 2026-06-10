using System.Text.Encodings.Web;
using Scaffolder.AgentRuntime;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Git;
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

// Agent runtime
builder.Services.AddAgentRuntime(builder.Configuration);

// Authentication
builder.Services.AddSingleton<ApiKeyRegistry>();

// Repository path validation (A2 security fix)
builder.Services.AddSingleton<RepositoryRootValidator>();

// Checkpoint GC background service (Guardrail 8)
builder.Services.AddHostedService<CheckpointGcService>();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteDb>().EnsureCreatedAsync();
await app.Services.GetRequiredService<WorkflowRestartService>().RecoverAsync(CancellationToken.None);

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
    RepositoryRootValidator repoValidator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    if (string.IsNullOrWhiteSpace(request.Task) || string.IsNullOrWhiteSpace(request.ModelSource))
        return Results.BadRequest(new { error = "task and model_source are required." });

    if (string.IsNullOrWhiteSpace(request.RepositoryPath) || string.IsNullOrWhiteSpace(request.OriginatingBranch))
        return Results.BadRequest(new { error = "repository_path and originating_branch are required." });

    ModelSource modelSource;
    try { modelSource = ModelSourceExtensions.FromApiString(request.ModelSource); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "model_source must be 'github-copilot' or 'microsoft-foundry'." }); }

    // Validate and canonicalize the repository path (A2 security fix).
    string canonicalRepoPath;
    try
    {
        canonicalRepoPath = repoValidator.ValidateAndCanonicalize(request.RepositoryPath);
    }
    catch (RunSubmissionValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var run = new Run
    {
        Id = RunId.New(),
        RepositoryPath = canonicalRepoPath,
        OriginatingBranch = request.OriginatingBranch,
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
    catch (RunSubmissionValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
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
    if (!IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // S1: Only serve the diff when the run is in a review-ready or terminal state.
    // Diff is withheld for Failed, InProgress, and Pending runs to prevent leaking
    // content from safety-failed or incomplete runs (FR-026 / SC-009).
    // Merging is included: the diff was already approved for review and the state
    // is only transiently different from AwaitingReview.
    string? diff = run.Status is RunStatus.AwaitingReview
                                or RunStatus.Merging
                                or RunStatus.Merged
                                or RunStatus.MergeFailed
                                or RunStatus.Declined
        ? run.Diff
        : null;

    return Results.Json(new RunResponse
    {
        RunId = run.Id.ToString(),
        Status = run.Status.ToApiString(),
        ModelSource = run.ModelSource.ToApiString(),
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        Result = run.Result,
        Diff = diff,
        StepCount = run.StepCount,
        TreeHash = run.TreeHash,
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

app.MapPost("/api/runs/{id}/review", async (
    HttpContext httpContext,
    string id,
    ReviewRequest request,
    SqliteRunStore runStore,
    RunStreamStore streamStore,
    RunWorkflowRegistry workflowRegistry,
    PendingRequestStore pendingStore,
    IWorktreeOperations worktreeOps,
    IMergeCoordinator mergeCoordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for review", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    // Idempotency: return current state when the terminal decision already matches.
    if (run.Status == RunStatus.Merged && request.Approved)
        return Results.Json(new ReviewResponse { RunId = id, Status = run.Status.ToApiString(), MergeResult = run.Result });
    if (run.Status == RunStatus.Declined && !request.Approved)
        return Results.Json(new ReviewResponse { RunId = id, Status = run.Status.ToApiString(), MergeResult = null });

    if (run.Status != RunStatus.AwaitingReview)
        return Results.Conflict(new { error = $"Run is in status '{run.Status.ToApiString()}' and cannot be reviewed." });

    // Guardrail 10: Atomic TryRemove for replay/double-POST protection.
    var pendingEntry = pendingStore.TryRemove(id);
    if (pendingEntry is null)
    {
        // Guardrail 2: On-demand fallback — if pending store is empty (e.g., after restart
        // before recovery fully repopulates), check the workflow's current status.
        var streamingRun = workflowRegistry.Get(id);
        if (streamingRun is null)
        {
            // Direct execution path: no live MAF workflow is registered for this run.
            // This covers: (a) test setups that populate the DB directly; (b) post-restart
            // recovery when no checkpoint exists (WorkflowRestartService re-creates the
            // stream entry but cannot resume the workflow without a checkpoint).
            // Auth is already validated above (IsOwner). Execute the merge/decline directly
            // using the same infrastructure as MergeExecutor so no guardrails are bypassed.
            logger.LogInformation(
                "Review decision: {Decision} (direct path). RunId={RunId} SubmittingUser={SubmittingUser} Reviewer={Reviewer}",
                request.Approved ? "approved" : "declined", id, run.SubmittingUser, caller.User);
            return await ExecuteDirectReviewAsync(
                id, runId, run, request, runStore, streamStore, worktreeOps, mergeCoordinator, logger, ct);
        }
        // If the run is registered but no pending request, the request was already consumed.
        return Results.StatusCode(409);
    }

    // Guardrail 9: IDOR defense-in-depth — verify caller owns the pending request.
    if (!string.Equals(caller.User, pendingEntry.OwnerUser, StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var streamingRunForReview = workflowRegistry.Get(id);
    if (streamingRunForReview is null)
        return Results.Conflict(new { error = "Workflow run is no longer active." });

    // S3: Structured operational record for the review decision.
    logger.LogInformation(
        "Review decision: {Decision}. RunId={RunId} SubmittingUser={SubmittingUser} Reviewer={Reviewer}",
        request.Approved ? "approved" : "declined", id, run.SubmittingUser, caller.User);

    // Emit merge.started before handing the approval to the workflow so the SSE stream
    // bridges the gap between the approve and the eventual merge.completed/merge.failed.
    if (request.Approved)
    {
        var liveEntry = streamStore.Get(id);
        if (liveEntry is not null)
        {
            var mergeStartedSeq = liveEntry.NextSequence();
            liveEntry.Record(new RunEvent(mergeStartedSeq, EventTypes.MergeStarted,
                new { tree_hash = run.TreeHash }));
        }
    }

    // Create the response and send it to the workflow to resume.
    var decision = new WorkflowReviewDecision(request.Approved);
    var externalResponse = pendingEntry.Request.CreateResponse(decision);
    await streamingRunForReview.SendResponseAsync(externalResponse);

    // Return immediately — the watch loop will handle the terminal state transition.
    var expectedStatus = request.Approved ? "merging" : "declined";
    return Results.Json(new ReviewResponse { RunId = id, Status = expectedStatus, MergeResult = null });
});

app.Run();

static bool IsOwner(HttpContext context, Run run) =>
    string.Equals(ApiKeyAuthMiddleware.GetCaller(context).User, run.SubmittingUser, StringComparison.Ordinal);

/// <summary>
/// Direct review execution: merge or decline without a live MAF workflow.
/// FALLBACK path — used when no workflow is registered for the run (test setup or
/// post-restart with no checkpoint). The primary path is SendResponseAsync through
/// the MAF workflow. Unexpected production hits are logged as warnings.
/// </summary>
static async Task<IResult> ExecuteDirectReviewAsync(
    string id,
    RunId runId,
    Run run,
    ReviewRequest request,
    SqliteRunStore runStore,
    RunStreamStore streamStore,
    IWorktreeOperations worktreeOps,
    IMergeCoordinator mergeCoordinator,
    ILogger<Program> logger,
    CancellationToken ct)
{
    // Structured warning: detect unexpected production usage of the fallback path.
    if (!string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Test", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "ExecuteDirectReviewAsync fallback entered for run {RunId} in non-test environment. " +
            "This should only occur post-restart with no checkpoint.", id);
    }

    var entry = streamStore.Get(id);

    if (!request.Approved)
    {
        await runStore.TryTransitionReviewAsync(runId, RunStatus.Declined, DateTimeOffset.UtcNow, null, CancellationToken.None)
            .ConfigureAwait(false);
        if (entry is not null)
        {
            var declinedSeq = entry.NextSequence();
            entry.Record(new RunEvent(declinedSeq, EventTypes.ReviewDeclined, new { }));
            streamStore.Complete(id);
        }
        return Results.Json(new ReviewResponse { RunId = id, Status = RunStatus.Declined.ToApiString(), MergeResult = null });
    }

    // Approve path: validate required merge data.
    if (run.TreeHash is null || run.WorktreeBranch is null || run.WorktreePath is null)
    {
        logger.LogError("Run {RunId} is missing required merge data in direct review path", id);
        return Results.Problem("Run is missing required merge data.", statusCode: 500);
    }

    // Worktree-exists + tree-hash-matches validation (same as WorkflowRestartService).
    if (!worktreeOps.WorktreeExists(run.WorktreePath))
    {
        logger.LogError("Worktree missing for run {RunId} at path during direct review", id);
        return Results.Conflict(new { error = "Worktree no longer exists. The run cannot be merged." });
    }

    var currentTreeHash = worktreeOps.GetTreeHash(run.WorktreePath);
    if (currentTreeHash is not null && !string.Equals(currentTreeHash, run.TreeHash, StringComparison.Ordinal))
    {
        logger.LogError(
            "Worktree tree hash mismatch for run {RunId} in direct review: expected={Expected} actual={Actual}",
            id, run.TreeHash, currentTreeHash);
        return Results.Problem("Worktree content has changed since review was requested.", statusCode: 409);
    }

    // Consolidated merge execution via the coordinator.
    if (entry is not null)
    {
        var mergeStartedSeq = entry.NextSequence();
        entry.Record(new RunEvent(mergeStartedSeq, EventTypes.MergeStarted,
            new { tree_hash = run.TreeHash }));
    }

    var mergeInput = new MergeInput(id, run.TreeHash, run.WorktreePath, run.WorktreeBranch, run.RepositoryPath, run.OriginatingBranch);
    var mergeExecResult = await mergeCoordinator.ExecuteMergeAsync(mergeInput, ct).ConfigureAwait(false);

    switch (mergeExecResult.Outcome)
    {
        case MergeExecutionOutcome.Merged:
            if (entry is not null)
            {
                var approvedSeq = entry.NextSequence();
                entry.Record(new RunEvent(approvedSeq, EventTypes.ReviewApproved, new { }));
                var mergedSeq = entry.NextSequence();
                entry.Record(new RunEvent(mergedSeq, EventTypes.MergeCompleted,
                    new { merged_commit_hash = mergeExecResult.CommitHash, previous_head_sha = mergeExecResult.PreviousHeadSha }));
                streamStore.Complete(id);
            }
            return Results.Json(new ReviewResponse
            {
                RunId = id,
                Status = RunStatus.Merged.ToApiString(),
                MergeResult = mergeExecResult.MergeResult,
            });

        case MergeExecutionOutcome.Blocked:
            return Results.Conflict(new
            {
                error  = mergeExecResult.Reason,
                status = RunStatus.AwaitingReview.ToApiString(),
            });

        case MergeExecutionOutcome.Conflict:
            if (entry is not null)
            {
                var approvedSeq2 = entry.NextSequence();
                entry.Record(new RunEvent(approvedSeq2, EventTypes.ReviewApproved, new { }));
                var failedSeq = entry.NextSequence();
                entry.Record(new RunEvent(failedSeq, EventTypes.MergeFailed,
                    new { reason = mergeExecResult.Reason }));
                streamStore.Complete(id);
            }
            return Results.Json(new ReviewResponse
            {
                RunId = id,
                Status = RunStatus.MergeFailed.ToApiString(),
                MergeResult = mergeExecResult.MergeResult,
            });

        case MergeExecutionOutcome.LockFailed:
            if (string.Equals(mergeExecResult.LockFailureReason, "already_merging", StringComparison.Ordinal))
                return Results.Conflict(new { error = "Run is already being merged." });
            if (string.Equals(mergeExecResult.LockFailureReason, "repository_path_not_found", StringComparison.Ordinal))
                return Results.Problem("Repository path does not exist.", statusCode: 400);
            return Results.Conflict(new { error = mergeExecResult.LockFailureReason });

        case MergeExecutionOutcome.InternalError:
            return Results.Problem("Merge failed unexpectedly.", statusCode: 500);

        default:
            throw new InvalidOperationException($"Unexpected merge execution outcome: {mergeExecResult.Outcome}");
    }
}

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
