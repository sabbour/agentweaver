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
            _logger.LogWarning("Project metrics disabled because no Application Insights workspace id was configured.");
            return Empty();
        }

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);

        // TODO(issue-106): this endpoint depends on PR #111 landing so the AgentWeaverMetrics
        // counters and GenAI semantic-convention dimensions exist in Application Insights.
        var throughputTask = QueryThroughputAsync(workspaceId, projectId, start, end, ct);
        var leaderboardTask = QueryLeaderboardAsync(workspaceId, projectId, start, end, ct);
        var invocationTrendTask = QueryInvocationTrendAsync(workspaceId, projectId, start, end, ct);
        var modelUsageTask = QueryModelUsageAsync(workspaceId, projectId, start, end, ct);
        var responseDurationTask = QueryResponseDurationAsync(workspaceId, projectId, start, end, ct);
        var ttftTask = QueryTtftAsync(workspaceId, projectId, start, end, ct);
        var agentBreakdownTask = QueryProjectAgentBreakdownAsync(workspaceId, projectId, start, end, ct);
        await Task.WhenAll(
            throughputTask,
            leaderboardTask,
            invocationTrendTask,
            modelUsageTask,
            responseDurationTask,
            ttftTask,
            agentBreakdownTask).ConfigureAwait(false);

        return new ProjectMetricsDto
        {
            Throughput = throughputTask.Result,
            Leaderboard = leaderboardTask.Result,
            InvocationTrend = invocationTrendTask.Result,
            ModelUsage = modelUsageTask.Result,
            ResponseDuration = responseDurationTask.Result,
            TimeToFirstToken = ttftTask.Result,
            AgentBreakdown = agentBreakdownTask.Result,
        };
    }

    public async Task<RunAgentTokenBreakdownDto> GetRunAgentTokenBreakdownAsync(
        string runId,
        string? projectId,
        CancellationToken ct = default)
    {
        var connectionString = _configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return EmptyRunBreakdown(runId);

        var workspaceId = ResolveWorkspaceId(connectionString);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return EmptyRunBreakdown(runId);

        var entries = await QueryRunAgentBreakdownAsync(workspaceId, runId, projectId, ct).ConfigureAwait(false);
        return new RunAgentTokenBreakdownDto
        {
            RunId = runId,
            Source = "app_insights",
            HasAgentData = HasMeaningfulAgentBreakdown(entries),
            TotalTokens = 0,
            TotalNanoAiu = entries.Sum(entry => entry.TotalNanoAiu),
            Breakdown = entries,
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
            | where name in ("agentweaver.run.created", "agentweaver.runs.created", "agentweaver.run.completed", "agentweaver.runs.completed")
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
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
            if (string.Equals(name, "agentweaver.run.created", StringComparison.Ordinal)
                || string.Equals(name, "agentweaver.runs.created", StringComparison.Ordinal))
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
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
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
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize cost_aic = sum(value) / 1000000000.0 by agent_name = coalesce(
                tostring(customDimensions["agent_name"]),
                tostring(customDimensions["gen_ai.agent.name"]),
                "unknown");
            leaderboard
            | join kind=leftouter costs on agent_name
            | extend success_rate = iff(runs_total == 0, 0.0, round(100.0 * success_count / runs_total, 0))
            | extend terminal_runs = runs_total
            | project agent_name, role, runs_this_week, runs_total, success_rate, success_count, terminal_runs, avg_duration_ms, cost_aic
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
            SuccessfulRuns = Convert.ToInt32(row[5] ?? 0),
            TerminalRuns = Convert.ToInt32(row[6] ?? 0),
            AvgDurationMs = row[7] is null ? null : Convert.ToInt64(row[7]),
            CostAic = row[8] is null ? 0m : Convert.ToDecimal(row[8]),
        }).ToList();
    }

    private async Task<IReadOnlyList<DailyInvocationPointDto>> QueryInvocationTrendAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            customMetrics
            | where name in ("agentweaver.run.created", "agentweaver.runs.created")
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize total = sum(value) by bin(timestamp, 1d)
            | order by timestamp asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        var counts = result.Table.Rows.ToDictionary(
            row => ReadDate(row[0]),
            row => Convert.ToInt32(row[1] ?? 0),
            StringComparer.Ordinal);

        var points = new List<DailyInvocationPointDto>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            points.Add(new DailyInvocationPointDto
            {
                Date = key,
                Count = counts.GetValueOrDefault(key),
            });
        }

        return points;
    }

    private async Task<IReadOnlyList<ModelUsageBreakdownDto>> QueryModelUsageAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            customMetrics
            | where name == "agentweaver.token.usage"
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = coalesce(
                tostring(customDimensions["model"]),
                tostring(customDimensions["model_id"]),
                tostring(customDimensions["gen_ai.request.model"]),
                tostring(customDimensions["gen_ai.response.model"]),
                "unknown")
            | summarize invocation_count = count(), total_nano_aiu = sum(value) by model_name
            | order by total_nano_aiu desc, model_name asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new ModelUsageBreakdownDto
        {
            Model = row[0]?.ToString() ?? "unknown",
            InvocationCount = Convert.ToInt32(row[1] ?? 0),
            TotalNanoAiu = Convert.ToInt64(row[2] ?? 0),
        }).ToList();
    }

    private async Task<IReadOnlyList<MetricPercentilesDto>> QueryResponseDurationAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            dependencies
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = coalesce(
                tostring(customDimensions["model"]),
                tostring(customDimensions["model_id"]),
                tostring(customDimensions["gen_ai.request.model"]),
                tostring(customDimensions["gen_ai.response.model"]),
                tostring(target),
                "unknown")
            | where isnotempty(model_name)
            | summarize p50_ms = percentile(toreal(duration / 1ms), 50), p95_ms = percentile(toreal(duration / 1ms), 95) by model_name
            | order by model_name asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new MetricPercentilesDto
        {
            Label = row[0]?.ToString() ?? "unknown",
            P50Ms = row[1] is null ? null : Convert.ToInt64(row[1]),
            P95Ms = row[2] is null ? null : Convert.ToInt64(row[2]),
        }).ToList();
    }

    private async Task<IReadOnlyList<MetricPercentilesDto>> QueryTtftAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            dependencies
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = coalesce(
                tostring(customDimensions["model"]),
                tostring(customDimensions["model_id"]),
                tostring(customDimensions["gen_ai.request.model"]),
                tostring(customDimensions["gen_ai.response.model"]),
                tostring(target),
                "unknown")
            | extend ttft_ms = coalesce(
                todouble(customMeasurements["time_to_first_token_ms"]),
                todouble(customMeasurements["ttft_ms"]),
                todouble(customMeasurements["gen_ai.response.ttft_ms"]),
                todouble(customMeasurements["gen_ai.server.time_to_first_token_ms"]))
            | where isnotempty(model_name) and isnotnull(ttft_ms) and ttft_ms > 0
            | summarize p50_ms = percentile(ttft_ms, 50), p95_ms = percentile(ttft_ms, 95) by model_name
            | order by model_name asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new MetricPercentilesDto
        {
            Label = row[0]?.ToString() ?? "unknown",
            P50Ms = row[1] is null ? null : Convert.ToInt64(row[1]),
            P95Ms = row[2] is null ? null : Convert.ToInt64(row[2]),
        }).ToList();
    }

    private async Task<IReadOnlyList<AgentUsageBreakdownDto>> QueryProjectAgentBreakdownAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            customMetrics
            | where name == "agentweaver.token.usage"
            | where timestamp between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(customDimensions["project.id"]) == "{EscapeKusto(projectId)}"
            | extend agent_name = coalesce(
                tostring(customDimensions["agent_name"]),
                tostring(customDimensions["gen_ai.agent.name"]),
                "unknown")
            | summarize invocation_count = count(), total_nano_aiu = sum(value) by agent_name
            | order by total_nano_aiu desc, agent_name asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new AgentUsageBreakdownDto
        {
            AgentName = row[0]?.ToString() ?? "unknown",
            InvocationCount = Convert.ToInt32(row[1] ?? 0),
            TotalTokens = 0,
            TotalNanoAiu = Convert.ToInt64(row[2] ?? 0),
        }).ToList();
    }

    private async Task<IReadOnlyList<AgentUsageBreakdownDto>> QueryRunAgentBreakdownAsync(
        string workspaceId,
        string runId,
        string? projectId,
        CancellationToken ct)
    {
        var timeTo = DateTimeOffset.UtcNow;
        var timeFrom = timeTo.AddDays(-30);
        var projectFilter = string.IsNullOrWhiteSpace(projectId)
            ? string.Empty
            : $"| where tostring(customDimensions[\"project.id\"]) == \"{EscapeKusto(projectId)}\"";
        var query =
            $"""
            customMetrics
            | where name == "agentweaver.token.usage"
            | where timestamp between (datetime({timeFrom.UtcDateTime:O}) .. datetime({timeTo.UtcDateTime:O}))
            {projectFilter}
            | where
                tostring(customDimensions["run_id"]) == "{EscapeKusto(runId)}"
                or tostring(customDimensions["runId"]) == "{EscapeKusto(runId)}"
                or tostring(customDimensions["run.id"]) == "{EscapeKusto(runId)}"
                or tostring(customDimensions["parent_run_id"]) == "{EscapeKusto(runId)}"
                or tostring(customDimensions["parentRunId"]) == "{EscapeKusto(runId)}"
            | extend agent_name = coalesce(
                tostring(customDimensions["agent_name"]),
                tostring(customDimensions["gen_ai.agent.name"]),
                "unknown")
            | summarize invocation_count = count(), total_nano_aiu = sum(value) by agent_name
            | order by total_nano_aiu desc, agent_name asc
            """;

        var result = await QueryAsync(workspaceId, query, timeFrom, timeTo, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows.Select(row => new AgentUsageBreakdownDto
        {
            AgentName = row[0]?.ToString() ?? "unknown",
            InvocationCount = Convert.ToInt32(row[1] ?? 0),
            TotalTokens = 0,
            TotalNanoAiu = Convert.ToInt64(row[2] ?? 0),
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
        InvocationTrend = [],
        ModelUsage = [],
        ResponseDuration = [],
        TimeToFirstToken = [],
        AgentBreakdown = [],
    };

    private static RunAgentTokenBreakdownDto EmptyRunBreakdown(string runId) => new()
    {
        RunId = runId,
        Source = "app_insights",
        HasAgentData = false,
        TotalTokens = 0,
        TotalNanoAiu = 0,
        Breakdown = [],
    };

    private static bool HasMeaningfulAgentBreakdown(IReadOnlyList<AgentUsageBreakdownDto> entries) =>
        entries.Any(entry => !string.Equals(entry.AgentName, "unknown", StringComparison.OrdinalIgnoreCase));

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
