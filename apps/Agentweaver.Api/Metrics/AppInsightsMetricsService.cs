using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Agentweaver.Api.Metrics;

public sealed class AppInsightsMetricsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppInsightsMetricsService> _logger;
    private readonly LogsQueryClient _client;

    public AppInsightsMetricsService(IConfiguration configuration, ILogger<AppInsightsMetricsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _client = new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<ProjectMetricsDto> GetProjectMetricsAsync(
        string projectId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default)
    {
        var connectionString = _configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Empty();

        var workspaceId = ResolveWorkspaceId(connectionString);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            _logger.LogDebug("Project metrics disabled because no Application Insights workspace id was configured.");
            return Empty();
        }

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);

        // TODO(issue-106): this endpoint depends on PR #111 landing so the AgentWeaverMetrics
        // counters and GenAI semantic-convention dimensions exist in Application Insights.
        var throughputTask = QueryThroughputAsync(workspaceId, projectId, start, end, ct);
        var leaderboardTask = QueryLeaderboardAsync(workspaceId, projectId, start, end, ct);
        await Task.WhenAll(throughputTask, leaderboardTask).ConfigureAwait(false);

        return new ProjectMetricsDto
        {
            Throughput = throughputTask.Result,
            Leaderboard = leaderboardTask.Result,
        };
    }

    private async Task<IReadOnlyList<ThroughputPointDto>> QueryThroughputAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            customMetrics
            | where name in ("agentweaver.run.created", "agentweaver.run.completed")
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where isempty(customDimensions["project.id"]) or tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize total = sum(value) by bin(timestamp, 1d), name
            | order by timestamp asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in result.Table.Rows)
        {
            var date = ReadDate(row[0]);
            var name = row[1]?.ToString() ?? string.Empty;
            var total = Convert.ToInt32(row[2] ?? 0);
            if (string.Equals(name, "agentweaver.run.created", StringComparison.Ordinal))
                created[date] = total;
            else
                done[date] = total;
        }

        var series = new List<ThroughputPointDto>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            series.Add(new ThroughputPointDto
            {
                Date = key,
                Created = created.GetValueOrDefault(key),
                Done = done.GetValueOrDefault(key),
            });
        }

        return series;
    }

    private async Task<IReadOnlyList<AgentLeaderboardEntryDto>> QueryLeaderboardAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            let leaderboard = dependencies
            | where isnotempty(customDimensions["gen_ai.agent.name"])
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where isempty(customDimensions["project.id"]) or tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize
                runs_total = count(),
                runs_this_week = countif(timestamp > ago(7d)),
                success_count = countif(success == true),
                avg_duration_ms = avg(toreal(duration / 1ms))
              by agent_name = tostring(customDimensions["gen_ai.agent.name"]),
                 role = tostring(customDimensions["gen_ai.agent.description"]);
            let costs = customMetrics
            | where name == "agentweaver.token.usage"
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where isempty(customDimensions["project.id"]) or tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize cost_aic = sum(value) / 1000000000.0 by agent_name = tostring(customDimensions["gen_ai.agent.name"]);
            leaderboard
            | join kind=leftouter costs on agent_name
            | extend success_rate = iff(runs_total == 0, 0.0, round(100.0 * success_count / runs_total, 0))
            | project agent_name, role, runs_this_week, runs_total, success_rate, avg_duration_ms, cost_aic
            | order by runs_total desc, agent_name asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new AgentLeaderboardEntryDto
        {
            AgentName = row[0]?.ToString() ?? "unknown",
            Role = string.IsNullOrWhiteSpace(row[1]?.ToString()) ? null : row[1]?.ToString(),
            RunsThisWeek = Convert.ToInt32(row[2] ?? 0),
            RunsTotal = Convert.ToInt32(row[3] ?? 0),
            SuccessRate = Convert.ToInt32(row[4] ?? 0),
            AvgDurationMs = row[5] is null ? null : Convert.ToInt64(row[5]),
            CostAic = row[6] is null ? 0m : Convert.ToDecimal(row[6]),
        }).ToList();
    }

    private async Task<LogsQueryResult?> QueryAsync(
        string workspaceId,
        string query,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        try
        {
            var response = await _client.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(from, to),
                cancellationToken: ct).ConfigureAwait(false);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Application Insights metrics query failed.");
            return null;
        }
    }

    private ProjectMetricsDto Empty() => new()
    {
        Throughput = [],
        Leaderboard = [],
    };

    private string? ResolveWorkspaceId(string connectionString)
    {
        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var parts = segment.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("WorkspaceId", StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        return _configuration["APPLICATIONINSIGHTS_WORKSPACE_ID"]
            ?? _configuration["ApplicationInsights:WorkspaceId"];
    }

    private static string EscapeKusto(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ReadDate(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd"),
            DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd"),
            _ => value?.ToString()?.Split('T')[0] ?? string.Empty,
        };
}
