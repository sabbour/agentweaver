using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Security;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Blueprint endpoints (Feature 012): list predefined blueprints, run durable generation jobs, and
/// validate a file blueprint. All require an authenticated caller; generation jobs are rebound to
/// their accepted subject and optional project on every status, result, cancel, and retry request.
/// </summary>
public static class BlueprintEndpoints
{
    public static void MapBlueprintEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/blueprints — list the predefined blueprints.
        app.MapGet("/api/blueprints", ListBlueprints)
            .WithName("ListBlueprints")
            .WithTags("Blueprints")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Lists the built-in blueprint catalog that can seed new projects.";
                return Task.CompletedTask;
            });

        // POST /api/blueprints/generate — accept a durable blueprint-generation job.
        app.MapPost("/api/blueprints/generate", GenerateBlueprintAsync)
            .WithName("GenerateBlueprint")
            .WithTags("Blueprints")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Accepts a durable Blueprint-generation job. Supply Idempotency-Key; poll the returned status URL.";
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Caller-chosen retry key. Reuse only for the identical Blueprint-generation request.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
                return Task.CompletedTask;
            })
            .Produces<BlueprintGenerationJobResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .RequiresAiExecutionContext("blueprint_generation");

        app.MapGet("/api/blueprints/generation-jobs/{jobId}", GetBlueprintGenerationJobAsync)
            .WithName("GetBlueprintGenerationJob")
            .WithTags("Blueprints")
            .Produces<BlueprintGenerationJobResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        app.MapGet("/api/blueprints/generation-jobs/{jobId}/result", GetBlueprintGenerationResultAsync)
            .WithName("GetBlueprintGenerationResult")
            .WithTags("Blueprints")
            .Produces<BlueprintGenerationResultResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        app.MapPost("/api/blueprints/generation-jobs/{jobId}/cancel", CancelBlueprintGenerationJobAsync)
            .WithName("CancelBlueprintGenerationJob")
            .WithTags("Blueprints")
            .Produces<BlueprintGenerationJobResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        app.MapPost("/api/blueprints/generation-jobs/{jobId}/retry", RetryBlueprintGenerationJobAsync)
            .WithName("RetryBlueprintGenerationJob")
            .WithTags("Blueprints")
            .Produces<BlueprintGenerationJobResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        // POST /api/blueprints/suggest — analyze a GitHub repository and recommend a catalog blueprint.
        app.MapPost("/api/blueprints/suggest", SuggestBlueprintAsync)
            .WithName("SuggestBlueprint")
            .WithTags("Blueprints")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Recommends the closest catalog blueprint for a target GitHub repository.";
                return Task.CompletedTask;
            });

        // POST /api/blueprints/validate — validate a file blueprint against the schema + role constraint.
        app.MapPost("/api/blueprints/validate", ValidateBlueprint)
            .WithName("ValidateBlueprint")
            .WithTags("Blueprints")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Validates a blueprint payload without mutating any project state.";
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Lists the built-in blueprints that a persona can apply directly when creating or reshaping a project.
    /// </summary>
    /// <response code="200">Returns the catalog blueprints with roster, workflow, and policy defaults.</response>
    public static IResult ListBlueprints(BlueprintService blueprints)
    {
        var list = blueprints.GetPredefinedCatalog()
            .Select(entry => BlueprintDto.FromModel(entry.Blueprint, entry.Exportability))
            .ToList();
        return Results.Ok(new ListBlueprintsResponse { Blueprints = list });
    }

    /// <summary>
    /// Generates a draft blueprint from a natural-language description, optionally grounded in an existing project or target repository.
    /// </summary>
    /// <param name="request">Prompt and optional project/repository context for blueprint generation.</param>
    /// <response code="202">Returns the accepted durable generation job.</response>
    /// <response code="400">The request was malformed or referenced an invalid project id.</response>
    /// <response code="403">The caller does not own the referenced project.</response>
    /// <response code="404">The referenced project was not found.</response>
    /// <response code="409">The idempotency key was already used for a different request.</response>
    /// <remarks>
    /// This is the fastest way for an agent to bootstrap a castable project shape from prose. When
    /// <c>generated_workflow_yaml</c> is returned, pass it back to project creation so the workflow is materialized.
    /// </remarks>
    public static async Task<IResult> GenerateBlueprintAsync(
        HttpContext httpContext,
        GenerateBlueprintRequest request,
        IProjectStore projectStore,
        IConfiguration configuration,
        IOptions<GenerationModelOptions> generationOptions,
        AiExecutionPlanService executionPlans,
        AiExecutionPlanAccessor executionPlanAccessor,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
            return Results.BadRequest(new { error = "description is required." });
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Results.BadRequest(new { error = "idempotency_key_required", message = "Idempotency-Key is required." });
        if (idempotencyKey.Length > 256)
            return Results.BadRequest(new { error = "idempotency_key_invalid", message = "Idempotency-Key must be 256 characters or fewer." });

        Project? project = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            if (!ProjectId.TryParse(request.ProjectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });
            project = await projectStore.GetAsync(pid, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();
            if (await ProjectAuthorization
                .RequireAccessAsync(httpContext, project, configuration, ProjectRole.Owner, ct)
                .ConfigureAwait(false) is { } denied)
            {
                return denied;
            }
        }

        using var execution = await EndpointHelpers.BeginAiExecutionAsync(
            httpContext,
            "blueprint_generation",
            project?.Id,
            executionPlans,
            executionPlanAccessor,
            ct).ConfigureAwait(false);
        if (execution.Error is not null)
            return execution.Error;
        var plan = execution.Plan!;
        var options = generationOptions.Value;
        var blueprintModel = project is null
            ? options.ResolveBlueprintModel()
            : options.ResolveBlueprintModel(project.BlueprintGenerationModel);
        var workflowModel = project is null
            ? options.ResolveWorkflowModel()
            : options.ResolveWorkflowModel(project.WorkflowGenerationModel);
        var fingerprint = BlueprintGenerationFingerprint.Create(
            request,
            project?.Id.ToString(),
            blueprintModel,
            workflowModel,
            plan);
        var created = await jobs.CreateOrGetAsync(new BlueprintGenerationJobCreate(
            plan.Subject,
            idempotencyKey,
            fingerprint,
            request.Description.Trim(),
            project?.Id.ToString(),
            request.TargetRepository?.Trim(),
            blueprintModel,
            workflowModel,
            plan.Provider.ProviderKind(),
            plan.Provider.ProviderType(),
            plan.Provider.ProviderKey()!,
            plan.Provider.ProviderScope(),
            plan.ResolutionScope,
            plan.Provider.CredentialVersion(),
            executionPlans.CreateQueuedProviderKey(plan)), ct).ConfigureAwait(false);

        if (created.Disposition == BlueprintGenerationJobCreateDisposition.Conflict)
        {
            return Results.Conflict(new
            {
                error = "idempotency_key_conflict",
                message = "The Idempotency-Key was already used for a different Blueprint-generation request.",
                job_id = created.Snapshot.Job.JobId,
            });
        }

        return Results.Accepted(
            $"/api/blueprints/generation-jobs/{created.Snapshot.Job.JobId}",
            ToJobResponse(
                created.Snapshot,
                created.Disposition == BlueprintGenerationJobCreateDisposition.Created
                    ? executionPlans.ToResponse(plan, "active")
                    : null));
    }

    public static async Task<IResult> GetBlueprintGenerationJobAsync(
        HttpContext httpContext,
        string jobId,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (snapshot is null || WorkflowGenerationJobPayload.TryDeserialize(snapshot.Job.Description, out _))
            return Results.NotFound();
        if (await RequireJobAccessAsync(httpContext, snapshot.Job, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return Results.Ok(ToJobResponse(snapshot));
    }

    public static async Task<IResult> GetBlueprintGenerationResultAsync(
        HttpContext httpContext,
        string jobId,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (snapshot is null || WorkflowGenerationJobPayload.TryDeserialize(snapshot.Job.Description, out _))
            return Results.NotFound();
        if (await RequireJobAccessAsync(httpContext, snapshot.Job, ct).ConfigureAwait(false) is { } denied)
            return denied;
        if (snapshot.Artifact is null)
        {
            return Results.Conflict(new
            {
                error = "blueprint_generation_not_complete",
                status = snapshot.Job.Status,
                failure = snapshot.Job.FailureCode is null ? null : ToFailure(snapshot.Job),
            });
        }

        return Results.Ok(new BlueprintGenerationResultResponse
        {
            JobId = jobId,
            ArtifactId = snapshot.Artifact.ArtifactId,
            LogicalId = snapshot.Artifact.LogicalId,
            Version = snapshot.Artifact.Version,
            Blueprint = JsonSerializer.Deserialize<BlueprintDto>(snapshot.Artifact.BlueprintJson)
                ?? throw new InvalidOperationException("Persisted Blueprint artifact is invalid."),
            GeneratedWorkflowYaml = snapshot.Artifact.GeneratedWorkflowYaml,
            Warnings = JsonSerializer.Deserialize<IReadOnlyList<string>>(snapshot.Artifact.WarningsJson) ?? [],
        });
    }

    public static async Task<IResult> CancelBlueprintGenerationJobAsync(
        HttpContext httpContext,
        string jobId,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (snapshot is null || WorkflowGenerationJobPayload.TryDeserialize(snapshot.Job.Description, out _))
            return Results.NotFound();
        if (await RequireJobAccessAsync(httpContext, snapshot.Job, ct).ConfigureAwait(false) is { } denied)
            return denied;
        var cancelled = await jobs.CancelAsync(jobId, ct).ConfigureAwait(false);
        return Results.Ok(ToJobResponse(cancelled!));
    }

    public static async Task<IResult> RetryBlueprintGenerationJobAsync(
        HttpContext httpContext,
        string jobId,
        BlueprintGenerationJobStore jobs,
        CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (snapshot is null || WorkflowGenerationJobPayload.TryDeserialize(snapshot.Job.Description, out _))
            return Results.NotFound();
        if (await RequireJobAccessAsync(httpContext, snapshot.Job, ct).ConfigureAwait(false) is { } denied)
            return denied;
        var retryable = snapshot.Job.Status == BlueprintGenerationJobStatuses.Cancelled
            || snapshot.Job.Status == BlueprintGenerationJobStatuses.Failed
                && snapshot.Job.FailureRetryable;
        if (!retryable)
        {
            return Results.Conflict(new
            {
                error = "blueprint_generation_not_retryable",
                status = snapshot.Job.Status,
            });
        }
        var retried = await jobs.RetryAsync(jobId, ct).ConfigureAwait(false);
        return Results.Accepted(
            $"/api/blueprints/generation-jobs/{jobId}",
            ToJobResponse(retried!));
    }

    /// <summary>
    /// Recommends the closest catalog blueprint for a GitHub repository before the project is imported.
    /// </summary>
    /// <param name="request">Repository coordinates in <c>owner/name</c> form.</param>
    /// <response code="200">Returns the recommended blueprint, confidence, and supporting signals.</response>
    /// <response code="400">The repository field was omitted.</response>
    public static async Task<IResult> SuggestBlueprintAsync(
        HttpContext httpContext,
        SuggestBlueprintRequest request,
        GitHubRepoBlueprintSuggestionService suggestions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Repository))
            return Results.BadRequest(new { error = "repository is required." });

        var caller = httpContext.GetCaller();
        var result = await suggestions
            .SuggestAsync(request.Repository!, caller.User, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    /// <summary>
    /// Validates an inline or generated blueprint without mutating any project state.
    /// </summary>
    /// <param name="request">The blueprint document to validate against schema and role constraints.</param>
    /// <response code="200">Returns whether the blueprint is valid and any validation errors.</response>
    /// <response code="400">The request body did not include a blueprint.</response>
    public static IResult ValidateBlueprint(
        ValidateBlueprintRequest request,
        BlueprintService blueprints)
    {
        if (request.Blueprint is null)
            return Results.BadRequest(new { error = "blueprint is required." });

        IReadOnlySet<string>? extraKnownWorkflowIds = null;
        if (!string.IsNullOrWhiteSpace(request.Blueprint.GeneratedWorkflowYaml))
        {
            var generated = WorkflowDefinitionLoader.Load(
                request.Blueprint.GeneratedWorkflowYaml,
                "generated",
                validationMode: WorkflowDefinitionValidationMode.Authoring);
            if (!generated.IsValid || generated.Definition is null)
            {
                return Results.Ok(new ValidateBlueprintResponse
                {
                    Valid = false,
                    Errors = [$"generated_workflow_yaml failed to parse: {generated.Error}"],
                });
            }

            extraKnownWorkflowIds = new HashSet<string>(
                [generated.Definition.Id], StringComparer.Ordinal);
        }

        var validation = blueprints.Validate(request.Blueprint.ToModel(), extraKnownWorkflowIds: extraKnownWorkflowIds);
        return Results.Ok(new ValidateBlueprintResponse
        {
            Valid = validation.Valid,
            Errors = validation.Errors,
        });
    }

    private static async Task<IResult?> RequireJobAccessAsync(
        HttpContext context,
        Agentweaver.Api.Memory.BlueprintGenerationJobRecord job,
        CancellationToken ct)
    {
        if (!context.GetCaller().Owns(job.Subject))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!ProjectId.TryParse(job.ProjectId, out var projectId))
            return null;
        var project = await context.RequestServices.GetRequiredService<IProjectStore>()
            .GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await ProjectAuthorization.RequireAccessAsync(
            context,
            project,
            context.RequestServices.GetRequiredService<IConfiguration>(),
            ProjectRole.Owner,
            ct).ConfigureAwait(false);
    }

    private static BlueprintGenerationJobResponse ToJobResponse(
        BlueprintGenerationJobSnapshot snapshot,
        AiExecutionContextResponse? executionContext = null)
    {
        var job = snapshot.Job;
        var baseUrl = $"/api/blueprints/generation-jobs/{job.JobId}";
        return new BlueprintGenerationJobResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            Attempt = job.Attempt,
            ProjectId = job.ProjectId,
            TargetRepository = job.TargetRepository,
            ProviderSnapshot = new BlueprintGenerationProviderSnapshotDto
            {
                ProviderKind = job.ProviderKind,
                ProviderType = job.ProviderType,
                ProviderKey = job.ProviderKey,
                ProviderScope = job.ProviderScope,
                ResolutionScope = job.ResolutionScope,
                BlueprintModel = job.BlueprintModel,
                WorkflowModel = job.WorkflowModel,
                CredentialBindingVersion = job.CredentialBindingVersion,
            },
            Artifact = snapshot.Artifact is null
                ? null
                : new BlueprintGenerationArtifactDto
                {
                    ArtifactId = snapshot.Artifact.ArtifactId,
                    LogicalId = snapshot.Artifact.LogicalId,
                    Version = snapshot.Artifact.Version,
                },
            Failure = job.FailureCode is null ? null : ToFailure(job),
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            StatusUrl = baseUrl,
            ResultUrl = $"{baseUrl}/result",
            CancelUrl = $"{baseUrl}/cancel",
            RetryUrl = $"{baseUrl}/retry",
            AiExecutionContext = executionContext,
        };
    }

    private static BlueprintGenerationFailureDto ToFailure(
        Agentweaver.Api.Memory.BlueprintGenerationJobRecord job) =>
        new()
        {
            Code = job.FailureCode!,
            Message = job.FailureMessage ?? "Blueprint generation failed.",
            Retryable = job.FailureRetryable,
        };
}
