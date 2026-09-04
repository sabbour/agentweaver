using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Pre-invocation provider resolution for every first-party generative AI action. This endpoint
/// never accepts prompt/content and never starts execution; it gives the UI an authoritative,
/// redacted provider context to display before the user commits the action.
/// </summary>
public static class AiExecutionContextEndpoints
{
    private enum ResolutionMode
    {
        RequiredProject,
        OptionalProject,
        Platform,
        User,
    }

    private static readonly IReadOnlyDictionary<string, ResolutionMode> Operations =
        new Dictionary<string, ResolutionMode>(StringComparer.Ordinal)
        {
            ["orchestration"] = ResolutionMode.RequiredProject,
            ["blueprint_generation"] = ResolutionMode.OptionalProject,
            ["workflow_generation"] = ResolutionMode.RequiredProject,
            ["skill_generation"] = ResolutionMode.RequiredProject,
            ["agent_generation"] = ResolutionMode.RequiredProject,
            ["team_generation"] = ResolutionMode.RequiredProject,
            ["casting_generation"] = ResolutionMode.RequiredProject,
            ["backlog_decomposition"] = ResolutionMode.RequiredProject,
            ["marketplace_catalog_classification"] = ResolutionMode.RequiredProject,
            ["outcome_spec_generation"] = ResolutionMode.RequiredProject,
            ["workflow_selection"] = ResolutionMode.RequiredProject,
            ["story_independence_classification"] = ResolutionMode.RequiredProject,
            ["assembly_gate_classification"] = ResolutionMode.RequiredProject,
            ["preview_classification"] = ResolutionMode.RequiredProject,
            ["preview_command_generation"] = ResolutionMode.RequiredProject,
            ["agent_turn"] = ResolutionMode.RequiredProject,
            ["rai"] = ResolutionMode.RequiredProject,
            ["rubberduck"] = ResolutionMode.RequiredProject,
            ["build_test"] = ResolutionMode.RequiredProject,
            ["scribe"] = ResolutionMode.RequiredProject,
            ["assistant_turn"] = ResolutionMode.Platform,
            ["user_session"] = ResolutionMode.User,
        };

    public static void MapAiExecutionContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/execution-context", ResolveAsync)
            .WithName("ResolveAiExecutionContext")
            .WithTags("AI execution")
            .AuthenticatedPlatform();
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext httpContext,
        AiExecutionContextRequest request,
        EffectiveModelProviderResolver resolver,
        IProjectStore projectStore,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var operation = request.Operation?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation) || !Operations.TryGetValue(operation, out var mode))
        {
            return Results.BadRequest(new
            {
                error = "invalid_ai_operation",
                message = "operation must name a supported generative AI action.",
            });
        }

        ProjectId? projectId = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            if (!ProjectId.TryParse(request.ProjectId, out var parsedProjectId))
                return Results.BadRequest(new { error = "invalid_project_id", message = "project_id is invalid." });

            var project = await projectStore.GetAsync(parsedProjectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound();
            if (await ProjectAuthorization
                .RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct)
                .ConfigureAwait(false) is { } denied)
            {
                return denied;
            }
            projectId = parsedProjectId;
        }

        if (mode == ResolutionMode.RequiredProject && projectId is null)
        {
            return Results.BadRequest(new
            {
                error = "project_id_required",
                message = $"project_id is required for {operation}.",
            });
        }
        if (mode is ResolutionMode.Platform or ResolutionMode.User && projectId is not null)
        {
            return Results.BadRequest(new
            {
                error = "project_id_not_supported",
                message = $"project_id is not used for {operation}.",
            });
        }

        var caller = httpContext.GetCaller();
        EffectiveModelProviderResult effective;
        string resolutionScope;
        if (mode == ResolutionMode.User)
        {
            if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
                return Results.Forbid();
            effective = await resolver.ResolveForSessionAsync(caller.EntraObjectId, ct).ConfigureAwait(false);
            resolutionScope = EffectiveModelProviderProvenance.ScopeUser;
        }
        else
        {
            var resolutionProjectId = mode is ResolutionMode.RequiredProject or ResolutionMode.OptionalProject
                ? projectId
                : null;
            effective = await resolver.ResolveAsync(resolutionProjectId, ct).ConfigureAwait(false);
            resolutionScope = resolutionProjectId is null
                ? EffectiveModelProviderProvenance.ScopePlatform
                : EffectiveModelProviderProvenance.ScopeProject;
        }

        return Results.Ok(new AiExecutionContextResponse
        {
            AiRequired = true,
            Operation = operation,
            Phase = "prepared",
            EffectiveModelProvider = effective.ToContract(resolutionScope),
        });
    }
}
