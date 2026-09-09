using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

public static class AiExecutionContextEndpoints
{
    public static void MapAiExecutionContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/execution-context", ResolveAsync)
            .WithName("ResolveAiExecutionContext")
            .WithTags("AI execution")
            .AuthenticatedSelfOrMcp();
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext httpContext,
        AiExecutionContextRequest request,
        AiExecutionPlanService plans,
        IProjectStore projectStore,
        IRunStore runStore,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!AiOperationCatalog.TryGet(request.Operation, out var operation))
        {
            return Results.BadRequest(new
            {
                error = "invalid_ai_operation",
                message = "operation must name a supported generative AI action.",
            });
        }

        var caller = httpContext.GetCaller();
        ProjectId? projectId = null;
        Run? run = null;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            if (!ProjectId.TryParse(request.ProjectId, out var parsedProjectId))
                return Results.BadRequest(new { error = "invalid_project_id", message = "project_id is invalid." });

            var project = await projectStore.GetAsync(parsedProjectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound();
            var minimumRole = operation.MinimumProjectRole ?? ProjectRole.Viewer;
            if (await ProjectAuthorization
                .RequireAccessAsync(httpContext, project, configuration, minimumRole, ct)
                .ConfigureAwait(false) is { } denied)
            {
                return denied;
            }
            projectId = parsedProjectId;
        }

        if (!string.IsNullOrWhiteSpace(request.RunId))
        {
            if (!RunId.TryParse(request.RunId, out var parsedRunId))
                return Results.BadRequest(new { error = "invalid_run_id", message = "run_id is invalid." });
            run = await runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            if (run is null)
                return Results.NotFound();
            if (projectId is not null && run.ProjectId != projectId)
                return Results.NotFound();
            if (projectId is null && run.ProjectId is { } runProjectId)
            {
                var project = await projectStore.GetAsync(runProjectId, ct).ConfigureAwait(false);
                if (project is null)
                    return Results.NotFound();
                var minimumRole = operation.MinimumProjectRole ?? ProjectRole.Viewer;
                if (await ProjectAuthorization
                    .RequireAccessAsync(httpContext, project, configuration, minimumRole, ct)
                    .ConfigureAwait(false) is { } denied)
                {
                    return denied;
                }
                projectId = runProjectId;
            }
        }

        if (run is not null)
        {
            if (await EndpointHelpers.RequireRunAccessAsync(
                    httpContext,
                    run,
                    operation.MinimumProjectRole ?? ProjectRole.Viewer,
                    ct).ConfigureAwait(false) is { } runDenied)
            {
                return runDenied;
            }
        }
        var permitsOwnedNonProjectRun = run is { ProjectId: null }
            && string.Equals(operation.Name, "agent_turn", StringComparison.Ordinal);
        if (operation.ResolutionMode == AiResolutionMode.RequiredProject
            && projectId is null
            && !permitsOwnedNonProjectRun)
        {
            return Results.BadRequest(new
            {
                error = "project_id_required",
                message = $"project_id is required for {operation.Name}.",
            });
        }
        if (operation.ResolutionMode is AiResolutionMode.Platform or AiResolutionMode.User
            && projectId is not null)
        {
            return Results.BadRequest(new
            {
                error = "project_id_not_supported",
                message = $"project_id is not used for {operation.Name}.",
            });
        }
        if (operation.RequiresPlatformRole
            && (operation.ResolutionMode != AiResolutionMode.OptionalProject || projectId is null)
            && !(operation.AllowsBroker
                && string.Equals(
                    caller.AuthenticationScheme,
                    AgentweaverAuthenticationSchemes.BrokerBearer,
                    StringComparison.Ordinal))
            && caller.PlatformRoles.Count == 0)
            return Results.Forbid();
        if (!HasRequiredCallerIdentity(operation, caller))
            return Results.Forbid();

        var plan = await plans.PrepareAsync(operation, projectId, caller, ct).ConfigureAwait(false);
        return Results.Ok(plans.ToResponse(plan, "prepared"));
    }

    internal static bool HasRequiredCallerIdentity(
        AiOperationDefinition operation,
        CallerContext caller) =>
        operation.ResolutionMode != AiResolutionMode.User
        || !string.IsNullOrWhiteSpace(caller.EntraObjectId);
}
