using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Blueprint endpoints (Feature 012): list predefined blueprints, generate a blueprint from a
/// description via the model, and validate a file blueprint. All require an authenticated caller;
/// blueprints are global (not project-scoped), so no owner check applies here.
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

        // POST /api/blueprints/generate — generate a single blueprint from a description.
        app.MapPost("/api/blueprints/generate", GenerateBlueprintAsync)
            .WithName("GenerateBlueprint")
            .WithTags("Blueprints")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description ??= "Generates a validated blueprint draft from prose, with optional project or repository grounding.";
                return Task.CompletedTask;
            });

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
    /// <response code="200">Returns a validated blueprint draft and any generated workflow YAML.</response>
    /// <response code="400">The request was malformed or referenced an invalid project id.</response>
    /// <response code="401">The configured provider rejected the generation request.</response>
    /// <response code="403">The caller does not own the referenced project.</response>
    /// <response code="404">The referenced project was not found.</response>
    /// <response code="422">The model returned an invalid blueprint draft.</response>
    /// <response code="429">The configured provider rate-limited the generation request.</response>
    /// <response code="502">The downstream model run failed after the request was accepted.</response>
    /// <response code="503">The configured provider or model inventory was unavailable.</response>
    /// <remarks>
    /// This is the fastest way for an agent to bootstrap a castable project shape from prose. When
    /// <c>generated_workflow_yaml</c> is returned, pass it back to project creation so the workflow is materialized.
    /// </remarks>
    public static async Task<IResult> GenerateBlueprintAsync(
        HttpContext httpContext,
        GenerateBlueprintRequest request,
        BlueprintService blueprints,
        IProjectStore projectStore,
        IOptions<GenerationModelOptions> generationOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
            return Results.BadRequest(new { error = "description is required." });

        Project? project = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            if (!ProjectId.TryParse(request.ProjectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });
            project = await projectStore.GetAsync(pid, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();
            if (!httpContext.GetCaller().Owns(project.Owner))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var caller = httpContext.GetCaller();
        var options = generationOptions.Value;
        var result = await blueprints.GenerateAsync(
            request.Description!,
            ct,
            caller.User,
            request.TargetRepository,
            request.ProjectId,
            project is null ? null : options.ResolveBlueprintModel(project.BlueprintGenerationModel),
            project is null ? null : options.ResolveWorkflowModel(project.WorkflowGenerationModel));
        if (!result.Succeeded)
        {
            if (IsProviderFailure(result.FailureKind))
                return Results.Json(new
                {
                    error = result.ErrorCode ?? "blueprint_provider_unavailable",
                    message = result.FailureMessage ?? "Blueprint generation could not reach the configured AI provider or model list. Check provider authentication, entitlement, model access, and configuration, then retry.",
                    details = result.Errors,
                    options = result.FailureKind == BlueprintGenerationFailureKind.ProviderRateLimited
                        ? ["retry"]
                        : new[] { "check_provider_auth", "check_provider_config", "retry" },
                }, statusCode: ProviderFailureStatus(result.FailureKind));

            return Results.UnprocessableEntity(new
            {
                error = "blueprint_generation_failed",
                message = "The generated blueprint could not be validated. You can regenerate with a more specific prompt or edit the draft fields and validate again.",
                details = result.Errors,
                options = new[] { "regenerate", "edit" },
            });
        }

        return Results.Ok(new GenerateBlueprintResponse
        {
            Blueprint = BlueprintDto.FromModel(result.Blueprint!),
            GeneratedWorkflowYaml = result.GeneratedWorkflowYaml,
            Warnings = result.Warnings,
        });
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

        var validation = blueprints.Validate(request.Blueprint.ToModel());
        return Results.Ok(new ValidateBlueprintResponse
        {
            Valid = validation.Valid,
            Errors = validation.Errors,
        });
    }

    private static bool IsProviderFailure(BlueprintGenerationFailureKind kind) =>
        kind is BlueprintGenerationFailureKind.ProviderAuthorization
            or BlueprintGenerationFailureKind.ProviderConfiguration
            or BlueprintGenerationFailureKind.ProviderUnavailable
            or BlueprintGenerationFailureKind.ProviderRateLimited
            or BlueprintGenerationFailureKind.ModelRunFailed;

    private static int ProviderFailureStatus(BlueprintGenerationFailureKind kind) =>
        kind switch
        {
            BlueprintGenerationFailureKind.ProviderAuthorization => StatusCodes.Status401Unauthorized,
            BlueprintGenerationFailureKind.ProviderRateLimited => StatusCodes.Status429TooManyRequests,
            BlueprintGenerationFailureKind.ModelRunFailed => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
}
