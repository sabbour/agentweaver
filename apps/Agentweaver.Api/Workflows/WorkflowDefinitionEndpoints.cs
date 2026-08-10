using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Squad;
using Microsoft.Extensions.Logging;
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

        // GET /api/projects/{projectId}/workflows/{workflowId}/trigger — structured trigger config
        // for UI-driven editing without hand-authoring YAML.
        app.MapGet("/api/projects/{projectId}/workflows/{workflowId}/trigger", async (
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

            return Results.Ok(WorkflowDtoMapper.ToTriggerConfigResponse(result.Definition.Triggers));
        });

        // PUT /api/projects/{projectId}/workflows/{workflowId}/trigger — create or replace one
        // trigger by type while preserving triggers of other types.
        app.MapPut("/api/projects/{projectId}/workflows/{workflowId}/trigger", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            WorkflowTriggerDto request,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;
            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });
            if (request is null)
                return Results.BadRequest(new { error = "trigger is required." });

            var current = registry.Get(project!, workflowId);
            if (current?.Definition is null) return Results.NotFound();

            if (!WorkflowDefinitionLoader.TryParseTrigger(
                    WorkflowDtoMapper.ToTriggerYamlDto(request),
                    workflowId,
                    out var trigger,
                    out var triggerError))
                return Results.BadRequest(new { error = triggerError ?? "Trigger validation failed." });

            var updatedTriggers = UpsertTrigger(current.Definition.Triggers, trigger!);
            var updatedDefinition = current.Definition with { Triggers = updatedTriggers };
            var persistError = await PersistWorkflowDefinitionAsync(project!, workflowId, updatedDefinition, projectStore, registry, ct);
            if (persistError is not null) return persistError;

            return Results.Ok(WorkflowDtoMapper.ToTriggerConfigResponse(updatedTriggers));
        });

        // PATCH /api/projects/{projectId}/workflows/{workflowId}/trigger — partial trigger update.
        // Preserves unspecified fields from the current trigger, then validates the merged result
        // through the same loader path as PUT.
        app.MapPatch("/api/projects/{projectId}/workflows/{workflowId}/trigger", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            WorkflowTriggerPatchRequest request,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;
            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });
            if (request is null)
                return Results.BadRequest(new { error = "trigger patch is required." });

            var current = registry.Get(project!, workflowId);
            if (current?.Definition is null) return Results.NotFound();

            WorkflowTrigger? currentTrigger;
            if (!string.IsNullOrWhiteSpace(request.Type))
            {
                if (!TryParseTriggerType(request.Type, out var requestedType))
                    return Results.BadRequest(new { error = "type must be 'schedule' or 'event'." });
                currentTrigger = current.Definition.Triggers.FirstOrDefault(t => t.Type == requestedType);
            }
            else if (current.Definition.Triggers.Count <= 1)
            {
                currentTrigger = current.Definition.Triggers.FirstOrDefault();
            }
            else
            {
                return Results.BadRequest(new { error = "type is required when a workflow has multiple triggers." });
            }

            WorkflowTriggerDto mergedTriggerDto;
            try
            {
                mergedTriggerDto = WorkflowDtoMapper.MergeTriggerPatch(
                    currentTrigger is null ? null : WorkflowDtoMapper.ToTriggerDto(currentTrigger),
                    request);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (!WorkflowDefinitionLoader.TryParseTrigger(
                    WorkflowDtoMapper.ToTriggerYamlDto(mergedTriggerDto),
                    workflowId,
                    out var trigger,
                    out var triggerError))
                return Results.BadRequest(new { error = triggerError ?? "Trigger validation failed." });

            var updatedTriggers = UpsertTrigger(current.Definition.Triggers, trigger!);
            var updatedDefinition = current.Definition with { Triggers = updatedTriggers };
            var persistError = await PersistWorkflowDefinitionAsync(project!, workflowId, updatedDefinition, projectStore, registry, ct);
            if (persistError is not null) return persistError;

            return Results.Ok(WorkflowDtoMapper.ToTriggerConfigResponse(updatedTriggers));
        });

        // DELETE without a type clears all triggers for backward compatibility. Supplying
        // ?type=schedule|event removes only that trigger type.
        app.MapDelete("/api/projects/{projectId}/workflows/{workflowId}/trigger", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;
            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });

            var current = registry.Get(project!, workflowId);
            if (current?.Definition is null) return Results.NotFound();

            var requestedType = httpContext.Request.Query["type"].ToString();
            IReadOnlyList<WorkflowTrigger> updatedTriggers;
            if (string.IsNullOrWhiteSpace(requestedType))
            {
                updatedTriggers = [];
            }
            else
            {
                if (!TryParseTriggerType(requestedType, out var triggerType))
                    return Results.BadRequest(new { error = "type must be 'schedule' or 'event'." });
                updatedTriggers = current.Definition.Triggers.Where(t => t.Type != triggerType).ToList();
            }

            var updatedDefinition = current.Definition with { Triggers = updatedTriggers };
            var persistError = await PersistWorkflowDefinitionAsync(project!, workflowId, updatedDefinition, projectStore, registry, ct);
            if (persistError is not null) return persistError;

            return Results.Ok(WorkflowDtoMapper.ToTriggerConfigResponse(updatedTriggers));
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
            if (workflowId is not null)
            {
                var candidate = registry.Get(project!, workflowId)?.Definition;
                if (candidate is null)
                    return Results.BadRequest(new { error = "unknown_workflow_id" });

                // Binder dry-run: a workflow may be loader-valid yet fail at runtime (e.g.
                // agent-evaluation's fan_out/fan_in have no executor). Reject it as a default before it is
                // ever selected for a run, with a 422 naming the runtime problem.
                try
                {
                    RunWorkflowGraphBinder.ValidateBindable(candidate);
                }
                catch (WorkflowBindException ex)
                {
                    return Results.UnprocessableEntity(new
                    {
                        error = $"Workflow cannot be set as default: it will fail at runtime: {ex.Message}",
                    });
                }
            }

            var now = DateTimeOffset.UtcNow;
            await projectStore.UpdateDefaultWorkflowAsync(project!.Id, workflowId, now, ct);

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
            if (workflowId is not null)
            {
                var candidate = registry.Get(project!, workflowId)?.Definition;
                if (candidate is null)
                    return Results.BadRequest(new { error = "unknown_workflow_id" });

                var validationErrors = RunWorkflowGraphBinder.GetBindabilityErrors(candidate);
                if (validationErrors.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = "workflow_not_bindable",
                        validation_errors = validationErrors,
                    });
            }

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

        // GET /api/projects/{projectId}/workflows/{workflowId}/yaml — raw YAML content (US7).
        // Built-ins are serialized from their immutable definition so callers can duplicate a template.
        app.MapGet("/api/projects/{projectId}/workflows/{workflowId}/yaml", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (!IsValidWorkflowId(workflowId))
                return Results.BadRequest(new { error = "Invalid workflow id." });

            var dir = Path.Combine(project!.WorkingDirectory, ".agentweaver", "workflows");
            var yaml = await TryReadWorkflowYamlAsync(dir, workflowId, ct);
            if (yaml is null)
            {
                var builtIn = registry.Get(project, workflowId);
                if (builtIn?.Definition is null || !builtIn.IsBuiltIn) return Results.NotFound();
                yaml = WorkflowDefinitionYamlSerializer.Serialize(builtIn.Definition);
            }

            return Results.Ok(new WorkflowYamlResponse { Yaml = yaml });
        });

        // POST /api/projects/{projectId}/workflows/{workflowId}/run — create a Ready, workflow-bound
        // backlog task. The coordinator claims it through the ordinary pickup path, just like a
        // schedule-triggered run, keeping the run visible and capacity-controlled.
        app.MapPost("/api/projects/{projectId}/workflows/{workflowId}/run", async (
            HttpContext httpContext,
            string projectId,
            string workflowId,
            IProjectStore projectStore,
            IBacklogTaskStore backlogStore,
            WorkflowRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var definition = registry.Get(project!, workflowId)?.Definition;
            if (definition is null) return Results.NotFound();

            var bindErrors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);
            if (bindErrors.Count > 0)
                return Results.BadRequest(new { error = "workflow_not_bindable", validation_errors = bindErrors });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var task = await WorkflowTriggerBacklogFactory.CreateReadyTaskAsync(
                backlogStore,
                project!,
                definition,
                title: $"Manual run: {definition.Name}",
                description: $"Manually triggered from the workflow library for '{definition.Id}'.",
                capturedBy: caller.User,
                idempotencyKey: $"workflow-manual-trigger:{definition.Id}:{Guid.NewGuid():N}",
                now: DateTimeOffset.UtcNow,
                ct: ct);

            return Results.Created(
                $"/api/projects/{projectId}/backlog/tasks/{task.Id}",
                new { task_id = task.Id.ToString() });
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
            ILoggerFactory loggerFactory,
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
                return Results.BadRequest(new
                {
                    error = loadResult.Error ?? "Workflow validation failed.",
                    line = errorLine,
                    warnings = loadResult.Warnings,
                });

            var definition = loadResult.Definition;

            // Step 3: Route id must match the YAML's declared id (prevents mismatched saves).
            if (!string.Equals(definition.Id, workflowId, StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    error = $"Workflow id '{definition.Id}' in YAML does not match route id '{workflowId}'. " +
                            "Update the 'id' field in the YAML to match, or use the correct route.",
                    line = errorLine
                });

            // Step 4: Binder dry-run — run the real RunWorkflowGraphBinder governance check, which
            // classifies every node and fails closed for any type not yet wired to a runtime executor
            // (fan_out / fan_in / serial / coordinator_composed) and for dangling edges. peer_review is
            // accepted: the binder now supports it. This rejects bind-invalid workflows BEFORE the file is
            // written, consistent with the binder's governance guarantee, with a 422 (loader-valid but
            // runtime-unbindable).
            try
            {
                RunWorkflowGraphBinder.ValidateBindable(definition);
            }
            catch (WorkflowBindException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message, line = errorLine });
            }

            // Step 5: Write to the project workspace.
            var workflowsDir = Path.Combine(project!.WorkingDirectory, ".agentweaver", "workflows");
            try
            {
                Directory.CreateDirectory(workflowsDir);
                var filePath = Path.Combine(workflowsDir, $"{workflowId}.yaml");

                // Resolve symlinks/reparse points before writing: an existing symlink at the target
                // (or a symlinked ancestor) could otherwise redirect the write to a file outside the
                // project workspace and overwrite it.
                var workspaceRoot = project.WorkingDirectory
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!WorkspacePathGuard.TryResolveContainedPath(workspaceRoot, filePath, out var safePath))
                    return Results.BadRequest(new { error = "Invalid workflow id." });

                await File.WriteAllTextAsync(safePath, request.Yaml, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"Could not write workflow file: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Step 6: Ensure the workflow id is in the project's allowed set before syncing.
            // When a blueprint has restricted AllowedWorkflowIds, FilterByAllowedSet drops any valid
            // workflow whose id is not in that set — including a freshly written file — causing
            // FindById to return null even though the file exists on disk. Extend the allowed set now
            // so the new workflow is immediately visible after Sync.
            var syncProject = project!;
            var allowedIds = project!.AllowedWorkflowIds;
            if (allowedIds is { Count: > 0 } &&
                !allowedIds.Contains(workflowId, StringComparer.OrdinalIgnoreCase))
            {
                var updatedIds = allowedIds.Append(workflowId).ToList();
                await projectStore.UpdateAllowedWorkflowIdsAsync(project.Id, updatedIds, DateTimeOffset.UtcNow, ct);
                syncProject = project with { AllowedWorkflowIds = updatedIds };
            }

            // Sync the registry and return the reloaded definition.
            var refreshedSet = registry.Sync(syncProject);
            var saved = refreshedSet.FindById(workflowId);
            if (saved?.Definition is null)
            {
                var writtenPath = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows", $"{workflowId}.yaml");
                var currentAllowed = syncProject.AllowedWorkflowIds is { Count: > 0 }
                    ? string.Join(", ", syncProject.AllowedWorkflowIds)
                    : "(unrestricted)";

                // Distinguish a post-write validation failure from a genuine discovery gap.
                var expectedSource = $"{workflowId}.yaml";
                var invalidEntry = refreshedSet.Results.FirstOrDefault(r =>
                    string.Equals(r.Source, expectedSource, StringComparison.OrdinalIgnoreCase) ||
                    (r.Definition is not null &&
                     string.Equals(r.Definition.Id, workflowId, StringComparison.OrdinalIgnoreCase)));

                var saveLogger = loggerFactory.CreateLogger("Agentweaver.Api.Workflows.WorkflowSave");
                saveLogger.LogError(
                    "Workflow '{WorkflowId}' was written to '{FilePath}' but was not returned by registry " +
                    "after Sync. AllowedWorkflowIds: [{AllowedIds}]. Post-sync error: {Error}",
                    workflowId, writtenPath, currentAllowed,
                    invalidEntry?.Error ?? "(workflow not discovered)");

                if (invalidEntry is not null)
                    return Results.UnprocessableEntity(new
                    {
                        error = $"Workflow '{workflowId}' was written but failed validation on reload: {invalidEntry.Error ?? "Workflow validation failed."}",
                        source = invalidEntry.Source,
                        warnings = invalidEntry.Warnings,
                    });

                return Results.Problem(
                    $"Workflow '{workflowId}' was written to disk but was not discovered by the registry. " +
                    "Verify the file is readable and the id in the YAML matches the route.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

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
            WorkflowRegistry registry,
            IWorkflowGenerator generator,
            IOptions<GenerationModelOptions> generationOptions,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (request is null || string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "description is required." });

            // FR-061: constrain generated nodes to the project's actual cast roles so the workflow is
            // immediately runnable. Falls back to the full catalog inside the generator when none exist.
            var teamRoles = TryReadTeamRoles(project!);
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var baseWorkflowId = Normalize(request.BaseWorkflowId);
            var baseYaml = string.IsNullOrWhiteSpace(request.BaseYaml) ? null : request.BaseYaml;
            var baseWorkflowIsBuiltIn = false;

            if (!string.IsNullOrWhiteSpace(baseYaml))
            {
                var load = WorkflowDefinitionLoader.Load(baseYaml!, "draft");
                if (!load.IsValid || load.Definition is null)
                    return Results.BadRequest(new
                    {
                        error = "base_yaml is not a valid workflow draft.",
                        validation_errors = new[] { load.Error ?? "Workflow validation failed." },
                    });

                var bindErrors = RunWorkflowGraphBinder.GetBindabilityErrors(load.Definition);
                if (bindErrors.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = "base_yaml is not runnable.",
                        validation_errors = bindErrors,
                    });

                baseWorkflowId ??= load.Definition.Id;
            }
            else if (baseWorkflowId is not null)
            {
                if (!IsValidWorkflowId(baseWorkflowId))
                    return Results.BadRequest(new { error = "Invalid base_workflow_id." });

                var baseWorkflow = registry.Get(project!, baseWorkflowId);
                if (baseWorkflow?.Definition is null)
                    return Results.BadRequest(new { error = "unknown_base_workflow_id" });

                baseWorkflowIsBuiltIn = baseWorkflow.IsBuiltIn;
                baseYaml = await TryReadWorkflowYamlAsync(
                    Path.Combine(project!.WorkingDirectory, ".agentweaver", "workflows"),
                    baseWorkflowId,
                    ct);
                baseYaml ??= WorkflowDefinitionYamlSerializer.Serialize(baseWorkflow.Definition);
            }

            try
            {
                var result = await generator.GenerateAsync(
                    new WorkflowGenerationRequest(
                        request.Description,
                        project!.Id.ToString(),
                        teamRoles,
                        UserId: caller.User,
                        TargetRepository: project.Origin.SourceRepository,
                        BaseWorkflowId: baseWorkflowId,
                        BaseWorkflowYaml: baseYaml,
                        BaseWorkflowIsBuiltIn: baseWorkflowIsBuiltIn,
                        GenerationModel: generationOptions.Value.ResolveWorkflowModel(project!.WorkflowGenerationModel)),
                    ct);

                return Results.Ok(new GenerateWorkflowResponse
                {
                    Yaml = result.GeneratedYaml,
                    WorkflowId = result.Workflow.Id,
                    WasCorrected = result.WasCorrected,
                    Mode = baseYaml is null ? "create" : "edit",
                    BaseWorkflowId = baseWorkflowId,
                    BaseWorkflowIsBuiltIn = baseWorkflowIsBuiltIn,
                });
            }
            catch (WorkflowGenerationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (AgentProviderException ex)
            {
                return Results.Json(new
                {
                    error = ex.ErrorCode,
                    message = ex.UserMessage,
                    kind = ex.FailureKind.ToString(),
                    retryable = ex.IsRetryable,
                    options = ex.FailureKind == AgentProviderFailureKind.RateLimited
                        ? new[] { "retry" }
                        : new[] { "check_provider_auth", "check_provider_config", "retry" },
                }, statusCode: ProviderFailureStatus(ex.FailureKind));
            }
        });
    }

    private static int ProviderFailureStatus(AgentProviderFailureKind kind) =>
        kind switch
        {
            AgentProviderFailureKind.Authorization => StatusCodes.Status401Unauthorized,
            AgentProviderFailureKind.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable,
        };

    /// <summary>Reads the project's cast role ids from its squad team, or null when none can be read.
    /// Used to constrain generated workflow nodes to roles the project can cast (FR-061). Reserved
    /// orchestration roles (Scribe, Work Monitor, Rai, Coordinator) are always present on every team's
    /// squad file but must never be offered to the generator as an assignable domain role.</summary>
    private static IReadOnlyList<string>? TryReadTeamRoles(Project project)
    {
        try
        {
            var team = new SquadReader(project.WorkingDirectory).ReadTeam();
            if (team is null) return null;
            var roles = team.Members
                .Select(m => m.Role.Id)
                .Where(r => !string.IsNullOrWhiteSpace(r) && !ReservedRoles.IsReserved(r))
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

    private static IReadOnlyList<WorkflowTrigger> UpsertTrigger(
        IReadOnlyList<WorkflowTrigger> current,
        WorkflowTrigger replacement)
    {
        var updated = current.ToList();
        var index = updated.FindIndex(trigger => trigger.Type == replacement.Type);
        if (index >= 0)
            updated[index] = replacement;
        else
            updated.Add(replacement);
        return updated;
    }

    private static bool TryParseTriggerType(string raw, out WorkflowTriggerType type)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "schedule":
                type = WorkflowTriggerType.Schedule;
                return true;
            case "event":
                type = WorkflowTriggerType.Event;
                return true;
            default:
                type = default;
                return false;
        }
    }

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
    internal static async Task<string?> TryReadWorkflowYamlAsync(string dir, string workflowId, CancellationToken ct)
    {
        if (IsReparsePoint(dir))
            return null;

        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var path = Path.Combine(dir, $"{workflowId}{ext}");
            try
            {
                if (File.Exists(path) &&
                    !IsReparsePoint(path) &&
                    WorkspacePathGuard.TryResolveContainedPath(dir, path, out var safePath))
                    return await File.ReadAllTextAsync(safePath, ct);
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

    private static async Task<IResult?> PersistWorkflowDefinitionAsync(
        Project project,
        string workflowId,
        WorkflowDefinition definition,
        IProjectStore projectStore,
        WorkflowRegistry registry,
        CancellationToken ct)
    {
        var yaml = WorkflowDefinitionYamlSerializer.Serialize(definition);
        var load = WorkflowDefinitionLoader.Load(yaml, workflowId);
        if (!load.IsValid || load.Definition is null)
            return Results.BadRequest(new { error = load.Error ?? "Workflow validation failed.", warnings = load.Warnings });

        try
        {
            var workflowsDir = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows");
            Directory.CreateDirectory(workflowsDir);
            var filePath = Path.Combine(workflowsDir, $"{workflowId}.yaml");
            var workspaceRoot = project.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!WorkspacePathGuard.TryResolveContainedPath(workspaceRoot, filePath, out var safePath))
                return Results.BadRequest(new { error = "Invalid workflow id." });

            await File.WriteAllTextAsync(safePath, yaml, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem($"Could not write workflow file: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var syncProject = project;
        var allowedIds = project.AllowedWorkflowIds;
        if (allowedIds is { Count: > 0 } &&
            !allowedIds.Contains(workflowId, StringComparer.OrdinalIgnoreCase))
        {
            var updatedIds = allowedIds.Append(workflowId).ToList();
            await projectStore.UpdateAllowedWorkflowIdsAsync(project.Id, updatedIds, DateTimeOffset.UtcNow, ct);
            syncProject = project with { AllowedWorkflowIds = updatedIds };
        }

        registry.Sync(syncProject);
        return null;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return true;
        }
    }
}
