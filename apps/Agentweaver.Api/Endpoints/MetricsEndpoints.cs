using Agentweaver.Api.Metrics;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/dashboard", async (
            HttpContext httpContext,
            string id,
            DashboardReadService dashboard,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            return Results.Ok(await dashboard.GetProjectDashboardAsync(project, ct).ConfigureAwait(false));
        });

        app.MapGet("/api/projects/{id}/metrics", async (
            HttpContext httpContext,
            string id,
            string? from,
            string? to,
            AppInsightsMetricsService metrics,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            return Results.Ok(await metrics.GetProjectMetricsAsync(
                id,
                ParseDateTimeOffset(from),
                ParseDateTimeOffset(to),
                ct).ConfigureAwait(false));
        });

        app.MapGet("/api/overview", async (DashboardReadService dashboard, CancellationToken ct) =>
            Results.Ok(await dashboard.GetOverviewAsync(ct).ConfigureAwait(false)));
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
    }
}
