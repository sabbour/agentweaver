using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agentweaver.Api.OpenApi;

internal sealed class BearerSecuritySchemeDocumentTransformer : IOpenApiDocumentTransformer
{
    internal const string BearerSchemeName = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Agentweaver API";
        document.Info.Version = context.DocumentName;
        document.Info.Description =
            "REST API for project creation, team casting, coordinator-run orchestration, review, and memory/decision workflows. " +
            "Protected operations require an Authorization header using either a GitHub bearer token or an Agentweaver-issued OAuth bearer token.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[BearerSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT or GitHub token",
            Name = "Authorization",
            Description =
                "Provide `Authorization: Bearer <token>`. Most API routes accept either a GitHub bearer token or an Agentweaver OAuth access token minted by this service.",
        };

        return Task.CompletedTask;
    }
}

internal sealed class BearerSecurityRequirementOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var path = "/" + (context.Description.RelativePath?.TrimStart('/') ?? string.Empty);
        var httpMethod = context.Description.HttpMethod ?? string.Empty;
        if (TryGetFallbackDescription(path, httpMethod, out var description))
        {
            operation.Description ??= description;
        }

        if (IsPublicPath(path))
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(
                BearerSecuritySchemeDocumentTransformer.BearerSchemeName,
                context.Document,
                BearerSecuritySchemeDocumentTransformer.BearerSchemeName)] = []
        });

        return Task.CompletedTask;
    }

    private static bool IsPublicPath(string path) =>
        path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/version", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/session/exchange", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/oauth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetFallbackDescription(string path, string httpMethod, out string description)
    {
        description = (path, httpMethod.ToUpperInvariant()) switch
        {
            ("/api/projects", "POST") => "Creates a project from a blank workspace or GitHub repository, optionally applying a blueprint and generated workflow atomically.",
            ("/api/projects", "GET") => "Lists the authenticated caller's projects with pagination metadata.",
            ("/api/projects/{id}", "GET") => "Returns one project's current metadata, ownership, and model defaults.",
            ("/api/projects/{id}/orchestrations", "POST") => "Starts a coordinator run for the project using either defineOutcome or direct planning mode.",
            ("/api/blueprints", "GET") => "Lists the built-in blueprint catalog that can seed new projects.",
            ("/api/blueprints/generate", "POST") => "Generates a validated blueprint draft from prose, with optional project or repository grounding.",
            ("/api/blueprints/suggest", "POST") => "Recommends the closest catalog blueprint for a target GitHub repository.",
            ("/api/blueprints/validate", "POST") => "Validates a blueprint payload without mutating any project state.",
            ("/api/casting/templates", "GET") => "Lists reusable casting templates for common scenario-driven team shapes.",
            ("/api/catalog/roles", "GET") => "Lists the role archetypes available for manual or generated team proposals.",
            ("/api/projects/{id}/casting/proposals", "POST") => "Creates a draft team proposal for a project from scenario, free-text, analysis, or manual role inputs.",
            ("/api/projects/{id}/casting/proposals/{proposalId}/confirm", "POST") => "Confirms a draft casting proposal and materializes it into the project's live team.",
            ("/api/runs/{id}/outcome-spec", "GET") => "Returns the current drafted outcome spec for a coordinator run before execution proceeds.",
            ("/api/runs/{id}/outcome-spec/confirm", "POST") => "Confirms the drafted outcome spec so the coordinator can decompose it into a work plan.",
            ("/api/runs/{id}/outcome-spec/revise", "POST") => "Requests a revised outcome spec while keeping the coordinator parked at the gate.",
            ("/api/runs/{coordinatorRunId}/work-plan", "GET") => "Returns the persisted coordinator work plan, including subtasks, dependencies, and assembly status.",
            ("/api/runs/{coordinatorRunId}/children", "GET") => "Lists child runs currently attached to the coordinator work plan.",
            ("/api/runs/{coordinatorRunId}/steer", "POST") => "Sends a steering directive to a running coordinator or one of its child runs.",
            ("/api/runs/{coordinatorRunId}/assembly/review", "POST") => "Submits the single collective human review decision for an assembled coordinator run.",
            _ => string.Empty,
        };

        return description.Length > 0;
    }
}
