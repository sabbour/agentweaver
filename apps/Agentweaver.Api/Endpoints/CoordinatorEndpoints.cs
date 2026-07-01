using System.Text.Encodings.Web;
using System.Text.Json;
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

namespace Agentweaver.Api.Endpoints;

public static class CoordinatorEndpoints
{
    public static void MapCoordinatorEndpoints(this WebApplication app)
    {
// GET /api/runs/{id}/outcome-spec — current persisted outcome spec for a coordinator run.
app.MapGet("/api/runs/{id}/outcome-spec", async (
    HttpContext httpContext,
    string id,
    IRunStore runStore,
    CoordinatorRunService coordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for outcome-spec", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var spec = await ReadOutcomeSpecWithBriefWaitAsync(coordinator, id, ct);
    if (spec is null) return Results.NotFound();

    return Results.Json(MapOutcomeSpec(spec));
});

// POST /api/runs/{id}/outcome-spec/confirm — confirm the drafted outcome spec.
app.MapPost("/api/runs/{id}/outcome-spec/confirm", async (
    HttpContext httpContext,
    string id,
    IRunStore runStore,
    CoordinatorRunService coordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for outcome-spec confirm", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var outcome = await coordinator.ConfirmOutcomeSpecAsync(id, caller.User, ct);

    return outcome switch
    {
        CoordinatorGateOutcome.Accepted => Results.Json(await ReadOutcomeSpecAsync(coordinator, id, ct)),
        CoordinatorGateOutcome.RunNotActive => await ReadConfirmedOutcomeSpecResultAsync(coordinator, id, ct)
            ?? Results.Conflict(new { error = "run_not_active", detail = await ReadFailureReasonAsync(runStore, runId, ct), message = "The coordinator run is not active and cannot be confirmed." }),
        CoordinatorGateOutcome.NoPendingGate => await ReadConfirmedOutcomeSpecResultAsync(coordinator, id, ct)
            ?? Results.Conflict(new { error = "no_pending_gate", message = "The outcome spec is not awaiting confirmation." }),
        _ => Results.Problem("Unexpected coordinator outcome.", statusCode: 500),
    };
});

// POST /api/runs/{id}/outcome-spec/revise — request a revision of the drafted outcome spec.
// Body: { feedback }. The coordinator re-drafts and re-suspends at the gate.
app.MapPost("/api/runs/{id}/outcome-spec/revise", async (
    HttpContext httpContext,
    string id,
    ReviseOutcomeSpecRequest request,
    IRunStore runStore,
    CoordinatorRunService coordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(id, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    if (string.IsNullOrWhiteSpace(request.Feedback))
        return Results.BadRequest(new { error = "feedback is required." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for outcome-spec revise", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var outcome = await coordinator.ReviseOutcomeSpecAsync(id, request.Feedback!, caller.User, ct);

    return outcome switch
    {
        CoordinatorGateOutcome.Accepted => Results.Json(await ReadOutcomeSpecAsync(coordinator, id, ct)),
        CoordinatorGateOutcome.RunNotActive => Results.Conflict(new { error = "run_not_active", detail = await ReadFailureReasonAsync(runStore, runId, ct), message = "The coordinator run is not active and cannot be revised." }),
        CoordinatorGateOutcome.NoPendingGate => Results.Conflict(new { error = "no_pending_gate", message = "The outcome spec is not awaiting confirmation." }),
        _ => Results.Problem("Unexpected coordinator outcome.", statusCode: 500),
    };
});

// -----------------------------------------------------------------------
// Coordinator orchestration (Feature 008 Phase 2) — work plan, children, steering.
// Thin HTTP over CoordinatorRunService/CoordinatorSteeringService: validate input, resolve
// owner-scoped context, delegate, and map the service result/exception to status codes
// (Principle III). No business logic lives in these endpoints.
// -----------------------------------------------------------------------

// GET /api/runs/{coordinatorRunId}/work-plan — the persisted work plan (subtasks + dependencies)
// for a coordinator run. 404 when the run is not a coordinator run / has no work plan yet.
app.MapGet("/api/runs/{coordinatorRunId}/work-plan", async (
    HttpContext httpContext,
    string coordinatorRunId,
    IRunStore runStore,
    CoordinatorRunService coordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(coordinatorRunId, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for work-plan", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var plan = await ReadWorkPlanWithBriefWaitAsync(coordinator, coordinatorRunId, ct);
    if (plan is null) return Results.NotFound();

    return Results.Json(MapWorkPlan(plan));
});

// GET /api/runs/{coordinatorRunId}/children — dispatched child runs paired with subtask status.
// Returns an empty array when nothing has been dispatched yet (or no plan exists).
app.MapGet("/api/runs/{coordinatorRunId}/children", async (
    HttpContext httpContext,
    string coordinatorRunId,
    IRunStore runStore,
    CoordinatorRunService coordinator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(coordinatorRunId, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for children", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var children = await coordinator.GetChildrenAsync(coordinatorRunId, ct);
    return Results.Json(children.Select(MapChild).ToList());
});

// POST /api/runs/{coordinatorRunId}/steer — relay a human steering directive to a running
// coordinator. Body: { kind: stop|redirect|amend, targetChildRunId?, instruction }. The descoped
// 'pause' verb, unknown verbs, and a missing instruction for redirect/amend are rejected by the
// service with a SteeringValidationException, which maps to 400 here. createdBy is the caller.
app.MapPost("/api/runs/{coordinatorRunId}/steer", async (
    HttpContext httpContext,
    string coordinatorRunId,
    SteerRequest request,
    IRunStore runStore,
    CoordinatorSteeringService steering,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(coordinatorRunId, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    if (string.IsNullOrWhiteSpace(request.Kind))
        return Results.BadRequest(new { error = "kind is required." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for steer", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    try
    {
        var directive = await steering.SteerAsync(
            coordinatorRunId,
            request.Kind!,
            string.IsNullOrWhiteSpace(request.TargetChildRunId) ? null : request.TargetChildRunId,
            request.Instruction ?? string.Empty,
            caller.User,
            ct);

        return Results.Json(MapSteeringDirective(directive), statusCode: StatusCodes.Status201Created);
    }
    catch (SteeringValidationException ex)
    {
        return Results.BadRequest(new { error = "steering_invalid", message = ex.Message });
    }
    catch (SteeringRecoveryExhaustedException ex)
    {
        return Results.Json(
            new { error = "steering_recovery_exhausted", message = ex.Message },
            statusCode: StatusCodes.Status409Conflict);
    }
});

// POST /api/runs/{coordinatorRunId}/assembly/review — the ONE collective human-review gate
// (Feature 008 Phase 3, D5). Mirrors POST /api/runs/{id}/review (owner-scoped, at-most-once) but
// delivers the decision to the service-driven AssemblyReviewGate the collective pipeline is awaiting.
// Body: { approved, request_changes?, feedback?, target_files? }. approve -> merge/scribe/complete;
// request_changes -> rejection inference + re-dispatch (D6); decline -> assembly_declined.
app.MapPost("/api/runs/{coordinatorRunId}/assembly/review", async (
    HttpContext httpContext,
    string coordinatorRunId,
    AssemblyReviewRequest request,
    IRunStore runStore,
    Agentweaver.Api.Coordinator.AssemblyReviewGate reviewGate,
    IServiceScopeFactory scopeFactory,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RunId.TryParse(coordinatorRunId, out var runId))
        return Results.BadRequest(new { error = "Invalid run id." });

    Run? run;
    try { run = await runStore.GetAsync(runId, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch run {RunId} for assembly review", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    var decision = new Agentweaver.Api.Coordinator.AssemblyReviewDecision(
        Approved: request.Approved,
        RequestChanges: request.RequestChanges,
        Feedback: request.Feedback,
        TargetFiles: request.TargetFiles,
        Reviewer: caller.User);

    var result = reviewGate.TrySubmit(coordinatorRunId, caller.User, decision, caller.GitHubLogin);
    if (result == Agentweaver.Api.Coordinator.AssemblyReviewSubmitResult.NotArmed
        && await IsAssemblyReviewPendingAsync(coordinatorRunId, scopeFactory, ct).ConfigureAwait(false))
    {
        var deferred = await TryDeferAssemblyReviewDecisionAsync(
            coordinatorRunId, decision, scopeFactory, logger, CancellationToken.None).ConfigureAwait(false);
        if (deferred)
            result = Agentweaver.Api.Coordinator.AssemblyReviewSubmitResult.Accepted;
    }

    logger.LogInformation(
        "Assembly review decision: {Decision}. RunId={RunId} Reviewer={Reviewer} Result={Result}",
        request.Approved ? "approved" : (request.RequestChanges ? "request-changes" : "declined"),
        coordinatorRunId, caller.User, result);

    return result switch
    {
        Agentweaver.Api.Coordinator.AssemblyReviewSubmitResult.Accepted =>
            Results.Json(new { runId = coordinatorRunId, accepted = true }),
        Agentweaver.Api.Coordinator.AssemblyReviewSubmitResult.Forbidden =>
            Results.StatusCode(StatusCodes.Status403Forbidden),
        // NotArmed: no collective review is currently awaited (not yet at the gate, or already consumed).
        _ => Results.Conflict(new { error = "no_assembly_review_pending" }),
    };
});

// GET /api/runs/{id}/assembly/files — the COLLECTIVE changed-file set for a coordinator run.
// The coordinator owns no worktree; the assembled output lives on the integration branch
// (agentweaver/integration/{id}). We diff that branch vs the originating branch and parse the
// unified diff into file entries, so the standard Changes/Files rail can review the collective
// output. Returns [] (never 409) before assembly has built the integration branch.
app.MapGet("/api/runs/{id}/assembly/files", async (
    HttpContext httpContext,
    string id,
    IRunStore runStore,
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
        logger.LogError(ex, "Failed to fetch run {RunId} for assembly files", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.NotFound();

    var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(id);
    string? aggregateDiff;
    try
    {
        aggregateDiff = worktreeManager.TryGetBranchDiff(run.RepositoryPath, run.OriginatingBranch, integrationBranch);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to compute assembly diff for run {RunId}", runId);
        return Results.Problem("Failed to compute assembly diff.", statusCode: 500);
    }

    var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(aggregateDiff ?? string.Empty);
    return Results.Json(entries);
});

// GET /api/runs/{id}/assembly/files/{**path} — per-file diff within the collective assembly diff.
app.MapGet("/api/runs/{id}/assembly/files/{**path}", async (
    HttpContext httpContext,
    string id,
    string path,
    IRunStore runStore,
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
        logger.LogError(ex, "Failed to fetch run {RunId} for assembly file diff", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.NotFound();

    var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
    if (string.IsNullOrEmpty(normalizedPath))
        return Results.BadRequest(new { error = "Invalid file path." });

    var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(id);
    string? aggregateDiff;
    try
    {
        aggregateDiff = worktreeManager.TryGetBranchDiff(run.RepositoryPath, run.OriginatingBranch, integrationBranch);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to compute assembly diff for run {RunId} path {Path}", runId, normalizedPath);
        return Results.Problem("Failed to compute assembly diff.", statusCode: 500);
    }

    if (string.IsNullOrEmpty(aggregateDiff))
        return Results.NotFound();

    // Whitelist: only serve paths present in the collective changed-file set.
    var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(aggregateDiff);
    var whitelistEntry = entries.FirstOrDefault(e => string.Equals(e.Path, normalizedPath, StringComparison.Ordinal));
    if (whitelistEntry is null)
        return Results.NotFound();

    var (fileDiff, isBinary) = WorkspaceFileEntryParser.ParseFileDiffFromUnifiedDiff(aggregateDiff, normalizedPath);
    return Results.Json(new WorkspaceFileDiff
    {
        Path     = normalizedPath,
        Diff     = isBinary ? null : fileDiff,
        Status   = whitelistEntry.Status,
        IsBinary = isBinary,
    });
});

// GET /api/runs/{id}/assembly/workspace — full file tree of the collective integration branch
// (agentweaver/integration/{id}) HEAD, so the Files tab can browse the assembled filesystem from a
// git perspective (every tracked file, not just the changed set). The coordinator owns no worktree,
// so we read the branch tip's commit tree directly. Returns [] (never 409) before assembly has built
// the integration branch.
app.MapGet("/api/runs/{id}/assembly/workspace", async (
    HttpContext httpContext,
    string id,
    IRunStore runStore,
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
        logger.LogError(ex, "Failed to fetch run {RunId} for assembly workspace", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.NotFound();
    if (string.IsNullOrEmpty(run.RepositoryPath))
        return Results.Json(Array.Empty<WorkspaceNode>());

    var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(id);
    try
    {
        using var repo = new Repository(run.RepositoryPath);
        var commit = repo.Branches[integrationBranch]?.Tip
                     ?? repo.Branches[$"refs/heads/{integrationBranch}"]?.Tip;
        if (commit is null)
            return Results.Json(Array.Empty<WorkspaceNode>());

        var nodes = new List<WorkspaceNode>();
        EnumerateAssemblyTree(commit.Tree, "", nodes);
        var sorted = nodes
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToArray();
        return Results.Json(sorted);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to enumerate assembly workspace for run {RunId}", runId);
        return Results.Problem("Failed to enumerate assembly workspace.", statusCode: 500);
    }
});

// GET /api/runs/{id}/assembly/content/{**path} — per-file CONTENT of the collective integration
// branch (agentweaver/integration/{id}) tip, so the review modal's Preview/source tab can render an
// assembled file. The coordinator owns NO worktree (its changes live on the integration branch), so
// the worktree-backed /workspace/files/{**path}/content endpoint 409s for coordinator runs; this
// reads the blob from the branch tip instead. This serves any tracked file on that integration branch
// because the Files tab displays the full assembled tree, not just the changed-file set.
app.MapGet("/api/runs/{id}/assembly/content/{**path}", async (
    HttpContext httpContext,
    string id,
    string path,
    IRunStore runStore,
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
        logger.LogError(ex, "Failed to fetch run {RunId} for assembly file content", runId);
        return Results.Problem("Failed to retrieve the run.", statusCode: 500);
    }

    if (run is null) return Results.NotFound();
    if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.NotFound();
    if (string.IsNullOrEmpty(run.RepositoryPath)) return Results.NotFound();

    if (!EndpointHelpers.TryValidateRelativePath(path, out var normalizedPath))
        return Results.BadRequest(new { error = "Invalid file path." });

    var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(id);

    try
    {
        using var repo = new Repository(run.RepositoryPath);
        var commit = repo.Branches[integrationBranch]?.Tip
                     ?? repo.Branches[$"refs/heads/{integrationBranch}"]?.Tip;
        if (commit is null)
            return Results.NotFound();

        var treeEntry = commit[normalizedPath];
        if (treeEntry is null || treeEntry.TargetType != TreeEntryTargetType.Blob)
            return Results.NotFound();   // e.g. a deleted file has no blob on the tip to preview

        var blob = (Blob)treeEntry.Target;
        return Results.Json(EndpointHelpers.BuildBlobContent(blob, normalizedPath));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to read assembly blob for run {RunId} path {Path}", runId, normalizedPath);
        return Results.Problem("Failed to read file content.", statusCode: 500);
    }
});
    }

// Recursively flattens a git commit tree into the flat WorkspaceNode listing the Files tab renders
// (folders first within each level; full forward-slash relative paths). Mirrors the per-run merged
// commit-tree enumeration in RunEndpoints.
static void EnumerateAssemblyTree(Tree tree, string prefix, List<WorkspaceNode> nodes)
{
    foreach (var entry in tree)
    {
        var entryPath = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
        if (entry.TargetType == TreeEntryTargetType.Tree)
        {
            nodes.Add(new WorkspaceNode { Path = entryPath, IsFolder = true, Status = null });
            EnumerateAssemblyTree((Tree)entry.Target, entryPath, nodes);
        }
        else if (entry.TargetType == TreeEntryTargetType.Blob)
        {
            nodes.Add(new WorkspaceNode { Path = entryPath, IsFolder = false, Status = null });
        }
    }
}

static async Task<bool> IsAssemblyReviewPendingAsync(
    string coordinatorRunId, IServiceScopeFactory scopeFactory, CancellationToken ct)
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
    return await db.WorkPlans.AsNoTracking()
        .AnyAsync(w => w.CoordinatorRunId == coordinatorRunId
            && w.Status == WorkPlanStatus.InReview
            && w.AssemblyStage == AssemblyStage.Review, ct)
        .ConfigureAwait(false);
}

static async Task<bool> TryDeferAssemblyReviewDecisionAsync(
    string coordinatorRunId,
    AssemblyReviewDecision decision,
    IServiceScopeFactory scopeFactory,
    ILogger<Program> logger,
    CancellationToken ct)
{
    var json = JsonSerializer.Serialize(decision, JsonDefaults.Options);
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

    var existing = await db.DeferredDecisions.AsNoTracking()
        .FirstOrDefaultAsync(d => d.RunId == coordinatorRunId, ct)
        .ConfigureAwait(false);
    if (existing is not null)
        return true;

    db.DeferredDecisions.Add(new CoordinatorDeferredDecisionRecord
    {
        RunId = coordinatorRunId,
        DecisionJson = json,
        CreatedAt = DateTimeOffset.UtcNow,
    });

    try
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    catch (DbUpdateException)
    {
        return true;
    }

    logger.LogInformation(
        "Assembly review decision for run {RunId} deferred for owner replica pickup",
        coordinatorRunId);
    return true;
}

// Maps a persisted coordinator OutcomeSpec to the web-client-facing camelCase response.
// Server state is rendered as-is (Principle III); the web panel parses scope/assumptions/
// clarifyingQuestions defensively.
static OutcomeSpecResponse MapOutcomeSpec(OutcomeSpec spec) => new()
{
    Goal = spec.Goal,
    DesiredOutcome = spec.DesiredOutcome,
    Scope = spec.Scope,
    Assumptions = spec.Assumptions,
    ClarifyingQuestions = spec.ClarifyingQuestions,
    Status = spec.Status,
    ConfirmedBy = spec.ConfirmedBy,
};

// Reads the current persisted spec after a confirm/revise so the response mirrors the
// web client's OutcomeSpec | null contract.
static async Task<OutcomeSpecResponse?> ReadOutcomeSpecAsync(
    CoordinatorRunService coordinator, string runId, CancellationToken ct)
{
    var spec = await coordinator.GetOutcomeSpecAsync(runId, ct);
    return spec is null ? null : MapOutcomeSpec(spec);
}

static async Task<OutcomeSpec?> ReadOutcomeSpecWithBriefWaitAsync(
    CoordinatorRunService coordinator, string runId, CancellationToken ct)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (true)
    {
        var spec = await coordinator.GetOutcomeSpecAsync(runId, ct);
        if (spec is not null || DateTimeOffset.UtcNow >= deadline)
            return spec;
        await Task.Delay(100, ct);
    }
}

static async Task<IResult?> ReadConfirmedOutcomeSpecResultAsync(
    CoordinatorRunService coordinator, string runId, CancellationToken ct)
{
    var spec = await coordinator.GetOutcomeSpecAsync(runId, ct);
    return spec?.Status == "confirmed" ? Results.Json(MapOutcomeSpec(spec)) : null;
}

static async Task<CoordinatorWorkPlanView?> ReadWorkPlanWithBriefWaitAsync(
    CoordinatorRunService coordinator, string runId, CancellationToken ct)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (true)
    {
        var plan = await coordinator.GetWorkPlanAsync(runId, ct);
        if (plan is not null || DateTimeOffset.UtcNow >= deadline)
            return plan;
        await Task.Delay(150, ct);
    }
}

// Reads the run's persisted FailureReason (the terminal Result code, e.g. "agent_quota_exceeded")
// so a run_not_active conflict can tell the caller *why* the run is no longer active. Best-effort:
// returns null when the run is gone or the lookup fails, so the response degrades to error-only.
static async Task<string?> ReadFailureReasonAsync(
    Agentweaver.Api.Infrastructure.IRunStore runStore, RunId runId, CancellationToken ct)
{
    try
    {
        var run = await runStore.GetAsync(runId, ct);
        return run?.Result;
    }
    catch
    {
        return null;
    }
}

// Maps a coordinator work-plan view to its camelCase response (Feature 008 Phase 2).
static WorkPlanResponse MapWorkPlan(CoordinatorWorkPlanView plan) => new()
{
    WorkPlanId = plan.WorkPlanId,
    CoordinatorRunId = plan.CoordinatorRunId,
    OutcomeSpecId = plan.OutcomeSpecId,
    Status = plan.Status,
    StatusReason = plan.StatusReason,
    IsolationSummary = plan.IsolationSummary,
    Subtasks = plan.Subtasks.Select(s => new WorkPlanSubtaskResponse
    {
        SubtaskId = s.SubtaskId,
        Title = s.Title,
        Scope = s.Scope,
        AssignedAgent = s.AssignedAgent,
        SelectedModelId = s.SelectedModelId,
        Phase = s.Phase,
        Isolation = s.Isolation,
        Status = s.Status,
        ChildRunId = s.ChildRunId,
    }).ToList(),
    Dependencies = plan.Dependencies.Select(d => new WorkPlanDependencyResponse
    {
        SubtaskId = d.SubtaskId,
        DependsOnSubtaskId = d.DependsOnSubtaskId,
    }).ToList(),
};

// Maps a coordinator child view to its camelCase response (Feature 008 Phase 2).
static CoordinatorChildResponse MapChild(CoordinatorChildView child) => new()
{
    SubtaskId = child.SubtaskId,
    ChildRunId = child.ChildRunId,
    SubtaskStatus = child.SubtaskStatus,
    AssignedAgent = child.AssignedAgent,
    SelectedModelId = child.SelectedModelId,
    ChildRunStatus = child.ChildRunStatus,
    WorktreeBranch = child.WorktreeBranch,
    TreeHash = child.TreeHash,
    StepCount = child.StepCount,
};

// Maps a steering directive view to its camelCase response (Feature 008 Phase 2).
static SteeringDirectiveResponse MapSteeringDirective(SteeringDirectiveView directive) => new()
{
    Id = directive.Id,
    CoordinatorRunId = directive.CoordinatorRunId,
    TargetChildRunId = directive.TargetChildRunId,
    Kind = directive.Kind,
    Instruction = directive.Instruction,
    Status = directive.Status,
    CreatedBy = directive.CreatedBy,
    CreatedAt = directive.CreatedAt,
    RelayedAt = directive.RelayedAt,
};
}
