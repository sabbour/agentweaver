using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agentweaver.Api.Metrics;

public sealed class AppInsightsMetricsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppInsightsMetricsService> _logger;
    private LogsQueryClient? _client;
    private readonly object _clientLock = new();

    public AppInsightsMetricsService(IConfiguration configuration, ILogger<AppInsightsMetricsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private LogsQueryClient? GetClient()
    {
        if (_client is not null) return _client;
        lock (_clientLock)
        {
            if (_client is not null) return _client;
            try
            {
                _client = new LogsQueryClient(
                    new DefaultAzureCredential(),
                    new LogsQueryClientOptions());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize AppInsights LogsQueryClient; metrics will be unavailable.");
            }
            return _client;
        }
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
        var aiCreditTrendTask = QueryAiCreditUsageTrendAsync(workspaceId, projectId, start, end, ct);
        await Task.WhenAll(
            throughputTask,
            leaderboardTask,
            invocationTrendTask,
            modelUsageTask,
            responseDurationTask,
            ttftTask,
            agentBreakdownTask,
            aiCreditTrendTask).ConfigureAwait(false);

        return new ProjectMetricsDto
        {
            Throughput = throughputTask.Result,
            Leaderboard = leaderboardTask.Result,
            InvocationTrend = invocationTrendTask.Result,
            ModelUsage = modelUsageTask.Result,
            ResponseDuration = responseDurationTask.Result,
            TimeToFirstToken = ttftTask.Result,
            AgentBreakdown = agentBreakdownTask.Result,
            AiCreditUsageTrend = aiCreditTrendTask.Result,
        };
    }

    public async Task<RunAgentTokenBreakdownDto> GetRunAgentTokenBreakdownAsync(
        string runId,
        string? projectId,
        IReadOnlyDictionary<string, string?>? agentNameByRunId = null,
        CancellationToken ct = default)
    {
        var connectionString = _configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return EmptyRunBreakdown(runId);

        var workspaceId = ResolveWorkspaceId(connectionString);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return EmptyRunBreakdown(runId);

        var entries = await QueryRunAgentBreakdownAsync(
            workspaceId,
            runId,
            projectId,
            agentNameByRunId,
            ct).ConfigureAwait(false);
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

    public async Task<RunTraceDto> GetRunTracesAsync(
        string runId,
        IReadOnlyDictionary<string, string?>? agentNameByRunId = null,
        CancellationToken ct = default)
    {
        var connectionString = _configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return EmptyRunTrace(runId);

        var workspaceId = ResolveWorkspaceId(connectionString);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return EmptyRunTrace(runId);

        var (spans, queryError) = await QueryRunTracesAsync(
            workspaceId,
            runId,
            agentNameByRunId,
            ct).ConfigureAwait(false);
        return new RunTraceDto
        {
            RunId = runId,
            Spans = spans,
            QueryError = queryError,
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
            AppMetrics
            | where Name in ("agentweaver.run.created", "agentweaver.runs.created", "agentweaver.run.completed", "agentweaver.runs.completed")
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize total = sum(Sum) by bin(TimeGenerated, 1d), Name
            | order by TimeGenerated asc
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
            let leaderboard = AppDependencies
            | where isnotempty(Properties["gen_ai.agent.name"])
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize
                runs_total = count(),
                runs_this_week = countif(TimeGenerated > ago(7d)),
                success_count = countif(Success == true),
                avg_duration_ms = avg(toreal(DurationMs))
              by agent_name = tostring(Properties["gen_ai.agent.name"]),
                 role = tostring(Properties["gen_ai.agent.description"]);
            let costs = AppMetrics
            | where Name == "agentweaver.token.usage"
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | extend agent_name = case(
                isnotempty(tostring(Properties["agent_name"])), tostring(Properties["agent_name"]),
                isnotempty(tostring(Properties["gen_ai.agent.name"])), tostring(Properties["gen_ai.agent.name"]),
                "unknown")
            | summarize cost_aic = sum(Sum) / 1000000000.0 by agent_name;
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
            AppMetrics
            | where Name in ("agentweaver.run.created", "agentweaver.runs.created")
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize total = sum(Sum) by bin(TimeGenerated, 1d)
            | order by TimeGenerated asc
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
            AppMetrics
            | where Name == "agentweaver.token.usage"
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = case(
                isnotempty(tostring(Properties["model"])), tostring(Properties["model"]),
                isnotempty(tostring(Properties["model_id"])), tostring(Properties["model_id"]),
                isnotempty(tostring(Properties["gen_ai.request.model"])), tostring(Properties["gen_ai.request.model"]),
                isnotempty(tostring(Properties["gen_ai.response.model"])), tostring(Properties["gen_ai.response.model"]),
                "unknown")
            | summarize invocation_count = count(), total_nano_aiu = sum(Sum) by model_name
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
            AppDependencies
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = case(
                isnotempty(tostring(Properties["model"])), tostring(Properties["model"]),
                isnotempty(tostring(Properties["model_id"])), tostring(Properties["model_id"]),
                isnotempty(tostring(Properties["gen_ai.request.model"])), tostring(Properties["gen_ai.request.model"]),
                isnotempty(tostring(Properties["gen_ai.response.model"])), tostring(Properties["gen_ai.response.model"]),
                isnotempty(tostring(Target)), tostring(Target),
                "unknown")
            | where isnotempty(model_name) and (
                tostring(Properties["agentweaver.span.kind"]) == "agent_turn"
                or isnotempty(tostring(Properties["gen_ai.operation.name"]))
                or isnotempty(tostring(Properties["gen_ai.agent.name"]))
                or isnotempty(tostring(Properties["agent_name"]))
                or isnotempty(tostring(Properties["gen_ai.request.model"]))
                or isnotempty(tostring(Properties["gen_ai.response.model"]))
            )
            | summarize p50_ms = percentile(toreal(DurationMs), 50), p95_ms = percentile(toreal(DurationMs), 95) by model_name
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
            AppDependencies
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | extend model_name = case(
                isnotempty(tostring(Properties["model"])), tostring(Properties["model"]),
                isnotempty(tostring(Properties["model_id"])), tostring(Properties["model_id"]),
                isnotempty(tostring(Properties["gen_ai.request.model"])), tostring(Properties["gen_ai.request.model"]),
                isnotempty(tostring(Properties["gen_ai.response.model"])), tostring(Properties["gen_ai.response.model"]),
                isnotempty(tostring(Target)), tostring(Target),
                "unknown")
            | extend ttft_ms = coalesce(
                todouble(Measurements["time_to_first_token_ms"]),
                todouble(Measurements["ttft_ms"]),
                todouble(Measurements["gen_ai.response.ttft_ms"]),
                todouble(Measurements["gen_ai.server.time_to_first_token_ms"]),
                todouble(Properties["time_to_first_token_ms"]),
                todouble(Properties["ttft_ms"]),
                todouble(Properties["gen_ai.response.ttft_ms"]),
                todouble(Properties["gen_ai.server.time_to_first_token_ms"]))
            | where isnotempty(model_name) and isnotnull(ttft_ms) and ttft_ms > 0 and (
                tostring(Properties["agentweaver.span.kind"]) == "agent_turn"
                or isnotempty(tostring(Properties["gen_ai.operation.name"]))
                or isnotempty(tostring(Properties["gen_ai.agent.name"]))
                or isnotempty(tostring(Properties["agent_name"]))
            )
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
            AppMetrics
            | where Name == "agentweaver.token.usage"
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | extend agent_name = case(
                isnotempty(tostring(Properties["agent_name"])), tostring(Properties["agent_name"]),
                isnotempty(tostring(Properties["gen_ai.agent.name"])), tostring(Properties["gen_ai.agent.name"]),
                "unknown")
            | summarize invocation_count = count(), total_nano_aiu = sum(Sum) by agent_name
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
        IReadOnlyDictionary<string, string?>? agentNameByRunId,
        CancellationToken ct)
    {
        var timeTo = DateTimeOffset.UtcNow;
        var timeFrom = timeTo.AddDays(-30);
        var runIds = agentNameByRunId?.Keys.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (runIds is null || runIds.Length == 0)
            runIds = [runId];
        var runIdPredicate = BuildRunIdDimensionPredicate(runId, runIds, "Properties");
        var projectFilter = string.IsNullOrWhiteSpace(projectId)
            ? string.Empty
            : $"| where tostring(Properties[\"project.id\"]) == \"{EscapeKusto(projectId)}\"";
        var query =
            $"""
            AppMetrics
            | where Name == "agentweaver.token.usage"
            | where TimeGenerated between (datetime({timeFrom.UtcDateTime:O}) .. datetime({timeTo.UtcDateTime:O}))
            {projectFilter}
            | where {runIdPredicate}
            | extend agent_name = case(
                isnotempty(tostring(Properties["agent_name"])), tostring(Properties["agent_name"]),
                isnotempty(tostring(Properties["gen_ai.agent.name"])), tostring(Properties["gen_ai.agent.name"]),
                "unknown")
            | extend run_id_dim = case(
                isnotempty(tostring(Properties["run_id"])), tostring(Properties["run_id"]),
                isnotempty(tostring(Properties["run.id"])), tostring(Properties["run.id"]),
                isnotempty(tostring(Properties["runId"])), tostring(Properties["runId"]),
                "")
            | summarize invocation_count = count(), total_nano_aiu = sum(Sum) by agent_name, run_id_dim
            | order by total_nano_aiu desc, agent_name asc
            """;

        var result = await QueryAsync(workspaceId, query, timeFrom, timeTo, ct).ConfigureAwait(false);
        if (result is null) return [];

        return result.Table.Rows
            .Select(row =>
            {
                var agentName = row[0]?.ToString();
                var rowRunId = row[1]?.ToString();
                if (string.IsNullOrWhiteSpace(agentName)
                    || string.Equals(agentName, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var mappedAgentName = ResolveFallbackAgentName(agentNameByRunId, rowRunId, runId);
                    if (!string.IsNullOrWhiteSpace(mappedAgentName))
                    {
                        agentName = mappedAgentName;
                    }
                }

                return new AgentUsageBreakdownDto
                {
                    AgentName = string.IsNullOrWhiteSpace(agentName) ? "unknown" : agentName,
                    InvocationCount = Convert.ToInt32(row[2] ?? 0),
                    TotalTokens = 0,
                    TotalNanoAiu = Convert.ToInt64(row[3] ?? 0),
                };
            })
            .GroupBy(entry => entry.AgentName, StringComparer.Ordinal)
            .Select(group => new AgentUsageBreakdownDto
            {
                AgentName = group.Key,
                InvocationCount = group.Sum(entry => entry.InvocationCount),
                TotalTokens = 0,
                TotalNanoAiu = group.Sum(entry => entry.TotalNanoAiu),
            })
            .OrderByDescending(entry => entry.TotalNanoAiu)
            .ThenBy(entry => entry.AgentName, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<AiCreditUsagePointDto>> QueryAiCreditUsageTrendAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var query =
            $"""
            AppMetrics
            | where Name == "agentweaver.token.usage"
            | where TimeGenerated between (datetime({from.UtcDateTime:O}) .. datetime({to.UtcDateTime:O}))
            | where tostring(Properties["project.id"]) == "{EscapeKusto(projectId)}"
            | summarize total_nano_aiu = sum(Sum) by bin(TimeGenerated, 1d)
            | order by TimeGenerated asc
            """;

        var result = await QueryAsync(workspaceId, query, from, to, ct).ConfigureAwait(false);
        if (result is null) return [];

        var totals = result.Table.Rows.ToDictionary(
            row => ReadDate(row[0]),
            row => Convert.ToInt64(row[1] ?? 0),
            StringComparer.Ordinal);

        var points = new List<AiCreditUsagePointDto>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            points.Add(new AiCreditUsagePointDto
            {
                Date = key,
                TotalNanoAiu = totals.GetValueOrDefault(key),
            });
        }

        return points;
    }

    private async Task<(IReadOnlyList<RunTraceSpanDto> Spans, string? QueryError)> QueryRunTracesAsync(
        string workspaceId,
        string runId,
        IReadOnlyDictionary<string, string?>? agentNameByRunId,
        CancellationToken ct)
    {
        var timeTo = DateTimeOffset.UtcNow;
        var timeFrom = timeTo.AddDays(-7);
        var runIds = agentNameByRunId?.Keys.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [runId];
        var runIdPredicate = BuildRunIdDimensionPredicate(runId, runIds, "Properties");
        var query =
            $"""
            let run_operations = materialize(
                union isfuzzy=true AppTraces, AppDependencies
                | where TimeGenerated > ago(7d)
                | where {runIdPredicate}
                | project operation_id = tostring(OperationId)
                | where isnotempty(operation_id)
            );
            let correlated_ops = run_operations | distinct operation_id;
            let agentic_dependencies = AppDependencies
                | where TimeGenerated > ago(7d)
                | where {runIdPredicate} or OperationId in (correlated_ops) or ParentId in (correlated_ops)
                | where tostring(Properties["agentweaver.span.kind"]) == "agent_turn"
                    or tostring(Properties["agentweaver.span.kind"]) == "tool_call"
                    or isnotempty(tostring(Properties["gen_ai.operation.name"]))
                    or isnotempty(tostring(Properties["gen_ai.agent.name"]))
                    or isnotempty(tostring(Properties["gen_ai.tool.name"]))
                    or isnotempty(tostring(Properties["agent_name"]))
                    or isnotempty(tostring(Properties["gen_ai.request.model"]))
                    or isnotempty(tostring(Properties["gen_ai.response.model"]))
                | project id = tostring(Id), parentId = tostring(ParentId), name = Name, timestamp = TimeGenerated, duration = DurationMs, success = Success, resultCode = ResultCode, customDimensions = Properties;
            let agentic_traces = AppTraces
                | where TimeGenerated > ago(7d)
                | where {runIdPredicate} or OperationId in (correlated_ops)
                | where tostring(Properties["agentweaver.span.kind"]) == "agent_turn"
                    or tostring(Properties["agentweaver.span.kind"]) == "tool_call"
                    or isnotempty(tostring(Properties["gen_ai.operation.name"]))
                    or isnotempty(tostring(Properties["gen_ai.agent.name"]))
                    or isnotempty(tostring(Properties["gen_ai.tool.name"]))
                    or isnotempty(tostring(Properties["agent_name"]))
                | project id = strcat("trace_", tostring(OperationId), "_", tostring(ParentId), "_", format_datetime(TimeGenerated, "yyyyMMddHHmmssfffffff")), parentId = tostring(ParentId), name = Message, timestamp = TimeGenerated, duration = todouble(0), success = tobool(1), resultCode = tostring(""), customDimensions = Properties;
            union isfuzzy=true
                (agentic_dependencies),
                (agentic_traces)
            | project
                id,
                parentId,
                name,
                timestamp,
                duration,
                success,
                resultCode,
                customDimensions
            | order by timestamp asc
            """;

        string? queryError = null;
        var result = await QueryAsync(
            workspaceId,
            query,
            timeFrom,
            timeTo,
            ct,
            _ => queryError = "Application Insights trace query failed.").ConfigureAwait(false);
        if (result is null) return ([], queryError);

        var spans = result.Table.Rows
            .Select((row, index) =>
            {
                var customDimensions = ReadCustomDimensions(row[7]);
                var timestamp = ReadDateTimeOffset(row[3]) ?? timeFrom;
                var spanRunId = ReadDimension(customDimensions, "run_id")
                    ?? ReadDimension(customDimensions, "run.id")
                    ?? ReadDimension(customDimensions, "runId");
                var spanKind = ReadDimension(customDimensions, "agentweaver.span.kind");
                var toolName = ReadDimension(customDimensions, "gen_ai.tool.name")
                    ?? ReadDimension(customDimensions, "tool_name");
                var operationName = ReadDimension(customDimensions, "gen_ai.operation.name");
                var model = ReadDimension(customDimensions, "gen_ai.response.model")
                    ?? ReadDimension(customDimensions, "gen_ai.request.model")
                    ?? ReadDimension(customDimensions, "model")
                    ?? ReadDimension(customDimensions, "model_id");
                return new RunTraceSpanDto
                {
                    Id = ReadRequiredString(row[0], $"{runId}-{index}"),
                    ParentId = NullIfWhiteSpace(row[1]?.ToString()),
                    Name = ReadRequiredString(row[2], "span"),
                    SpanType = ClassifySpanType(spanKind, toolName, operationName, model),
                    Timestamp = timestamp,
                    DurationMs = ReadDurationMs(row[4]),
                    Success = ReadBool(row[5]),
                    ResultCode = NullIfWhiteSpace(row[6]?.ToString()),
                    ToolName = toolName,
                    AgentName = ReadDimension(customDimensions, "agent_name")
                        ?? ReadDimension(customDimensions, "gen_ai.agent.name")
                        ?? ResolveFallbackAgentName(agentNameByRunId, spanRunId, runId),
                    Model = model,
                    InputTokens = ReadDimensionLong(customDimensions, "gen_ai.usage.input_tokens"),
                    OutputTokens = ReadDimensionLong(customDimensions, "gen_ai.usage.output_tokens"),
                    OperationName = operationName,
                };
            })
            .ToList();
        return (spans, null);
    }

    /// <summary>
    /// Classifies a trace span into the three transaction-trace node types the UI renders:
    /// <c>invoke-agent</c>, <c>llm</c>, or <c>tool</c>. Uses the Agentweaver span-kind marker when
    /// present, then falls back to gen AI semantic-convention hints (tool name / operation name /
    /// model). Defaults to <c>invoke-agent</c> so an unclassified agentic span still renders.
    /// </summary>
    internal static string ClassifySpanType(string? spanKind, string? toolName, string? operationName, string? model)
    {
        if (string.Equals(spanKind, "tool_call", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(toolName)
            || string.Equals(operationName, "execute_tool", StringComparison.OrdinalIgnoreCase))
            return "tool";
        if (string.Equals(spanKind, "agent_turn", StringComparison.OrdinalIgnoreCase))
            return "invoke-agent";
        if (string.Equals(operationName, "chat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationName, "text_completion", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(model))
            return "llm";
        return "invoke-agent";
    }

    private async Task<LogsQueryResult?> QueryAsync(
        string workspaceId,
        string query,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct,
        Action<Exception>? onError = null,
        [CallerMemberName] string context = "")
    {
        var client = GetClient();
        if (client is null) return null;
        try
        {
            var response = await client.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(from, to),
                cancellationToken: ct).ConfigureAwait(false);
            return response.Value;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            _logger.LogError(
                ex,
                "Application Insights query failed in {QueryContext}. KQL (truncated): {Query}",
                context,
                TruncateQuery(query));
            return null;
        }
    }

    private static string TruncateQuery(string query)
    {
        const int maxLoggedQueryLength = 4_000;
        return query.Length <= maxLoggedQueryLength
            ? query
            : query[..maxLoggedQueryLength] + "...";
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
        AiCreditUsageTrend = [],
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

    private static RunTraceDto EmptyRunTrace(string runId) => new()
    {
        RunId = runId,
        Spans = [],
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

    private static string BuildRunIdDimensionPredicate(string rootRunId, IReadOnlyList<string> runIds, string customDimensionsColumn)
    {
        var escapedRootRunId = EscapeKusto(rootRunId);
        var runIdList = string.Join(", ", runIds.Select(id => $"\"{EscapeKusto(id)}\""));
        return $"""
            tostring({customDimensionsColumn}["run_id"]) in ({runIdList})
            or tostring({customDimensionsColumn}["runId"]) in ({runIdList})
            or tostring({customDimensionsColumn}["RunId"]) in ({runIdList})
            or tostring({customDimensionsColumn}["run.id"]) in ({runIdList})
            or tostring({customDimensionsColumn}["parent_run_id"]) in ({runIdList})
            or tostring({customDimensionsColumn}["parentRunId"]) in ({runIdList})
            or tostring({customDimensionsColumn}["ParentRunId"]) in ({runIdList})
            or tostring({customDimensionsColumn}["run_id"]) startswith "{escapedRootRunId}-"
            or tostring({customDimensionsColumn}["run.id"]) startswith "{escapedRootRunId}-"
            """;
    }

    private static string? ResolveFallbackAgentName(
        IReadOnlyDictionary<string, string?>? agentNameByRunId,
        string? spanRunId,
        string rootRunId)
    {
        if (agentNameByRunId is null || agentNameByRunId.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(spanRunId)
            && agentNameByRunId.TryGetValue(spanRunId, out var exact)
            && !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        return agentNameByRunId.TryGetValue(rootRunId, out var root) && !string.IsNullOrWhiteSpace(root)
            ? root
            : null;
    }

    private static string ReadRequiredString(object? value, string fallback) =>
        string.IsNullOrWhiteSpace(value?.ToString()) ? fallback : value!.ToString()!;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool ReadBool(object? value) =>
        value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };

    private static double ReadDurationMs(object? value) =>
        value switch
        {
            TimeSpan span => span.TotalMilliseconds,
            double number => number,
            float number => number,
            decimal number => (double)number,
            int number => number,
            long number => number,
            string text when TimeSpan.TryParse(text, out var parsedSpan) => parsedSpan.TotalMilliseconds,
            string text when double.TryParse(text, out var parsedDouble) => parsedDouble,
            _ => 0d,
        };

    private static DateTimeOffset? ReadDateTimeOffset(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime()),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => null,
        };

    private static IReadOnlyDictionary<string, string?> ReadCustomDimensions(object? value)
    {
        if (value is null)
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return ReadCustomDimensionsFromJson(element);

        if (value is BinaryData binaryData)
            return ReadCustomDimensions(binaryData.ToString());

        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? ReadCustomDimensionsFromJson(document.RootElement)
                    : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> ReadCustomDimensionsFromJson(JsonElement element)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
        return values;
    }

    private static string? ReadDimension(IReadOnlyDictionary<string, string?> dimensions, string key) =>
        dimensions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static long? ReadDimensionLong(IReadOnlyDictionary<string, string?> dimensions, string key)
    {
        var value = ReadDimension(dimensions, key);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string ReadDate(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd"),
            DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd"),
            _ => value?.ToString()?.Split('T')[0] ?? string.Empty,
        };
}
