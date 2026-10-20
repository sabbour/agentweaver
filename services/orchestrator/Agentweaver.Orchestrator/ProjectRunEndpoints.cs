using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Maps project-scoped run reads that do not require a run-bound token.
/// </summary>
public static class ProjectRunEndpoints
{
    private static readonly HashSet<string> ListQueryNames =
        new(["state", "cursor", "pageSize"], StringComparer.Ordinal);

    /// <summary>
    /// Registers <c>GET /api/projects/{projectId}/runs</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapProjectRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/runs", ListRunsAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Lists the project's runs newest first after a fresh Projects read-authority check.
    /// </summary>
    /// <remarks>
    /// An unbound or project-bound caller sees every run in the project. A run-bound caller sees only its run.
    /// Unknown or repeated query parameters return 400.
    /// </remarks>
    private static Task<IResult> ListRunsAsync(
        string projectId,
        HttpContext context,
        ProjectsRunSelectionClient projects,
        ProjectRunReadStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = ReadSingleValuedQuery(context.Request.Query, ListQueryNames);
            var states = ProjectRunReadStore.ParseStateFilter(query.GetValueOrDefault("state"));
            var pageSize = ParsePageSize(query.GetValueOrDefault("pageSize"));
            var authorization = await projects.ReadProjectReadAuthorityAsync(
                context, projectId, cancellationToken).ConfigureAwait(false);
            var page = await store.ListAsync(
                authorization.TenantId,
                projectId,
                authorization.BoundRunId,
                states,
                query.GetValueOrDefault("cursor"),
                pageSize,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(page);
        }, cancellationToken);

    /// <summary>
    /// Reads query parameters, rejecting names outside <paramref name="allowed"/> and repeated values.
    /// </summary>
    /// <exception cref="CoordinationException">A parameter is unknown or repeated.</exception>
    internal static Dictionary<string, string> ReadSingleValuedQuery(IQueryCollection query, HashSet<string> allowed)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in query)
        {
            if (!allowed.Contains(name) || value.Count != 1 || value[0] is null)
                throw new CoordinationException("query_invalid", StatusCodes.Status400BadRequest);
            values[name] = value[0]!;
        }
        return values;
    }

    /// <summary>
    /// Parses an optional page size, defaulting to <see cref="ProjectRunReadStore.DefaultPageSize"/>.
    /// </summary>
    /// <exception cref="CoordinationException">The value is not an integer from 1 to the maximum.</exception>
    internal static int ParsePageSize(string? value)
    {
        if (value is null)
            return ProjectRunReadStore.DefaultPageSize;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize is < 1 or > ProjectRunReadStore.MaximumPageSize)
            throw new CoordinationException("page_size_invalid", StatusCodes.Status400BadRequest);
        return pageSize;
    }
}
