using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Squad;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
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
    public static void MapWorkflowDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workflows/grammar", () => Results.Ok(WorkflowGrammarContract.ToDto()))
            .WithName("GetWorkflowGrammar")
            .WithTags("Workflows")
            .WithDescription(
                "Returns the versioned YAML workflow grammar accepted by runtime validation, including " +
                "node types, bindability, edge conditions, transition rules, triggers, and limits.")
            .Produces<WorkflowGrammarDto>(StatusCodes.Status200OK)
            .OperationalAnonymous();

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
            AiExecutionPlanService executionPlans,
            AiExecutionPlanAccessor executionPlanAccessor,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var definition = registry.Get(project!, workflowId)?.Definition;
            if (definition is null) return Results.NotFound();

            var bindErrors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);
            if (bindErrors.Count > 0)
                return Results.BadRequest(new { error = "workflow_not_bindable", validation_errors = bindErrors });

            var caller = httpContext.GetCaller();
            using var execution = await EndpointHelpers.BeginAiExecutionAsync(
                httpContext,
                "orchestration",
                project!.Id,
                executionPlans,
                executionPlanAccessor,
                ct).ConfigureAwait(false);
            execution.Activate();
            if (execution.Error is not null)
                return execution.Error;

            var task = await WorkflowTriggerBacklogFactory.CreateReadyTaskAsync(
                backlogStore,
                project,
                definition,
                title: $"Manual run: {definition.Name}",
                description: $"Manually triggered from the workflow library for '{definition.Id}'.",
                capturedBy: caller.User,
                idempotencyKey: $"workflow-manual-trigger:{definition.Id}:{Guid.NewGuid():N}",
                now: DateTimeOffset.UtcNow,
                ct: ct,
                capturedByUserId: caller.EntraObjectId ?? caller.User,
                aiExecutionProviderKey: executionPlans.CreateQueuedProviderKey(execution.Plan!));

            return Results.Created(
                $"/api/projects/{projectId}/backlog/tasks/{task.Id}",
                new { task_id = task.Id.ToString() });
        })
            .RequiresAiExecutionContext("orchestration");

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
            var loadResult = WorkflowDefinitionLoader.Load(
                request.Yaml,
                workflowId,
                validationMode: WorkflowDefinitionValidationMode.Authoring);
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
                return Results.UnprocessableEntity(new
                {
                    error = "workflow_not_bindable",
                    validation_errors = new[] { ex.Message },
                    transition_issues = RunWorkflowGraphBinder.GetTransitionIssues(definition),
                    line = errorLine,
                });
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

        // POST /api/projects/{projectId}/workflows/generate — accept durable generation of an UNSAVED
        // workflow draft. The accepted request survives disconnects and is deduplicated by Idempotency-Key.
        app.MapPost("/api/projects/{projectId}/workflows/generate", async (
            HttpContext httpContext,
            string projectId,
            GenerateWorkflowRequest request,
            IProjectStore projectStore,
            WorkflowRegistry registry,
            IOptions<GenerationModelOptions> generationOptions,
            AiExecutionPlanService executionPlans,
            AiExecutionPlanAccessor executionPlanAccessor,
            BlueprintGenerationJobStore jobs,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            if (request is null || string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "description is required." });
            var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return Results.BadRequest(new { error = "idempotency_key_required", message = "Idempotency-Key is required." });
            if (idempotencyKey.Length > 256)
                return Results.BadRequest(new { error = "idempotency_key_invalid", message = "Idempotency-Key must be 256 characters or fewer." });

            // FR-061: constrain generated nodes to the project's actual cast roles so the workflow is
            // immediately runnable. Falls back to the full catalog inside the generator when none exist.
            var teamRoles = TryReadTeamRoles(project!);
            var caller = httpContext.GetCaller();
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
                        error = "workflow_not_bindable",
                        validation_errors = bindErrors,
                        transition_issues = RunWorkflowGraphBinder.GetTransitionIssues(load.Definition),
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

            using var execution = await EndpointHelpers.BeginAiExecutionAsync(
                httpContext,
                "workflow_generation",
                project!.Id,
                executionPlans,
                executionPlanAccessor,
                ct).ConfigureAwait(false);
            if (execution.Error is not null)
                return execution.Error;

            var generationModel = generationOptions.Value.ResolveWorkflowModel(project!.WorkflowGenerationModel);
            var payload = new WorkflowGenerationJobPayload(
                request.Description.Trim(),
                project.Id.ToString(),
                teamRoles,
                caller.User,
                project.Origin.SourceRepository,
                baseWorkflowId,
                baseYaml,
                baseWorkflowIsBuiltIn,
                generationModel,
                request.ContentOnly);
            var fingerprint = CreateWorkflowGenerationFingerprint(payload, execution.Plan!);
            var created = await jobs.CreateOrGetAsync(new BlueprintGenerationJobCreate(
                execution.Plan!.Subject,
                idempotencyKey,
                fingerprint,
                payload.Serialize(),
                project.Id.ToString(),
                project.Origin.SourceRepository,
                null,
                generationModel,
                execution.Plan.Provider.ProviderKind(),
                execution.Plan.Provider.ProviderType(),
                execution.Plan.Provider.ProviderKey()!,
                execution.Plan.Provider.ProviderScope(),
                execution.Plan.ResolutionScope,
                execution.Plan.Provider.CredentialVersion(),
                executionPlans.CreateQueuedProviderKey(execution.Plan)), ct).ConfigureAwait(false);

            if (created.Disposition == BlueprintGenerationJobCreateDisposition.Conflict)
            {
                return Results.Conflict(new
                {
                    error = "idempotency_key_conflict",
                    message = "The Idempotency-Key was already used for a different workflow-generation request.",
                    job_id = created.Snapshot.Job.JobId,
                });
            }

            return Results.Accepted(
                $"/api/projects/{projectId}/workflows/generation-jobs/{created.Snapshot.Job.JobId}",
                ToWorkflowJobResponse(
                    created.Snapshot,
                    created.Disposition == BlueprintGenerationJobCreateDisposition.Created
                        ? executionPlans.ToResponse(execution.Plan, "active")
                        : null));
        })
            .WithName("GenerateWorkflow")
            .WithTags("Workflows")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Accepts a durable workflow-generation job. Supply Idempotency-Key and poll the returned status URL.";
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Caller-chosen retry key. Reuse only for the identical workflow-generation request.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
                return Task.CompletedTask;
            })
            .Produces<WorkflowGenerationJobResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequiresAiExecutionContext("workflow_generation");

        app.MapGet("/api/projects/{projectId}/workflows/generation-jobs/{jobId}", GetWorkflowGenerationJobAsync)
            .WithName("GetWorkflowGenerationJob")
            .WithTags("Workflows");
        app.MapGet("/api/projects/{projectId}/workflows/generation-jobs/{jobId}/result", GetWorkflowGenerationResultAsync)
            .WithName("GetWorkflowGenerationResult")
            .WithTags("Workflows");
        app.MapPost("/api/projects/{projectId}/workflows/generation-jobs/{jobId}/cancel", CancelWorkflowGenerationJobAsync)
            .WithName("CancelWorkflowGenerationJob")
            .WithTags("Workflows");
        app.MapPost("/api/projects/{projectId}/workflows/generation-jobs/{jobId}/retry", RetryWorkflowGenerationJobAsync)
            .WithName("RetryWorkflowGenerationJob")
            .WithTags("Workflows");
    }

    private static async Task<IResult> GetWorkflowGenerationJobAsync(
        HttpContext context,
        string projectId,
        string jobId,
        IProjectStore projects,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var (project, error) = await ResolveOwnedProjectAsync(context, projectId, projects, ct);
        if (error is not null) return error;
        var snapshot = await GetAuthorizedWorkflowJobAsync(context, project!, jobId, jobs, ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(ToWorkflowJobResponse(snapshot));
    }

    private static async Task<IResult> GetWorkflowGenerationResultAsync(
        HttpContext context,
        string projectId,
        string jobId,
        IProjectStore projects,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var (project, error) = await ResolveOwnedProjectAsync(context, projectId, projects, ct);
        if (error is not null) return error;
        var snapshot = await GetAuthorizedWorkflowJobAsync(context, project!, jobId, jobs, ct);
        if (snapshot is null)
            return Results.NotFound();
        if (snapshot.Artifact is null)
        {
            return Results.Conflict(new
            {
                error = "workflow_generation_not_complete",
                status = snapshot.Job.Status,
                failure = snapshot.Job.FailureCode is null ? null : ToWorkflowFailure(snapshot.Job),
            });
        }

        var yaml = snapshot.Artifact.GeneratedWorkflowYaml;
        if (string.IsNullOrWhiteSpace(yaml))
            return Results.Problem("Persisted workflow generation artifact has no YAML.");
        var loaded = WorkflowDefinitionLoader.Load(yaml, "generated-artifact");
        if (!loaded.IsValid || loaded.Definition is null)
            return Results.Problem("Persisted workflow generation artifact is invalid.");
        var metadata = JsonSerializer.Deserialize<WorkflowGenerationArtifactMetadata>(
            snapshot.Artifact.BlueprintJson)
            ?? throw new InvalidOperationException("Persisted workflow generation metadata is invalid.");
        return Results.Ok(new WorkflowGenerationResultResponse
        {
            JobId = jobId,
            ArtifactId = snapshot.Artifact.ArtifactId,
            WorkflowId = snapshot.Artifact.LogicalId,
            Version = snapshot.Artifact.Version,
            Yaml = yaml,
            WasCorrected = metadata.WasCorrected,
            Mode = metadata.Mode,
            BaseWorkflowId = metadata.BaseWorkflowId,
            BaseWorkflowIsBuiltIn = metadata.BaseWorkflowIsBuiltIn,
            Graph = WorkflowDtoMapper.ToGraph(loaded.Definition),
        });
    }

    private static async Task<IResult> CancelWorkflowGenerationJobAsync(
        HttpContext context,
        string projectId,
        string jobId,
        IProjectStore projects,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var (project, error) = await ResolveOwnedProjectAsync(context, projectId, projects, ct);
        if (error is not null) return error;
        var snapshot = await GetAuthorizedWorkflowJobAsync(context, project!, jobId, jobs, ct);
        if (snapshot is null)
            return Results.NotFound();
        var cancelled = await jobs.CancelAsync(jobId, ct).ConfigureAwait(false);
        return Results.Ok(ToWorkflowJobResponse(cancelled!));
    }

    private static async Task<IResult> RetryWorkflowGenerationJobAsync(
        HttpContext context,
        string projectId,
        string jobId,
        IProjectStore projects,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var (project, error) = await ResolveOwnedProjectAsync(context, projectId, projects, ct);
        if (error is not null) return error;
        var snapshot = await GetAuthorizedWorkflowJobAsync(context, project!, jobId, jobs, ct);
        if (snapshot is null)
            return Results.NotFound();
        var retryable = snapshot.Job.Status == BlueprintGenerationJobStatuses.Cancelled
            || snapshot.Job.Status == BlueprintGenerationJobStatuses.Failed
                && snapshot.Job.FailureRetryable;
        if (!retryable)
        {
            return Results.Conflict(new
            {
                error = "workflow_generation_not_retryable",
                status = snapshot.Job.Status,
            });
        }
        var retried = await jobs.RetryAsync(jobId, ct).ConfigureAwait(false);
        return Results.Accepted(
            $"/api/projects/{projectId}/workflows/generation-jobs/{jobId}",
            ToWorkflowJobResponse(retried!));
    }

    private static async Task<BlueprintGenerationJobSnapshot?> GetAuthorizedWorkflowJobAsync(
        HttpContext context,
        Project project,
        string jobId,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (snapshot is null
            || !WorkflowGenerationJobPayload.TryDeserialize(snapshot.Job.Description, out _)
            || !string.Equals(snapshot.Job.ProjectId, project.Id.ToString(), StringComparison.Ordinal)
            || !context.GetCaller().Owns(snapshot.Job.Subject))
        {
            return null;
        }
        return snapshot;
    }

    private static WorkflowGenerationJobResponse ToWorkflowJobResponse(
        BlueprintGenerationJobSnapshot snapshot,
        AiExecutionContextResponse? executionContext = null)
    {
        var job = snapshot.Job;
        var baseUrl = $"/api/projects/{job.ProjectId}/workflows/generation-jobs/{job.JobId}";
        return new WorkflowGenerationJobResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            Attempt = job.Attempt,
            ProjectId = job.ProjectId!,
            ProviderSnapshot = new WorkflowGenerationProviderSnapshotDto
            {
                ProviderKind = job.ProviderKind,
                ProviderType = job.ProviderType,
                ProviderKey = job.ProviderKey,
                ProviderScope = job.ProviderScope,
                ResolutionScope = job.ResolutionScope,
                WorkflowModel = job.WorkflowModel,
                CredentialBindingVersion = job.CredentialBindingVersion,
            },
            Artifact = snapshot.Artifact is null
                ? null
                : new WorkflowGenerationArtifactDto
                {
                    ArtifactId = snapshot.Artifact.ArtifactId,
                    WorkflowId = snapshot.Artifact.LogicalId,
                    Version = snapshot.Artifact.Version,
                },
            Failure = job.FailureCode is null ? null : ToWorkflowFailure(job),
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            StatusUrl = baseUrl,
            ResultUrl = $"{baseUrl}/result",
            CancelUrl = $"{baseUrl}/cancel",
            RetryUrl = $"{baseUrl}/retry",
            AiExecutionContext = executionContext,
        };
    }

    private static WorkflowGenerationFailureDto ToWorkflowFailure(BlueprintGenerationJobRecord job) =>
        new()
        {
            Code = job.FailureCode!,
            Message = job.FailureMessage ?? "Workflow generation failed.",
            Retryable = job.FailureRetryable,
        };

    private static string CreateWorkflowGenerationFingerprint(
        WorkflowGenerationJobPayload request,
        AiExecutionPlan plan)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.Description,
            request.ProjectId,
            request.TeamRoles,
            request.UserId,
            request.TargetRepository,
            request.BaseWorkflowId,
            request.BaseWorkflowYaml,
            request.BaseWorkflowIsBuiltIn,
            request.GenerationModel,
            request.ContentOnly,
            provider_key = plan.Provider.ProviderKey(),
            credential_binding_version = plan.Provider.CredentialVersion(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

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

        var caller = httpContext.GetCaller();
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
