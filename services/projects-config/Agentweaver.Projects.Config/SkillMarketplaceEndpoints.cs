namespace Agentweaver.Projects.Config;

public static class SkillMarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapSkillMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        var sources = app.MapGroup("/api/projects/{projectId}/skill-marketplaces/sources")
            .RequireAuthorization();

        sources.MapGet("/", async (
            string projectId,
            HttpContext context,
            ProjectMarketplaceSourceService service,
            ProjectAuthorizationOwner authorizationOwner,
            bool? includeRemoved,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "includeRemoved") ||
                    context.Request.Query["includeRemoved"].Count > 1)
                    throw MarketplaceSourceException.InvalidRequest("Only one includeRemoved query parameter is supported.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var result = await service.ListAsync(
                    caller, projectId, includeRemoved ?? false, cancellationToken).ConfigureAwait(false);
                return Results.Ok(result.Select(MarketplaceSourceView.From));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (MarketplaceSourceException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("ListProjectMarketplaceSources");

        sources.MapPost("/", async (
            string projectId,
            CreateMarketplaceSourceRequest request,
            HttpContext context,
            ProjectMarketplaceSourceService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var source = await service.CreateAsync(caller, projectId, request, cancellationToken)
                    .ConfigureAwait(false);
                var view = MarketplaceSourceView.From(source);
                return Results.Created(
                    $"/api/projects/{Uri.EscapeDataString(projectId)}/skill-marketplaces/sources/{source.SourceId:D}",
                    view);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (MarketplaceSourceException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("CreateProjectMarketplaceSource");

        sources.MapPut("/{sourceId}", async (
            string projectId,
            string sourceId,
            UpdateMarketplaceSourceRequest request,
            HttpContext context,
            ProjectMarketplaceSourceService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var parsedSourceId = ParseSourceId(sourceId);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var source = await service.UpdateAsync(
                    caller, projectId, parsedSourceId, request, cancellationToken).ConfigureAwait(false);
                return Results.Ok(MarketplaceSourceView.From(source));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (MarketplaceSourceException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("UpdateProjectMarketplaceSource");

        sources.MapDelete("/{sourceId}", async (
            string projectId,
            string sourceId,
            HttpContext context,
            ProjectMarketplaceSourceService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "expectedRevision") ||
                    context.Request.Query["expectedRevision"].Count != 1 ||
                    !long.TryParse(context.Request.Query["expectedRevision"], out var expectedRevision))
                    throw MarketplaceSourceException.InvalidRequest("One expectedRevision query parameter is required.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var source = await service.RemoveAsync(
                    caller,
                    projectId,
                    ParseSourceId(sourceId),
                    expectedRevision,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(MarketplaceSourceView.From(source));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (MarketplaceSourceException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("RemoveProjectMarketplaceSource");

        sources.MapGet("/{sourceId}/browse", async (
            string projectId,
            string sourceId,
            HttpContext context,
            ProjectMarketplaceSourceService sourceService,
            SkillMarketplaceBrowseService browseService,
            ProjectsConfigService projects,
            ProjectAuthorizationOwner authorizationOwner,
            long? expectedSourceRevision,
            string? query,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key =>
                        key is not ("expectedSourceRevision" or "query" or "page" or "pageSize")) ||
                    context.Request.Query.Any(item => item.Value.Count > 1))
                    throw MarketplaceSourceException.InvalidRequest("The browse query contains an unsupported or repeated parameter.");
                if (expectedSourceRevision is null)
                    throw MarketplaceSourceException.InvalidRequest("Expected source revision is required.");

                var parsedSourceId = ParseSourceId(sourceId);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var source = await sourceService.GetForBrowseAsync(
                    caller, projectId, parsedSourceId, expectedSourceRevision.Value, cancellationToken)
                    .ConfigureAwait(false);
                var result = await browseService.BrowseAsync(
                    projectId,
                    source,
                    new MarketplaceBrowseRequest
                    {
                        ExpectedSourceRevision = expectedSourceRevision.Value,
                        Query = query,
                        Page = page ?? 1,
                        PageSize = pageSize ?? SkillMarketplaceBrowseService.DefaultPageSize,
                    },
                    async checkCancellation =>
                    {
                        var refreshed = await ResolveCallerAsync(
                                context, authorizationOwner, checkCancellation)
                            .ConfigureAwait(false);
                        if (!SameActor(caller, refreshed))
                            throw MarketplaceSourceException.Forbidden();
                        _ = await projects.GetMarketplaceProjectAsync(
                                refreshed, projectId, requireWrite: false, checkCancellation)
                            .ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (MarketplaceSourceException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("BrowseProjectMarketplaceSource");

        return app;
    }

    private static Guid ParseSourceId(string sourceId) =>
        Guid.TryParse(sourceId, out var parsed)
            ? parsed
            : throw MarketplaceSourceException.NotFound();

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        CancellationToken cancellationToken) =>
        authorizationOwner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);

    private static bool SameActor(
        ProjectAuthorizationContext original,
        ProjectAuthorizationContext current) =>
        string.Equals(original.Issuer, current.Issuer, StringComparison.Ordinal) &&
        string.Equals(original.ActorId, current.ActorId, StringComparison.Ordinal) &&
        string.Equals(original.TenantId, current.TenantId, StringComparison.Ordinal) &&
        original.MembershipId == current.MembershipId &&
        original.MembershipRevision == current.MembershipRevision;

    private static IResult ToProblem(ProjectConfigException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

    private static IResult ToProblem(MarketplaceSourceException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}
