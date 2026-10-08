using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectRequest
{
    public required string Name { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectRequest
{
    public required long ExpectedRevision { get; init; }
    public required string Name { get; init; }
    public required ProjectLifecycleState State { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectConfigurationRequest
{
    public required long ExpectedRevision { get; init; }
    public required ProjectConfiguration Configuration { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePlatformRuntimeDefaultsRequest
{
    public required long ExpectedRevision { get; init; }
    public required PlatformRuntimeDefaults Defaults { get; init; }
}

public static class ProjectConfigEndpoints
{
    public static IEndpointRouteBuilder MapProjectConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var authorization = app.MapGroup("/api/authorization").RequireAuthorization();
        authorization.MapGet("/context", async (
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Forbidden();
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(caller.ToEffectiveResponse());
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("GetAuthorizationContext");

        var projects = app.MapGroup("/api/projects").RequireAuthorization();

        projects.MapPost("/", async (
            CreateProjectRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await service.CreateProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    request.Name,
                    cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/projects/{created.ProjectId}", created);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/", async (
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListProjectsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}", async (
            string projectId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "runId") ||
                    context.Request.Query["runId"].Count > 1)
                    throw ProjectConfigException.Forbidden();
                var runId = context.Request.Query["runId"].FirstOrDefault();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.GetProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    runId,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("GetProject");

        projects.MapPatch("/{projectId}", async (
            string projectId,
            UpdateProjectRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedRevision,
                    request.Name,
                    request.State,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/configuration", async (
            string projectId,
            long? revision,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetProjectConfigurationAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    revision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPut("/{projectId}/configuration", async (
            string projectId,
            UpdateProjectConfigurationRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateProjectConfigurationAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedRevision,
                    request.Configuration,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPut("/{projectId}/runs/{runId}/selection", async (
            string projectId,
            string runId,
            AcceptRunSelectionRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.AcceptRunSelectionAsync(
                    caller, projectId, runId, request, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/runs/{runId}/selection", async (
            string projectId,
            string runId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.GetRunSelectionAsync(
                    caller, projectId, runId, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        var platform = app.MapGroup("/api/platform/runtime-defaults").RequireAuthorization();
        platform.MapGet("/", async (
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetPlatformRuntimeDefaultsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });
        platform.MapPut("/", async (
            UpdatePlatformRuntimeDefaultsRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdatePlatformRuntimeDefaultsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    request.ExpectedRevision,
                    request.Defaults,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        return app;
    }

    private static IResult ToProblem(ProjectConfigException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        CancellationToken cancellationToken) =>
        authorizationOwner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);
}
