using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Security;

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
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "description is required." });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await blueprints.GenerateAsync(
                request.Description!, ct, caller.User, request.TargetRepository);
            if (!result.Succeeded)
                return Results.UnprocessableEntity(new
                {
                    error = "blueprint_generation_failed",
                    message = "The generated blueprint could not be validated.",
                    details = result.Errors,
                });

            return Results.Ok(new GenerateBlueprintResponse
            {
                Blueprint = BlueprintDto.FromModel(result.Blueprint!),
                GeneratedWorkflowYaml = result.GeneratedWorkflowYaml,
                Warnings = result.Warnings,
            });
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
}
