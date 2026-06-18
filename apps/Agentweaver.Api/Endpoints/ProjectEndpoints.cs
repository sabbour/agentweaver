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

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
// POST /api/projects — create blank or from GitHub
app.MapPost("/api/projects", async (
    HttpContext httpContext,
    CreateProjectRequest request,
    ProjectService projectService,
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
        return Results.Problem("Failed to create the project.", statusCode: 500);
    }
});

// GET /api/server/info — public server metadata (no auth required)
app.MapGet("/api/server/info", () => Results.Ok(new
{
    data_directory = AppPaths.DataDirectory,
})).AllowAnonymous();

// GET /api/projects — list all projects
app.MapGet("/api/projects", async (
    ProjectService projectService,
    CancellationToken ct) =>
{
    var views = await projectService.ListViewsAsync(ct);
    return Results.Ok(views.Select(v => MapProject(v.Project, v.Available)));
});

// GET /api/projects/{id} — get a single project
app.MapGet("/api/projects/{id}", async (
    string id,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var view = await projectService.GetViewAsync(projectId, ct);
    return view is null ? Results.NotFound() : Results.Ok(MapProject(view.Project, view.Available));
});

// PATCH /api/projects/{id} — rename
app.MapMethods("/api/projects/{id}", ["PATCH"], async (
    string id,
    UpdateProjectNameRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "name is required." });

    bool updated;
    try { updated = await projectService.RenameAsync(projectId, request.Name!, ct); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// PUT /api/projects/{id}/provider-settings — update provider defaults
app.MapPut("/api/projects/{id}/provider-settings", async (
    string id,
    UpdateProjectProviderSettingsRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

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
    string id,
    RelinkProjectRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        return Results.BadRequest(new { error = "working_directory is required." });

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
    string id,
    SqliteRunStore runStore,
    CoordinatorStatusReader coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var runs = await runStore.GetRunsByProjectAsync(projectId, ct: ct);

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
        };
    }));
});

// GET /api/projects/{id}/runs/{workflowRunId} — get a single workflow run by its workflow_run_id
app.MapGet("/api/projects/{id}/runs/{workflowRunId}", async (
    string id,
    string workflowRunId,
    SqliteRunStore runStore,
    CoordinatorStatusReader coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out _))
        return Results.BadRequest(new { error = "Invalid project id." });

    var run = await runStore.GetByWorkflowRunIdAsync(workflowRunId, ct);
    if (run is null) return Results.NotFound();

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

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

    // Load project
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

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
};
}
