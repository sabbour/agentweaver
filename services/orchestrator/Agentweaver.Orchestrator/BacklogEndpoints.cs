using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
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

    private static Task<IResult> RemoveBacklogDependencyAsync(
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
                var result = await backlog.RemoveDependencyAsync(
                    new BacklogTaskReference(projectId, taskId),
                    new BacklogTaskReference(projectId, prerequisiteTaskId),
                    request.ExpectedGraphRevision,
                    cancellationToken).ConfigureAwait(false);
                return BacklogResult(result);
            });

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
                    ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                    return BacklogResult(result);

                await events.EnsureSessionAsync(
                    context,
                    new SessionIdentity(projectId, runId, result.Value!.RootSessionId),
                    ct).ConfigureAwait(false);
                return Results.Ok(result.Value);
            });

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

    private delegate Task<IResult> BacklogOperation(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        Func<CancellationToken, Task> revalidate,
        CancellationToken cancellationToken);
}

public sealed record AddBacklogTaskRequest(string TaskId, long ExpectedGraphRevision);

public sealed record SetBacklogTaskStateRequest(
    BacklogTaskState State,
    long ExpectedGraphRevision,
    long ExpectedTaskRevision);

public sealed record BacklogTaskRevisionRequest(
    long ExpectedGraphRevision,
    long ExpectedTaskRevision);

public sealed record BacklogGraphRevisionRequest(long ExpectedGraphRevision);

public sealed record ClaimBacklogTaskRequest(
    long ExpectedGraphRevision,
    long ExpectedTaskRevision,
    string IdempotencyKey);
