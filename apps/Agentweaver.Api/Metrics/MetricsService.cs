using System.Globalization;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Squad.Squad;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Metrics;

/// <summary>
/// Assembles per-project dashboard metrics and the global "Now" overview entirely from live stores
/// (the <c>runs</c>, <c>backlog_tasks</c>, and <c>projects</c> tables) plus the in-process
/// coordinator heartbeat surface. No values are fabricated, estimated, or mocked
/// (Constitution Principle VII). Cost is never reported (Agentweaver records no real cost source).
///
/// <para>Provider-agnostic (spec-018): data access is delegated to a thin per-provider loader that
/// reads raw rows once — EF Core LINQ over <see cref="MemoryDbContext"/> when the active provider is
/// Postgres, raw SQLite SQL over <see cref="SqliteDb"/> otherwise — after which ALL grouping,
/// aggregation, and time math runs in process. This keeps a single source of truth for the metric
/// formulas across providers and avoids dialect-specific SQL (no <c>julianday</c>, no
/// <c>EXTRACT(EPOCH …)</c>). Singleton-safe: every call loads its own rows, no shared mutable state.</para>
/// </summary>
public sealed class MetricsService
{
    // Non-terminal run states that represent live, in-flight orchestration work.
    private static readonly HashSet<string> ActiveStatuses =
        new(StringComparer.Ordinal) { "pending", "in_progress", "awaiting_review", "committing", "merging" };

    // Terminal SUCCESS states. 'completed' is the legacy success terminal; 'merged' is the
    // full pipeline success terminal; 'assemble_ready' is the coordinator child success terminal.
    private static readonly HashSet<string> SuccessStatuses =
        new(StringComparer.Ordinal) { "merged", "completed", "assemble_ready" };

    // Any terminal (finished) state, used for the throughput "done" series.
    private static readonly HashSet<string> FinishedStatuses =
        new(StringComparer.Ordinal) { "merged", "completed", "assemble_ready", "declined", "failed", "merge_failed" };

    private readonly SqliteDb _db;
    private readonly IProjectStore _projectStore;
    private readonly HeartbeatStatusStore _heartbeatStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITokenUsageStore _usageStore;
    private readonly ILogger<MetricsService>? _logger;
    private readonly bool _isPostgres;

    public MetricsService(
        SqliteDb db,
        IProjectStore projectStore,
        HeartbeatStatusStore heartbeatStore,
        IServiceScopeFactory scopeFactory,
        ITokenUsageStore usageStore,
        IConfiguration configuration,
        ILogger<MetricsService>? logger = null)
    {
        _db = db;
        _projectStore = projectStore;
        _heartbeatStore = heartbeatStore;
        _scopeFactory = scopeFactory;
        _usageStore = usageStore;
        _logger = logger;
        var provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        _isPostgres = provider is "postgres" or "postgresql";
    }

    /// <summary>
    /// Minimal projection of a <c>runs</c> row carrying only the columns the dashboard/overview
    /// aggregates need. Loaded once per request by <see cref="LoadRunsAsync"/> so every metric is
    /// computed in process from the same provider-neutral shape.
    /// </summary>
    private readonly record struct RunRow(
        string? ProjectId,
        string? AgentName,
        string Status,
        string Origin,
        string? ParentRunId,
        string? Task,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        long ReviewWaitMs);

    // Active (working) duration of a run, in ms, EXCLUDING accrued human-review dwell, clamped at 0.
    private static double ActiveDurationMs(RunRow r) =>
        ActiveDurationMsExcludingReview(r.StartedAt, r.EndedAt!.Value, r.ReviewWaitMs);

    // UTC calendar day (yyyy-MM-dd) of a timestamp, matching SQLite date() on the stored ISO value.
    private static string UtcDay(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------
    // ENDPOINT 1 — Per-project dashboard
    // ---------------------------------------------------------------------------------

    public Task<ProjectDashboardDto> GetProjectDashboardAsync(Project project, CancellationToken ct = default) =>
        GetProjectDashboardAsync(project, from: null, to: null, ct);

    public async Task<ProjectDashboardDto> GetProjectDashboardAsync(
        Project project,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var weekAgo = now.AddDays(-7);
        var (dashboardFrom, dashboardTo) = ResolveDashboardRange(from, to, now);
        // 30-day window inclusive of today: today and the preceding 29 days.
        var windowStart = now.UtcDateTime.Date.AddDays(-29);
        var pid = project.Id.ToString();

        var runs = await LoadRunsAsync(pid, ct).ConfigureAwait(false);

        var summary = ReadSummary(runs, weekAgo);
        var throughput = ReadThroughput(runs, windowStart);
        var agentRoles = ReadAgentRoles(project);
        var leaderboard = ReadLeaderboard(runs, dashboardFrom, dashboardTo, agentRoles);

        TokenUsageSummaryDto? tokenUsage = null;
        try
        {
            var usage = await _usageStore.GetProjectUsageAsync(pid, dashboardFrom, dashboardTo, ct).ConfigureAwait(false);
            tokenUsage = ToSummaryDto(usage);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MetricsService: could not load token usage for project {ProjectId}.", pid);
        }

        return new ProjectDashboardDto
        {
            ProjectId       = pid,
            ProjectName     = project.Name,
            GeneratedUtc    = now,
            Summary         = summary,
            Throughput      = throughput,
            AgentLeaderboard = leaderboard,
            TokenUsage      = tokenUsage,
        };
    }

    private static (DateTimeOffset From, DateTimeOffset To) ResolveDashboardRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now)
    {
        var resolvedTo = to ?? now;
        var resolvedFrom = from ?? (to is null ? now.AddDays(-7) : resolvedTo.AddDays(-7));
        return (resolvedFrom, resolvedTo);
    }

    private static DashboardSummaryDto ReadSummary(IReadOnlyList<RunRow> runs, DateTimeOffset weekAgo)
    {
        var activeAgents = new HashSet<string>(StringComparer.Ordinal);
        int runsThisWeek = 0, activeRuns = 0, tasksDoneThisWeek = 0;
        foreach (var r in runs)
        {
            if (r.StartedAt >= weekAgo) runsThisWeek++;
            if (r.Status == "in_progress")
            {
                activeRuns++;
                if (r.AgentName is not null) activeAgents.Add(r.AgentName);
            }
            if (SuccessStatuses.Contains(r.Status) && r.EndedAt is { } ended && ended >= weekAgo)
                tasksDoneThisWeek++;
        }

        return new DashboardSummaryDto
        {
            RunsThisWeek      = runsThisWeek,
            RunsTotal         = runs.Count,
            ActiveRuns        = activeRuns,
            ActiveAgents      = activeAgents.Count,
            TasksDoneThisWeek = tasksDoneThisWeek,
        };
    }

    private static IReadOnlyList<ThroughputPointDto> ReadThroughput(IReadOnlyList<RunRow> runs, DateTime windowStart)
    {
        var startDate = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in runs)
        {
            var createdDay = UtcDay(r.StartedAt);
            if (string.CompareOrdinal(createdDay, startDate) >= 0)
                created[createdDay] = created.GetValueOrDefault(createdDay) + 1;

            if (r.EndedAt is { } ended && FinishedStatuses.Contains(r.Status))
            {
                var doneDay = UtcDay(ended);
                if (string.CompareOrdinal(doneDay, startDate) >= 0)
                    done[doneDay] = done.GetValueOrDefault(doneDay) + 1;
            }
        }

        var series = new List<ThroughputPointDto>(30);
        for (int i = 0; i < 30; i++)
        {
            var day = windowStart.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            series.Add(new ThroughputPointDto
            {
                Date    = day,
                Created = created.GetValueOrDefault(day),
                Done    = done.GetValueOrDefault(day),
            });
        }
        return series;
    }

    private static IReadOnlyList<AgentLeaderboardEntryDto> ReadLeaderboard(
        IReadOnlyList<RunRow> runs,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        IReadOnlyDictionary<string, string> agentRoles)
    {
        // Aggregate per agent_name (children included, matching the original GROUP BY agent_name).
        var acc = new Dictionary<string, (int runsThisWeek, int runsTotal, int success, int terminal, double durSum, int durCount)>(StringComparer.Ordinal);
        foreach (var r in runs)
        {
            if (r.AgentName is null) continue;
            var a = acc.GetValueOrDefault(r.AgentName);
            a.runsTotal++;
            if (r.StartedAt >= rangeStart && r.StartedAt <= rangeEnd) a.runsThisWeek++;
            if (SuccessStatuses.Contains(r.Status)) a.success++;
            if (FinishedStatuses.Contains(r.Status)) a.terminal++;
            if (r.EndedAt is not null)
            {
                a.durSum += ActiveDurationMs(r);
                a.durCount++;
            }
            acc[r.AgentName] = a;
        }

        return acc
            .OrderByDescending(kv => kv.Value.runsTotal)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AgentLeaderboardEntryDto
            {
                Agent          = kv.Key,
                RoleTitle      = ResolveAgentRole(agentRoles, kv.Key),
                RunsThisWeek   = kv.Value.runsThisWeek,
                RunsTotal      = kv.Value.runsTotal,
                SuccessfulRuns = kv.Value.success,
                TerminalRuns   = kv.Value.terminal,
                SuccessRate    = kv.Value.terminal == 0 ? 0d : (double)kv.Value.success / kv.Value.terminal,
                AvgDurationMs  = kv.Value.durCount == 0 ? null : kv.Value.durSum / kv.Value.durCount,
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ReadAgentRoles(Project project)
    {
        try
        {
            var reader = new SquadReader(project.WorkingDirectory);
            var roles = NewRoleDictionary();
            AddRoleAliases(roles, "Coordinator", "Coordinator");
            AddRoleAliases(roles, "Squad", "Coordinator");

            var team = reader.ReadTeam();
            if (team is null) return roles;

            var memberRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in team.Members.Where(m => !string.IsNullOrWhiteSpace(m.Role.Title)))
            {
                AddRoleAliases(memberRoles, member.Name, member.Role.Title);
                AddRoleAliases(roles, member.Name, member.Role.Title);
            }

            var registry = reader.ReadRegistry();
            foreach (var (registryName, member) in registry.Agents)
            {
                var roleTitle = ResolveAgentRole(memberRoles, member.PersistentName)
                    ?? (!string.IsNullOrWhiteSpace(member.PreviousName) ? ResolveAgentRole(memberRoles, member.PreviousName) : null);
                if (string.IsNullOrWhiteSpace(roleTitle)) continue;

                AddRoleAliases(roles, registryName, roleTitle);
                AddRoleAliases(roles, member.Name, roleTitle);
                AddRoleAliases(roles, member.PersistentName, roleTitle);
                if (!string.IsNullOrWhiteSpace(member.PreviousName))
                    AddRoleAliases(roles, member.PreviousName, roleTitle);
            }

            return roles;
        }
        catch (Exception)
        {
            var roles = NewRoleDictionary();
            AddRoleAliases(roles, "Coordinator", "Coordinator");
            AddRoleAliases(roles, "Squad", "Coordinator");
            return roles;
        }
    }

    private static Dictionary<string, string> NewRoleDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static void AddRoleAliases(IDictionary<string, string> roles, string? agentName, string roleTitle)
    {
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(roleTitle)) return;

        var trimmed = agentName.Trim();
        roles.TryAdd(trimmed, roleTitle);

        var normalized = NormalizeAgentName(trimmed);
        if (normalized.Length > 0)
            roles.TryAdd(normalized, roleTitle);
    }

    private static string? ResolveAgentRole(IReadOnlyDictionary<string, string> roles, string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return null;

        var trimmed = agentName.Trim();
        if (roles.TryGetValue(trimmed, out var role)) return role;

        var normalized = NormalizeAgentName(trimmed);
        return normalized.Length > 0 && roles.TryGetValue(normalized, out role) ? role : null;
    }

    private static string NormalizeAgentName(string value)
    {
        var buffer = new char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, n);
    }

    // ---------------------------------------------------------------------------------
    // ENDPOINT 2 — Global "Now" overview
    // ---------------------------------------------------------------------------------

    public async Task<OverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var todayUtc = now.UtcDateTime.Date;

        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);
        var names = projects.ToDictionary(p => p.Id.ToString(), p => p.Name, StringComparer.Ordinal);

        var runs = await LoadRunsAsync(null, ct).ConfigureAwait(false);
        var readyProjectIds = await LoadReadyBacklogProjectIdsAsync(ct).ConfigureAwait(false);

        var atAGlance = ReadAtAGlance(runs, readyProjectIds, todayUtc);
        var liveSessions = ReadLiveSessions(runs, names);
        var workflowRuns = ReadActiveWorkflowRuns(runs, names);
        var activeProjects = ReadActiveProjects(runs, readyProjectIds, names);
        var recent = ReadRecentActivity(runs, names);

        AppUsageDto? tokenUsage = null;
        try
        {
            var from = now.AddDays(-30);
            var appUsage = await _usageStore.GetAppUsageAsync(from, now, ct).ConfigureAwait(false);
            tokenUsage = ToAppUsageDto(appUsage, from, now);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MetricsService: could not load app-level token usage.");
        }

        return new OverviewDto
        {
            GeneratedUtc       = now,
            AtAGlance          = atAGlance,
            LiveSessions       = liveSessions,
            ActiveWorkflowRuns = workflowRuns,
            ActiveProjects     = activeProjects,
            RecentActivity     = recent,
            TokenUsage         = tokenUsage,
        };
    }

    private AtAGlanceDto ReadAtAGlance(
        IReadOnlyList<RunRow> runs, IReadOnlyList<string> readyProjectIds, DateTime todayUtc)
    {
        int inFlight = 0, pendingRuns = 0, doneToday = 0, mergeFailed = 0;
        var activeProjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in runs)
        {
            switch (r.Status)
            {
                case "in_progress":
                    inFlight++;
                    if (r.ProjectId is not null) activeProjectIds.Add(r.ProjectId);
                    break;
                case "pending":
                    pendingRuns++;
                    break;
                case "merge_failed":
                    mergeFailed++;
                    break;
            }
            if (SuccessStatuses.Contains(r.Status) && r.EndedAt is { } ended
                && ended.UtcDateTime.Date == todayUtc)
                doneToday++;
        }
        activeProjectIds.UnionWith(readyProjectIds);

        var degraded = !_heartbeatStore.Enabled || _heartbeatStore.LastError is not null || mergeFailed > 0;

        return new AtAGlanceDto
        {
            InFlight       = inFlight,
            QueuedWork     = pendingRuns + readyProjectIds.Count,
            DoneToday      = doneToday,
            ActiveProjects = activeProjectIds.Count,
            Health         = degraded ? "degraded" : "healthy",
        };
    }

    private static IReadOnlyList<LiveSessionDto> ReadLiveSessions(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names)
    {
        return runs
            .Where(r => r.Status == "in_progress" && r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new LiveSessionDto
            {
                ProjectId       = r.ProjectId!,
                ProjectName     = names[r.ProjectId!],
                Agent           = r.AgentName,
                Status          = r.Status,
                StartedUtc      = r.StartedAt,
                LastActivityUtc = r.EndedAt ?? r.StartedAt,
            })
            .ToList();
    }

    private static IReadOnlyList<ActiveWorkflowRunDto> ReadActiveWorkflowRuns(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names)
    {
        return runs
            .Where(r => ActiveStatuses.Contains(r.Status) && r.ParentRunId is null
                        && r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new ActiveWorkflowRunDto
            {
                ProjectId   = r.ProjectId!,
                ProjectName = names[r.ProjectId!],
                Trigger     = string.IsNullOrEmpty(r.Origin) ? "interactive" : r.Origin,
                Status      = r.Status,
                StartedUtc  = r.StartedAt,
            })
            .ToList();
    }

    private static IReadOnlyList<ActiveProjectDto> ReadActiveProjects(
        IReadOnlyList<RunRow> runs,
        IReadOnlyList<string> readyProjectIds,
        IReadOnlyDictionary<string, string> names)
    {
        // Per-project run rollup: active (in_progress), pending, and latest activity timestamp.
        var rollup = new Dictionary<string, (int active, int pending, DateTimeOffset? lastAct)>(StringComparer.Ordinal);
        foreach (var r in runs)
        {
            if (r.ProjectId is null) continue;
            var acc = rollup.GetValueOrDefault(r.ProjectId);
            if (r.Status == "in_progress") acc.active++;
            if (r.Status == "pending") acc.pending++;
            var activity = r.EndedAt ?? r.StartedAt;
            if (acc.lastAct is null || activity > acc.lastAct) acc.lastAct = activity;
            rollup[r.ProjectId] = acc;
        }

        // Ready backlog tasks per project.
        var ready = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pid in readyProjectIds)
            ready[pid] = ready.GetValueOrDefault(pid) + 1;

        var ids = new HashSet<string>(rollup.Keys, StringComparer.Ordinal);
        ids.UnionWith(ready.Keys);

        var result = new List<ActiveProjectDto>();
        foreach (var id in ids)
        {
            if (!names.TryGetValue(id, out var name)) continue;
            var (active, pending, lastAct) = rollup.GetValueOrDefault(id, (0, 0, null));
            var readyCount = ready.GetValueOrDefault(id);
            var queued = pending + readyCount;
            if (active == 0 && queued == 0) continue;

            result.Add(new ActiveProjectDto
            {
                ProjectId       = id,
                ProjectName     = name,
                ActiveCount     = active,
                QueuedCount     = queued,
                LastActivityUtc = lastAct,
            });
        }

        return result
            .OrderByDescending(p => p.ActiveCount)
            .ThenByDescending(p => p.QueuedCount)
            .ToList();
    }

    private static IReadOnlyList<RecentActivityDto> ReadRecentActivity(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names)
    {
        return runs
            .Where(r => r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.EndedAt ?? r.StartedAt)
            .Take(20)
            .Select(r => new RecentActivityDto
            {
                ProjectId    = r.ProjectId!,
                ProjectName  = names[r.ProjectId!],
                Label        = Truncate(r.Task ?? string.Empty, 80),
                Kind         = r.Status,
                TimestampUtc = r.EndedAt ?? r.StartedAt,
            })
            .ToList();
    }

    // ---------------------------------------------------------------------------------
    // Provider-agnostic data access (spec-018): EF Core over MemoryDbContext for Postgres,
    // raw SQLite SQL over SqliteDb otherwise. Aggregation always happens in process above.
    // ---------------------------------------------------------------------------------

    private async Task<IReadOnlyList<RunRow>> LoadRunsAsync(string? projectId, CancellationToken ct)
    {
        if (_isPostgres)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var query = db.Runs.AsNoTracking();
            if (projectId is not null) query = query.Where(r => r.ProjectId == projectId);
            return await query
                .Select(r => new RunRow(
                    r.ProjectId, r.AgentName, r.Status, r.Origin, r.ParentRunId, r.Task,
                    r.StartedAt, r.EndedAt, r.ReviewWaitMs))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT project_id, agent_name, status, origin, parent_run_id, task, " +
            "started_at, ended_at, review_wait_ms FROM runs" +
            (projectId is null ? ";" : " WHERE project_id = $pid;");
        if (projectId is not null) cmd.Parameters.AddWithValue("$pid", projectId);

        var rows = new List<RunRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RunRow(
                ProjectId:    r.IsDBNull(0) ? null : r.GetString(0),
                AgentName:    r.IsDBNull(1) ? null : r.GetString(1),
                Status:       r.GetString(2),
                Origin:       r.IsDBNull(3) ? "interactive" : r.GetString(3),
                ParentRunId:  r.IsDBNull(4) ? null : r.GetString(4),
                Task:         r.IsDBNull(5) ? null : r.GetString(5),
                StartedAt:    ParseTs(r.GetString(6)),
                EndedAt:      r.IsDBNull(7) ? null : ParseTs(r.GetString(7)),
                ReviewWaitMs: r.IsDBNull(8) ? 0L : r.GetInt64(8)));
        }
        return rows;
    }

    private async Task<IReadOnlyList<string>> LoadReadyBacklogProjectIdsAsync(CancellationToken ct)
    {
        if (_isPostgres)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            return await db.BacklogTasks.AsNoTracking()
                .Where(t => t.State == "ready")
                .Select(t => t.ProjectId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM backlog_tasks WHERE state = 'ready';";
        var ids = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            if (!r.IsDBNull(0)) ids.Add(r.GetString(0));
        return ids;
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Active (working) duration of a run, in milliseconds, EXCLUDING the cumulative time it spent
    /// parked in the awaiting_review human-review gate (<paramref name="reviewWaitMs"/> = the run's
    /// accrued <c>review_wait_ms</c>). Total elapsed minus review dwell, clamped at 0. Use this for any
    /// per-run duration computed in process so it matches the dashboard aggregates exactly.
    /// </summary>
    internal static double ActiveDurationMsExcludingReview(
        DateTimeOffset startedAt, DateTimeOffset endedAt, long reviewWaitMs) =>
        Math.Max(0d, (endedAt - startedAt).TotalMilliseconds - reviewWaitMs);

    // Truncates an activity label to at most <paramref name="max"/> chars. When the value is longer it
    // cuts at a word boundary (no mid-word cut) at or under max-1 chars and appends a single-char
    // ellipsis, so the total length stays <= max. Falls back to a hard cut + ellipsis only when there
    // is no usable space (e.g. one unbroken token longer than max).
    private const char Ellipsis = '\u2026';

    internal static string Truncate(string value, int max)
    {
        value = value.TrimEnd();
        if (value.Length <= max) return value;

        var head = value[..(max - 1)];
        var lastSpace = head.LastIndexOf(' ');

        // Only honor the word boundary when it leaves a reasonable amount of text (>= half of max),
        // otherwise a long leading token would shrink the label too far.
        if (lastSpace >= max / 2)
            head = head[..lastSpace];

        return head.TrimEnd() + Ellipsis;
    }

    // ---------------------------------------------------------------------------------
    // Token usage DTO mapping helpers (Feature 019)
    // ---------------------------------------------------------------------------------

    internal static TokenUsageSummaryDto ToSummaryDto(TokenUsageSummary summary) =>
        new()
        {
            InputTokens  = summary.InputTokens,
            OutputTokens = summary.OutputTokens,
            TotalTokens  = summary.TotalTokens,
            TotalNanoAiu = summary.TotalNanoAiu,
            ByModel      = summary.ByModel.Select(m => new TokenUsageByModelDto
            {
                ModelId      = m.ModelId,
                InputTokens  = m.InputTokens,
                OutputTokens = m.OutputTokens,
                TotalNanoAiu = m.TotalNanoAiu,
            }).ToList(),
        };

    internal static AppUsageDto ToAppUsageDto(
        IReadOnlyList<TokenUsageByProject> byProject, DateTimeOffset from, DateTimeOffset to)
    {
        var now = DateTimeOffset.UtcNow;
        var allModels = byProject
            .SelectMany(p => p.ByModel)
            .GroupBy(m => m.ModelId, StringComparer.Ordinal)
            .Select(g => new TokenUsageByModelDto
            {
                ModelId      = g.Key,
                InputTokens  = g.Sum(m => m.InputTokens),
                OutputTokens = g.Sum(m => m.OutputTokens),
                TotalNanoAiu = g.Sum(m => m.TotalNanoAiu),
            })
            .ToList();

        var totalTokens = byProject.Sum(p => p.TotalTokens);
        var totalNano   = byProject.Sum(p => p.TotalNanoAiu);

        var projectDtos = byProject.Select(p => new ProjectUsageDto
        {
            ProjectId    = p.ProjectId,
            ProjectName  = p.ProjectName,
            TotalTokens  = p.TotalTokens,
            TotalNanoAiu = p.TotalNanoAiu,
            ByModel      = p.ByModel.Select(m => new TokenUsageByModelDto
            {
                ModelId      = m.ModelId,
                InputTokens  = m.InputTokens,
                OutputTokens = m.OutputTokens,
                TotalNanoAiu = m.TotalNanoAiu,
            }).ToList(),
        }).ToList();

        return new AppUsageDto
        {
            GeneratedUtc = now,
            FromUtc      = from,
            ToUtc        = to,
            TotalTokens  = totalTokens,
            TotalNanoAiu = totalNano,
            ByProject    = projectDtos,
            ByModel      = allModels,
        };
    }
}
