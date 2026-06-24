using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Project-scoped workflow-definition endpoints (Feature 010, FR-039/040). Lists the project's
/// discovered workflows with their validation status, re-reads <c>.agentweaver/workflows/</c> on an
/// explicit Sync, and returns a single workflow's effective definition. All discovery, validation, and
/// resolution is server-side (Principles III, IV); the clients only render the results. Owner-scoped
/// like the other project endpoints: 404 when the project is missing, 403 when the caller is not the
/// project owner.
/// </summary>
public static class WorkflowDefinitionEndpoints
{
    public static void MapWorkflowDefinitionEndpoints(this WebApplication app)
    {
        // GET /api/projects/{projectId}/workflows — list discovered workflows + validation status.
        app.MapGet("/api/projects/{projectId}/workflows", async (
            HttpContext httpContext,
            string projectId,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var set = registry.GetOrLoad(project!);
            return Results.Ok(BuildListResponse(project!, set));
        });

        // POST /api/projects/{projectId}/workflows/sync — re-read from disk, refresh the loaded set.
        app.MapPost("/api/projects/{projectId}/workflows/sync", async (
            HttpContext httpContext,
            string projectId,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var set = registry.Sync(project!);
            return Results.Ok(BuildListResponse(project!, set));
        });

        // GET /api/projects/{projectId}/workflows/{workflowId} — single workflow definition.
        app.MapGet("/api/projects/{projectId}/workflows/{workflowId}", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var result = registry.Get(project!, workflowId);
            if (result?.Definition is null) return Results.NotFound();

            return Results.Ok(WorkflowDtoMapper.ToDetail(result, EffectiveDefaultId(project!)));
        });

        // PUT /api/projects/{projectId}/workflows/default — set the project's default workflow (FR-041).
        // Body { workflow_id: string|null }. A null/omitted workflow_id clears back to the built-in
        // default. A non-null id must resolve to a valid workflow in the project's registry first.
        app.MapPut("/api/projects/{projectId}/workflows/default", async (
            HttpContext httpContext,
            string projectId,
            SetWorkflowSelectionRequest request,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var workflowId = Normalize(request.WorkflowId);
            if (workflowId is not null && registry.Get(project!, workflowId)?.Definition is null)
                return Results.BadRequest(new { error = "unknown_workflow_id" });

            await projectStore.UpdateDefaultWorkflowAsync(project!.Id, workflowId, DateTimeOffset.UtcNow, ct);

            var updated = await projectStore.GetAsync(project.Id, ct);
            if (updated is null) return Results.NotFound();
            return Results.Ok(BuildListResponse(updated, registry.GetOrLoad(updated)));
        });

        // PUT /api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override — set a per-task
        // workflow override (FR-042). Body { workflow_id: string|null }. A null/omitted workflow_id
        // clears the override. A non-null id must resolve in the project's registry. The override may
        // only be changed while the task is unclaimed (FR-042 gate): a claimed task yields 409.
        app.MapPut("/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override", async (
            HttpContext httpContext,
            string projectId,
            string taskId,
            SetWorkflowSelectionRequest request,
            IProjectStore projectStore,
            IBacklogTaskStore backlogStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (!BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid task id." });

            var workflowId = Normalize(request.WorkflowId);
            if (workflowId is not null && registry.Get(project!, workflowId)?.Definition is null)
                return Results.BadRequest(new { error = "unknown_workflow_id" });

            var task = await backlogStore.GetAsync(project!.Id, tid, ct);
            if (task is null) return Results.NotFound();
            if (task.State == BacklogTaskState.Claimed)
                return Results.Conflict(new { error = "task_claimed" });

            var applied = await backlogStore.UpdateWorkflowOverrideAsync(project.Id, tid, workflowId, ct);
            if (!applied)
            {
                // Lost the race: the task was claimed (or removed) between the read and the write.
                var current = await backlogStore.GetAsync(project.Id, tid, ct);
                if (current is null) return Results.NotFound();
                return Results.Conflict(new { error = "task_claimed" });
            }

            var updated = await backlogStore.GetAsync(project.Id, tid, ct);
            if (updated is null) return Results.NotFound();
            return Results.Ok(new WorkflowOverrideResponse
            {
                TaskId = updated.Id.ToString(),
                WorkflowOverrideId = updated.WorkflowOverrideId,
            });
        });
    }

    /// <summary>Normalizes an incoming workflow id: trims and treats empty/whitespace as null (clear).</summary>
    private static string? Normalize(string? workflowId) =>
        string.IsNullOrWhiteSpace(workflowId) ? null : workflowId.Trim();

    private static WorkflowListResponse BuildListResponse(Project project, ProjectWorkflowSet set)
    {
        var effectiveDefault = EffectiveDefaultId(project);
        return new WorkflowListResponse
        {
            DefaultWorkflowId = effectiveDefault,
            Workflows = set.Results.Select(r => WorkflowDtoMapper.ToSummary(r, effectiveDefault)).ToList(),
        };
    }

    /// <summary>The project's effective default workflow id: its configured default (FR-041) or the
    /// built-in default when none is set.</summary>
    private static string EffectiveDefaultId(Project project) =>
        string.IsNullOrWhiteSpace(project.DefaultWorkflowId)
            ? BuiltInWorkflows.DefaultWorkflowId
            : project.DefaultWorkflowId!;

    /// <summary>Resolves the route project and enforces owner authorization. Returns the project on
    /// success, or an IResult (400/404/403) describing the failure.</summary>
    private static async Task<(Project? Project, IResult? Error)> ResolveOwnedProjectAsync(
        HttpContext httpContext, string projectId, IProjectStore projectStore, CancellationToken ct)
    {
        if (!ProjectId.TryParse(projectId, out var pid))
            return (null, Results.BadRequest(new { error = "Invalid project id." }));

        var project = await projectStore.GetAsync(pid, ct);
        if (project is null) return (null, Results.NotFound());

        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
        if (!caller.Owns(project.Owner))
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));

        return (project, null);
    }
}
