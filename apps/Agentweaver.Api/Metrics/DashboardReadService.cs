using System.Globalization;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Metrics;

public sealed class DashboardReadService
{
    private static readonly HashSet<string> ActiveStatuses =
        new(StringComparer.Ordinal) { "pending", "in_progress", "awaiting_review", "committing", "merging" };

    private static readonly HashSet<string> SuccessStatuses =
        new(StringComparer.Ordinal) { "merged", "completed", "assemble_ready" };

    private readonly SqliteDb _db;
    private readonly IProjectStore _projectStore;
    private readonly HeartbeatStatusStore _heartbeatStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _isPostgres;

    public DashboardReadService(
        SqliteDb db,
        IProjectStore projectStore,
        HeartbeatStatusStore heartbeatStore,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _db = db;
        _projectStore = projectStore;
        _heartbeatStore = heartbeatStore;
        _scopeFactory = scopeFactory;
        var provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        _isPostgres = provider is "postgres" or "postgresql";
    }

    private readonly record struct RunRow(
        string? ProjectId,
        string? AgentName,
        string Status,
        string Origin,
        string? ParentRunId,
        string? Task,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt);

    public async Task<ProjectDashboardDto> GetProjectDashboardAsync(Project project, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var weekAgo = now.AddDays(-7);
        var pid = project.Id.ToString();
        var runs = await LoadRunsAsync(pid, ct).ConfigureAwait(false);

        var activeAgents = new HashSet<string>(StringComparer.Ordinal);
        var runsThisWeek = 0;
        var activeRuns = 0;
        var tasksDoneThisWeek = 0;

        foreach (var run in runs)
        {
            if (run.StartedAt >= weekAgo) runsThisWeek++;
            if (run.Status == "in_progress")
            {
                activeRuns++;
                if (run.AgentName is not null) activeAgents.Add(run.AgentName);
            }

            if (SuccessStatuses.Contains(run.Status) && run.EndedAt is { } ended && ended >= weekAgo)
                tasksDoneThisWeek++;
        }

        return new ProjectDashboardDto
        {
            ProjectId = pid,
            ProjectName = project.Name,
            GeneratedUtc = now,
            Throughput = [],
            AgentLeaderboard = [],
            Summary = new DashboardSummaryDto
            {
                RunsThisWeek = runsThisWeek,
                RunsTotal = runs.Count,
                ActiveRuns = activeRuns,
                ActiveAgents = activeAgents.Count,
                TasksDoneThisWeek = tasksDoneThisWeek,
            },
        };
    }

    public async Task<OverviewDto> GetOverviewAsync(CallerContext caller, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var todayUtc = now.UtcDateTime.Date;

        var projects = (await _projectStore.ListAsync(ct).ConfigureAwait(false))
            .Where(project => caller.Owns(project.Owner))
            .ToList();
        var names = projects.ToDictionary(p => p.Id.ToString(), p => p.Name, StringComparer.Ordinal);
        var visibleProjectIds = names.Keys.ToHashSet(StringComparer.Ordinal);

        var runs = (await LoadRunsAsync(null, ct).ConfigureAwait(false))
            .Where(run => run.ProjectId is not null && visibleProjectIds.Contains(run.ProjectId))
            .ToList();
        var readyProjectIds = (await LoadReadyBacklogProjectIdsAsync(ct).ConfigureAwait(false))
            .Where(visibleProjectIds.Contains)
            .ToList();

        return new OverviewDto
        {
            GeneratedUtc = now,
            AtAGlance = ReadAtAGlance(runs, readyProjectIds, todayUtc),
            LiveSessions = ReadLiveSessions(runs, names),
            ActiveWorkflowRuns = ReadActiveWorkflowRuns(runs, names),
            ActiveProjects = ReadActiveProjects(runs, readyProjectIds, names),
            RecentActivity = ReadRecentActivity(runs, names),
        };
    }

    private AtAGlanceDto ReadAtAGlance(
        IReadOnlyList<RunRow> runs, IReadOnlyList<string> readyProjectIds, DateTime todayUtc)
    {
        var inFlight = 0;
        var pendingRuns = 0;
        var doneToday = 0;
        var mergeFailed = 0;
        var activeProjectIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in runs)
        {
            switch (run.Status)
            {
                case "in_progress":
                    inFlight++;
                    if (run.ProjectId is not null) activeProjectIds.Add(run.ProjectId);
                    break;
                case "pending":
                    pendingRuns++;
                    break;
                case "merge_failed":
                    mergeFailed++;
                    break;
            }

            if (SuccessStatuses.Contains(run.Status) && run.EndedAt is { } ended
                && ended.UtcDateTime.Date == todayUtc)
            {
                doneToday++;
            }
        }

        activeProjectIds.UnionWith(readyProjectIds);
        var degraded = !_heartbeatStore.Enabled || _heartbeatStore.LastError is not null || mergeFailed > 0;

        return new AtAGlanceDto
        {
            InFlight = inFlight,
            QueuedWork = pendingRuns + readyProjectIds.Count,
            DoneToday = doneToday,
            ActiveProjects = activeProjectIds.Count,
            Health = degraded ? "degraded" : "healthy",
        };
    }

    private static IReadOnlyList<LiveSessionDto> ReadLiveSessions(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names) =>
        runs
            .Where(r => r.Status == "in_progress" && r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new LiveSessionDto
            {
                ProjectId = r.ProjectId!,
                ProjectName = names[r.ProjectId!],
                Agent = r.AgentName,
                Status = r.Status,
                StartedUtc = r.StartedAt,
                LastActivityUtc = r.EndedAt ?? r.StartedAt,
            })
            .ToList();

    private static IReadOnlyList<ActiveWorkflowRunDto> ReadActiveWorkflowRuns(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names) =>
        runs
            .Where(r => ActiveStatuses.Contains(r.Status) && r.ParentRunId is null
                        && r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new ActiveWorkflowRunDto
            {
                ProjectId = r.ProjectId!,
                ProjectName = names[r.ProjectId!],
                Trigger = string.IsNullOrEmpty(r.Origin) ? "interactive" : r.Origin,
                Status = r.Status,
                StartedUtc = r.StartedAt,
            })
            .ToList();

    private static IReadOnlyList<ActiveProjectDto> ReadActiveProjects(
        IReadOnlyList<RunRow> runs,
        IReadOnlyList<string> readyProjectIds,
        IReadOnlyDictionary<string, string> names)
    {
        var rollup = new Dictionary<string, (int active, int pending, DateTimeOffset? lastAct)>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (run.ProjectId is null) continue;
            var acc = rollup.GetValueOrDefault(run.ProjectId);
            if (run.Status == "in_progress") acc.active++;
            if (run.Status == "pending") acc.pending++;
            var activity = run.EndedAt ?? run.StartedAt;
            if (acc.lastAct is null || activity > acc.lastAct) acc.lastAct = activity;
            rollup[run.ProjectId] = acc;
        }

        var ready = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pid in readyProjectIds)
            ready[pid] = ready.GetValueOrDefault(pid) + 1;

        var ids = new HashSet<string>(rollup.Keys, StringComparer.Ordinal);
        ids.UnionWith(ready.Keys);

        return ids
            .Where(id => names.ContainsKey(id))
            .Select(id =>
            {
                var (active, pending, lastAct) = rollup.GetValueOrDefault(id, (0, 0, null));
                var queued = pending + ready.GetValueOrDefault(id);
                return new ActiveProjectDto
                {
                    ProjectId = id,
                    ProjectName = names[id],
                    ActiveCount = active,
                    QueuedCount = queued,
                    LastActivityUtc = lastAct,
                };
            })
            .Where(p => p.ActiveCount > 0 || p.QueuedCount > 0)
            .OrderByDescending(p => p.ActiveCount)
            .ThenByDescending(p => p.QueuedCount)
            .ToList();
    }

    private static IReadOnlyList<RecentActivityDto> ReadRecentActivity(
        IReadOnlyList<RunRow> runs, IReadOnlyDictionary<string, string> names) =>
        runs
            .Where(r => r.ProjectId is not null && names.ContainsKey(r.ProjectId))
            .OrderByDescending(r => r.EndedAt ?? r.StartedAt)
            .Take(20)
            .Select(r => new RecentActivityDto
            {
                ProjectId = r.ProjectId!,
                ProjectName = names[r.ProjectId!],
                Label = Truncate(r.Task ?? string.Empty, 80),
                Kind = r.Status,
                TimestampUtc = r.EndedAt ?? r.StartedAt,
            })
            .ToList();

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
                    r.StartedAt, r.EndedAt))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT project_id, agent_name, status, origin, parent_run_id, task, started_at, ended_at FROM runs" +
            (projectId is null ? ";" : " WHERE project_id = $pid;");
        if (projectId is not null) cmd.Parameters.AddWithValue("$pid", projectId);

        var rows = new List<RunRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RunRow(
                ProjectId: reader.IsDBNull(0) ? null : reader.GetString(0),
                AgentName: reader.IsDBNull(1) ? null : reader.GetString(1),
                Status: reader.GetString(2),
                Origin: reader.IsDBNull(3) ? "interactive" : reader.GetString(3),
                ParentRunId: reader.IsDBNull(4) ? null : reader.GetString(4),
                Task: reader.IsDBNull(5) ? null : reader.GetString(5),
                StartedAt: ParseTs(reader.GetString(6)),
                EndedAt: reader.IsDBNull(7) ? null : ParseTs(reader.GetString(7))));
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
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private const char Ellipsis = '\u2026';

    internal static string Truncate(string value, int max)
    {
        value = value.TrimEnd();
        if (value.Length <= max) return value;

        var head = value[..(max - 1)];
        var lastSpace = head.LastIndexOf(' ');
        if (lastSpace >= max / 2)
            head = head[..lastSpace];

        return head.TrimEnd() + Ellipsis;
    }
}
