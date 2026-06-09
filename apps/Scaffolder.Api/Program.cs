using System.Text.Encodings.Web;
using Scaffolder.AgentRuntime;
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

    if (string.IsNullOrWhiteSpace(request.RepositoryPath) || string.IsNullOrWhiteSpace(request.OriginatingBranch))
        return Results.BadRequest(new { error = "repository_path and originating_branch are required." });

    ModelSource modelSource;
    try { modelSource = ModelSourceExtensions.FromApiString(request.ModelSource); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "model_source must be 'github-copilot' or 'microsoft-foundry'." }); }

    var run = new Run
    {
        Id = RunId.New(),
        RepositoryPath = request.RepositoryPath,
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
    WorktreeManager worktreeManager,
    RepositoryMergeLock mergeLock,
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

    // A3 null-guard: the entry may be absent after a process restart. In that case
    // events are not emitted to the stream, but the DB transition is still applied.
    var entry = streamStore.Get(id);

    if (!request.Approved)
    {
        // S3: Structured operational record for the decline decision.
        logger.LogInformation(
            "Review decision: declined. RunId={RunId} SubmittingUser={SubmittingUser} Reviewer={Reviewer}",
            id, run.SubmittingUser, caller.User);

        var declined = await runStore.TryTransitionReviewAsync(
            runId, RunStatus.Declined, DateTimeOffset.UtcNow, null, CancellationToken.None);

        if (!declined)
            return Results.Conflict(new { error = "Run status changed concurrently; please retry." });

        logger.LogInformation("Review outcome: declined. RunId={RunId} Reviewer={Reviewer}", id, caller.User);

        if (entry is not null)
        {
            var declinedSeq = entry.NextSequence();
            entry.Record(new RunEvent(declinedSeq, EventTypes.ReviewDeclined, new { declined_by = caller.User }));
            streamStore.Complete(id);
        }

        // Worktree is preserved on decline per acceptance scenario 3.
        return Results.Json(new ReviewResponse { RunId = id, Status = RunStatus.Declined.ToApiString(), MergeResult = null });
    }

    // ---- Approve path ----

    // MF4: Defensive canonicalization — verify the stored repository path resolves to a real
    // directory before acquiring locks or touching git. No workspace-root prefix restriction
    // is configured in this project; see decision record tank-story4-merge-hybrid.md.
    string canonicalRepoPath;
    try { canonicalRepoPath = Path.GetFullPath(run.RepositoryPath); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to canonicalize repository path for run {RunId}", id);
        return Results.Problem("Invalid repository path.", statusCode: 400);
    }

    if (!Directory.Exists(canonicalRepoPath))
    {
        logger.LogWarning("Repository path does not exist for run {RunId}", id);
        return Results.Problem("Repository path does not exist.", statusCode: 400);
    }

    if (run.TreeHash is null || run.WorktreeBranch is null || run.WorktreePath is null)
    {
        logger.LogError("Run {RunId} is missing tree hash or worktree coordinates", id);
        return Results.Problem("Run is missing required merge data.", statusCode: 500);
    }

    // MF2: Acquire per-repository lock for the entire check-then-merge critical section.
    // Serializes concurrent approvals on the same repository and closes the TOCTOU window.
    using var lockHandle = await mergeLock.TryAcquireAsync(
        canonicalRepoPath, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

    if (lockHandle is null)
        return Results.Conflict(new { error = "Repository is busy; try again." });

    // MF3: CAS gate — atomically transition AwaitingReview → Merging before any mutation.
    // If another concurrent request already won this gate, return 409 retriable.
    var casSucceeded = await runStore.TryStartMergingAsync(runId, CancellationToken.None);
    if (!casSucceeded)
        return Results.Conflict(new { error = "Run is already being merged." });

    // S3: Structured operational record for the approve decision.
    logger.LogInformation(
        "Review decision: approved. RunId={RunId} SubmittingUser={SubmittingUser} Reviewer={Reviewer}",
        id, run.SubmittingUser, caller.User);

    MergeOutcome outcome;
    try
    {
        outcome = worktreeManager.MergeWorktree(
            run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch, run.TreeHash);
    }
    catch (Exception ex)
    {
        // MF6: Fail-safe — on any unexpected exception, revert Merging → AwaitingReview.
        // Never transition to a terminal state here. Preserve worktree branch for inspection.
        var headSha = worktreeManager.TryGetCurrentHeadSha(run.RepositoryPath);
        logger.LogError(ex,
            "Merge operation threw unexpectedly for run {RunId}. " +
            "CurrentHeadSha={CurrentHeadSha} WorktreeBranch={WorktreeBranch}. " +
            "Reverting to awaiting_review for manual recovery.",
            id, headSha ?? "(unknown)", run.WorktreeBranch);

        var reverted = await runStore.RevertMergingAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (!reverted)
            logger.LogWarning("Revert to awaiting_review was a no-op for run {RunId} (status was not merging)", id);
        return Results.Problem(
            "Merge operation failed unexpectedly. The run has been reverted to awaiting review.",
            statusCode: 500);
    }

    if (outcome.Kind == MergeOutcomeKind.Merged)
    {
        // S2: merge_result is a safe, enumerated string — never contains raw file content.
        var mergeResult = $"merged:{outcome.CommitHash}";

        await runStore.CompleteMergingAsync(
            runId, RunStatus.Merged, DateTimeOffset.UtcNow, mergeResult, CancellationToken.None)
            .ConfigureAwait(false);

        // MF5: Structured audit record for a successful merge.
        logger.LogInformation(
            "Merge outcome: success. RunId={RunId} CommitHash={CommitHash} MergeMode={MergeMode} " +
            "PreviousHeadSha={PreviousHeadSha} NewHeadSha={NewHeadSha} WasFastForward={WasFastForward} " +
            "RepositoryPath={RepositoryPath} Reviewer={Reviewer}",
            id, outcome.CommitHash, outcome.MergeMode,
            outcome.PreviousHeadSha, outcome.NewHeadSha, outcome.WasFastForward,
            canonicalRepoPath, caller.User);

        if (entry is not null)
        {
            var approvedSeq = entry.NextSequence();
            entry.Record(new RunEvent(approvedSeq, EventTypes.ReviewApproved,
                new { tree_hash = run.TreeHash, approved_by = caller.User }));
            var mergedSeq = entry.NextSequence();
            // MF5: previous_head_sha is safe (a git SHA) and aids operational tracing.
            entry.Record(new RunEvent(mergedSeq, EventTypes.MergeCompleted,
                new { merged_commit_hash = outcome.CommitHash, previous_head_sha = outcome.PreviousHeadSha }));
            streamStore.Complete(id);
        }

        // Remove the worktree on successful merge only.
        try { worktreeManager.RemoveWorktree(run.RepositoryPath, run.WorktreePath, run.WorktreeBranch); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to remove worktree for run {RunId} after merge", id); }

        return Results.Json(new ReviewResponse { RunId = id, Status = RunStatus.Merged.ToApiString(), MergeResult = mergeResult });
    }

    if (outcome.Kind == MergeOutcomeKind.Blocked)
    {
        // Retriable precondition failure — no mutations occurred in git.
        // Revert Merging → AwaitingReview so the client can fix the condition and re-approve.
        // Do NOT record review.approved — the approve was not accepted.
        // Stream stays open.
        var reverted = await runStore.RevertMergingAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (!reverted)
            logger.LogWarning("Revert to awaiting_review was a no-op for run {RunId} (status was not merging)", id);

        logger.LogInformation(
            "Merge outcome: blocked (retriable). RunId={RunId} Reason={Reason} Reviewer={Reviewer}",
            id, outcome.Reason, caller.User);

        return Results.Conflict(new
        {
            error  = outcome.Reason,
            status = RunStatus.AwaitingReview.ToApiString(),
        });
    }

    // Conflict — terminal.
    {
        // S2: Reason is a safe human-readable category string from WorktreeManager.
        var safeDetails = outcome.Reason ?? "merge_conflict";
        var mergeResult = $"conflict:{safeDetails}";

        await runStore.CompleteMergingAsync(
            runId, RunStatus.MergeFailed, DateTimeOffset.UtcNow, mergeResult, CancellationToken.None)
            .ConfigureAwait(false);

        // S3: Structured operational record for the merge failure.
        logger.LogInformation(
            "Merge outcome: conflict. RunId={RunId} Details={Details} Reviewer={Reviewer}",
            id, safeDetails, caller.User);

        if (entry is not null)
        {
            var approvedSeq = entry.NextSequence();
            entry.Record(new RunEvent(approvedSeq, EventTypes.ReviewApproved,
                new { tree_hash = run.TreeHash, approved_by = caller.User }));
            var failedSeq = entry.NextSequence();
            // S2: reason mirrors WorktreeManager's generic conflict strings.
            entry.Record(new RunEvent(failedSeq, EventTypes.MergeFailed, new { reason = safeDetails }));
            streamStore.Complete(id);
        }

        // Worktree is preserved on merge failure per FR-016.
        return Results.Json(new ReviewResponse { RunId = id, Status = RunStatus.MergeFailed.ToApiString(), MergeResult = mergeResult });
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
