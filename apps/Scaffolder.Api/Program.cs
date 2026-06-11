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

// Agent runtime
builder.Services.AddAgentRuntime();

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
    RunStreamStore streamStore,
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

    // Populate sandbox status from the in-memory event stream (sandbox.selected /
    // sandbox.warning events emitted by the agent runner at startup).
    // Returns null for older runs whose stream entries have been evicted.
    SandboxStatusDto? sandboxStatus = null;
    var streamEntry = streamStore.Get(id);
    if (streamEntry is not null)
    {
        var events = streamEntry.GetSnapshotSince(0).Events;
        var selectedEvt = events.FirstOrDefault(e => e.Type == "sandbox.selected");
        if (selectedEvt is not null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(selectedEvt.Payload,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            var parsed = System.Text.Json.JsonSerializer.Deserialize<SandboxSelectedPayload>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null)
            {
                var hasNetworkWarning = events.Any(e => e.Type == "sandbox.warning");
                sandboxStatus = new SandboxStatusDto
                {
                    Backend = parsed.Backend ?? string.Empty,
                    IsRealIsolation = parsed.IsRealIsolation,
                    SelectionReason = parsed.Reason,
                    HasNetworkWarning = hasNetworkWarning,
                };
            }
        }
    }

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
        Sandbox = sandboxStatus,
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
            catch (OperationCanceledException) { return; }
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
        bool reviewRequestedSent = false;

        while (!ct.IsCancellationRequested)
        {
            var snapshot = entry.GetSnapshotSince(lastSeen);

            foreach (var evt in snapshot.Events)
            {
                await WriteSseEventAsync(httpContext.Response, evt, ct);
                if (evt.Sequence > lastSeen)
                    lastSeen = evt.Sequence;
                if (evt.Type == EventTypes.ReviewRequested) reviewRequestedSent = true;
            }

            if (snapshot.IsCompleted)
                break;
            // The MAF workflow pauses at the HITL gate once review.requested is emitted.
            // The stream entry is never marked completed in that state, so close the stream
            // here to let the client poll for the review decision via GET /api/runs/{id}.
            // Also break when the client reconnects with Last-Event-ID at/after
            // review.requested — in that case reviewRequestedSent stays false because
            // GetSnapshotSince returns no events, but the event is already in history.
            if (entry.IsAwaitingReview && (reviewRequestedSent || entry.HasEventType(EventTypes.ReviewRequested))) break;

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

// POST /api/runs/{id}/commit — stages and commits the worktree, then transitions the run to Completed.
// This is the "Commit Changes" action: it does not merge to the originating branch and does not
// delete the worktree. Intended for runs in awaiting_review state.
app.MapPost("/api/runs/{id}/commit", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    RunStreamStore streamStore,
    WorktreeManager worktreeManager,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (OperationCanceledException) { return Results.Empty; }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for commit", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!IsOwner(httpContext, run)) return Results.NotFound();

    if (run.Status != RunStatus.AwaitingReview)
        return Results.Conflict(new { error = $"Run is in status '{run.Status.ToApiString()}' and cannot be committed." });

    if (string.IsNullOrEmpty(run.WorktreePath) || !Directory.Exists(run.WorktreePath))
        return Results.Conflict(new { error = "Worktree not available." });

    try { worktreeManager.CommitChanges(run.WorktreePath, runId); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to commit worktree changes for run {RunId}", runId);
        return Results.Problem("Failed to commit worktree changes.", statusCode: 500);
    }

    var transitioned = await runStore.TryTransitionReviewAsync(
        runId, RunStatus.Completed, DateTimeOffset.UtcNow, null, ct);
    if (!transitioned)
        return Results.Conflict(new { error = "Run status has changed; could not transition to completed." });

    var entry = streamStore.Get(id);
    if (entry is not null)
    {
        var seq = entry.NextSequence();
        entry.Record(new RunEvent(seq, EventTypes.RunCompleted, new { result = "committed" }));
        streamStore.Complete(id);
    }

    logger.LogInformation("Run {RunId} committed by {User}", id, ApiKeyAuthMiddleware.GetCaller(httpContext).User);

    return Results.Json(new CommitResponse { RunId = id, Status = RunStatus.Completed.ToApiString() });
});

// GET /api/runs/{id}/workspace — flat directory listing of all files in the worktree (not just changed).
// Used by the Files tab in the artifact browser. Only available for runs with an active worktree.
app.MapGet("/api/runs/{id}/workspace", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
    WorktreeManager worktreeManager,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (OperationCanceledException) { return Results.Empty; }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for workspace listing", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!IsOwner(httpContext, run)) return Results.NotFound();

    // Worktree is gone for terminal statuses that remove or abandon it.
    if (run.Status is RunStatus.Failed or RunStatus.Merged or RunStatus.Declined or RunStatus.MergeFailed)
        return Results.NotFound();

    // Pending runs have no worktree yet.
    if (run.Status is RunStatus.Pending)
        return Results.Json(Array.Empty<WorkspaceNode>());

    if (string.IsNullOrEmpty(run.WorktreePath) || !Directory.Exists(run.WorktreePath))
        return Results.NotFound();

    try
    {
        // Build a path → status map from the changed-file set.
        var changedFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyDictionary<string, (int Added, int Removed)> committedLineCounts   = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, (int Added, int Removed)> uncommittedLineCounts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(run.WorktreeBranch))
        {
            try
            {
                var committed = worktreeManager.GetCommittedFileEntries(
                    run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch);
                foreach (var e in committed) changedFiles[e.Path] = e.Status;
                committedLineCounts = worktreeManager.GetFileDiffLineCounts(
                    run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not compute committed entries for workspace listing of run {RunId}", runId);
            }
        }

        try
        {
            var uncommitted = worktreeManager.GetUncommittedFileEntries(run.WorktreePath);
            foreach (var e in uncommitted) changedFiles[e.Path] = e.Status;
            uncommittedLineCounts = worktreeManager.GetUncommittedFileDiffLineCounts(run.WorktreePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not compute uncommitted entries for workspace listing of run {RunId}", runId);
        }

        var worktreeRoot = run.WorktreePath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var nodes = new List<WorkspaceNode>();

        // Enumerate directories (exclude .git).
        foreach (var dir in Directory.GetDirectories(worktreeRoot, "*", SearchOption.AllDirectories))
        {
            var rel = dir.Substring(worktreeRoot.Length)
                         .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Replace('\\', '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            nodes.Add(new WorkspaceNode { Path = rel, IsFolder = true, Status = null });
        }

        // Enumerate files (exclude .git file/directory and its contents).
        foreach (var file in Directory.GetFiles(worktreeRoot, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(worktreeRoot.Length)
                          .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Replace('\\', '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            changedFiles.TryGetValue(rel, out var status);

            var (cAdded, cRemoved) = committedLineCounts.TryGetValue(rel, out var cc) ? cc : (0, 0);
            var (uAdded, uRemoved) = uncommittedLineCounts.TryGetValue(rel, out var uc) ? uc : (0, 0);

            nodes.Add(new WorkspaceNode
            {
                Path         = rel,
                IsFolder     = false,
                Status       = status,
                AddedLines   = Math.Max(0, cAdded   + uAdded),
                RemovedLines = Math.Max(0, cRemoved + uRemoved),
            });
        }

        // Sort: folders first (alphabetically), then files (alphabetically).
        var sorted = nodes
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToArray();

        return Results.Json(sorted);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to list workspace for run {RunId}", runId);
        return Results.Problem("Failed to list workspace.", statusCode: 500);
    }
});

app.MapPost("/api/runs/{id}/shell-approvals", (
    string id,
    ShellApprovalRequest body,
    IShellApprovalStore approvalStore) =>
{
    if (string.IsNullOrWhiteSpace(body.CommandHash))
        return Results.BadRequest(new { error = "command_hash is required." });

    approvalStore.Approve(id, body.CommandHash);
    return Results.Ok(new { run_id = id, command_hash = body.CommandHash, approved = true });
});

app.MapGet("/api/sandbox-policy", async (
    string? repository_path,
    ISandboxPolicyStore policyStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(repository_path))
        return Results.BadRequest(new { error = "repository_path is required." });

    try
    {
        var policy = await policyStore.GetPolicyAsync(repository_path, ct);
        return Results.Json(ToSandboxPolicyDto(policy));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to get sandbox policy for {RepositoryPath}", repository_path);
        return Results.Problem("Failed to retrieve the sandbox policy.", statusCode: 500);
    }
});

app.MapPut("/api/sandbox-policy", async (
    SandboxPolicyDto request,
    ISandboxPolicyStore policyStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RepositoryPath))
        return Results.BadRequest(new { error = "repository_path is required." });

    var policy = ToSandboxPolicyDomain(request);

    try
    {
        await policyStore.SetPolicyAsync(policy, ct);
        var saved = await policyStore.GetPolicyAsync(policy.RepositoryPath, ct);
        return Results.Json(ToSandboxPolicyDto(saved));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save sandbox policy for {RepositoryPath}", request.RepositoryPath);
        return Results.Problem("Failed to save the sandbox policy.", statusCode: 500);
    }
});

// GET /api/runs/{id}/files — returns the changed-file set for a run (FR-034)
app.MapGet("/api/runs/{id}/files", async (
    HttpContext httpContext,
    string id,
    string? filter,
    SqliteRunStore runStore,
    WorktreeManager worktreeManager,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    // A trailing slash on this URL indicates an empty file-path attempt that ASP.NET
    // Core's trailing-slash normalization sends here instead of to the diff endpoint.
    // Reject it so callers receive 400 rather than an unintended file-list response.
    if (httpContext.Request.Path.HasValue && httpContext.Request.Path.Value!.EndsWith('/'))
        return Results.BadRequest(new { error = "Invalid file path." });

    // Validate filter before any run lookup so an invalid value always returns 400.
    var normalizedFilter = (filter ?? "all").ToLowerInvariant();
    if (normalizedFilter is not ("all" or "committed" or "uncommitted" or "last-commit"))
        return Results.BadRequest(new { error = "filter must be all, committed, uncommitted, or last-commit." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (OperationCanceledException) { return Results.Empty; }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for file list", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!IsOwner(httpContext, run)) return Results.NotFound();

    // Failed runs do not serve artifacts (aligns with diff-withholding policy FR-026 / SC-009).
    if (run.Status is RunStatus.Pending or RunStatus.Failed)
        return Results.Json(Array.Empty<WorkspaceFileEntry>());

    bool isTerminal = run.Status is RunStatus.Merged or RunStatus.Declined;

    if (isTerminal)
    {
        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(run.Diff ?? string.Empty);
        return Results.Json(entries);
    }

    if (string.IsNullOrEmpty(run.WorktreePath) || string.IsNullOrEmpty(run.WorktreeBranch))
        return Results.Conflict(new { error = "Worktree not available." });

    if (!Directory.Exists(run.WorktreePath))
        return Results.Conflict(new { error = "Worktree not available." });

    try
    {
        IReadOnlyList<WorkspaceFileEntry> result = normalizedFilter switch
        {
            "committed"   => worktreeManager.GetCommittedFileEntries(run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch),
            "uncommitted" => worktreeManager.GetUncommittedFileEntries(run.WorktreePath),
            "last-commit" => worktreeManager.GetLastCommitFileEntries(run.WorktreePath),
            _             => MergeFileEntries(
                                 worktreeManager.GetCommittedFileEntries(run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch),
                                 worktreeManager.GetUncommittedFileEntries(run.WorktreePath)),
        };

        // Populate per-file line counts with a single Patch comparison per scope.
        var committedCounts   = worktreeManager.GetFileDiffLineCounts(run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch);
        var uncommittedCounts = worktreeManager.GetUncommittedFileDiffLineCounts(run.WorktreePath);
        result = ApplyLineCounts(result, committedCounts, uncommittedCounts);

        return Results.Json(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to compute file entries for run {RunId}", runId);
        return Results.Problem("Failed to compute file entries.", statusCode: 500);
    }
});

// GET /api/runs/{id}/files/{**path} — unified handler for per-file diff (FR-035) and file content.
// Content endpoint: GET /api/runs/{id}/files/{**path}/content
// Note: ASP.NET Core route templates cannot have a literal suffix after a catch-all parameter,
// so the content endpoint is handled here by detecting the "/content" suffix on the path parameter.
// A URL of /files/src/app.ts/content arrives with path="src/app.ts/content"; the handler strips
// the suffix to obtain the real file path. Known edge case: a file literally named "content"
// inside a subdirectory (e.g. src/content) will be treated as a content request for its parent.
app.MapGet("/api/runs/{id}/files/{**path}", async (
    HttpContext httpContext,
    string id,
    string path,
    SqliteRunStore runStore,
    WorktreeManager worktreeManager,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (OperationCanceledException) { return Results.Empty; }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for file diff", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!IsOwner(httpContext, run)) return Results.NotFound();

    // Detect whether this is a content request (URL ends with "/content").
    const string contentSuffix = "/content";
    bool isContentRequest = path.EndsWith(contentSuffix, StringComparison.Ordinal);
    string pathForValidation = isContentRequest ? path[..^contentSuffix.Length] : path;

    // Path validation after ownership check to prevent leaking run existence via error-code differences.
    if (!TryValidateRelativePath(pathForValidation, out var normalizedPath))
        return Results.BadRequest(new { error = "Invalid file path." });

    // Post-open path containment check using the stored worktree root.
    if (!string.IsNullOrEmpty(run.WorktreePath))
    {
        var worktreeRoot = run.WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(worktreeRoot, normalizedPath));
        var rootWithSep = worktreeRoot + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, cmp))
            return Results.BadRequest(new { error = "Invalid file path." });
    }

    // Failed runs do not serve artifacts (aligns with diff-withholding policy FR-026 / SC-009).
    if (run.Status is RunStatus.Pending or RunStatus.Failed)
        return Results.NotFound();

    // --- Content endpoint branch ---
    if (isContentRequest)
    {
        // Content is only available for live runs that have an accessible worktree.
        // Terminal-state runs (merged, declined) no longer have a worktree on disk.
        if (run.Status is RunStatus.Merged or RunStatus.Declined or RunStatus.MergeFailed)
            return Results.NotFound();

        if (string.IsNullOrEmpty(run.WorktreePath) || !Directory.Exists(run.WorktreePath))
            return Results.Conflict(new { error = "Worktree not available." });

        var worktreeRoot2 = run.WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(worktreeRoot2, normalizedPath));

        if (!File.Exists(fullPath))
            return Results.NotFound();

        try
        {
            const int maxContentBytes = 1 * 1024 * 1024; // 1 MB
            const int binaryProbeBytes = 8192;

            // Binary detection: check the first 8 KB for null bytes.
            bool isBinaryFile;
            using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buf = new byte[binaryProbeBytes];
                int read = await probe.ReadAsync(buf, 0, binaryProbeBytes, ct);
                isBinaryFile = buf.AsSpan(0, read).IndexOf((byte)0) >= 0;
            }

            if (isBinaryFile)
            {
                return Results.Json(new WorkspaceFileContent
                {
                    Path     = normalizedPath,
                    Content  = null,
                    IsBinary = true,
                    Language = DetectLanguage(normalizedPath),
                });
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > maxContentBytes)
            {
                return Results.Json(new WorkspaceFileContent
                {
                    Path     = normalizedPath,
                    Content  = null,
                    IsBinary = false,
                    Language = "too_large",
                });
            }

            var content = await File.ReadAllTextAsync(fullPath, ct);
            return Results.Json(new WorkspaceFileContent
            {
                Path     = normalizedPath,
                Content  = content,
                IsBinary = false,
                Language = DetectLanguage(normalizedPath),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read file content for run {RunId} path {Path}", runId, normalizedPath);
            return Results.Problem("Failed to read file content.", statusCode: 500);
        }
    }

    // --- Diff endpoint branch ---
    bool isTerminal = run.Status is RunStatus.Merged or RunStatus.Declined;

    IReadOnlyList<WorkspaceFileEntry> allEntries;

    if (isTerminal)
    {
        allEntries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(run.Diff ?? string.Empty);
    }
    else
    {
        if (string.IsNullOrEmpty(run.WorktreePath) || string.IsNullOrEmpty(run.WorktreeBranch))
            return Results.Conflict(new { error = "Worktree not available." });
        if (!Directory.Exists(run.WorktreePath))
            return Results.Conflict(new { error = "Worktree not available." });

        try
        {
            allEntries = MergeFileEntries(
                worktreeManager.GetCommittedFileEntries(run.RepositoryPath, run.OriginatingBranch, run.WorktreeBranch),
                worktreeManager.GetUncommittedFileEntries(run.WorktreePath));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compute file whitelist for run {RunId}", runId);
            return Results.Problem("Failed to retrieve file list.", statusCode: 500);
        }
    }

    // Whitelist check: only serve paths present in the changed-file set.
    var whitelistEntry = allEntries.FirstOrDefault(
        e => string.Equals(e.Path, normalizedPath, StringComparison.Ordinal));
    if (whitelistEntry is null)
        return Results.NotFound();

    if (isTerminal)
    {
        var (storedDiff, isBinary) = WorkspaceFileEntryParser.ParseFileDiffFromUnifiedDiff(run.Diff ?? string.Empty, normalizedPath);
        return Results.Json(new WorkspaceFileDiff
        {
            Path     = normalizedPath,
            Diff     = isBinary ? null : storedDiff,
            Status   = whitelistEntry.Status,
            IsBinary = isBinary,
        });
    }

    try
    {
        var (fileDiff, isBinaryFile2) = worktreeManager.GetFileDiffEntry(
            run.RepositoryPath, run.WorktreePath!, run.OriginatingBranch, run.WorktreeBranch!, normalizedPath);

        return Results.Json(new WorkspaceFileDiff
        {
            Path     = normalizedPath,
            Diff     = fileDiff,
            Status   = whitelistEntry.Status,
            IsBinary = isBinaryFile2,
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to compute file diff for run {RunId}", runId);
        return Results.Problem("Failed to compute file diff.", statusCode: 500);
    }
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
    if (currentTreeHash is null || !string.Equals(currentTreeHash, run.TreeHash, StringComparison.Ordinal))
    {
        logger.LogError(
            "Worktree tree hash could not be verified or mismatched for run {RunId} in direct review: expected={Expected} actual={Actual}",
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

static SandboxPolicyDto ToSandboxPolicyDto(SandboxPolicy policy) => new()
{
    RepositoryPath             = policy.RepositoryPath,
    ShellEnabled               = policy.ShellEnabled,
    Direct                     = policy.Direct,
    NetworkEnabled             = policy.NetworkEnabled,
    AllowedRepositoryRoots     = policy.AllowedRepositoryRoots,
    DestructiveCommandPatterns = policy.DestructiveCommandPatterns,
    RequireApprovalForAllShell = policy.RequireApprovalForAllShell,
    RedactPii                  = policy.RedactPii,
    MaxOutputBytes             = policy.MaxOutputBytes,
};

static SandboxPolicy ToSandboxPolicyDomain(SandboxPolicyDto dto) => new()
{
    RepositoryPath             = dto.RepositoryPath,
    ShellEnabled               = dto.ShellEnabled,
    Direct                     = dto.Direct,
    NetworkEnabled             = dto.NetworkEnabled,
    AllowedRepositoryRoots     = dto.AllowedRepositoryRoots,
    DestructiveCommandPatterns = dto.DestructiveCommandPatterns,
    RequireApprovalForAllShell = dto.RequireApprovalForAllShell,
    RedactPii                  = dto.RedactPii,
    MaxOutputBytes             = dto.MaxOutputBytes,
};

/// <summary>
/// Validates a relative file path from a route parameter. Normalizes percent-encoded
/// separators (%2F, %5C) that ASP.NET Core does not decode in catch-all route params,
/// then rejects null bytes, control characters (including DEL and C1), rooted paths,
/// UNC paths, device paths, drive-relative paths, parent-traversal segments, and on
/// Windows, Alternate Data Stream specifiers. Returns false on any violation; sets
/// normalizedPath to the canonical relative form on success.
/// </summary>
static bool TryValidateRelativePath(string? rawPath, out string normalizedPath)
{
    normalizedPath = string.Empty;
    if (string.IsNullOrEmpty(rawPath)) return false;

    // Normalize percent-encoded separators that ASP.NET Core does not decode in catch-all params.
    // This must happen first so all subsequent checks operate on the decoded path.
    rawPath = rawPath.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
                     .Replace("%5C", "/", StringComparison.OrdinalIgnoreCase);

    foreach (var c in rawPath)
    {
        // Reject C0 control characters (including null byte).
        if (c == '\0' || c < ' ') return false;
        // Reject DEL and C1 control characters.
        if (c == '\u007F' || (c >= '\u0080' && c <= '\u009F')) return false;
    }

    // Reject raw UNC paths before separator normalization.
    if (rawPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;

    var normalized = rawPath.Replace('\\', '/');

    // Reject absolute and rooted paths (handles /, C:\, C:/).
    if (Path.IsPathRooted(normalized)) return false;

    // Reject normalized UNC (//host) and device (//./device) paths.
    if (normalized.StartsWith("//", StringComparison.Ordinal)) return false;

    // Reject drive-relative paths not already caught by IsPathRooted on non-Windows hosts.
    if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        return false;

    // Reject parent-traversal segments.
    foreach (var segment in normalized.Split('/'))
    {
        if (segment == "..") return false;
    }

    // On Windows, reject paths containing ':' after drive-letter checks above.
    // Any remaining ':' indicates a Windows Alternate Data Stream specifier.
    if (OperatingSystem.IsWindows() && normalized.Contains(':', StringComparison.Ordinal))
        return false;

    normalizedPath = normalized;
    return true;
}

/// <summary>
/// Merges committed and uncommitted file-entry lists into a single deduplicated list.
/// When a path appears in both, the uncommitted entry wins (more current status).
/// </summary>
static IReadOnlyList<WorkspaceFileEntry> MergeFileEntries(
    IReadOnlyList<WorkspaceFileEntry> committed,
    IReadOnlyList<WorkspaceFileEntry> uncommitted)
{
    var merged = new Dictionary<string, WorkspaceFileEntry>(StringComparer.Ordinal);
    foreach (var e in committed)   merged[e.Path] = e;
    foreach (var e in uncommitted) merged[e.Path] = e;
    return [.. merged.Values];
}

/// <summary>
/// Applies per-file line counts to a list of file entries, merging committed and uncommitted
/// counts per path. Counts are capped at zero to guard against any negative values.
/// </summary>
static IReadOnlyList<WorkspaceFileEntry> ApplyLineCounts(
    IReadOnlyList<WorkspaceFileEntry> entries,
    IReadOnlyDictionary<string, (int Added, int Removed)> committedCounts,
    IReadOnlyDictionary<string, (int Added, int Removed)> uncommittedCounts)
{
    return entries.Select(e =>
    {
        var (cAdded, cRemoved) = committedCounts.TryGetValue(e.Path,   out var cc) ? cc : (0, 0);
        var (uAdded, uRemoved) = uncommittedCounts.TryGetValue(e.Path, out var uc) ? uc : (0, 0);
        return e with
        {
            AddedLines   = Math.Max(0, cAdded   + uAdded),
            RemovedLines = Math.Max(0, cRemoved + uRemoved),
        };
    }).ToList();
}

/// <summary>
/// Maps a file extension to a language identifier accepted by react-syntax-highlighter.
/// Returns null for unknown extensions.
/// </summary>
static string? DetectLanguage(string path)
{
    var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    return ext switch
    {
        "cs"                                    => "csharp",
        "ts" or "tsx"                           => "typescript",
        "js" or "jsx"                           => "javascript",
        "json"                                  => "json",
        "md"                                    => "markdown",
        "css"                                   => "css",
        "html"                                  => "html",
        "xml" or "csproj" or "props" or "targets" => "xml",
        "yaml" or "yml"                         => "yaml",
        "sh" or "bash"                          => "bash",
        "ps1"                                   => "powershell",
        "py"                                    => "python",
        "go"                                    => "go",
        "rs"                                    => "rust",
        "java"                                  => "java",
        "cpp" or "cc" or "cxx" or "c" or "h" or "hpp" => "cpp",
        "sql"                                   => "sql",
        "txt"                                   => "plaintext",
        _                                       => null
    };
}

public partial class Program { }

/// <summary>
/// Typed record for deserializing the <c>sandbox.selected</c> event payload.
/// The payload is stored as an anonymous object and serialized with camelCase,
/// so <see cref="System.Text.Json.JsonSerializerOptions.PropertyNameCaseInsensitive"/> is used.
/// </summary>
file sealed class SandboxSelectedPayload
{
    public string? Backend { get; init; }
    public bool IsRealIsolation { get; init; }
    public string? Reason { get; init; }
}
