using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Squad;
using YamlDotNet.Core;

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

        // GET /api/projects/{projectId}/workflows/{workflowId}/graph — static graph descriptor (US6).
        // Returns a WorkflowGraphDto that maps each node/edge to the shape consumed by WorkflowGraphPanel.
        app.MapGet("/api/projects/{projectId}/workflows/{workflowId}/graph", async (
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

            return Results.Ok(WorkflowDtoMapper.ToGraph(result.Definition));
        });

        // GET /api/projects/{projectId}/workflows/{workflowId}/yaml — raw YAML content on disk (US7).
        // Returns 404 for built-in workflows (no on-disk file) and for unknown workflow ids.
        app.MapGet("/api/projects/{projectId}/workflows/{workflowId}/yaml", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });

            var dir = Path.Combine(project!.WorkingDirectory, ".agentweaver", "workflows");
            var yaml = await TryReadWorkflowYamlAsync(dir, workflowId, ct);
            if (yaml is null) return Results.NotFound();

            return Results.Ok(new WorkflowYamlResponse { Yaml = yaml });
        });

        // PUT /api/projects/{projectId}/workflows/{workflowId} — parse, binder dry-run, save (US7).
        // Returns 200 WorkflowDetailDto on success; 400 { error, line? } on parse/validation failure.
        // The YAML's declared 'id' must match the route {workflowId}.
        app.MapPut("/api/projects/{projectId}/workflows/{workflowId}", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            SaveWorkflowRequest request,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });

            // Step 1: Attempt a pre-parse to capture YamlException line numbers before the loader
            // normalises the message.
            int? errorLine = null;
            try
            {
                var preDeserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                preDeserializer.Deserialize<object>(request.Yaml);
            }
            catch (YamlException ex)
            {
                errorLine = (int)ex.Start.Line;
                return Results.BadRequest(new { error = $"YAML parse error at line {ex.Start.Line}: {ex.Message}", line = errorLine });
            }

            // Step 2: Full load + structural validation via the real loader.
            var loadResult = WorkflowDefinitionLoader.Load(request.Yaml, workflowId);
            if (!loadResult.IsValid || loadResult.Definition is null)
                return Results.BadRequest(new { error = loadResult.Error ?? "Workflow validation failed.", line = errorLine });

            var definition = loadResult.Definition;

            // Step 3: Route id must match the YAML's declared id (prevents mismatched saves).
            if (!string.Equals(definition.Id, workflowId, StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    error = $"Workflow id '{definition.Id}' in YAML does not match route id '{workflowId}'. " +
                            "Update the 'id' field in the YAML to match, or use the correct route.",
                    line = errorLine
                });

            // Step 4: Binder dry-run — classify every node and reject types not yet wired to a
            // runtime executor. This fails-closed before the file is written, consistent with the
            // binder's governance guarantee.
            foreach (var node in definition.Nodes)
            {
                var kind = NodeClassifier.Classify(node);
                if (kind is NodeKind.FanOut or NodeKind.FanIn or NodeKind.Serial
                         or NodeKind.PeerReview or NodeKind.CoordinatorComposed)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Node '{node.Id}' (type '{WorkflowDtoMapper.NodeTypeToApi(node.Type)}') is accepted " +
                                "by the schema but is not yet wired to a runtime executor. Use " +
                                "prompt/check/merge/scribe/terminal nodes in authored workflows.",
                        line = errorLine
                    });
                }
            }

            // Step 5: Write to the project workspace.
            var workflowsDir = Path.Combine(project!.WorkingDirectory, ".agentweaver", "workflows");
            try
            {
                Directory.CreateDirectory(workflowsDir);
                var filePath = Path.Combine(workflowsDir, $"{workflowId}.yaml");
                await File.WriteAllTextAsync(filePath, request.Yaml, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"Could not write workflow file: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Step 6: Sync the registry and return the reloaded definition.
            var refreshedSet = registry.Sync(project);
            var saved = refreshedSet.FindById(workflowId);
            if (saved?.Definition is null)
                return Results.Problem(
                    "Workflow was written but could not be re-loaded after sync. Check file permissions.",
                    statusCode: StatusCodes.Status500InternalServerError);

            return Results.Ok(WorkflowDtoMapper.ToDetail(saved, EffectiveDefaultId(project)));
        });

        // POST /api/projects/{projectId}/workflows/generate — generate a DRAFT workflow from a
        // natural-language description (Feature 015 US10, FR-056–FR-061). Returns the generated YAML as
        // an UNSAVED draft for the client to open in the editor; nothing is written to disk here. The
        // generator validates the model output and performs exactly one correction pass (FR-060) before
        // failing closed with a structured 400.
        app.MapPost("/api/projects/{projectId}/workflows/generate", async (
            HttpContext httpContext,
            string projectId,
            GenerateWorkflowRequest request,
            IProjectStore projectStore,
            IWorkflowGenerator generator,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (request is null || string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "description is required." });

            // FR-061: constrain generated nodes to the project's actual cast roles so the workflow is
            // immediately runnable. Falls back to the full catalog inside the generator when none exist.
            var teamRoles = TryReadTeamRoles(project!);

            try
            {
                var result = await generator.GenerateAsync(
                    new WorkflowGenerationRequest(request.Description, project!.Id.ToString(), teamRoles), ct);

                return Results.Ok(new GenerateWorkflowResponse
                {
                    Yaml = result.GeneratedYaml,
                    WorkflowId = result.Workflow.Id,
                    WasCorrected = result.WasCorrected,
                });
            }
            catch (WorkflowGenerationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    /// <summary>Reads the project's cast role ids from its squad team, or null when none can be read.
    /// Used to constrain generated workflow nodes to roles the project can cast (FR-061).</summary>
    private static IReadOnlyList<string>? TryReadTeamRoles(Project project)
    {
        try
        {
            var team = new SquadReader(project.WorkingDirectory).ReadTeam();
            if (team is null) return null;
            var roles = team.Members
                .Select(m => m.Role.Id)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return roles.Count == 0 ? null : roles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>Returns true when <paramref name="id"/> is a safe workflow id: no path separators or
    /// directory traversal sequences, so it can be used directly as a filename component.</summary>
    private static bool IsValidWorkflowId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        !id.Contains('/') && !id.Contains('\\') && !id.Contains("..");

    /// <summary>Attempts to read a workflow's raw YAML from <paramref name="dir"/>/<paramref
    /// name="workflowId"/>.yaml (or .yml). Returns null when neither file exists.</summary>
    private static async Task<string?> TryReadWorkflowYamlAsync(string dir, string workflowId, CancellationToken ct)
    {
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var path = Path.Combine(dir, $"{workflowId}{ext}");
            try
            {
                if (File.Exists(path))
                    return await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File exists but is unreadable — surface as not found; the registry error covers
                // the validation side.
                _ = ex;
            }
        }
        return null;
    }
}
