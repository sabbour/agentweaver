using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Maps authenticated HTTP operations for a project's persisted task backlog.
/// </summary>
public static partial class CoordinationEndpoints
{
    /// <summary>
    /// Registers backlog read, task-edit, dependency, and claim routes under the project run API.
    /// </summary>
    public static IEndpointRouteBuilder MapBacklogEndpoints(this IEndpointRouteBuilder app)
    {
        var projectBacklog = app.MapGroup("/api/projects/{projectId}/backlog")
            .RequireAuthorization();
        projectBacklog.MapGet("/", ReadProjectBacklogAsync);
        projectBacklog.MapPost("/tasks", CaptureProjectBacklogTaskAsync);
        projectBacklog.MapPut("/tasks/{taskId}/content", EditProjectBacklogTaskAsync);
        var backlog = app.MapGroup("/api/projects/{projectId}/runs/{runId}/backlog")
            .RequireAuthorization();
        backlog.MapGet("/", ReadBacklogAsync);
        backlog.MapPost("/tasks", AddBacklogTaskAsync);
        backlog.MapPut("/tasks/{taskId}/state", SetBacklogTaskStateAsync);
        backlog.MapPost("/tasks/{taskId}/archive", ArchiveBacklogTaskAsync);
        backlog.MapPut(
            "/tasks/{taskId}/dependencies/{prerequisiteTaskId}",
            AddBacklogDependencyAsync);
        backlog.MapDelete(
            "/tasks/{taskId}/dependencies/{prerequisiteTaskId}",
            RemoveBacklogDependencyAsync);
        backlog.MapPost("/tasks/{taskId}/claim", ClaimBacklogTaskAsync);
        backlog.MapPost("/promotions", PromoteBacklogWorkPlanAsync);
        projectBacklog.MapGet("/promotions", ReadProjectBacklogPromotionsAsync);
        projectBacklog.MapGet("/origins", ReadProjectBacklogTaskOriginsAsync);
        return app;
    }

    /// <summary>Reads a project backlog without an accepted run.</summary>
    private static Task<IResult> ReadProjectBacklogAsync(
        string projectId, HttpContext context, ProjectsRunSelectionClient projects, BacklogOwnerStore backlog) =>
        ExecuteProjectBacklogOperationAsync(projectId, context, projects, write: false,
            async (_, ct) => BacklogResult(await backlog.ReadGraphAsync(projectId, ct).ConfigureAwait(false)));

    /// <summary>Captures project work using current owner authority and an optimistic graph revision.</summary>
    private static Task<IResult> CaptureProjectBacklogTaskAsync(
        string projectId, CaptureBacklogTaskRequest request, HttpContext context,
        ProjectsRunSelectionClient projects, BacklogOwnerStore backlog) =>
        ExecuteProjectBacklogOperationAsync(projectId, context, projects, write: true,
            async (revalidate, ct) => BacklogResult(await backlog.AddTaskAsync(
                new BacklogTaskReference(projectId, request.TaskId), request.Content,
                request.ExpectedGraphRevision, ct, revalidate).ConfigureAwait(false)));

    /// <summary>Replaces task content without creating or changing an execution reservation.</summary>
    private static Task<IResult> EditProjectBacklogTaskAsync(
        string projectId, string taskId, EditBacklogTaskContentRequest request, HttpContext context,
        ProjectsRunSelectionClient projects, BacklogOwnerStore backlog) =>
        ExecuteProjectBacklogOperationAsync(projectId, context, projects, write: true,
            async (revalidate, ct) => BacklogResult(await backlog.SetTaskContentAsync(
                new BacklogTaskReference(projectId, taskId), request.Content,
                request.ExpectedGraphRevision, request.ExpectedTaskRevision, ct, revalidate).ConfigureAwait(false)));

    /// <summary>Reads recorded plan promotions for a project, oldest first.</summary>
    private static Task<IResult> ReadProjectBacklogPromotionsAsync(
        string projectId, HttpContext context, ProjectsRunSelectionClient projects, BacklogOwnerStore backlog) =>
        ExecuteProjectBacklogOperationAsync(projectId, context, projects, write: false,
            async (_, ct) => Results.Ok(new
            {
                promotions = await backlog.ReadPromotionsAsync(projectId, ct).ConfigureAwait(false)
            }));

    /// <summary>Reads the origin of each promoted task. Tasks captured directly have no entry.</summary>
    private static Task<IResult> ReadProjectBacklogTaskOriginsAsync(
        string projectId, HttpContext context, ProjectsRunSelectionClient projects, BacklogOwnerStore backlog) =>
        ExecuteProjectBacklogOperationAsync(projectId, context, projects, write: false,
            async (_, ct) => Results.Ok(new
            {
                origins = await backlog.ReadTaskOriginsAsync(projectId, ct).ConfigureAwait(false)
            }));

    /// <summary>
    /// Promotes the run's current confirmed work plan into backlog tasks and records each task's origin.
    /// </summary>
    private static Task<IResult> PromoteBacklogWorkPlanAsync(
        string projectId,
        string runId,
        PromoteBacklogWorkPlanRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (actor, selection, revalidate, ct) =>
            {
                if (request is null || string.IsNullOrWhiteSpace(request.RootSessionId) ||
                    string.IsNullOrWhiteSpace(request.WorkPlanId) ||
                    request.ExpectedDecisionStateVersion < 1 || request.ExpectedExecutionFence < 1)
                    throw new CoordinationException("backlog_promotion_invalid", StatusCodes.Status400BadRequest);
                var identity = new SessionIdentity(projectId, runId, request.RootSessionId);
                var decision = await decisions.ReadCurrentAsync(actor, identity, selection, ct)
                    .ConfigureAwait(false);
                if (decision.StateVersion != request.ExpectedDecisionStateVersion ||
                    decision.State.Fence != request.ExpectedExecutionFence ||
                    !decision.State.CanDispatch ||
                    decision.State.ConfirmedWorkPlan is not { } confirmedPlan ||
                    !string.Equals(confirmedPlan.Plan.Id, request.WorkPlanId, StringComparison.Ordinal))
                    throw new CoordinationException("backlog_promotion_stale", StatusCodes.Status409Conflict);
                var selectionContext = await runSelectionContexts.ReadAsync(
                    selection.Selection, decision.State.Fence, ct).ConfigureAwait(false);
                if (selectionContext is null && RequiresProviderBinding(decision.State, confirmedPlan.Plan))
                    throw new CoordinationException(
                        "backlog_promotion_selection_unavailable", StatusCodes.Status409Conflict);
                selectionContext ??= CoordinatorWorkflowCatalog.CreateRunSelectionContext(
                    selection.Selection.Snapshot);
                var result = await backlog.PromoteWorkPlanAsync(
                    actor,
                    selection,
                    request.RootSessionId,
                    request.WorkPlanId,
                    request.ExpectedDecisionStateVersion,
                    request.ExpectedExecutionFence,
                    request.ExpectedGraphRevision,
                    request.IdempotencyKey,
                    selectionContext,
                    decisions,
                    revalidate,
                    ct).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>Rechecks actor, membership, bindings and role revisions after waits and before commit.</summary>
    private static Task<IResult> ExecuteProjectBacklogOperationAsync(
        string projectId, HttpContext context, ProjectsRunSelectionClient projects, bool write,
        Func<Func<CancellationToken, Task>, CancellationToken, Task<IResult>> operation) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var authority = await projects.ReadBacklogProjectAuthorityAsync(
                context, projectId, write, context.RequestAborted).ConfigureAwait(false);
            async Task Revalidate(CancellationToken ct)
            {
                var current = await projects.ReadBacklogProjectAuthorityAsync(context, projectId, write, ct)
                    .ConfigureAwait(false);
                if (!BacklogProjectAuthorityIsUnchanged(authority, current, projectId, write))
                    throw new CoordinationException("backlog_project_authority_changed", StatusCodes.Status409Conflict);
            }
            var result = await operation(Revalidate, context.RequestAborted).ConfigureAwait(false);
            if (!write)
                await Revalidate(context.RequestAborted).ConfigureAwait(false);
            return result;
        }, context.RequestAborted);

    /// <summary>Compares only authority that governs this operation, independent of unrelated grant order.</summary>
    internal static bool BacklogProjectAuthorityIsUnchanged(
        ProjectsAuthorizationContext before, ProjectsAuthorizationContext after, string projectId, bool write)
    {
        static IEnumerable<(string, string, long)> Grants(
            ProjectsAuthorizationContext authority, string project, string permission) =>
            authority.EffectiveAuthority.Where(item =>
                    item.ResourceType == "project" && item.ResourceId == project ||
                    item.ResourceType == "tenant" && item.ResourceId == authority.TenantId)
                .SelectMany(item => item.Permissions.Where(grant => grant.Permission == permission)
                    .Select(grant => (item.ResourceType, item.ResourceId, grant.RoleRevision)))
                .Distinct().Order();
        var permission = write ? "writeProjects" : "readProjects";
        return before.ContractVersion == after.ContractVersion && before.Issuer == after.Issuer &&
            before.ActorId == after.ActorId && before.TenantId == after.TenantId &&
            before.MembershipRevision == after.MembershipRevision && before.BoundProjectId == after.BoundProjectId &&
            before.BoundRunId == after.BoundRunId &&
            Grants(before, projectId, permission).SequenceEqual(Grants(after, projectId, permission));
    }

    /// <summary>
    /// Reads the project backlog after validating the caller and accepted run selection.
    /// </summary>
    private static Task<IResult> ReadBacklogAsync(
        string projectId,
        string runId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, cancellationToken) =>
            {
                var graph = await backlog.ReadGraphAsync(projectId, cancellationToken).ConfigureAwait(false);
                return BacklogResult(graph);
            });

    /// <summary>
    /// Adds a task using the caller's expected graph revision.
    /// </summary>
    private static Task<IResult> AddBacklogTaskAsync(
        string projectId,
        string runId,
        AddBacklogTaskRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, ct) =>
            {
                var result = await backlog.AddTaskAsync(
                    new BacklogTaskReference(projectId, request.TaskId),
                    request.ExpectedGraphRevision,
                    ct).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>
    /// Changes a task state using both graph and task revision checks.
    /// </summary>
    private static Task<IResult> SetBacklogTaskStateAsync(
        string projectId,
        string runId,
        string taskId,
        SetBacklogTaskStateRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, cancellationToken) =>
            {
                var result = await backlog.SetTaskStateAsync(
                    new BacklogTaskReference(projectId, taskId),
                    request.State,
                    request.ExpectedGraphRevision,
                    request.ExpectedTaskRevision,
                    cancellationToken).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>
    /// Archives a task only when its graph and task revisions still match.
    /// </summary>
    private static Task<IResult> ArchiveBacklogTaskAsync(
        string projectId,
        string runId,
        string taskId,
        BacklogTaskRevisionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, cancellationToken) =>
            {
                var result = await backlog.ArchiveTaskAsync(
                    new BacklogTaskReference(projectId, taskId),
                    request.ExpectedGraphRevision,
                    request.ExpectedTaskRevision,
                    cancellationToken).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>
    /// Adds a prerequisite edge through the owner store's graph validation.
    /// </summary>
    private static Task<IResult> AddBacklogDependencyAsync(
        string projectId,
        string runId,
        string taskId,
        string prerequisiteTaskId,
        BacklogGraphRevisionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, cancellationToken) =>
            {
                var result = await backlog.AddDependencyAsync(
                    new BacklogTaskReference(projectId, taskId),
                    new BacklogTaskReference(projectId, prerequisiteTaskId),
                    request.ExpectedGraphRevision,
                    cancellationToken).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>
    /// Removes a prerequisite edge through the owner store's graph validation.
    /// </summary>
    private static Task<IResult> RemoveBacklogDependencyAsync(
        string projectId,
        string runId,
        string taskId,
        string prerequisiteTaskId,
        [FromBody] BacklogGraphRevisionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (_, _, _, cancellationToken) =>
            {
                var result = await backlog.RemoveDependencyAsync(
                    new BacklogTaskReference(projectId, taskId),
                    new BacklogTaskReference(projectId, prerequisiteTaskId),
                    request.ExpectedGraphRevision,
                    cancellationToken).ConfigureAwait(false);
                return BacklogResult(result);
            });

    /// <summary>
    /// Claims a ready task, then ensures its accepted root session exists in the Events owner.
    /// </summary>
    /// <remarks>The prerequisite reader is required; without it the request fails closed with service unavailable.</remarks>
    private static Task<IResult> ClaimBacklogTaskAsync(
        string projectId,
        string runId,
        string taskId,
        ClaimBacklogTaskRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOwnerStore backlog,
        CoordinationOwnerStore coordination,
        CoordinatorDecisionOwnerStore decisions,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteBacklogOperationAsync(projectId, runId, context, options, projects,
            async (actor, selection, revalidate, ct) =>
            {
                var evidenceReader = context.RequestServices
                    .GetService<IBacklogPrerequisiteEvidenceReader>()
                    ?? throw new CoordinationException(
                        "backlog_evidence_unavailable", StatusCodes.Status503ServiceUnavailable);
                var admission = context.RequestServices.GetService<RuntimeRunAdmissionClient>();
                var result = await backlog.ClaimTaskAsync(
                    actor,
                    selection,
                    new BacklogTaskReference(projectId, taskId),
                    request.ExpectedGraphRevision,
                    request.ExpectedTaskRevision,
                    request.IdempotencyKey,
                    evidenceReader,
                    coordination,
                    decisions,
                    revalidate,
                    ct, admission is null ? null : token => admission.ReadAsync(context, selection, token))
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                    return BacklogResult(result);

                await events.EnsureSessionAsync(
                    context,
                    new SessionIdentity(projectId, runId, result.Value!.RootSessionId),
                    ct).ConfigureAwait(false);
                return Results.Ok(result.Value);
            });

    /// <summary>
    /// Runs a backlog operation with owner authentication and authority checks before and after the operation.
    /// </summary>
    private static Task<IResult> ExecuteBacklogOperationAsync(
        string projectId,
        string runId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        BacklogOperation operation) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, context.RequestAborted).ConfigureAwait(false);
            Task Revalidate(CancellationToken cancellationToken) =>
                RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken);

            await Revalidate(context.RequestAborted).ConfigureAwait(false);
            var result = await operation(actor, selection, Revalidate, context.RequestAborted)
                .ConfigureAwait(false);
            await Revalidate(context.RequestAborted).ConfigureAwait(false);
            return result;
        }, context.RequestAborted);

    /// <summary>
    /// Maps successful core results to HTTP 200 and validation, missing-task, or conflict issues to their HTTP status.
    /// </summary>
    private static IResult BacklogResult<T>(BacklogCoreResult<T> result) where T : class
    {
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        var statusCode = result.Issues.Any(issue => issue.Code == BacklogIssueCode.MissingTask)
            ? StatusCodes.Status404NotFound
            : result.Issues.Any(issue => issue.Code is
                BacklogIssueCode.InvalidProjectId or
                BacklogIssueCode.InvalidTaskReference or
                BacklogIssueCode.InvalidGraphRevision or
                BacklogIssueCode.InvalidTaskRevision or
                BacklogIssueCode.InvalidTaskState or
                BacklogIssueCode.InvalidTaskContent or
                BacklogIssueCode.InvalidTaskCollection or
                BacklogIssueCode.InvalidDependencyCollection or
                BacklogIssueCode.CrossProjectDependency or
                BacklogIssueCode.SelfDependency or
                BacklogIssueCode.InvalidPrerequisiteCollection or
                BacklogIssueCode.InvalidPrerequisiteSnapshot)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
        return Results.Json(new { issues = result.Issues }, statusCode: statusCode);
    }

    /// <summary>
    /// Carries the authenticated actor, accepted run, and authority revalidation callback to a backlog operation.
    /// </summary>
    private delegate Task<IResult> BacklogOperation(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        Func<CancellationToken, Task> revalidate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request body for adding a task at a specific backlog graph revision.
/// </summary>
public sealed record AddBacklogTaskRequest(string TaskId, long ExpectedGraphRevision);

/// <summary>Project capture request with bounded task content and graph revision.</summary>
public sealed record CaptureBacklogTaskRequest(string TaskId, BacklogTaskContent Content, long ExpectedGraphRevision);

/// <summary>Full content replacement guarded by both project graph and task revisions.</summary>
public sealed record EditBacklogTaskContentRequest(
    BacklogTaskContent Content, long ExpectedGraphRevision, long ExpectedTaskRevision);

/// <summary>
/// Request body for changing a task state with graph and task revision guards.
/// </summary>
public sealed record SetBacklogTaskStateRequest(
    BacklogTaskState State,
    long ExpectedGraphRevision,
    long ExpectedTaskRevision);

/// <summary>
/// Request body for an archive operation guarded by graph and task revisions.
/// </summary>
public sealed record BacklogTaskRevisionRequest(
    long ExpectedGraphRevision,
    long ExpectedTaskRevision);

/// <summary>
/// Request body for changing dependency edges at an expected graph revision.
/// </summary>
public sealed record BacklogGraphRevisionRequest(long ExpectedGraphRevision);

/// <summary>
/// Request body for promoting a run's confirmed work plan into backlog tasks.
/// </summary>
/// <remarks>Retry with the same idempotency key and values to read the stored promotion.</remarks>
public sealed record PromoteBacklogWorkPlanRequest(
    string RootSessionId,
    string WorkPlanId,
    long ExpectedDecisionStateVersion,
    long ExpectedExecutionFence,
    long ExpectedGraphRevision,
    string IdempotencyKey);

/// <summary>
/// Request body for claiming a task with optimistic revisions and a retry-safe idempotency key.
/// </summary>
public sealed record ClaimBacklogTaskRequest(
    long ExpectedGraphRevision,
    long ExpectedTaskRevision,
    string IdempotencyKey);
