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
app.MapPost("/api/projects", CreateProjectAsync)
    .WithName("CreateProject")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??=
            "Creates a project from a blank workspace or GitHub repository, optionally applying a blueprint and generated workflow atomically.";
        return Task.CompletedTask;
    });

// GET /api/server/info — public server metadata (no auth required)
app.MapGet("/api/server/info", (IProjectWorkspaceProvider workspaceProvider) => Results.Ok(new
{
    data_directory          = AppPaths.DataDirectory,
    workspace_auto_assigned = workspaceProvider.AutoAssignsPath,
})).AllowAnonymous();

// GET /api/projects — list all projects (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects", ListProjectsAsync)
    .WithName("ListProjects")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Lists the authenticated caller's projects with pagination metadata.";
        return Task.CompletedTask;
    });

// GET /api/projects/{id} — get a single project
app.MapGet("/api/projects/{id}", GetProjectAsync)
    .WithName("GetProject")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Returns one project's current metadata, ownership, and model defaults.";
        return Task.CompletedTask;
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
        !IsAllowedModelId(request.DefaultModelMicrosoftFoundry) ||
        !IsAllowedModelId(request.BlueprintGenerationModel) ||
        !IsAllowedModelId(request.WorkflowGenerationModel) ||
        !IsAllowedModelId(request.OutcomeSpecGenerationModel))
        return Results.BadRequest(new { error = "model_id is not allowed." });

    bool updated;
    try
    {
        updated = await projectService.UpdateProviderSettingsAsync(
            projectId, request.DefaultProvider,
            request.DefaultModelGitHubCopilot,
            request.DefaultModelMicrosoftFoundry,
            request.BlueprintGenerationModel,
            request.WorkflowGenerationModel,
            request.OutcomeSpecGenerationModel,
            ct);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// DELETE /api/projects/{id}?confirm=true — record-only delete
app.MapDelete("/api/projects/{id}", async (
    HttpContext httpContext,
    string id,
    ProjectService projectService,
    IRunStore runStore,
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

// GET /api/projects/{id}/runs — list runs for a project (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/runs", async (
    HttpContext httpContext,
    string id,
    string? agent,
    bool? terminal_only,
    bool? include_children,
    int? limit,
    int? page,
    int? page_size,
    IProjectStore projectStore,
    IRunStore runStore,
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
    // Deterministic, newest-first order so pages are stable across requests.
    runs = runs.OrderByDescending(r => r.StartedAt).ToList();

    // Legacy `limit` param (pre-pagination) is honored as a page_size alias for one release so
    // existing callers that only pass `limit` keep getting a bounded, single-page result. New
    // callers should use `page`/`page_size` — see decisions/inbox/niobe-pagination-contract.md.
    var effectivePageSize = page_size ?? (limit is > 0 ? limit : null);

    // For coordinator runs, surface the work-plan orchestration status so the list can render
    // "Dispatching" / "Awaiting assembly" / "Failed: <reason>" instead of the bare run status.
    var coordinatorRunIds = runs
        .Where(r => r.ParentRunId is null && string.Equals(r.AgentName, "Coordinator", StringComparison.Ordinal))
        .Select(r => r.Id.ToString())
        .ToList();
    var coordinatorStatuses = await coordinator.GetCoordinatorStatusesAsync(coordinatorRunIds, ct);

    var summaries = runs.Select(r =>
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
    }).ToList();

    return Results.Ok(Paging.Of(summaries, page, effectivePageSize));
});

static bool IsTerminalHistoryStatus(RunStatus status) =>
    status is RunStatus.Completed or RunStatus.Merged or RunStatus.AssembleReady
        or RunStatus.Declined or RunStatus.Failed or RunStatus.MergeFailed;

// POST /api/projects/{id}/runs — deprecated direct run submission route
app.MapPost("/api/projects/{id}/runs", () => Results.Problem(
    title: "Single-run endpoint deprecated",
    detail: "Start work through POST /api/projects/{id}/orchestrations so the Coordinator can decompose, assemble, review, merge, and scribe.",
    statusCode: StatusCodes.Status410Gone));

// -----------------------------------------------------------------------
// Coordinator orchestration (Feature 008 Phase 1) — thin HTTP over CoordinatorRunService.
// The HTTP layer validates input, resolves owner-scoped context, and maps the service result
// to status codes. All orchestration lives behind CoordinatorRunService (Principle III).
// -----------------------------------------------------------------------

// POST /api/projects/{id}/orchestrations — start a coordinator run. Default/defineOutcome drafts a
// confirmable outcome spec and suspends at the confirmation gate; direct plans from the prompt.
// Body: { goal, start_mode?, modelId? }.
app.MapPost("/api/projects/{id}/orchestrations", StartOrchestrationAsync)
    .WithName("StartProjectOrchestration")
    .WithTags("Coordinator")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Starts a coordinator run for the project using either defineOutcome or direct planning mode.";
        return Task.CompletedTask;
    });
    }

    /// <summary>
    /// Creates a project workspace, optionally cloning a GitHub repository and applying a blueprint before the first run.
    /// </summary>
    /// <param name="request">The project origin, workspace path, model defaults, and optional blueprint materialization payload.</param>
    /// <response code="201">Returns the created project, including any blueprint-derived defaults that were applied.</response>
    /// <response code="400">The request was malformed or the selected blueprint/workflow payload was invalid.</response>
    /// <response code="500">Project creation or rollback failed unexpectedly.</response>
    /// <response code="503">The target workspace root is unavailable.</response>
    /// <remarks>
    /// Persona-style drivers should prefer this route over manual file bootstrapping because it atomically creates the
    /// project, validates blueprint inputs, and rolls back on apply failures.
    /// </remarks>
    public static async Task<IResult> CreateProjectAsync(
        HttpContext httpContext,
        CreateProjectRequest request,
        ProjectService projectService,
        BlueprintService blueprintService,
        IRunStore runStore,
        RunWorkflowRegistry workflowRegistry,
        IProjectStore projectStore,
        IProjectWorkspaceProvider workspaceProvider,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "name is required." });

        if (string.IsNullOrWhiteSpace(request.Origin) ||
            (request.Origin != "blank" && request.Origin != "github"))
            return Results.BadRequest(new { error = "origin must be 'blank' or 'github'." });

        if (request.Origin == "github" && string.IsNullOrWhiteSpace(request.SourceRepository))
            return Results.BadRequest(new { error = "source_repository is required when origin is 'github'." });

        // working_directory is only mandatory when the active workspace provider cannot auto-assign
        // one (e.g. LocalFilesystemWorkspaceProvider). Providers that report AutoAssignsPath == true
        // (e.g. PersistentVolumeWorkspaceProvider) already derive the path deterministically from the
        // project id in ResolveWorkingDirectoryAsync and ignore any client-supplied value, so requiring
        // it here would force every client to leak server filesystem layout for no benefit (#333).
        if (!workspaceProvider.AutoAssignsPath && string.IsNullOrWhiteSpace(request.WorkingDirectory))
            return Results.BadRequest(new { error = "working_directory is required." });

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
            // Pass through as-is when supplied; auto-assigning providers ignore this value entirely
            // (ResolveWorkingDirectoryAsync derives the path from the project id instead), and
            // LocalFilesystemWorkspaceProvider treats an empty/relative path as "assign one under the
            // configured workspace root" rather than throwing.
            var requestedWorkingDirectory = request.WorkingDirectory ?? string.Empty;

            if (request.Origin == "blank")
            {
                project = await projectService.CreateBlankAsync(
                    request.Name!, requestedWorkingDirectory,
                    request.DefaultProvider, request.DefaultModelGitHubCopilot,
                    request.DefaultModelMicrosoftFoundry, caller.User, ct);
            }
            else
            {
                project = await projectService.CreateFromGitHubAsync(
                    request.Name!, request.SourceRepository!, requestedWorkingDirectory,
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

                    var pid = ProjectId.Parse(project.Id.ToString());
                    await projectStore.UpdateSourceBlueprintAsync(
                        pid, blueprintSourceId, blueprintSourceType, DateTimeOffset.UtcNow, ct);
                }
                catch (Exception blueprintEx)
                {
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
            return Results.Json(
                new { error = "workspace_unavailable", message = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create project");
            return Results.Problem(
                $"Failed to create the project. {ex.GetType().Name}: {ex.Message}",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Lists the caller's visible projects with paging metadata so a persona can discover where to work next.
    /// </summary>
    /// <param name="page">Optional one-based page number.</param>
    /// <param name="page_size">Optional page size.</param>
    /// <response code="200">Returns only the projects owned by the authenticated caller.</response>
    public static async Task<IResult> ListProjectsAsync(
        HttpContext httpContext,
        ProjectService projectService,
        int? page,
        int? page_size,
        CancellationToken ct)
    {
        var views = await projectService.ListViewsAsync(ct);
        var projects = views
            .Where(v => IsProjectOwner(httpContext, v.Project))
            .Select(v => MapProject(v.Project, v.Available))
            .ToList();
        return Results.Ok(Paging.Of(projects, page, page_size));
    }

    /// <summary>
    /// Returns the current metadata and model defaults for one project.
    /// </summary>
    /// <param name="id">The project identifier returned by project creation or listing endpoints.</param>
    /// <response code="200">Returns the requested project.</response>
    /// <response code="400">The project id was malformed.</response>
    /// <response code="403">The caller does not own the project.</response>
    /// <response code="404">The project does not exist.</response>
    public static async Task<IResult> GetProjectAsync(
        HttpContext httpContext,
        string id,
        ProjectService projectService,
        CancellationToken ct)
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return Results.BadRequest(new { error = "Invalid project id." });

        var view = await projectService.GetViewAsync(projectId, ct);
        if (view is null) return Results.NotFound();
        if (!IsProjectOwner(httpContext, view.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Results.Ok(MapProject(view.Project, view.Available));
    }

    /// <summary>
    /// Starts a coordinator run for a project so the system can plan, delegate, assemble, and review work.
    /// </summary>
    /// <param name="id">The project identifier that owns the new coordinator run.</param>
    /// <param name="request">The goal, start mode, model override, and autonomy flags for the orchestration.</param>
    /// <response code="201">Returns the new coordinator run id.</response>
    /// <response code="400">The project id, goal, start mode, or model override was invalid.</response>
    /// <response code="403">The caller does not own the project.</response>
    /// <response code="404">The project was not found.</response>
    /// <response code="409">The project is deleting or its workspace is unavailable.</response>
    /// <response code="422">The project has no dispatchable team roster.</response>
    /// <remarks>
    /// This is the main entry point a persona should use to kick off work. In <c>defineOutcome</c> mode the coordinator
    /// first drafts an outcome spec, while <c>direct</c> goes straight to planning from the goal prompt.
    /// </remarks>
    public static async Task<IResult> StartOrchestrationAsync(
        HttpContext httpContext,
        string id,
        StartOrchestrationRequest request,
        IProjectStore projectStore,
        IProjectWorkspaceProvider workspaceProvider,
        CoordinatorRunService coordinator,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return Results.BadRequest(new { error = "Invalid project id." });

        if (string.IsNullOrWhiteSpace(request.Goal))
            return Results.BadRequest(new { error = "goal is required." });

        if (!TryParseCoordinatorStartMode(request.StartMode ?? request.Mode, out var startMode))
            return Results.BadRequest(new { error = "start_mode must be 'defineOutcome' or 'direct'." });

        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

        var project = await projectStore.GetAsync(projectId, ct);
        if (project is null) return Results.NotFound();
        if (!IsProjectOwner(httpContext, project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!IsAllowedModelId(request.ModelId))
            return Results.BadRequest(new { error = "model_id is not allowed." });

        if (project.State == ProjectState.Deleting)
            return Results.Conflict(new { error = "project_deleting", message = "The project is being deleted and cannot accept new runs." });

        if (!workspaceProvider.IsAvailable(project.WorkingDirectory))
            return Results.Conflict(new { error = "workspace_unavailable", message = "The project workspace is not available." });

        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? project.ProviderSettings.GitHubCopilotModel
            : request.ModelId;

        RunId runId;
        try
        {
            runId = await coordinator.StartCoordinatorRunAsync(
                projectId,
                request.Goal!,
                caller.User,
                project.WorkingDirectory,
                project.DefaultBranch,
                modelId,
                request.AutoApproveTools,
                request.Autopilot,
                ct,
                workflowOverrideId: request.WorkflowOverrideId,
                startMode: startMode);
        }
        catch (NoTeamException)
        {
            return Results.Conflict(new { error = NoTeamException.ErrorCode, message = NoTeamException.DefaultMessage });
        }
        catch (InvalidTeamException ex)
        {
            logger.LogError(ex, "Failed to read dispatchable team roster for project {ProjectId}", projectId);
            return Results.UnprocessableEntity(new { error = InvalidTeamException.ErrorCode, message = InvalidTeamException.DefaultMessage });
        }

        return Results.Created(
            $"/api/runs/{runId}",
            new StartOrchestrationResponse { RunId = runId.ToString() });
    }

    private static bool TryParseCoordinatorStartMode(string? raw, out CoordinatorStartMode mode)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "defineOutcome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "define_outcome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "outcomeSpec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "outcome_spec", StringComparison.OrdinalIgnoreCase))
        {
            mode = CoordinatorStartMode.DefineOutcome;
            return true;
        }

        if (string.Equals(raw, "direct", StringComparison.OrdinalIgnoreCase))
        {
            mode = CoordinatorStartMode.Direct;
            return true;
        }

        mode = CoordinatorStartMode.DefineOutcome;
        return false;
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
    BlueprintGenerationModel = p.BlueprintGenerationModel,
    WorkflowGenerationModel = p.WorkflowGenerationModel,
    OutcomeSpecGenerationModel = p.OutcomeSpecGenerationModel,
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
    return caller.Owns(project.Owner);
}

private static bool IsAllowedModelId(string? modelId) =>
    string.IsNullOrWhiteSpace(modelId) || AllowedModelRegex.IsMatch(modelId.Trim());
}
