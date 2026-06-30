using Agentweaver.Api.Metrics;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Maps the token usage query endpoints (Feature 019: AI Credit and token monitoring).
/// Four-level hierarchy: run -> workflow run -> project -> app.
/// Register with <c>app.MapUsageEndpoints()</c> in Program.cs.
/// </summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        // Agent run token usage.
        app.MapGet("/api/runs/{id}/usage", async (
            string id,
            ITokenUsageStore usageStore,
            CancellationToken ct) =>
        {
            var summary = await usageStore.GetRunUsageAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(MetricsService.ToSummaryDto(summary));
        });

        // Orchestration/workflow run token usage (sum of all child runs).
        app.MapGet("/api/workflow-runs/{id}/usage", async (
            string id,
            ITokenUsageStore usageStore,
            CancellationToken ct) =>
        {
            var summary = await usageStore.GetWorkflowRunUsageAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(MetricsService.ToSummaryDto(summary));
        });

        // Project usage with time range filter (default last 30 days).
        app.MapGet("/api/projects/{id}/usage", async (
            HttpContext httpContext,
            string id,
            string? from,
            string? to,
            ITokenUsageStore usageStore,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            var now = DateTimeOffset.UtcNow;
            var fromTs = ParseDateTimeOffset(from) ?? now.AddDays(-30);
            var toTs   = ParseDateTimeOffset(to)   ?? now;

            var summary = await usageStore.GetProjectUsageAsync(id, fromTs, toTs, ct).ConfigureAwait(false);
            return Results.Ok(MetricsService.ToSummaryDto(summary));
        });

        // App-level usage. Requires API key authentication (same gate as GET /api/overview).
        app.MapGet("/api/usage", async (
            string? from,
            string? to,
            ITokenUsageStore usageStore,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var fromTs = ParseDateTimeOffset(from) ?? now.AddDays(-30);
            var toTs   = ParseDateTimeOffset(to)   ?? now;

            var byProject = await usageStore.GetAppUsageAsync(fromTs, toTs, ct).ConfigureAwait(false);
            var dto = MetricsService.ToAppUsageDto(byProject, fromTs, toTs);
            return Results.Ok(dto);
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
