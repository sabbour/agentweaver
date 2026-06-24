using System.Globalization;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Squad.Squad;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Api.Metrics;

/// <summary>
/// Assembles per-project dashboard metrics and the global "Now" overview entirely from live stores
/// (the <c>runs</c>, <c>backlog_tasks</c>, and <c>projects</c> SQLite tables) plus the in-process
/// coordinator heartbeat surface. No values are fabricated, estimated, or mocked
/// (Constitution Principle VII). Cost is never reported (Agentweaver records no real cost source).
///
/// <para>Every public method opens a single SQLite connection and groups/aggregates in SQL to keep
/// the 30-second auto-refresh pages cheap (no N+1). Singleton-safe: connection-per-call, no shared
/// mutable state.</para>
/// </summary>
public sealed class MetricsService
{
    // Non-terminal run states that represent live, in-flight orchestration work.
    private const string ActiveStatuses = "'pending','in_progress','awaiting_review','committing','merging'";

    // Terminal SUCCESS states. 'completed' is the legacy success terminal; 'merged' is the
    // full pipeline success terminal; 'assemble_ready' is the coordinator child success terminal.
    private const string SuccessStatuses = "'merged','completed','assemble_ready'";

    // Any terminal (finished) state, used for the throughput "done" series.
    private const string FinishedStatuses = "'merged','completed','assemble_ready','declined','failed','merge_failed'";

    // Active (working) duration of a FINISHED run in ms, EXCLUDING the cumulative time the run spent
    // parked in the awaiting_review human-review gate (runs.review_wait_ms, accrued on every exit from
    // awaiting_review). Single source of truth so every duration aggregate excludes review dwell
    // identically; clamped at 0 so review-heavy rows never report negative. See
    // <see cref="ActiveDurationMsExcludingReview"/> for the equivalent C# computation.
    private const string ActiveDurationMsSql =
        "MAX(0, ((julianday(ended_at) - julianday(started_at)) * 86400000.0) - COALESCE(review_wait_ms, 0))";

    private readonly SqliteDb _db;
    private readonly IProjectStore _projectStore;
    private readonly HeartbeatStatusStore _heartbeatStore;

    public MetricsService(SqliteDb db, IProjectStore projectStore, HeartbeatStatusStore heartbeatStore)
    {
        _db = db;
        _projectStore = projectStore;
        _heartbeatStore = heartbeatStore;
    }

    // ---------------------------------------------------------------------------------
    // ENDPOINT 1 — Per-project dashboard
    // ---------------------------------------------------------------------------------

    public async Task<ProjectDashboardDto> GetProjectDashboardAsync(Project project, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var weekAgo = now.AddDays(-7);
        // 30-day window inclusive of today: today and the preceding 29 days.
        var windowStart = now.UtcDateTime.Date.AddDays(-29);
        var pid = project.Id.ToString();

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);

        var summary = await ReadSummaryAsync(conn, pid, weekAgo, ct).ConfigureAwait(false);
        var throughput = await ReadThroughputAsync(conn, pid, windowStart, ct).ConfigureAwait(false);
        var agentRoles = ReadAgentRoles(project);
        var leaderboard = await ReadLeaderboardAsync(conn, pid, weekAgo, agentRoles, ct).ConfigureAwait(false);

        return new ProjectDashboardDto
        {
            ProjectId       = pid,
            ProjectName     = project.Name,
            GeneratedUtc    = now,
            Summary         = summary,
            Throughput      = throughput,
            AgentLeaderboard = leaderboard,
        };
    }

    private static async Task<DashboardSummaryDto> ReadSummaryAsync(
        SqliteConnection conn, string pid, DateTimeOffset weekAgo, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT
                COALESCE(SUM(CASE WHEN julianday(started_at) >= julianday($weekAgo) THEN 1 ELSE 0 END), 0),
                COUNT(*),
                COALESCE(SUM(CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END), 0),
                COUNT(DISTINCT CASE WHEN status = 'in_progress' AND agent_name IS NOT NULL THEN agent_name END),
                COALESCE(SUM(CASE WHEN status IN ({SuccessStatuses}) AND ended_at IS NOT NULL
                                   AND julianday(ended_at) >= julianday($weekAgo) THEN 1 ELSE 0 END), 0)
            FROM runs
            WHERE project_id = $pid;
            """;
        cmd.Parameters.AddWithValue("$pid", pid);
        cmd.Parameters.AddWithValue("$weekAgo", Iso(weekAgo));

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return new DashboardSummaryDto
        {
            RunsThisWeek      = r.GetInt32(0),
            RunsTotal         = r.GetInt32(1),
            ActiveRuns        = r.GetInt32(2),
            ActiveAgents      = r.GetInt32(3),
            TasksDoneThisWeek = r.GetInt32(4),
        };
    }

    private static async Task<IReadOnlyList<ThroughputPointDto>> ReadThroughputAsync(
        SqliteConnection conn, string pid, DateTime windowStart, CancellationToken ct)
    {
        var startDate = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = new Dictionary<string, int>(StringComparer.Ordinal);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT date(started_at) AS d, COUNT(*)
                FROM runs
                WHERE project_id = $pid AND date(started_at) >= $start
                GROUP BY d;
                """;
            cmd.Parameters.AddWithValue("$pid", pid);
            cmd.Parameters.AddWithValue("$start", startDate);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (!r.IsDBNull(0)) created[r.GetString(0)] = r.GetInt32(1);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"""
                SELECT date(ended_at) AS d, COUNT(*)
                FROM runs
                WHERE project_id = $pid AND ended_at IS NOT NULL
                  AND status IN ({FinishedStatuses}) AND date(ended_at) >= $start
                GROUP BY d;
                """;
            cmd.Parameters.AddWithValue("$pid", pid);
            cmd.Parameters.AddWithValue("$start", startDate);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (!r.IsDBNull(0)) done[r.GetString(0)] = r.GetInt32(1);
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

    private static async Task<IReadOnlyList<AgentLeaderboardEntryDto>> ReadLeaderboardAsync(
        SqliteConnection conn,
        string pid,
        DateTimeOffset weekAgo,
        IReadOnlyDictionary<string, string> agentRoles,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT
                agent_name,
                COALESCE(SUM(CASE WHEN julianday(started_at) >= julianday($weekAgo) THEN 1 ELSE 0 END), 0),
                COUNT(*),
                COALESCE(SUM(CASE WHEN status IN ({SuccessStatuses}) THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ({FinishedStatuses}) THEN 1 ELSE 0 END), 0),
                AVG(CASE WHEN ended_at IS NOT NULL THEN {ActiveDurationMsSql} END)
            FROM runs
            WHERE project_id = $pid AND agent_name IS NOT NULL
            GROUP BY agent_name
            ORDER BY COUNT(*) DESC, agent_name ASC;
            """;
        cmd.Parameters.AddWithValue("$pid", pid);
        cmd.Parameters.AddWithValue("$weekAgo", Iso(weekAgo));

        var result = new List<AgentLeaderboardEntryDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var agent = r.GetString(0);
            var successfulRuns = r.GetInt32(3);
            var terminalRuns = r.GetInt32(4);
            result.Add(new AgentLeaderboardEntryDto
            {
                Agent         = agent,
                RoleTitle     = ResolveAgentRole(agentRoles, agent),
                RunsThisWeek  = r.GetInt32(1),
                RunsTotal     = r.GetInt32(2),
                SuccessfulRuns = successfulRuns,
                TerminalRuns  = terminalRuns,
                SuccessRate   = terminalRuns == 0 ? 0d : (double)successfulRuns / terminalRuns,
                AvgDurationMs = r.IsDBNull(5) ? null : r.GetDouble(5),
            });
        }
        return result;
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
        var todayUtc = now.UtcDateTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);
        var names = projects.ToDictionary(p => p.Id.ToString(), p => p.Name, StringComparer.Ordinal);

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);

        var atAGlance = await ReadAtAGlanceAsync(conn, todayUtc, ct).ConfigureAwait(false);
        var liveSessions = await ReadLiveSessionsAsync(conn, names, ct).ConfigureAwait(false);
        var workflowRuns = await ReadActiveWorkflowRunsAsync(conn, names, ct).ConfigureAwait(false);
        var activeProjects = await ReadActiveProjectsAsync(conn, names, ct).ConfigureAwait(false);
        var recent = await ReadRecentActivityAsync(conn, names, ct).ConfigureAwait(false);

        return new OverviewDto
        {
            GeneratedUtc       = now,
            AtAGlance          = atAGlance,
            LiveSessions       = liveSessions,
            ActiveWorkflowRuns = workflowRuns,
            ActiveProjects     = activeProjects,
            RecentActivity     = recent,
        };
    }

    private async Task<AtAGlanceDto> ReadAtAGlanceAsync(SqliteConnection conn, string todayUtc, CancellationToken ct)
    {
        int inFlight, pendingRuns, readyTasks, doneToday, activeProjects, mergeFailed;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"""
                SELECT
                    (SELECT COUNT(*) FROM runs WHERE status = 'in_progress'),
                    (SELECT COUNT(*) FROM runs WHERE status = 'pending'),
                    (SELECT COUNT(*) FROM backlog_tasks WHERE state = 'ready'),
                    (SELECT COUNT(*) FROM runs WHERE status IN ({SuccessStatuses})
                        AND ended_at IS NOT NULL AND date(ended_at) = $today),
                    (SELECT COUNT(*) FROM (
                        SELECT project_id FROM runs WHERE status = 'in_progress' AND project_id IS NOT NULL
                        UNION
                        SELECT project_id FROM backlog_tasks WHERE state = 'ready')),
                    (SELECT COUNT(*) FROM runs WHERE status = 'merge_failed');
                """;
            cmd.Parameters.AddWithValue("$today", todayUtc);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            inFlight       = r.GetInt32(0);
            pendingRuns    = r.GetInt32(1);
            readyTasks     = r.GetInt32(2);
            doneToday      = r.GetInt32(3);
            activeProjects = r.GetInt32(4);
            mergeFailed    = r.GetInt32(5);
        }

        var degraded = !_heartbeatStore.Enabled || _heartbeatStore.LastError is not null || mergeFailed > 0;

        return new AtAGlanceDto
        {
            InFlight       = inFlight,
            QueuedWork     = pendingRuns + readyTasks,
            DoneToday      = doneToday,
            ActiveProjects = activeProjects,
            Health         = degraded ? "degraded" : "healthy",
        };
    }

    private static async Task<IReadOnlyList<LiveSessionDto>> ReadLiveSessionsAsync(
        SqliteConnection conn, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, agent_name, status, started_at, ended_at
            FROM runs
            WHERE status = 'in_progress' AND project_id IS NOT NULL
            ORDER BY julianday(started_at) DESC;
            """;
        var result = new List<LiveSessionDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var projectId = r.GetString(0);
            if (!names.TryGetValue(projectId, out var name)) continue;
            var started = ParseTs(r.GetString(3));
            var ended = r.IsDBNull(4) ? (DateTimeOffset?)null : ParseTs(r.GetString(4));
            result.Add(new LiveSessionDto
            {
                ProjectId       = projectId,
                ProjectName     = name,
                Agent           = r.IsDBNull(1) ? null : r.GetString(1),
                Status          = r.GetString(2),
                StartedUtc      = started,
                LastActivityUtc = ended ?? started,
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<ActiveWorkflowRunDto>> ReadActiveWorkflowRunsAsync(
        SqliteConnection conn, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT project_id, origin, status, started_at
            FROM runs
            WHERE status IN ({ActiveStatuses}) AND parent_run_id IS NULL AND project_id IS NOT NULL
            ORDER BY julianday(started_at) DESC;
            """;
        var result = new List<ActiveWorkflowRunDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var projectId = r.GetString(0);
            if (!names.TryGetValue(projectId, out var name)) continue;
            result.Add(new ActiveWorkflowRunDto
            {
                ProjectId   = projectId,
                ProjectName = name,
                Trigger     = r.IsDBNull(1) ? "interactive" : r.GetString(1),
                Status      = r.GetString(2),
                StartedUtc  = ParseTs(r.GetString(3)),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<ActiveProjectDto>> ReadActiveProjectsAsync(
        SqliteConnection conn, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        // Per-project run rollup: active (in_progress), pending, and latest activity timestamp.
        var rollup = new Dictionary<string, (int active, int pending, string? lastAct)>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT
                    project_id,
                    SUM(CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END),
                    MAX(COALESCE(ended_at, started_at))
                FROM runs
                WHERE project_id IS NOT NULL
                GROUP BY project_id;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                rollup[r.GetString(0)] = (
                    r.GetInt32(1),
                    r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3));
            }
        }

        // Ready backlog tasks per project.
        var ready = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT project_id, COUNT(*)
                FROM backlog_tasks
                WHERE state = 'ready'
                GROUP BY project_id;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                ready[r.GetString(0)] = r.GetInt32(1);
        }

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
                LastActivityUtc = lastAct is null ? null : ParseTs(lastAct),
            });
        }

        return result
            .OrderByDescending(p => p.ActiveCount)
            .ThenByDescending(p => p.QueuedCount)
            .ToList();
    }

    private static async Task<IReadOnlyList<RecentActivityDto>> ReadRecentActivityAsync(
        SqliteConnection conn, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, task, status, started_at, ended_at
            FROM runs
            WHERE project_id IS NOT NULL
            ORDER BY julianday(COALESCE(ended_at, started_at)) DESC
            LIMIT 20;
            """;
        var result = new List<RecentActivityDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var projectId = r.GetString(0);
            if (!names.TryGetValue(projectId, out var name)) continue;
            var task = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            var status = r.GetString(2);
            var ts = r.IsDBNull(4) ? ParseTs(r.GetString(3)) : ParseTs(r.GetString(4));
            result.Add(new RecentActivityDto
            {
                ProjectId    = projectId,
                ProjectName  = name,
                Label        = Truncate(task, 80),
                Kind         = status,
                TimestampUtc = ts,
            });
        }
        return result;
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Active (working) duration of a run, in milliseconds, EXCLUDING the cumulative time it spent
    /// parked in the awaiting_review human-review gate (<paramref name="reviewWaitMs"/> = the run's
    /// accrued <c>review_wait_ms</c>). The C# counterpart of <see cref="ActiveDurationMsSql"/>: both
    /// compute total elapsed minus review dwell, clamped at 0. Use this for any per-run duration
    /// computed in process so it matches the dashboard aggregates exactly.
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
}
