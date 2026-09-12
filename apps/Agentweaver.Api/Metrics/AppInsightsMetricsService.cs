using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Agentweaver.Domain;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agentweaver.Api.Metrics;

public sealed class AppInsightsMetricsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppInsightsMetricsService> _logger;
    private LogsQueryClient? _client;
    private readonly object _clientLock = new();

    /// <summary>
    /// Process-wide concurrency budget for <c>LogsQueryClient.QueryWorkspaceAsync</c> calls (#208 point
    /// 3). This service is registered as a singleton (see <c>Program.cs</c>), so a single semaphore here
    /// bounds the total number of simultaneous Azure Monitor workspace queries across every concurrent
    /// HTTP request/subquery fan-out, not just the ~8 subqueries of one <see cref="GetProjectMetricsAsync"/>
    /// call. This does not implement caching or single-flight de-duplication (a larger follow-up); it
    /// only prevents unbounded fan-out (e.g. an Overview page loading 4 projects x 2 ranges x 8
    /// subqueries = 64 simultaneous queries) from all reaching Azure Monitor at once.
    /// </summary>
    private const int MaxConcurrentWorkspaceQueries = 16;
    private readonly SemaphoreSlim _queryConcurrency = new(MaxConcurrentWorkspaceQueries, MaxConcurrentWorkspaceQueries);
    private static readonly TimeSpan WorkspaceQueryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WorkspaceQueryCooldown = TimeSpan.FromMinutes(1);
    private long _workspaceUnavailableUntilUtcTicks;

    public AppInsightsMetricsService(IConfiguration configuration, ILogger<AppInsightsMetricsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Test-only seam: lets unit tests inject a fake/mock <see cref="LogsQueryClient"/> subclass so
    /// <see cref="QueryAsync"/>'s cancellation-vs-genuine-failure classification (#208) can be exercised
    /// without a real Application Insights workspace or credential. Production code paths always go
    /// through <see cref="GetClient"/>'s lazy <see cref="DefaultAzureCredential"/>-based construction.
    /// </summary>
    internal void SetClientForTesting(LogsQueryClient client) => _client = client;

    /// <summary>
    /// Accumulates genuine (non-cancellation) Application Insights query failures across the parallel
    /// subqueries of a single top-level metrics batch (e.g. one <see cref="GetProjectMetricsAsync"/>
    /// call), so the caller logs one Warning per batch — with the count and distinct query contexts —
    /// instead of one per subquery (#208 point 2, "failure aggregation").
    /// </summary>
    private sealed class QueryFailureSink
    {
        private readonly object _gate = new();
        private readonly List<(string Context, Exception Exception)> _failures = [];

        public void Record(string context, Exception exception)
        {
            lock (_gate) { _failures.Add((context, exception)); }
        }

        public IReadOnlyList<(string Context, Exception Exception)> Snapshot()
        {
            lock (_gate) { return _failures.ToList(); }
        }
    }

    /// <summary>Logs the failures accumulated in <paramref name="sink"/> once, with one line per batch
    /// (not per subquery), including the distinct failing contexts and total count.</summary>
    private void LogAggregatedFailures(QueryFailureSink sink, string batchName)
    {
        var failures = sink.Snapshot();
        if (failures.Count == 0) return;

        var contexts = string.Join(", ", failures.Select(f => f.Context).Distinct(StringComparer.Ordinal));
        _logger.LogError(
            failures[0].Exception,
            "Application Insights query batch {BatchName} had {FailureCount} genuine (non-cancellation) " +
            "subquery failure(s) across contexts [{Contexts}]. Logging once per batch; first exception attached.",
            batchName,
            failures.Count,
            contexts);
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
        //
        // #208 point 2: one shared failure sink for this entire 8-way batch, so genuine (non-
        // cancellation) Azure Monitor failures are logged once for the whole batch instead of once per
        // subquery. Caller-requested cancellation (#208 point 1) is NOT recorded here — it propagates
        // as an exception out of Task.WhenAll below and is not a "failure" for aggregation purposes.
        var failures = new QueryFailureSink();
        var throughputTask = QueryThroughputAsync(workspaceId, projectId, start, end, ct, failures);
        var leaderboardTask = QueryLeaderboardAsync(workspaceId, projectId, start, end, ct, failures);
        var invocationTrendTask = QueryInvocationTrendAsync(workspaceId, projectId, start, end, ct, failures);
        var modelUsageTask = QueryModelUsageAsync(workspaceId, projectId, start, end, ct, failures);
        var responseDurationTask = QueryResponseDurationAsync(workspaceId, projectId, start, end, ct, failures);
        var ttftTask = QueryTtftAsync(workspaceId, projectId, start, end, ct, failures);
        var agentBreakdownTask = QueryProjectAgentBreakdownAsync(workspaceId, projectId, start, end, ct, failures);
        var aiCreditTrendTask = QueryAiCreditUsageTrendAsync(workspaceId, projectId, start, end, ct, failures);
        await Task.WhenAll(
            throughputTask,
            leaderboardTask,
            invocationTrendTask,
            modelUsageTask,
            responseDurationTask,
            ttftTask,
            agentBreakdownTask,
            aiCreditTrendTask).ConfigureAwait(false);
        LogAggregatedFailures(failures, nameof(GetProjectMetricsAsync));

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
        IReadOnlyDictionary<string, RunTraceContext>? traceContextsByRunId = null,
        bool full = false,
        CancellationToken ct = default)
    {
        var connectionString = _configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return UnavailableRunTrace(runId, "Application Insights trace telemetry is not configured.");

        var workspaceId = ResolveWorkspaceId(connectionString);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return UnavailableRunTrace(runId, "Application Insights workspace id is not configured.");

        var (spans, queryError, isTruncated) = await QueryRunTracesAsync(
            workspaceId,
            runId,
            agentNameByRunId,
            traceContextsByRunId,
            full,
            ct).ConfigureAwait(false);
        return new RunTraceDto
        {
            RunId = runId,
            Spans = spans,
            QueryError = queryError,
            IsTruncated = isTruncated,
        };
    }

    private async Task<IReadOnlyList<ThroughputPointDto>> QueryThroughputAsync(
        string workspaceId,
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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

        return AggregateRunAgentBreakdown(result.Table.Rows
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
            }));
    }

    internal static IReadOnlyList<AgentUsageBreakdownDto> AggregateRunAgentBreakdown(
        IEnumerable<AgentUsageBreakdownDto> entries)
    {
        return entries
            .GroupBy(entry => entry.AgentName, StringComparer.OrdinalIgnoreCase)
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
        CancellationToken ct,
        QueryFailureSink? failures = null)
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

        var result = await QueryAsync(workspaceId, query, from, to, ct, failures: failures).ConfigureAwait(false);
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

    private async Task<(IReadOnlyList<RunTraceSpanDto> Spans, string? QueryError, bool IsTruncated)> QueryRunTracesAsync(
        string workspaceId,
        string runId,
        IReadOnlyDictionary<string, string?>? agentNameByRunId,
        IReadOnlyDictionary<string, RunTraceContext>? traceContextsByRunId,
        bool full,
        CancellationToken ct)
    {
        const int initialSpanLimit = 250;
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
            {(full ? string.Empty : $"| take {initialSpanLimit + 1}")}
            """;

        string? queryError = null;
        var result = await QueryAsync(
            workspaceId,
            query,
            timeFrom,
            timeTo,
            ct,
            exception => queryError = DescribeTraceQueryFailure(exception)).ConfigureAwait(false);
        if (result is null) return ([], queryError, false);

        var isTruncated = !full && result.Table.Rows.Count > initialSpanLimit;
        var rows = isTruncated ? result.Table.Rows.Take(initialSpanLimit) : result.Table.Rows;
        var spans = rows
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
                var toolCallId = ReadDimension(customDimensions, "tool.call.id")
                    ?? ReadDimension(customDimensions, "gen_ai.tool.call.id");
                var operationName = ReadDimension(customDimensions, "gen_ai.operation.name");
                var model = ReadDimension(customDimensions, "gen_ai.response.model")
                    ?? ReadDimension(customDimensions, "gen_ai.request.model")
                    ?? ReadDimension(customDimensions, "model")
                    ?? ReadDimension(customDimensions, "model_id");
                RunTraceContext? traceContext = null;
                if (traceContextsByRunId is not null)
                    traceContextsByRunId.TryGetValue(spanRunId ?? runId, out traceContext);
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
                    ToolCallId = toolCallId,
                    AgentName = ReadDimension(customDimensions, "agent_name")
                        ?? ReadDimension(customDimensions, "gen_ai.agent.name")
                        ?? ResolveFallbackAgentName(agentNameByRunId, spanRunId, runId),
                    Model = model,
                    InputTokens = ReadDimensionLong(customDimensions, "gen_ai.usage.input_tokens"),
                    OutputTokens = ReadDimensionLong(customDimensions, "gen_ai.usage.output_tokens"),
                    TotalNanoAiu = ReadDimensionLong(customDimensions, "agentweaver.aiu.nano"),
                    OperationName = operationName,
                    Attributes = ProjectTraceAttributes(
                        customDimensions,
                        traceContext,
                        spanRunId,
                        ReadBool(row[5]),
                        NullIfWhiteSpace(row[6]?.ToString())),
                };
            })
            .ToList();
        return (spans, null, isTruncated);
    }

    /// <summary>
    /// Projects only the fixed <see cref="TraceSpanAttributesDto"/> contract from an App Insights
    /// custom-dimension bag. The source bag can contain third-party/unbounded telemetry, so this
    /// method must remain an explicit allow-list rather than returning a copied dictionary.
    /// </summary>
    internal static TraceSpanAttributesDto ProjectTraceAttributes(
        IReadOnlyDictionary<string, string?> dimensions,
        RunTraceContext? context,
        string? observedRunId,
        bool success,
        string? resultCode)
    {
        var runId = BoundedDimension(dimensions, TraceTelemetry.RunId, TraceTelemetry.LegacyRunId)
            ?? BoundedValue(observedRunId)
            ?? BoundedValue(context?.RunId);
        return new TraceSpanAttributesDto
        {
            SessionId = BoundedDimension(dimensions, TraceTelemetry.SessionId),
            RunId = runId,
            ParentRunId = BoundedDimension(dimensions, "parent_run_id")
                ?? BoundedValue(context?.ParentRunId),
            ProjectId = BoundedDimension(dimensions, TraceTelemetry.ProjectId)
                ?? BoundedValue(context?.ProjectId),
            AgentName = BoundedDimension(dimensions, TraceTelemetry.AgentName, "agent_name")
                ?? BoundedValue(context?.AgentName),
            WorkflowRunId = BoundedDimension(dimensions, TraceTelemetry.WorkflowRunId)
                ?? BoundedValue(context?.WorkflowRunId),
            OperationName = AllowedDimension(dimensions, TraceTelemetry.OperationName,
                ["chat", "text_completion", "execute_tool"]),
            ModelId = BoundedDimension(dimensions, TraceTelemetry.ResponseModel, TraceTelemetry.RequestModel, "model", "model_id")
                ?? BoundedValue(context?.ModelId),
            ProviderSource = AllowedDimension(dimensions, TraceTelemetry.ProviderSource, ["github-copilot", "byok"])
                ?? AllowedValue(context?.ProviderSource, ["github-copilot", "byok"]),
            ProviderKind = AllowedDimension(dimensions, TraceTelemetry.ProviderKind,
                ["github_copilot", "byok", "project_github_copilot", "platform_github_copilot", "user_github_copilot", "user_byok"]),
            ProviderType = AllowedDimension(dimensions, TraceTelemetry.ProviderType, ["openai", "azure", "anthropic"]),
            ProviderScope = AllowedDimension(dimensions, TraceTelemetry.ProviderScope, ["project", "platform", "user"]),
            RoutingDecision = AllowedDimension(dimensions, TraceTelemetry.RoutingDecision, ["direct", "fallback"]),
            ToolName = BoundedDimension(dimensions, TraceTelemetry.ToolName, "tool_name"),
            ToolCallId = BoundedDimension(dimensions, TraceTelemetry.ToolCallId, "gen_ai.tool.call.id"),
            ToolSuccess = ReadDimensionBoolean(dimensions, TraceTelemetry.ToolSuccess),
            PolicyDecision = AllowedDimension(dimensions, TraceTelemetry.PolicyDecision,
                [TraceTelemetry.DecisionAllowed, TraceTelemetry.DecisionDenied, TraceTelemetry.DecisionEvaluationError]),
            AuthorizationDecision = AllowedDimension(dimensions, TraceTelemetry.AuthorizationDecision,
                [TraceTelemetry.DecisionApprovalRequired, TraceTelemetry.DecisionApproved, TraceTelemetry.DecisionDenied, TraceTelemetry.DecisionAutoApproved]),
            PolicyShellEnabled = ReadDimensionBoolean(dimensions, TraceTelemetry.PolicyShellEnabled),
            PolicyNetworkEnabled = ReadDimensionBoolean(dimensions, TraceTelemetry.PolicyNetworkEnabled),
            PolicyAutoApproveTools = ReadDimensionBoolean(dimensions, TraceTelemetry.PolicyAutoApproveTools)
                ?? context?.AutoApproveTools,
            RunStatus = BoundedDimension(dimensions, "agentweaver.run.status")
                ?? BoundedValue(context?.RunStatus),
            SandboxBackend = BoundedDimension(dimensions, TraceTelemetry.SandboxBackend)
                ?? BoundedValue(context?.SandboxBackend),
            SandboxIsolated = ReadDimensionBoolean(dimensions, TraceTelemetry.SandboxIsolated)
                ?? context?.SandboxIsolated,
            RuntimePurpose = AllowedDimension(dimensions, TraceTelemetry.RuntimePurpose,
                ["default", "assembly_build_test", "implementation_turn", "operator_assistant"]),
            InputTokens = ReadDimensionLong(dimensions, TraceTelemetry.InputTokens),
            OutputTokens = ReadDimensionLong(dimensions, TraceTelemetry.OutputTokens),
            TotalTokens = ReadDimensionLong(dimensions, TraceTelemetry.TotalTokens),
            TotalNanoAiu = ReadDimensionLong(dimensions, TraceTelemetry.NanoAiu),
            Status = AllowedDimension(dimensions, TraceTelemetry.Status, ["success", "error"])
                ?? (success ? "success" : "error"),
            ErrorType = BoundedDimension(dimensions, TraceTelemetry.ErrorType)
                ?? (!success ? BoundedValue(resultCode) : null),
            Execution = ProjectExecutionDiagnostics(dimensions),
        };
    }

    private static ExecutionDiagnosticsDto? ProjectExecutionDiagnostics(
        IReadOnlyDictionary<string, string?> dimensions)
    {
        var diagnostics = new ExecutionDiagnosticsDto
        {
            QueueEnteredAt = ReadBoundedTimestamp(dimensions, "agentweaver.execution.queue.entered_at"),
            DispatchStartedAt = ReadBoundedTimestamp(dimensions, "agentweaver.execution.dispatch.started_at"),
            ProcessStartedAt = ReadBoundedTimestamp(dimensions, TraceTelemetry.ProcessStartedAt),
            ProcessEndedAt = ReadBoundedTimestamp(dimensions, TraceTelemetry.ProcessEndedAt),
            HostProcessCpuMs = ReadNonNegativeLong(dimensions, TraceTelemetry.HostProcessCpuMs),
            HostProcessWorkingSetBytes = ReadNonNegativeLong(dimensions, TraceTelemetry.HostProcessWorkingSetBytes),
            HostProcessPeakWorkingSetBytes = ReadNonNegativeLong(dimensions, TraceTelemetry.HostProcessPeakWorkingSetBytes),
        };
        return diagnostics.QueueEnteredAt is null
            && diagnostics.DispatchStartedAt is null
            && diagnostics.ProcessStartedAt is null
            && diagnostics.ProcessEndedAt is null
            && diagnostics.HostProcessCpuMs is null
            && diagnostics.HostProcessWorkingSetBytes is null
            && diagnostics.HostProcessPeakWorkingSetBytes is null
                ? null
                : diagnostics;
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
        QueryFailureSink? failures = null,
        [CallerMemberName] string context = "")
    {
        var client = GetClient();
        if (client is null) return null;

        if (TryGetWorkspaceCooldown(out var remaining))
        {
            var exception = new TelemetryQueryUnavailableException(
                $"Application Insights workspace queries are paused for approximately {Math.Ceiling(remaining.TotalSeconds)} seconds after a dependency failure.");
            onError?.Invoke(exception);
            failures?.Record(context, exception);
            return null;
        }

        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queryCts.CancelAfter(WorkspaceQueryTimeout);
        var leaseAcquired = false;
        try
        {
            // This timeout covers both time spent queued behind the process-wide concurrency budget
            // and the Azure Monitor request, preventing an unavailable workspace from consuming
            // request threads or multiplying work as trace traffic grows.
            await _queryConcurrency.WaitAsync(queryCts.Token).ConfigureAwait(false);
            leaseAcquired = true;
            var response = await client.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(from, to),
                cancellationToken: queryCts.Token).ConfigureAwait(false);
            return response.Value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // #208 point 1: caller-requested cancellation is normal request-abort control flow, not a
            // genuine Azure Monitor dependency failure. Propagate quietly — no Warning/exception
            // telemetry, no failure-sink recording. ASP.NET Core's hosting diagnostics recognize an
            // OperationCanceledException tied to the request's own RequestAborted token and do not log
            // it as an unhandled error either.
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex is OperationCanceledException
                ? new TimeoutException($"Application Insights workspace query exceeded the {WorkspaceQueryTimeout.TotalSeconds:0}-second timeout.", ex)
                : ex;
            MarkWorkspaceUnavailable();
            onError?.Invoke(failure);
            if (failures is not null)
            {
                // #208 point 2: part of a top-level batch — record for a single aggregated log line
                // instead of logging once per subquery.
                failures.Record(context, failure);
            }
            else
            {
                _logger.LogError(
                    failure,
                    "Application Insights query failed in {QueryContext} ({FailureType}); workspace queries are paused for {CooldownSeconds} seconds.",
                    context,
                    failure.GetType().Name,
                    WorkspaceQueryCooldown.TotalSeconds);
            }
            return null;
        }
        finally
        {
            if (leaseAcquired)
                _queryConcurrency.Release();
        }
    }

    private bool TryGetWorkspaceCooldown(out TimeSpan remaining)
    {
        var unavailableUntil = new DateTimeOffset(Interlocked.Read(ref _workspaceUnavailableUntilUtcTicks), TimeSpan.Zero);
        remaining = unavailableUntil - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero;
    }

    private void MarkWorkspaceUnavailable() =>
        Interlocked.Exchange(ref _workspaceUnavailableUntilUtcTicks, DateTimeOffset.UtcNow.Add(WorkspaceQueryCooldown).Ticks);

    private static RunTraceDto UnavailableRunTrace(string runId, string queryError) => new()
    {
        RunId = runId,
        Spans = [],
        QueryError = queryError,
    };

    private static string DescribeTraceQueryFailure(Exception exception) =>
        exception is TimeoutException
            ? $"Application Insights trace telemetry did not respond within {WorkspaceQueryTimeout.TotalSeconds:0} seconds. Trace retrieval is paused briefly to protect responsiveness; retry shortly."
            : exception is TelemetryQueryUnavailableException
                ? "Application Insights trace telemetry is temporarily unavailable after a dependency failure. Retry shortly."
                : "Application Insights trace telemetry is temporarily unavailable. Retry shortly.";

    private sealed class TelemetryQueryUnavailableException(string message) : Exception(message);

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

    private static long? ReadNonNegativeLong(IReadOnlyDictionary<string, string?> dimensions, string key)
    {
        var value = ReadDimensionLong(dimensions, key);
        return value is >= 0 ? value : null;
    }

    private static DateTimeOffset? ReadBoundedTimestamp(
        IReadOnlyDictionary<string, string?> dimensions, string key)
    {
        var value = BoundedValue(ReadDimension(dimensions, key));
        return DateTimeOffset.TryParse(value, out var timestamp)
            && timestamp >= DateTimeOffset.UnixEpoch
            && timestamp <= DateTimeOffset.UtcNow.AddDays(1)
                ? timestamp
                : null;
    }

    private static bool? ReadDimensionBoolean(IReadOnlyDictionary<string, string?> dimensions, string key)
    {
        var value = ReadDimension(dimensions, key);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? BoundedDimension(IReadOnlyDictionary<string, string?> dimensions, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = BoundedValue(ReadDimension(dimensions, key));
            if (value is not null)
                return value;
        }
        return null;
    }

    private static string? AllowedDimension(
        IReadOnlyDictionary<string, string?> dimensions,
        string key,
        IReadOnlyCollection<string> allowed) =>
        AllowedValue(ReadDimension(dimensions, key), allowed);

    private static string? AllowedValue(string? value, IReadOnlyCollection<string> allowed)
    {
        var bounded = BoundedValue(value);
        return bounded is not null && allowed.Contains(bounded, StringComparer.Ordinal)
            ? bounded
            : null;
    }

    private static string? BoundedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 160 && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    private static string ReadDate(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd"),
            DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd"),
            _ => value?.ToString()?.Split('T')[0] ?? string.Empty,
        };
}
