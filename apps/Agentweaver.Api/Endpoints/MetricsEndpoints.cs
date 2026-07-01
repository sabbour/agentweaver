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
            AppInsightsMetricsService metrics,
            string? from,
            string? to,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            var summary = await dashboard.GetProjectDashboardAsync(project, ct).ConfigureAwait(false);
            var metricDto = await metrics.GetProjectMetricsAsync(
                projectId.ToString(),
                ParseDateTimeOffset(from),
                ParseDateTimeOffset(to),
                ct).ConfigureAwait(false);

            return Results.Ok(summary with
            {
                Throughput = metricDto.Throughput,
                AgentLeaderboard = metricDto.Leaderboard.Select(entry => new DashboardAgentLeaderboardEntryDto
                {
                    Agent = entry.AgentName,
                    RoleTitle = entry.Role,
                    RunsThisWeek = entry.RunsThisWeek,
                    RunsTotal = entry.RunsTotal,
                    SuccessRate = entry.TerminalRuns > 0 ? entry.SuccessfulRuns / (double)entry.TerminalRuns : 0d,
                    SuccessfulRuns = entry.SuccessfulRuns,
                    TerminalRuns = entry.TerminalRuns,
                    AvgDurationMs = entry.AvgDurationMs,
                }).ToList(),
            });
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
                projectId.ToString(),
                ParseDateTimeOffset(from),
                ParseDateTimeOffset(to),
                ct).ConfigureAwait(false));
        });

        app.MapGet("/api/overview", async (
            HttpContext httpContext,
            DashboardReadService dashboard,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            return Results.Ok(await dashboard.GetOverviewAsync(caller, ct).ConfigureAwait(false));
        });
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
