using Agentweaver.Api.Metrics;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Maps the read-only metrics/aggregation endpoints that power the per-project Dashboard and the
/// global "Now" Overview pages. Register with <c>app.MapMetricsEndpoints()</c> in Program.cs after
/// the other endpoint registrations.
///
/// <para>Every value returned is sourced from live stores (no mocks, no fabricated metrics,
/// Constitution Principle VII). Cost is never reported because Agentweaver has no real cost source.</para>
/// </summary>
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this WebApplication app)
    {
        // Per-project dashboard. Owner-authorized (same pattern as other project endpoints).
        app.MapGet("/api/projects/{id}/dashboard", async (
            HttpContext httpContext,
            string id,
            MetricsService metrics,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            var dto = await metrics.GetProjectDashboardAsync(project, ct).ConfigureAwait(false);
            return Results.Ok(dto);
        });

        // Global cross-project "Now" overview. API-key authenticated like other global endpoints
        // (e.g. GET /api/diagnostics, GET /api/projects).
        app.MapGet("/api/overview", async (
            MetricsService metrics,
            CancellationToken ct) =>
        {
            var dto = await metrics.GetOverviewAsync(ct).ConfigureAwait(false);
            return Results.Ok(dto);
        });
    }
}
