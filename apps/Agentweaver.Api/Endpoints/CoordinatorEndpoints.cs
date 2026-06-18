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

namespace Agentweaver.Api.Endpoints;

public static class CoordinatorEndpoints
{
    public static void MapCoordinatorEndpoints(this WebApplication app)
    {
// GET /api/runs/{id}/outcome-spec — current persisted outcome spec for a coordinator run.
app.MapGet("/api/runs/{id}/outcome-spec", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
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

    var spec = await coordinator.GetOutcomeSpecAsync(id, ct);
    if (spec is null) return Results.NotFound();

    return Results.Json(MapOutcomeSpec(spec));
});

// POST /api/runs/{id}/outcome-spec/confirm — confirm the drafted outcome spec.
app.MapPost("/api/runs/{id}/outcome-spec/confirm", async (
    HttpContext httpContext,
    string id,
    SqliteRunStore runStore,
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
        CoordinatorGateOutcome.RunNotActive => Results.Conflict(new { error = "run_not_active", message = "The coordinator run is not active and cannot be confirmed." }),
        CoordinatorGateOutcome.NoPendingGate => Results.Conflict(new { error = "no_pending_gate", message = "The outcome spec is not awaiting confirmation." }),
        _ => Results.Problem("Unexpected coordinator outcome.", statusCode: 500),
    };
});

// POST /api/runs/{id}/outcome-spec/revise — request a revision of the drafted outcome spec.
// Body: { feedback }. The coordinator re-drafts and re-suspends at the gate.
app.MapPost("/api/runs/{id}/outcome-spec/revise", async (
    HttpContext httpContext,
    string id,
    ReviseOutcomeSpecRequest request,
    SqliteRunStore runStore,
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
        CoordinatorGateOutcome.RunNotActive => Results.Conflict(new { error = "run_not_active", message = "The coordinator run is not active and cannot be revised." }),
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
    SqliteRunStore runStore,
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

    var plan = await coordinator.GetWorkPlanAsync(coordinatorRunId, ct);
    if (plan is null) return Results.NotFound();

    return Results.Json(MapWorkPlan(plan));
});

// GET /api/runs/{coordinatorRunId}/children — dispatched child runs paired with subtask status.
// Returns an empty array when nothing has been dispatched yet (or no plan exists).
app.MapGet("/api/runs/{coordinatorRunId}/children", async (
    HttpContext httpContext,
    string coordinatorRunId,
    SqliteRunStore runStore,
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
    SqliteRunStore runStore,
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
    SqliteRunStore runStore,
    Agentweaver.Api.Coordinator.AssemblyReviewGate reviewGate,
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

    var result = reviewGate.TrySubmit(coordinatorRunId, caller.User, decision);

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

// Maps a coordinator work-plan view to its camelCase response (Feature 008 Phase 2).
static WorkPlanResponse MapWorkPlan(CoordinatorWorkPlanView plan) => new()
{
    WorkPlanId = plan.WorkPlanId,
    CoordinatorRunId = plan.CoordinatorRunId,
    OutcomeSpecId = plan.OutcomeSpecId,
    Status = plan.Status,
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
