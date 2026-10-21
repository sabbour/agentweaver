using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions;

/// <summary>Maps project-scoped, no-store usage reads with fresh Projects authority.</summary>
public static class ProjectUsageEndpoints
{
    /// <summary>Registers the usage owner route independently of native ingestion configuration.</summary>
    public static IEndpointRouteBuilder MapProjectUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/usage", ReadAsync).RequireAuthorization();
        return app;
    }

    /// <summary>Rejects unknown or repeated query values and reads a bounded UTC interval.</summary>
    internal static ProjectMetricsRange ReadQuery(IQueryCollection query, DateTimeOffset now)
    {
        foreach (var (name, value) in query)
            if (name is not ("from" or "to") || value.Count != 1 || value[0] is null)
                throw new ArgumentException("query_invalid");
        return ProjectMetricsRange.Parse(query["from"].SingleOrDefault(), query["to"].SingleOrDefault(), null, now);
    }

    /// <summary>Returns stable query failures, then authorizes and narrows reads for run-bound callers.</summary>
    internal static async Task<IResult> ReadAsync(
        string projectId, HttpContext context, ProjectsAuthorizationContextClient projects,
        ProjectUsageReadStore store, IConfiguration configuration, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var range = ReadQuery(context.Request.Query, timeProvider.GetUtcNow());
            var audience = configuration["Identity:Audience"]
                ?? throw new InvalidOperationException("Missing Events audience.");
            var current = await projects.GetProjectReadAsync(context, projectId, audience, cancellationToken);
            return Results.Ok(await store.ReadAsync(
                current.TenantId, projectId, current.BoundRunId, range, cancellationToken));
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Problem(statusCode: (int)exception.StatusCode, title: exception.Code);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "query_invalid",
                extensions: new Dictionary<string, object?> { ["code"] = "query_invalid" });
        }
    }
}
