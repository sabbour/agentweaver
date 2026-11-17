namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Maps project-scoped, no-store Azure Retail price searches.</summary>
public static class AzureRetailPriceEndpoints
{
    /// <summary>Registers the authenticated model pricing search route.</summary>
    public static IEndpointRouteBuilder MapAzureRetailPriceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/projects/{projectId}/model-pricing/azure-retail",
            SearchAsync).RequireAuthorization();
        return app;
    }

    /// <summary>Rejects unknown or repeated query values before authorizing and searching.</summary>
    internal static (string Region, string Search) ReadQuery(IQueryCollection query)
    {
        if (query.Count != 2 ||
            !query.TryGetValue("region", out var regions) || regions.Count != 1 || regions[0] is null ||
            !query.TryGetValue("search", out var searches) || searches.Count != 1 || searches[0] is null)
            throw new ArgumentException("query_invalid");
        return (regions[0]!, searches[0]!);
    }

    /// <summary>Checks current Projects authority before making the bounded Azure Retail request.</summary>
    internal static async Task<IResult> SearchAsync(
        string projectId,
        HttpContext context,
        ProjectsAuthorizationContextClient projects,
        AzureRetailPriceLookup lookup,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var (region, search) = ReadQuery(context.Request.Query);
            var audience = configuration["Identity:Audience"]
                ?? throw new InvalidOperationException("Missing Events audience.");
            await projects.AuthorizeModelPricingSearchAsync(
                context, projectId, audience, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await lookup.SearchAsync(region, search, cancellationToken).ConfigureAwait(false));
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Problem(statusCode: (int)exception.StatusCode, title: exception.Code);
        }
        catch (AzureRetailPriceLookupException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: exception.Code);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "query_invalid",
                extensions: new Dictionary<string, object?> { ["code"] = "query_invalid" });
        }
    }
}
