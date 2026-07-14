using Agentweaver.Api.Metrics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
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
            bool? includeMetrics,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            var metricsFrom = ParseDateTimeOffset(from);
            var metricsTo = ParseDateTimeOffset(to);
            var summary = await dashboard.GetProjectDashboardAsync(project, ct).ConfigureAwait(false);

            // #208 point 4: callers that already fetch the full metrics DTO from
            // `/api/projects/{id}/metrics` separately (e.g. DashboardPage.tsx) can pass
            // `includeMetrics=false` so this endpoint skips its own internal 8-way
            // `GetProjectMetricsAsync` fan-out, instead of both endpoints triggering it concurrently for
            // the same page load. Defaults to `true` to preserve the existing response shape/behavior
            // for any other caller.
            if (includeMetrics == false)
                return Results.Ok(summary);

            var metricDto = await metrics.GetProjectMetricsAsync(
                projectId.ToString(),
                metricsFrom,
                metricsTo,
                ct).ConfigureAwait(false);
            var fallbackUsage = await dashboard.GetProjectUsageFallbackAsync(
                projectId.ToString(),
                metricsFrom ?? DateTimeOffset.UtcNow.AddDays(-30),
                metricsTo ?? DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
            metricDto = MergeProjectMetrics(metricDto, fallbackUsage.ModelUsage, fallbackUsage.AgentBreakdown);

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

            var metricsFrom = ParseDateTimeOffset(from);
            var metricsTo = ParseDateTimeOffset(to);
            var metricDto = await metrics.GetProjectMetricsAsync(
                projectId.ToString(),
                metricsFrom,
                metricsTo,
                ct).ConfigureAwait(false);
            var fallbackUsage = await dashboard.GetProjectUsageFallbackAsync(
                projectId.ToString(),
                metricsFrom ?? DateTimeOffset.UtcNow.AddDays(-30),
                metricsTo ?? DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
            return Results.Ok(MergeProjectMetrics(metricDto, fallbackUsage.ModelUsage, fallbackUsage.AgentBreakdown));
        });

        app.MapGet("/api/overview", async (
            HttpContext httpContext,
            DashboardReadService dashboard,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            return Results.Ok(await dashboard.GetOverviewAsync(caller, ct).ConfigureAwait(false));
        });

        app.MapGet("/api/runs/{id}/token-breakdown", async (
            HttpContext httpContext,
            string id,
            IRunStore runStore,
            AppInsightsMetricsService metrics,
            DashboardReadService dashboard,
            CancellationToken ct) =>
        {
            if (!RunId.TryParse(id, out var runId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var agentNameByRunId = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [run.Id.ToString()] = run.AgentName,
            };
            if (run.ParentRunId is null)
            {
                var children = await runStore.GetRunsByParentAsync(run.Id.ToString(), ct).ConfigureAwait(false);
                foreach (var child in children)
                    agentNameByRunId[child.Id.ToString()] = child.AgentName;
            }

            var projectId = run.ProjectId?.ToString();
            var appInsights = await metrics.GetRunAgentTokenBreakdownAsync(
                id,
                projectId,
                agentNameByRunId,
                ct).ConfigureAwait(false);
            if (appInsights.HasAgentData)
                return Results.Ok(appInsights);

            return Results.Ok(await dashboard.GetRunAgentTokenBreakdownFallbackAsync(id, ct).ConfigureAwait(false));
        });

        app.MapGet("/api/metrics/runs/{runId}/traces", async (
            HttpContext httpContext,
            string runId,
            IRunStore runStore,
            AppInsightsMetricsService metrics,
            CancellationToken ct) =>
        {
            if (!RunId.TryParse(runId, out var parsedRunId))
                return Results.BadRequest(new { error = "Invalid run id." });

            var run = await runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            if (run is null) return Results.NotFound();
            if (!EndpointHelpers.IsOwner(httpContext, run)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var agentNameByRunId = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [run.Id.ToString()] = run.AgentName,
            };
            if (run.ParentRunId is null)
            {
                var children = await runStore.GetRunsByParentAsync(run.Id.ToString(), ct).ConfigureAwait(false);
                foreach (var child in children)
                    agentNameByRunId[child.Id.ToString()] = child.AgentName;
            }

            return Results.Ok(await metrics.GetRunTracesAsync(runId, agentNameByRunId, ct).ConfigureAwait(false));
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

    private static ProjectMetricsDto MergeProjectMetrics(
        ProjectMetricsDto metrics,
        IReadOnlyList<ModelUsageBreakdownDto> fallbackModelUsage,
        IReadOnlyList<AgentUsageBreakdownDto> fallbackAgentBreakdown) =>
        metrics with
        {
            ModelUsage = HasMeaningfulModelUsage(metrics.ModelUsage) ? metrics.ModelUsage : fallbackModelUsage,
            AgentBreakdown = HasMeaningfulAgentBreakdown(metrics.AgentBreakdown) ? metrics.AgentBreakdown : fallbackAgentBreakdown,
        };

    private static bool HasMeaningfulModelUsage(IReadOnlyList<ModelUsageBreakdownDto> entries) =>
        entries.Any(entry => !string.Equals(entry.Model, "unknown", StringComparison.OrdinalIgnoreCase));

    private static bool HasMeaningfulAgentBreakdown(IReadOnlyList<AgentUsageBreakdownDto> entries) =>
        entries.Any(entry => !string.Equals(entry.AgentName, "unknown", StringComparison.OrdinalIgnoreCase));
}
