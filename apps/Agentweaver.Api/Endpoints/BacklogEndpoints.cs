using Agentweaver.Api.Contracts;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Project-scoped backlog + Kanban board endpoints (Feature 009). Every mutate handler verifies the
/// task belongs to the route project by passing the route projectId into the project-scoped store
/// method (a task can never be mutated through another project's route). Bearer auth via
/// <see cref="ApiKeyAuthMiddleware.GetCaller"/>; snake_case DTOs.
/// </summary>
public static class BacklogEndpoints
{
    public static void MapBacklogEndpoints(this WebApplication app)
    {
        // POST /api/projects/{projectId}/backlog/tasks — capture (FR-001/002/003/016)
        app.MapPost("/api/projects/{projectId}/backlog/tasks", async (
            HttpContext httpContext,
            string projectId,
            CaptureBacklogTaskRequest request,
            IProjectStore projectStore,
            IBacklogTaskStore backlogStore,
            IGitHubTokenStore tokenStore,
            IGitHubTokenScopeProvider scopeProvider,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "title is required." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

            // Persist the actual signed-in GitHub login as who captured the task (not the API-key
            // Auth:User config value). Mirrors the signed-in guard in GET /api/auth/github: resolve the
            // caller's scope and use its identity Login when signed in; fall back to caller.User
            // otherwise (signed out / never signed in).
            var scope = scopeProvider.Resolve(caller.User);
            var entry = await tokenStore.GetAsync(scope, ct);
            var login = entry.Status == GitHubTokenStatus.SignedIn
                ? (await tokenStore.GetIdentityAsync(scope, ct))?.Login
                : null;
            var capturedBy = login ?? caller.User;

            var existing = await backlogStore.ListByProjectAsync(pid, ct);
            var orderKey = KeyForIndex(BucketKeys(existing, BacklogTaskState.Backlog), targetIndex: null, movingTaskId: null);

            var task = new BacklogTask
            {
                Id = BacklogTaskId.New(),
                ProjectId = pid,
                Title = request.Title!.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
                State = BacklogTaskState.Backlog,
                OrderKey = orderKey,
                CapturedBy = capturedBy,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await backlogStore.InsertAsync(task, ct);
            return Results.Created(
                $"/api/projects/{projectId}/backlog/tasks/{task.Id}", MapTask(task));
        });

        // PATCH /api/projects/{projectId}/backlog/tasks/{taskId} — edit (FR-005)
        app.MapPatch("/api/projects/{projectId}/backlog/tasks/{taskId}", async (
            HttpContext httpContext,
            string projectId,
            string taskId,
            EditBacklogTaskRequest request,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "title is required." });

            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;
            var updated = await backlogStore.UpdateContentAsync(pid, tid, request.Title!.Trim(), description, ct);
            if (!updated) return Results.NotFound();

            var task = await backlogStore.GetAsync(pid, tid, ct);
            return task is null ? Results.NotFound() : Results.Ok(MapTask(task));
        });

        // DELETE /api/projects/{projectId}/backlog/tasks/{taskId} — delete (FR-005)
        app.MapDelete("/api/projects/{projectId}/backlog/tasks/{taskId}", async (
            string projectId,
            string taskId,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });

            var deleted = await backlogStore.TryDeleteAsync(pid, tid, ct);
            if (deleted) return Results.NoContent();

            // Distinguish "claimed (not deletable)" from "not found in project".
            var task = await backlogStore.GetAsync(pid, tid, ct);
            if (task is null || task.ArchivedAt is not null) return Results.NotFound();
            return Results.Conflict(new { error = "task_claimed" });
        });

        // POST /api/projects/{projectId}/backlog/tasks/{taskId}/ready — Backlog -> Ready (FR-006/010)
        app.MapPost("/api/projects/{projectId}/backlog/tasks/{taskId}/ready", async (
            string projectId,
            string taskId,
            MoveBacklogTaskRequest? request,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });

            var task = await backlogStore.GetAsync(pid, tid, ct);
            if (task is null) return Results.NotFound();
            if (task.State != BacklogTaskState.Backlog)
                return Results.Conflict(new { error = "not_in_backlog" });

            var existing = await backlogStore.ListByProjectAsync(pid, ct);
            var newKey = KeyForIndex(BucketKeys(existing, BacklogTaskState.Ready), request?.TargetIndex, movingTaskId: null);

            try
            {
                var moved = await backlogStore.TryMoveToReadyAsync(pid, tid, newKey, DateTimeOffset.UtcNow, ct);
                if (!moved) return Results.Conflict(new { error = "not_in_backlog" });
            }
            catch (OrderKeyConflictException)
            {
                return Results.Conflict(new { error = "order_conflict" });
            }

            var updated = await backlogStore.GetAsync(pid, tid, ct);
            return updated is null ? Results.NotFound() : Results.Ok(MapTask(updated));
        });

        // POST /api/projects/{projectId}/backlog/ready-all — bulk Backlog -> Ready (FR-006/010)
        app.MapPost("/api/projects/{projectId}/backlog/ready-all", async (
            string projectId,
            IProjectStore projectStore,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            var moved = await backlogStore.MoveAllBacklogToReadyAsync(pid, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new ReadyAllResponse { Moved = moved });
        });

        // POST /api/projects/{projectId}/backlog/tasks/{taskId}/backlog — Ready -> Backlog (FR-007/018)
        app.MapPost("/api/projects/{projectId}/backlog/tasks/{taskId}/backlog", async (
            string projectId,
            string taskId,
            MoveBacklogTaskRequest? request,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });

            var task = await backlogStore.GetAsync(pid, tid, ct);
            if (task is null) return Results.NotFound();
            if (task.State == BacklogTaskState.Claimed)
                return Results.Conflict(new { error = "task_already_claimed" });

            var existing = await backlogStore.ListByProjectAsync(pid, ct);
            var newKey = KeyForIndex(BucketKeys(existing, BacklogTaskState.Backlog), request?.TargetIndex, movingTaskId: null);

            try
            {
                var moved = await backlogStore.TryMoveToBacklogAsync(pid, tid, newKey, ct);
                if (!moved)
                {
                    // Lost the race: either claimed in between, or no longer in Ready.
                    var current = await backlogStore.GetAsync(pid, tid, ct);
                    if (current is null) return Results.NotFound();
                    return Results.Conflict(new { error = "task_already_claimed" });
                }
            }
            catch (OrderKeyConflictException)
            {
                return Results.Conflict(new { error = "order_conflict" });
            }

            var updated = await backlogStore.GetAsync(pid, tid, ct);
            return updated is null ? Results.NotFound() : Results.Ok(MapTask(updated));
        });

        // POST /api/projects/{projectId}/backlog/tasks/{taskId}/reorder — within-bucket reorder (FR-018a)
        app.MapPost("/api/projects/{projectId}/backlog/tasks/{taskId}/reorder", async (
            string projectId,
            string taskId,
            ReorderBacklogTaskRequest request,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });

            var task = await backlogStore.GetAsync(pid, tid, ct);
            if (task is null) return Results.NotFound();
            if (task.State == BacklogTaskState.Claimed)
                return Results.Conflict(new { error = "task_claimed" });

            var existing = await backlogStore.ListByProjectAsync(pid, ct);
            var newKey = KeyForIndex(BucketKeys(existing, task.State), request.TargetIndex, movingTaskId: tid.ToString());

            try
            {
                var reordered = await backlogStore.TryReorderAsync(pid, tid, task.State, newKey, ct);
                if (!reordered)
                {
                    var current = await backlogStore.GetAsync(pid, tid, ct);
                    if (current is null) return Results.NotFound();
                    return Results.Conflict(new { error = "task_claimed" });
                }
            }
            catch (OrderKeyConflictException)
            {
                return Results.Conflict(new { error = "order_conflict" });
            }

            var updated = await backlogStore.GetAsync(pid, tid, ct);
            return updated is null ? Results.NotFound() : Results.Ok(MapTask(updated));
        });

        // POST /api/projects/{projectId}/backlog/tasks/{taskId}/archive — remove task card off-board.
        app.MapPost("/api/projects/{projectId}/backlog/tasks/{taskId}/archive", async (
            string projectId,
            string taskId,
            IBacklogTaskStore backlogStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid) || !BacklogTaskId.TryParse(taskId, out var tid))
                return Results.BadRequest(new { error = "Invalid id." });

            var archived = await backlogStore.TryArchiveAsync(pid, tid, DateTimeOffset.UtcNow, ct);
            if (!archived) return Results.NotFound();

            var task = await backlogStore.GetAsync(pid, tid, ct);
            return task is null ? Results.NotFound() : Results.Ok(MapTask(task));
        });

        // GET /api/projects/{projectId}/board — full board (FR-013..016a/019)
        app.MapGet("/api/projects/{projectId}/board", async (
            string projectId,
            bool? include_terminal_history,
            IProjectStore projectStore,
            BoardProjectionService boardService,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            var board = await boardService.GetBoardAsync(pid, include_terminal_history ?? false, ct);
            return Results.Ok(board);
        });

        // GET /api/projects/{projectId}/workflow-stages — canonical run-buckets only
        app.MapGet("/api/projects/{projectId}/workflow-stages", async (
            string projectId,
            IProjectStore projectStore,
            WorkflowStageProjector projector,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            var stages = projector.GetStages();

            return Results.Ok(new WorkflowStagesResponse
            {
                Available = stages.Count > 0,
                Stages = stages.Select(s => new WorkflowStageDto { Id = s.Id, Label = s.Label }).ToList(),
            });
        });

        // GET /api/projects/{projectId}/backlog/settings (FR-008a)
        app.MapGet("/api/projects/{projectId}/backlog/settings", async (
            string projectId,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();
            return Results.Ok(MapSettings(project));
        });

        // PUT /api/projects/{projectId}/backlog/settings (FR-008a)
        app.MapPut("/api/projects/{projectId}/backlog/settings", async (
            string projectId,
            BacklogSettingsDto request,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (request.MaxReadyPerHeartbeat is < 1 or > 20)
                return Results.BadRequest(new { error = "max_ready_per_heartbeat must be between 1 and 20." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            await projectStore.UpdatePickupSettingsAsync(
                pid, request.MaxReadyPerHeartbeat, request.PickupAutopilot, request.PickupAutoApproveTools,
                DateTimeOffset.UtcNow, ct);

            var updated = await projectStore.GetAsync(pid, ct);
            return updated is null ? Results.NotFound() : Results.Ok(MapSettings(updated));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>The order_keys of a single bucket, ascending. The moving task (if any) is excluded so
    /// a reorder never computes a key against its own current position.</summary>
    private static List<string> BucketKeys(
        IReadOnlyList<BacklogTask> all, BacklogTaskState state, string? movingTaskId = null) =>
        all.Where(t => t.State == state && t.Id.ToString() != movingTaskId)
           .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
           .Select(t => t.OrderKey)
           .ToList();

    private static List<string> BucketKeys(IReadOnlyList<BacklogTask> all, BacklogTaskState state) =>
        BucketKeys(all, state, movingTaskId: null);

    /// <summary>
    /// Computes a fresh order_key that places a task at <paramref name="targetIndex"/> within the
    /// ordered <paramref name="orderedKeys"/> of the destination bucket (excluding the moving task).
    /// A null index appends to the bottom (lowest priority). 0 = top (highest priority).
    /// </summary>
    private static string KeyForIndex(List<string> orderedKeys, int? targetIndex, string? movingTaskId)
    {
        _ = movingTaskId;   // exclusion already applied by the caller via BucketKeys.
        var count = orderedKeys.Count;
        var index = targetIndex ?? count;
        if (index < 0) index = 0;
        if (index > count) index = count;

        var lo = index > 0 ? orderedKeys[index - 1] : null;
        var hi = index < count ? orderedKeys[index] : null;
        return OrderKey.Between(lo, hi);
    }

    private static BacklogTaskDto MapTask(BacklogTask t) => new()
    {
        TaskId = t.Id.ToString(),
        ProjectId = t.ProjectId.ToString(),
        Title = t.Title,
        Description = t.Description,
        State = t.State.ToApiString(),
        OrderKey = t.OrderKey,
        CapturedBy = t.CapturedBy,
        CreatedAt = t.CreatedAt,
        CommittedAt = t.CommittedAt,
        ClaimedAt = t.ClaimedAt,
        RunId = t.RunId?.ToString(),
        ArchivedAt = t.ArchivedAt,
    };

    private static BacklogSettingsDto MapSettings(Project p) => new()
    {
        MaxReadyPerHeartbeat = p.MaxReadyPerHeartbeat,
        PickupAutopilot = p.PickupAutopilot,
        PickupAutoApproveTools = p.PickupAutoApproveTools,
    };
}
