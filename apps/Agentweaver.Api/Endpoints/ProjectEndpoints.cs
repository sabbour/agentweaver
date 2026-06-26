using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;

namespace Agentweaver.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
// POST /api/projects — create blank or from GitHub
app.MapPost("/api/projects", async (
    HttpContext httpContext,
    CreateProjectRequest request,
    ProjectService projectService,
    BlueprintService blueprintService,
    SqliteRunStore runStore,
    RunWorkflowRegistry workflowRegistry,
    IProjectStore projectStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "name is required." });

    if (string.IsNullOrWhiteSpace(request.Origin) ||
        (request.Origin != "blank" && request.Origin != "github"))
        return Results.BadRequest(new { error = "origin must be 'blank' or 'github'." });

    if (request.Origin == "github" && string.IsNullOrWhiteSpace(request.SourceRepository))
        return Results.BadRequest(new { error = "source_repository is required when origin is 'github'." });

    if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        return Results.BadRequest(new { error = "working_directory is required." });

    // Resolve and validate any selected blueprint BEFORE creating the project so an invalid blueprint
    // (e.g. a roster role that is neither in the catalog nor supplied as a new role) is rejected
    // without leaving an orphaned project (Feature 012).
    if (!string.IsNullOrWhiteSpace(request.BlueprintId) && request.Blueprint is not null)
        return Results.BadRequest(new { error = "Provide either blueprint_id or an inline blueprint, not both." });

    Agentweaver.Squad.Model.Blueprint? blueprintToApply = null;

    if (!string.IsNullOrWhiteSpace(request.BlueprintId))
    {
        blueprintToApply = blueprintService.GetPredefinedById(request.BlueprintId!);
        if (blueprintToApply is null)
            return Results.BadRequest(new { error = $"No predefined blueprint with id '{request.BlueprintId}'." });
    }
    else if (request.Blueprint is not null)
    {
        blueprintToApply = request.Blueprint.ToModel();
    }

    if (blueprintToApply is not null)
    {
        // When a generated workflow YAML is included, parse it to get the id so validation can treat
        // it as a known workflow (it hasn't been materialized to disk yet — FR-063).
        IReadOnlySet<string>? extraKnownWorkflowIds = null;
        if (!string.IsNullOrWhiteSpace(request.GeneratedWorkflowYaml))
        {
            var genWf = WorkflowDefinitionLoader.Load(request.GeneratedWorkflowYaml, "generated");
            if (genWf.IsValid && genWf.Definition is not null)
                extraKnownWorkflowIds = new HashSet<string>([genWf.Definition.Id], StringComparer.Ordinal);
        }

        var validation = blueprintService.Validate(
            blueprintToApply,
            BlueprintService.ValidationProject(request.WorkingDirectory),
            extraKnownWorkflowIds);
        if (!validation.Valid)
            return Results.BadRequest(new { error = "invalid_blueprint", details = validation.Errors });
    }

    // Track blueprint provenance for SetSourceBlueprint call after creation
    string? blueprintSourceId = null;
    string? blueprintSourceType = null;
    if (!string.IsNullOrWhiteSpace(request.BlueprintId))
    {
        blueprintSourceId = request.BlueprintId;
        blueprintSourceType = "predefined";
    }
    else if (request.Blueprint is not null)
    {
        blueprintSourceId = "inline";
        blueprintSourceType = "inline";
    }

    try
    {
        Agentweaver.Domain.Project project;
        if (request.Origin == "blank")
        {
            project = await projectService.CreateBlankAsync(
                request.Name!, request.WorkingDirectory!,
                request.DefaultProvider, request.DefaultModelGitHubCopilot,
                request.DefaultModelMicrosoftFoundry, caller.User, ct);
        }
        else
        {
            project = await projectService.CreateFromGitHubAsync(
                request.Name!, request.SourceRepository!, request.WorkingDirectory!,
                request.DefaultProvider, request.DefaultModelGitHubCopilot,
                request.DefaultModelMicrosoftFoundry, caller.User, ct);
        }

        if (blueprintToApply is not null)
        {
            try
            {
                var applyResult = await blueprintService.ApplyAsync(
                    project.Id.ToString(), blueprintToApply,
                    request.GeneratedWorkflowYaml, ct);
                if (!applyResult.Valid)
                {
                    await projectService.RollbackCreationAsync(project.Id, runStore, workflowRegistry, ct);
                    return Results.BadRequest(new { error = "invalid_blueprint", details = applyResult.Errors });
                }

                // Record blueprint provenance inside the creation transaction boundary so a provenance
                // write failure rolls back the project and all generated files.
                var pid = ProjectId.Parse(project.Id.ToString());
                await projectStore.UpdateSourceBlueprintAsync(
                    pid, blueprintSourceId, blueprintSourceType, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception blueprintEx)
            {
                // Rollback: blueprint application failed — delete the orphaned project
                logger.LogError(blueprintEx,
                    "Blueprint application failed for project {ProjectId}; rolling back project creation",
                    project.Id);
                try
                {
                    await projectService.RollbackCreationAsync(project.Id, runStore, workflowRegistry, ct);
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx,
                        "Rollback delete failed for orphaned project {ProjectId}", project.Id);
                }
                throw;
            }

            // Re-read so the response reflects the workflow/review/sandbox defaults the blueprint set.
            var view = await projectService.GetViewAsync(project.Id, ct);
            if (view is not null)
                return Results.Created($"/api/projects/{project.Id}", MapProject(view.Project, view.Available));
        }

        return Results.Created($"/api/projects/{project.Id}", MapProject(project, available: true));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (WorkspaceUnavailableException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to create project");
        return Results.Problem(
            $"Failed to create the project. {ex.GetType().Name}: {ex.Message}",
            statusCode: 500);
    }
});

// GET /api/server/info — public server metadata (no auth required)
app.MapGet("/api/server/info", () => Results.Ok(new
{
    data_directory = AppPaths.DataDirectory,
})).AllowAnonymous();

// GET /api/projects — list all projects
app.MapGet("/api/projects", async (
    HttpContext httpContext,
    ProjectService projectService,
    CancellationToken ct) =>
{
    var views = await projectService.ListViewsAsync(ct);
    return Results.Ok(views
        .Where(v => IsProjectOwner(httpContext, v.Project))
        .Select(v => MapProject(v.Project, v.Available)));
});

// GET /api/projects/{id} — get a single project
app.MapGet("/api/projects/{id}", async (
    HttpContext httpContext,
    string id,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, view.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(MapProject(view.Project, view.Available));
});

// PATCH /api/projects/{id} — rename
app.MapMethods("/api/projects/{id}", ["PATCH"], async (
    HttpContext httpContext,
    string id,
    UpdateProjectNameRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "name is required." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, view.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    bool updated;
    try { updated = await projectService.RenameAsync(projectId, request.Name!, ct); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// PUT /api/projects/{id}/provider-settings — update provider defaults
app.MapPut("/api/projects/{id}/provider-settings", async (
    HttpContext httpContext,
    string id,
    UpdateProjectProviderSettingsRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, view.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    if (!IsAllowedModelId(request.DefaultModelGitHubCopilot) ||
        !IsAllowedModelId(request.DefaultModelMicrosoftFoundry))
        return Results.BadRequest(new { error = "model_id is not allowed." });

    bool updated;
    try
    {
        updated = await projectService.UpdateProviderSettingsAsync(
            projectId, request.DefaultProvider,
            request.DefaultModelGitHubCopilot, request.DefaultModelMicrosoftFoundry, ct);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// POST /api/projects/{id}/relink — relink to moved directory
app.MapPost("/api/projects/{id}/relink", async (
    HttpContext httpContext,
    string id,
    RelinkProjectRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        return Results.BadRequest(new { error = "working_directory is required." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, view.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    bool updated;
    try { updated = await projectService.RelinkAsync(projectId, request.WorkingDirectory!, ct); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// DELETE /api/projects/{id}?confirm=true — record-only delete
app.MapDelete("/api/projects/{id}", async (
    HttpContext httpContext,
    string id,
    ProjectService projectService,
    SqliteRunStore runStore,
    RunWorkflowRegistry workflowRegistry,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var confirm = httpContext.Request.Query["confirm"].FirstOrDefault();
    if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "confirm=true query parameter is required for delete." });

    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var deleteView = await projectService.GetViewAsync(projectId, ct);
    if (deleteView is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, deleteView.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    bool deleted;
    try
    {
        deleted = await projectService.DeleteAsync(projectId, runStore, workflowRegistry, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to delete project {ProjectId}", id);
        return Results.Problem("Failed to delete the project.", statusCode: 500);
    }
    return deleted ? Results.NoContent() : Results.NotFound();
});

// GET /api/projects/{id}/runs — list runs for a project
app.MapGet("/api/projects/{id}/runs", async (
    HttpContext httpContext,
    string id,
    string? agent,
    bool? terminal_only,
    bool? include_children,
    int? limit,
    IProjectStore projectStore,
    SqliteRunStore runStore,
    CoordinatorStatusReader coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var runs = await runStore.GetRunsByProjectAsync(projectId, includeChildren: include_children ?? false, ct: ct);
    if (!string.IsNullOrWhiteSpace(agent))
        runs = runs.Where(r => string.Equals(r.AgentName, agent, StringComparison.Ordinal)).ToList();
    if (terminal_only == true)
        runs = runs.Where(r => IsTerminalHistoryStatus(r.Status)).ToList();
    if (limit is > 0)
        runs = runs.Take(Math.Min(limit.Value, 100)).ToList();

    // For coordinator runs, surface the work-plan orchestration status so the list can render
    // "Dispatching" / "Awaiting assembly" / "Failed: <reason>" instead of the bare run status.
    var coordinatorRunIds = runs
        .Where(r => r.ParentRunId is null && string.Equals(r.AgentName, "Coordinator", StringComparison.Ordinal))
        .Select(r => r.Id.ToString())
        .ToList();
    var coordinatorStatuses = await coordinator.GetCoordinatorStatusesAsync(coordinatorRunIds, ct);

    return Results.Ok(runs.Select(r =>
    {
        var isCoordinator = r.ParentRunId is null && string.Equals(r.AgentName, "Coordinator", StringComparison.Ordinal);
        return new WorkflowRunSummary
        {
            WorkflowRunId = r.WorkflowRunId ?? r.Id.ToString(),
        ExecutionId   = r.Id.ToString(),
        Task          = r.Task,
        Status        = r.Status.ToApiString(),
        AgentName     = r.AgentName,
        ReviewedBy    = r.ReviewedBy,
        StartedAt     = r.StartedAt,
        EndedAt       = r.EndedAt,
        ModelId       = r.ModelId,
        Result        = r.Result,
        CoordinatorStatus = coordinatorStatuses.GetValueOrDefault(r.Id.ToString()),
        CoordinatorStatusReason = isCoordinator ? r.Result : null,
        ArchivedAt = r.ArchivedAt,
        };
    }));
});

static bool IsTerminalHistoryStatus(RunStatus status) =>
    status is RunStatus.Completed or RunStatus.Merged or RunStatus.AssembleReady
        or RunStatus.Declined or RunStatus.Failed or RunStatus.MergeFailed;

// GET /api/projects/{id}/runs/{workflowRunId} — get a single workflow run by its workflow_run_id
app.MapGet("/api/projects/{id}/runs/{workflowRunId}", async (
    HttpContext httpContext,
    string id,
    string workflowRunId,
    IProjectStore projectStore,
    SqliteRunStore runStore,
    CoordinatorStatusReader coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var run = await runStore.GetByWorkflowRunIdAsync(workflowRunId, ct);
    if (run is null) return Results.NotFound();

    // Guard against cross-project data leakage: ensure the run belongs to the requested project.
    if (run.ProjectId is null || !string.Equals(run.ProjectId.Value.ToString(), id, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    string? coordinatorStatus = null;
    var isCoordinatorRun = run.ParentRunId is null && string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal);
    if (isCoordinatorRun)
        coordinatorStatus = (await coordinator.GetCoordinatorStatusesAsync(new[] { run.Id.ToString() }, ct))
            .GetValueOrDefault(run.Id.ToString());

    return Results.Ok(new WorkflowRunSummary
    {
        WorkflowRunId = run.WorkflowRunId ?? run.Id.ToString(),
        ExecutionId   = run.Id.ToString(),
        Task          = run.Task,
        Status        = run.Status.ToApiString(),
        AgentName     = run.AgentName,
        ReviewedBy    = run.ReviewedBy,
        StartedAt     = run.StartedAt,
        EndedAt       = run.EndedAt,
        ModelId       = run.ModelId,
        Result        = run.Result,
        CoordinatorStatus = coordinatorStatus,
        CoordinatorStatusReason = isCoordinatorRun ? run.Result : null,
        ArchivedAt = run.ArchivedAt,
    });
});

// POST /api/projects/{id}/runs — start a run within a project
app.MapPost("/api/projects/{id}/runs", async (
    HttpContext httpContext,
    string id,
    CreateProjectRunRequest request,
    IProjectStore projectStore,
    IProjectWorkspaceProvider workspaceProvider,
    SqliteRunStore runStore,
    SqliteWorkflowRunStore workflowRunStore,
    RunStreamStore streamStore,
    RunOrchestrator orchestrator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.Task))
        return Results.BadRequest(new { error = "task is required." });

    if (!string.IsNullOrWhiteSpace(request.AgentName) && !AgentNameSlugRegex.IsMatch(request.AgentName))
        return Results.BadRequest(new { error = "agent_name must be a lowercase slug containing only a-z, 0-9, and '-'." });

    if (!IsAllowedModelId(request.ModelId))
        return Results.BadRequest(new { error = "model_id is not allowed." });

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    // Load project
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, project)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Reject if project is being deleted
    if (project.State == ProjectState.Deleting)
        return Results.Conflict(new { error = "project_deleting", message = "The project is being deleted and cannot accept new runs." });

    // Reject if workspace unavailable
    if (!workspaceProvider.IsAvailable(project.WorkingDirectory))
        return Results.Conflict(new { error = "workspace_unavailable", message = "The project workspace is not available. Use relink to reconnect the project." });

    // Resolve provider (explicit -> project default)
    ModelSource modelSource;
    if (!string.IsNullOrWhiteSpace(request.ModelSource))
    {
        try { modelSource = ModelSourceExtensions.FromApiString(request.ModelSource); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "model_source must be 'github-copilot' or 'microsoft-foundry'." }); }
    }
    else
    {
        modelSource = project.ProviderSettings.DefaultProvider;
    }

    // Resolve model id (explicit -> project default for the selected provider -> null)
    string? modelId = request.ModelId;
    if (string.IsNullOrWhiteSpace(modelId))
    {
        modelId = modelSource == ModelSource.GitHubCopilot
            ? project.ProviderSettings.GitHubCopilotModel
            : project.ProviderSettings.MicrosoftFoundryModel;
    }

    // Base branch (explicit -> project default)
    var baseBranch = string.IsNullOrWhiteSpace(request.BaseBranch)
        ? project.DefaultBranch
        : request.BaseBranch;

    // Block built-in system agents from being run directly
    if (!string.IsNullOrWhiteSpace(request.AgentName) &&
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Scribe", "Ralph", "Rai" }
            .Contains(request.AgentName))
        return Results.BadRequest(new { error = $"'{request.AgentName}' is a built-in system agent and cannot be run directly." });

    if (!string.IsNullOrWhiteSpace(request.AgentName))
    {
        var team = new SquadReader(project.WorkingDirectory).ReadTeam();
        var member = team?.Members.FirstOrDefault(m =>
            string.Equals(m.Name, request.AgentName, StringComparison.OrdinalIgnoreCase));
        if (member is null || member.Status != CastMemberStatus.Active)
            return Results.BadRequest(new { error = $"agent_name '{request.AgentName}' is not an active team member." });
    }

    // Load agent charter if agent_name provided
    string? agentCharter = null;
    if (!string.IsNullOrWhiteSpace(request.AgentName))
    {
        var charterPath = Path.Combine(
            project.WorkingDirectory, ".squad", "agents",
            request.AgentName.ToLowerInvariant(), "charter.md");
        if (File.Exists(charterPath))
            agentCharter = await File.ReadAllTextAsync(charterPath, ct);
    }

    // Build reserved run (Pending)
    var workflowRunId = Guid.NewGuid().ToString();
    var run = new Run
    {
        Id = RunId.New(),
        RepositoryPath = project.WorkingDirectory,
        OriginatingBranch = baseBranch,
        ModelSource = modelSource,
        ModelId = modelId,
        Task = request.Task!,
        SubmittingUser = caller.User,
        Status = RunStatus.Pending,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
        AgentName = string.IsNullOrWhiteSpace(request.AgentName) ? null : request.AgentName,
        AgentCharter = agentCharter,
        WorkflowRunId = workflowRunId,
    };

    // Insert the workflow run envelope first
    await workflowRunStore.InsertAsync(new WorkflowRun
    {
        Id = workflowRunId,
        ProjectId = projectId,
        Task = request.Task!,
        SubmittingUser = caller.User,
        StartedAt = DateTimeOffset.UtcNow,
    }, ct);

    // Atomically reserve the run row (Pending) only when project is still Active
    bool reserved = await runStore.TryCreateProjectRunAsync(run, ct);
    if (!reserved)
        return Results.Conflict(new { error = "project_deleting", message = "The project is being deleted and cannot accept new runs." });

    // Fire-and-forget: start the workflow in the background so the HTTP response is immediate.
    // The run is already reserved as Pending; startup transitions it to InProgress.
    // On any failure, compensate by terminalizing the run so it never sticks as Pending.
    // CancellationToken.None is intentional — the HTTP request's ct will be cancelled once
    // the response is sent, but the workflow must keep running after that.
    _ = Task.Run(async () =>
    {
        try
        {
            await orchestrator.StartReservedProjectRunAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start project run {RunId} for project {ProjectId}", run.Id, projectId);
            try
            {
                await runStore.TrySetTerminalStatusAsync(
                    run.Id, RunStatus.Failed, DateTimeOffset.UtcNow,
                    "run_start_failed", CancellationToken.None).ConfigureAwait(false);
                var streamEntry = streamStore.Get(run.Id.ToString());
                if (streamEntry is not null)
                {
                    streamEntry.RecordNext(EventTypes.RunFailed, new { reason = "run_start_failed" });
                    streamStore.Complete(run.Id.ToString());
                }
            }
            catch (Exception compensationEx)
            {
                logger.LogError(compensationEx, "Compensation failed for reserved run {RunId}", run.Id);
            }
        }
    });

    return Results.Accepted(
        $"/api/runs/{run.Id}",
        new CreateRunResponse { RunId = run.Id.ToString(), WorkflowRunId = workflowRunId, Status = "pending" });
});

// -----------------------------------------------------------------------
// Coordinator orchestration (Feature 008 Phase 1) — thin HTTP over CoordinatorRunService.
// The HTTP layer validates input, resolves owner-scoped context, and maps the service result
// to status codes. All orchestration lives behind CoordinatorRunService (Principle III).
// -----------------------------------------------------------------------

// POST /api/projects/{id}/orchestrations — start a coordinator run that drafts a confirmable
// outcome spec and suspends at the confirmation gate. Body: { goal, modelId? }.
app.MapPost("/api/projects/{id}/orchestrations", async (
    HttpContext httpContext,
    string id,
    StartOrchestrationRequest request,
    IProjectStore projectStore,
    IProjectWorkspaceProvider workspaceProvider,
    CoordinatorRunService coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.Goal))
        return Results.BadRequest(new { error = "goal is required." });

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (!IsProjectOwner(httpContext, project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!IsAllowedModelId(request.ModelId))
        return Results.BadRequest(new { error = "model_id is not allowed." });

    if (project.State == ProjectState.Deleting)
        return Results.Conflict(new { error = "project_deleting", message = "The project is being deleted and cannot accept new runs." });

    if (!workspaceProvider.IsAvailable(project.WorkingDirectory))
        return Results.Conflict(new { error = "workspace_unavailable", message = "The project workspace is not available. Use relink to reconnect the project." });

    // The coordinator provider is fixed to GitHub Copilot (Constitution Principle II). Resolve the
    // model id the same way the run-start endpoint does: explicit override -> project default ->
    // null (the service falls back to the role-default model). Repository path, originating branch,
    // and submitting user are taken from the project + authenticated caller, mirroring POST /runs.
    var modelId = string.IsNullOrWhiteSpace(request.ModelId)
        ? project.ProviderSettings.GitHubCopilotModel
        : request.ModelId;

    var runId = await coordinator.StartCoordinatorRunAsync(
        projectId,
        request.Goal!,
        caller.User,
        project.WorkingDirectory,
        project.DefaultBranch,
        modelId,
        request.AutoApproveTools,
        request.Autopilot,
        ct);

    return Results.Created(
        $"/api/runs/{runId}",
        new StartOrchestrationResponse { RunId = runId.ToString() });
});
    }

static ProjectResponse MapProject(Project p, bool available) => new()
{
    ProjectId = p.Id.ToString(),
    Name = p.Name,
    Origin = p.Origin.ToApiString(),
    SourceRepository = p.Origin.SourceRepository,
    WorkingDirectory = p.WorkingDirectory,
    DefaultBranch = p.DefaultBranch,
    Owner = p.Owner,
    DefaultProvider = p.ProviderSettings.DefaultProvider.ToApiString(),
    DefaultModelGitHubCopilot = p.ProviderSettings.GitHubCopilotModel,
    DefaultModelMicrosoftFoundry = p.ProviderSettings.MicrosoftFoundryModel,
    Available = available,
    State = p.State == ProjectState.Active ? "active" : "deleting",
    CreatedAt = p.CreatedAt,
    UpdatedAt = p.UpdatedAt,
    SourceBlueprintId = p.SourceBlueprintId,
    SourceBlueprintType = p.SourceBlueprintType,
    AllowedWorkflowIds = p.AllowedWorkflowIds,
};

private static readonly Regex AgentNameSlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);
private static readonly Regex AllowedModelRegex = new("^(gpt|claude|o)[a-z0-9._-]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static bool IsProjectOwner(HttpContext httpContext, Agentweaver.Domain.Project project)
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    return caller.Owns(project.Owner) || string.Equals(caller.User, "admin", StringComparison.OrdinalIgnoreCase);
}

private static bool IsAllowedModelId(string? modelId) =>
    string.IsNullOrWhiteSpace(modelId) || AllowedModelRegex.IsMatch(modelId.Trim());
}
