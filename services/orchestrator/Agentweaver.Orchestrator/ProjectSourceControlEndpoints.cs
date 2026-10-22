using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Maps project-scoped Source Control list reads over data that Agentweaver already stored.
/// </summary>
/// <remarks>
/// These reads make no source control provider call and use no provider credential. They read the
/// issues that periodic issue sync stored in the backlog and the pull requests that runs recorded
/// when they created or reused one. Every response uses <c>Cache-Control: no-store</c>.
/// </remarks>
public static class ProjectSourceControlEndpoints
{
    private static readonly HashSet<string> ListQueryNames =
        new(["state", "cursor", "pageSize"], StringComparer.Ordinal);

    /// <summary>
    /// Registers <c>GET /api/projects/{projectId}/source-control/issues</c> and
    /// <c>GET /api/projects/{projectId}/source-control/pull-requests</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapProjectSourceControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/source-control/issues", ListIssuesAsync).RequireAuthorization();
        app.MapGet("/api/projects/{projectId}/source-control/pull-requests", ListPullRequestsAsync)
            .RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Lists synced project issues after a fresh Projects backlog read-authority check.
    /// </summary>
    /// <remarks>
    /// Issues are backlog content, so this read uses the same authority as the project backlog read.
    /// It requires <c>api.read</c> and current <c>readProjects</c> on the project or tenant, and it
    /// rejects run-bound tokens. Authority is checked again after the read, so permission lost during
    /// the read returns 403 and no items. Unknown or repeated query parameters return 400.
    /// </remarks>
    internal static Task<IResult> ListIssuesAsync(
        string projectId,
        HttpContext context,
        ProjectsRunSelectionClient projects,
        ProjectIssueReadStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            // Set no-store first so error responses are not cached either.
            context.Response.Headers.CacheControl = "no-store";
            // Validate all query input before any authority call or database read. Unknown or
            // repeated keys fail here, so a typo can't silently fall back to a default filter.
            var query = ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, ListQueryNames);
            var state = ProjectIssueReadStore.ParseStateFilter(query.GetValueOrDefault("state"));
            var pageSize = ProjectRunEndpoints.ParsePageSize(query.GetValueOrDefault("pageSize"));
            // Ask Projects for the caller's current read authority. Projects, not a token claim,
            // decides the tenant, so the store can only read rows in that tenant and project.
            var authorization = await projects.ReadBacklogProjectAuthorityAsync(
                context, projectId, write: false, cancellationToken).ConfigureAwait(false);
            var page = await store.ListAsync(
                authorization.TenantId,
                projectId,
                state,
                query.GetValueOrDefault("cursor"),
                pageSize,
                cancellationToken).ConfigureAwait(false);
            // Check authority again after the read. If the caller lost permission while the read ran,
            // the check throws 403 and the page is dropped. A tenant change also counts as a loss,
            // because the page was read under the old tenant.
            var current = await projects.ReadBacklogProjectAuthorityAsync(
                context, projectId, write: false, cancellationToken).ConfigureAwait(false);
            if (current.TenantId != authorization.TenantId)
                throw new CoordinationException("backlog_project_permission_denied", StatusCodes.Status403Forbidden);
            return Results.Ok(page);
        }, cancellationToken);

    /// <summary>
    /// Lists pull requests recorded by the project's runs after a fresh Projects read-authority check.
    /// </summary>
    /// <remarks>
    /// Uses the same authority as the project run list. An unbound or project-bound caller sees every
    /// recorded pull request in the project. A run-bound caller sees only those its run recorded.
    /// The list covers only pull requests that runs created or reused after migration 032 was applied;
    /// the response states that start time. Before 032 is applied, the read returns 503
    /// <c>pull_request_records_unavailable</c>, not an empty list. Authority is checked again after the
    /// read, so permission lost during the read returns 403 and no items. Unknown or repeated query
    /// parameters return 400.
    /// </remarks>
    internal static Task<IResult> ListPullRequestsAsync(
        string projectId,
        HttpContext context,
        ProjectsRunSelectionClient projects,
        ProjectPullRequestReadStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            // Same order as the issue list: no-store, then strict query validation before any read.
            context.Response.Headers.CacheControl = "no-store";
            var query = ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, ListQueryNames);
            var state = ProjectPullRequestReadStore.ParseStateFilter(query.GetValueOrDefault("state"));
            var pageSize = ProjectRunEndpoints.ParsePageSize(query.GetValueOrDefault("pageSize"));
            // The run-list authority also reports a bound run. A run-bound token gets a BoundRunId,
            // and the store then returns only that run's records.
            var authorization = await projects.ReadProjectReadAuthorityAsync(
                context, projectId, cancellationToken).ConfigureAwait(false);
            // The store returns 503 pull_request_records_unavailable when migration 032 isn't applied.
            var page = await store.ListAsync(
                authorization.TenantId,
                projectId,
                authorization.BoundRunId,
                state,
                query.GetValueOrDefault("cursor"),
                pageSize,
                cancellationToken).ConfigureAwait(false);
            // Recheck after the read. A changed tenant or run binding means the page was read under
            // authority the caller no longer has, so return 403 and drop the page.
            var current = await projects.ReadProjectReadAuthorityAsync(
                context, projectId, cancellationToken).ConfigureAwait(false);
            if (current.TenantId != authorization.TenantId || current.BoundRunId != authorization.BoundRunId)
                throw new CoordinationException("project_read_permission_denied", StatusCodes.Status403Forbidden);
            return Results.Ok(page);
        }, cancellationToken);
}
