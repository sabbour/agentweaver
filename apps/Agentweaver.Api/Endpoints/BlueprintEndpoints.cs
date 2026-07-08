using Agentweaver.Api.Blueprints;
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
    public static void MapBlueprintEndpoints(this WebApplication app)
    {
        // GET /api/blueprints — list the predefined blueprints.
        app.MapGet("/api/blueprints", (BlueprintService blueprints) =>
        {
            var list = blueprints.GetPredefined()
                .Select(BlueprintDto.FromModel)
                .ToList();
            return Results.Ok(new ListBlueprintsResponse { Blueprints = list });
        });

        // POST /api/blueprints/generate — generate a single blueprint from a description.
        app.MapPost("/api/blueprints/generate", async (
            HttpContext httpContext,
            GenerateBlueprintRequest request,
            BlueprintService blueprints,
            IProjectStore projectStore,
            IOptions<GenerationModelOptions> generationOptions,
            CancellationToken ct) =>
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
                if (!ApiKeyAuthMiddleware.GetCaller(httpContext).Owns(project.Owner))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
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
                            ? new[] { "retry" }
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
        });

        // POST /api/blueprints/suggest — analyze a GitHub repository and recommend a catalog blueprint.
        app.MapPost("/api/blueprints/suggest", async (
            HttpContext httpContext,
            SuggestBlueprintRequest request,
            GitHubRepoBlueprintSuggestionService suggestions,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Repository))
                return Results.BadRequest(new { error = "repository is required." });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await suggestions
                .SuggestAsync(request.Repository!, caller.User, ct)
                .ConfigureAwait(false);
            return Results.Ok(result);
        });

        // POST /api/blueprints/validate — validate a file blueprint against the schema + role constraint.
        app.MapPost("/api/blueprints/validate", (
            ValidateBlueprintRequest request,
            BlueprintService blueprints) =>
        {
            if (request.Blueprint is null)
                return Results.BadRequest(new { error = "blueprint is required." });

            var validation = blueprints.Validate(request.Blueprint.ToModel());
            return Results.Ok(new ValidateBlueprintResponse
            {
                Valid = validation.Valid,
                Errors = validation.Errors,
            });
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
