using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

public static class ProjectGitHubIdentityEndpoints
{
    public static void MapProjectGitHubIdentityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId}/github-identity", async (
            HttpContext httpContext,
            string projectId,
            IProjectStore projectStore,
            IProjectRoleAuthorizationService authorizationService,
            ProjectGitHubIdentityService identityService,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var parsedProjectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
                return Results.Conflict(new { error = "Linked GitHub identities require Entra sign-in." });

            var project = await projectStore.GetAsync(parsedProjectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound();

            if (!await authorizationService.HasRoleAsync(caller, parsedProjectId, ProjectRole.Viewer, ct).ConfigureAwait(false))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var effective = await identityService.GetEffectiveIdentityAsync(parsedProjectId, caller.EntraObjectId!, ct).ConfigureAwait(false);
            return Results.Ok(MapResponse(projectId, effective));
        });

        app.MapPut("/api/projects/{projectId}/github-identity", async (
            HttpContext httpContext,
            string projectId,
            UpdateProjectGitHubIdentityRequest request,
            IProjectStore projectStore,
            IProjectRoleAuthorizationService authorizationService,
            ProjectGitHubIdentityService identityService,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var parsedProjectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
                return Results.Conflict(new { error = "Linked GitHub identities require Entra sign-in." });

            var project = await projectStore.GetAsync(parsedProjectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound();

            if (!await authorizationService.HasRoleAsync(caller, parsedProjectId, ProjectRole.Contributor, ct).ConfigureAwait(false))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                await identityService.SetOverrideAsync(parsedProjectId, caller.EntraObjectId!, request?.GitHubLogin, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            return Results.NoContent();
        });
    }

    private static ProjectGitHubIdentityResponse MapResponse(
        string projectId,
        EffectiveProjectGitHubIdentity effective) => new()
    {
        ProjectId = projectId,
        ProjectOverrideLogin = effective.OverrideLogin,
        EffectiveLogin = effective.EffectiveLink?.GitHubLogin,
        EffectiveAvatarUrl = effective.EffectiveLink?.AvatarUrl,
        CopilotEntitled = effective.EffectiveLink?.CopilotEntitled,
        IsDefault = effective.EffectiveLink?.IsDefault,
        LinkedAt = effective.EffectiveLink?.LinkedAt,
        ResolutionSource = effective.ResolutionSource,
    };
}
