using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Projects.Config;

public static class IssueSyncAuthorityEndpoints
{
    internal const string Channel = "IssueSyncIdentity";

    public static void MapIssueSyncAuthority(this IEndpointRouteBuilder app, bool internalEnabled)
    {
        app.MapGet("/api/projects/{projectId}/issue-sync-owner", async (
            string projectId, HttpContext context, ProjectAuthorizationOwner owner,
            ProjectsConfigService projects, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var actor = await owner.ResolveAsync(context.User,
                    context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(), ct);
                actor.RequireUnboundRequest();
                actor.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
                if (!actor.HasRole(ProjectAuthorityResourceType.Project, projectId, ProjectAuthorityRole.Owner))
                    throw ProjectConfigException.Forbidden();
                await projects.GetProjectAsync(actor, projectId, ct);
                return Results.Ok(new IssueSyncOwnerProof(projectId, actor.TenantId, actor.ActorId));
            }
            catch (ProjectConfigException)
            { return Results.Json(new { error = "issue_sync_creator_not_owner" }, statusCode: 403); }
        }).RequireAuthorization();
        app.MapGet("/api/projects/{projectId}/issue-sync-grant-revocation", async (
            string projectId, HttpContext context, ProjectAuthorizationOwner owner,
            ProjectsConfigService projects, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var actor = await owner.ResolveAsync(context.User,
                    context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(), ct);
                actor.RequireUnboundRequest();
                actor.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
                var admin = actor.HasRole(ProjectAuthorityResourceType.Tenant, actor.TenantId, ProjectAuthorityRole.TenantAdmin);
                if (!admin && !actor.HasRole(ProjectAuthorityResourceType.Project, projectId, ProjectAuthorityRole.Owner))
                    throw ProjectConfigException.Forbidden();
                await projects.GetProjectAsync(actor, projectId, ct);
                return Results.Ok(new IssueSyncRevocationProof(projectId, actor.TenantId, actor.ActorId, admin));
            }
            catch (ProjectConfigException)
            { return Results.Json(new { error = "issue_sync_grant_revocation_denied" }, statusCode: 403); }
        }).RequireAuthorization();
        app.MapGet("/api/projects/{projectId}/issue-sync-authority", async (
            string projectId, HttpContext context, ProjectAuthorizationOwner owner,
            ProjectsConfigService projects, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var actor = await owner.ResolveAsync(context.User,
                    context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(), ct);
                actor.RequireUnboundRequest();
                actor.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
                return Results.Ok(await ReadProofAsync(actor, projectId, projects, ct));
            }
            catch (ProjectConfigException)
            {
                return Results.Json(new { error = "issue_sync_creator_not_owner" }, statusCode: 403);
            }
            catch (RuntimeAuthorizationException error)
            {
                return Results.Json(new { error = error.Code }, statusCode: 409);
            }
        }).RequireAuthorization();
        if (!internalEnabled)
            return;
        app.MapPost("/internal/issue-sync/project-authority", async (
            IssueSyncAuthorityQuery query, HttpContext context, ProjectAuthorizationOwner owner,
            ProjectsConfigService projects, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                IssueSyncServiceTransport.RequirePeer(context, Channel);
                if (!Guid.TryParseExact(query.CreatorId, "D", out var creator) || creator == Guid.Empty)
                    throw new RuntimeAuthorizationException("issue_sync_creator_invalid");
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new("sub", query.CreatorId), new("tenant_id", query.TenantId),
                    new("scope", $"{ProjectAuthorizationOwner.ApiReadScope} {ProjectAuthorizationOwner.ProjectAdminScope}")
                ], "IssueSyncIdentity"));
                var actor = await owner.ResolveAsync(principal, [query.TenantId], ct);
                return Results.Ok(await ReadProofAsync(actor, query.ProjectId, projects, ct));
            }
            catch (ProjectConfigException)
            {
                return Results.Json(new { error = "issue_sync_creator_not_owner" }, statusCode: 403);
            }
            catch (RuntimeAuthorizationException error)
            {
                return Results.Json(new { error = error.Code }, statusCode: 403);
            }
        }).AllowAnonymous();
    }

    private static async Task<IssueSyncProjectProof> ReadProofAsync(
        ProjectAuthorizationContext actor, string projectId, ProjectsConfigService projects, CancellationToken ct)
    {
        if (!actor.HasRole(ProjectAuthorityResourceType.Project, projectId, ProjectAuthorityRole.Owner))
            throw new RuntimeAuthorizationException("issue_sync_creator_not_owner");
        var project = await projects.GetProjectAsync(actor, projectId, ct);
        if (project.State != ProjectLifecycleState.Active)
            throw new RuntimeAuthorizationException("issue_sync_project_inactive");
        var configuration = await projects.GetProjectConfigurationAsync(actor, projectId, null, ct);
        var source = configuration.Configuration.SourceControl;
        if (source?.AuthMode != SourceControlAuthMode.GitHubApp || source.IdentityConnectionId is null)
            throw new RuntimeAuthorizationException("issue_sync_repository_not_selected");
        if (configuration.Configuration.ProviderOverrides.Any(provider =>
            provider.Seam == ProviderSeam.SourceControl && provider.ProviderId != SourceControlProviderIds.GitHub))
            throw new RuntimeAuthorizationException("issue_sync_provider_unavailable");
        return new(projectId, actor.TenantId, actor.ActorId, source.IdentityConnectionId, source.Repository);
    }
}
