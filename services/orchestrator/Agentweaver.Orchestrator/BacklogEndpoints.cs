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
        return app;
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
/// Request body for claiming a task with optimistic revisions and a retry-safe idempotency key.
/// </summary>
public sealed record ClaimBacklogTaskRequest(
    long ExpectedGraphRevision,
    long ExpectedTaskRevision,
    string IdempotencyKey);
