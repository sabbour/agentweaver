using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Exposes caller attention to the existing bell and Chief of Staff views.</summary>
public static class UserNotificationEndpoints
{
    /// <summary>Maps caller-scoped list and idempotent dismissal routes.</summary>
    public static IEndpointRouteBuilder MapUserNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", ListAsync).RequireAuthorization();
        app.MapPost("/api/notifications/{notificationId}/dismiss", DismissAsync).RequireAuthorization();
        return app;
    }

    private static Task<IResult> ListAsync(
        HttpContext context, ProjectsRunSelectionClient projects, UserNotificationStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (size, cursor, projectId, includeDismissed) = ParseQuery(context.Request.Query);
            var authority = await projects.ReadNotificationAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
            var page = await store.ListAsync(
                authority, size, cursor, cancellationToken, projectId, includeDismissed).ConfigureAwait(false);
            var current = await projects.ReadNotificationAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
            RequireUnchangedAuthority(authority, current);
            return Results.Ok(page);
        }, cancellationToken);

    private static Task<IResult> DismissAsync(
        string notificationId, HttpContext context, ProjectsRunSelectionClient projects,
        UserNotificationStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0 || context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
                throw new CoordinationException("query_invalid", 400);
            var authority = await projects.ReadNotificationAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
            await store.DismissAsync(authority, notificationId, cancellationToken, async ct =>
            {
                var current = await projects.ReadNotificationAuthorityAsync(context, ct).ConfigureAwait(false);
                RequireUnchangedAuthority(authority, current);
            }).ConfigureAwait(false);
            return Results.NoContent();
        }, cancellationToken);

    internal static (int PageSize, string? Cursor, string? ProjectId, bool IncludeDismissed) ParseQuery(IQueryCollection query)
    {
        foreach (var (key, values) in query)
            if (key is not ("pageSize" or "cursor" or "projectId" or "includeDismissed") ||
                values.Count != 1 || string.IsNullOrEmpty(values[0]))
                throw new CoordinationException("query_invalid", 400);
        var size = 25;
        if (query.TryGetValue("pageSize", out var value) &&
            (!int.TryParse(value[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out size) || size is < 1 or > 100))
            throw new CoordinationException("query_invalid", 400);
        var includeDismissed = false;
        if (query.TryGetValue("includeDismissed", out var dismissed))
        {
            if (dismissed[0] is not ("true" or "false"))
                throw new CoordinationException("query_invalid", 400);
            includeDismissed = dismissed[0] == "true";
        }
        var projectId = query.TryGetValue("projectId", out var project) ? project[0] : null;
        if (projectId is not null) CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        return (size, query.TryGetValue("cursor", out var cursor) ? cursor[0] : null, projectId, includeDismissed);
    }

    private static void RequireUnchangedAuthority(
        ProjectsAuthorizationContext previous, ProjectsAuthorizationContext current)
    {
        if (System.Text.Json.JsonSerializer.Serialize(previous) != System.Text.Json.JsonSerializer.Serialize(current))
            throw new CoordinationException("projects_authorization_changed", 409);
    }
}
