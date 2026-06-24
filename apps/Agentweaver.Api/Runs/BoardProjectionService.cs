using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Composes the Kanban board read model in one response: Backlog + Ready intake tasks and
/// run-backed cards folded into the fixed Problems / Human Review / Active / Done buckets.
/// Reads only committed server state (Principle VII). The Done column collapses older runs unless
/// the full history is requested (FR-016a).
/// </summary>
public sealed class BoardProjectionService
{
    private const int TerminalRecentLimit = 20;

    private readonly IBacklogTaskStore _backlogStore;
    private readonly SqliteRunStore _runStore;
    private readonly IWorkflowStageProjector _stageProjector;
    private readonly IServiceScopeFactory _scopeFactory;

    public BoardProjectionService(
        IBacklogTaskStore backlogStore,
        SqliteRunStore runStore,
        IWorkflowStageProjector stageProjector,
        IServiceScopeFactory scopeFactory)
    {
        _backlogStore = backlogStore;
        _runStore = runStore;
        _stageProjector = stageProjector;
        _scopeFactory = scopeFactory;
    }

    public async Task<BoardDto> GetBoardAsync(ProjectId projectId, bool includeTerminalHistory, CancellationToken ct)
    {
        var tasks = await _backlogStore.ListByProjectAsync(projectId, ct).ConfigureAwait(false);
        var runs = await _runStore.GetRunsByProjectAsync(projectId, includeChildren: false, ct).ConfigureAwait(false);

        // Fixed canonical workflow buckets; no topology-derived columns are exposed on the board.
        var stages = _stageProjector.GetStages();
        var workflowAvailable = stages.Count > 0;

        var columns = new List<BoardColumnDto>
        {
            BuildIntakeColumn("backlog", "Backlog", tasks, BacklogTaskState.Backlog),
            BuildIntakeColumn("ready", "Ready", tasks, BacklogTaskState.Ready),
        };

        // Active (non-terminal) coordinator run ids backing the board — the set the per-agent
        // work-queue rollup aggregates over (historical/merged-away runs in the terminal column are
        // excluded so the homepage "Agents" rail reflects current in-flight work only).
        var activeRunIds = new List<string>();

        if (workflowAvailable)
        {
            // run_id -> backlog task id, for run-card provenance (Claimed tasks are represented by
            // their coordinator run, not a separate card).
            var runToTask = tasks
                .Where(t => t.RunId is not null)
                .ToDictionary(t => t.RunId!.Value.ToString(), t => t.Id.ToString());

            var runIds = runs.Select(r => r.Id.ToString()).ToList();
            var planStages = await GetWorkPlanStagesAsync(runIds, ct).ConfigureAwait(false);

            // Bucket each top-level run into the column its persisted state currently maps to.
            var byStage = new Dictionary<string, List<RunCardDto>>(StringComparer.Ordinal);
            foreach (var stage in stages)
                byStage[stage.Id] = new List<RunCardDto>();

            foreach (var run in runs)
            {
                CoordinatorWorkPlanStage? planStage =
                    planStages.TryGetValue(run.Id.ToString(), out var ps) ? ps : null;
                var stageId = _stageProjector.CoordinatorRunToStageId(run, planStage);
                if (!byStage.TryGetValue(stageId, out var bucket))
                    continue;   // defensive: unknown stage id never surfaces a broken column

                if (!string.Equals(stageId, WorkflowStageProjector.DoneStageId, StringComparison.Ordinal)
                    && !string.Equals(stageId, WorkflowStageProjector.ProblemsStageId, StringComparison.Ordinal))
                    activeRunIds.Add(run.Id.ToString());

                runToTask.TryGetValue(run.Id.ToString(), out var backlogTaskId);
                bucket.Add(new RunCardDto
                {
                    RunId = run.Id.ToString(),
                    WorkflowRunId = run.WorkflowRunId,
                    BacklogTaskId = backlogTaskId,
                    Task = run.Task,
                    Status = run.Status.ToApiString(),
                    WorkPlanStatus = planStage?.Status,
                    AssemblyStage = planStage?.AssemblyStage,
                    StageId = stageId,
                    AgentName = run.AgentName,
                    RetriedFrom = run.RetriedFrom,
                    StartedAt = run.StartedAt,
                    EndedAt = run.EndedAt,
                    ArchivedAt = run.ArchivedAt,
                });
            }

            foreach (var stage in stages)
            {
                var cards = byStage[stage.Id];
                if (string.Equals(stage.Id, WorkflowStageProjector.DoneStageId, StringComparison.Ordinal))
                {
                    columns.Add(BuildTerminalColumn(stage, cards, includeTerminalHistory));
                }
                else
                {
                    var ordered = cards.OrderBy(c => c.StartedAt).Cast<object>().ToList();
                    columns.Add(new BoardColumnDto
                    {
                        Id = stage.Id, Kind = "workflow", Label = stage.Label, Cards = ordered,
                    });
                }
            }
        }

        var agentQueues = await BuildAgentQueuesAsync(activeRunIds, ct).ConfigureAwait(false);

        return new BoardDto
        {
            ProjectId = projectId.ToString(),
            WorkflowStagesAvailable = workflowAvailable,
            Columns = columns,
            AgentQueues = agentQueues,
        };
    }

    private static BoardColumnDto BuildIntakeColumn(
        string id, string label, IReadOnlyList<BacklogTask> tasks, BacklogTaskState state)
    {
        var cards = tasks
            .Where(t => t.State == state)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .ThenBy(t => t.CommittedAt ?? t.CreatedAt)
            .ThenBy(t => t.Id.ToString(), StringComparer.Ordinal)
            .Select(t => (object)new TaskCardDto
            {
                TaskId = t.Id.ToString(),
                Title = t.Title,
                Description = t.Description,
                State = state.ToApiString(),
                OrderKey = t.OrderKey,
                CapturedBy = t.CapturedBy,
                CreatedAt = t.CreatedAt,
                CommittedAt = t.CommittedAt,
                ArchivedAt = t.ArchivedAt,
            })
            .ToList();
        return new BoardColumnDto { Id = id, Kind = "intake", Label = label, Cards = cards };
    }

    private static BoardColumnDto BuildTerminalColumn(
        WorkflowStage stage, List<RunCardDto> cards, bool includeTerminalHistory)
    {
        var ordered = cards
            .OrderByDescending(c => c.EndedAt ?? c.StartedAt)
            .ToList();

        if (includeTerminalHistory || ordered.Count <= TerminalRecentLimit)
        {
            return new BoardColumnDto
            {
                Id = stage.Id, Kind = "workflow", Label = stage.Label,
                Cards = ordered.Cast<object>().ToList(),
            };
        }

        var recent = ordered.Take(TerminalRecentLimit).Cast<object>().ToList();
        return new BoardColumnDto
        {
            Id = stage.Id, Kind = "workflow", Label = stage.Label,
            Cards = recent, CollapsedCount = ordered.Count - TerminalRecentLimit,
        };
    }

    /// <summary>
    /// Reads the lightweight (Status, AssemblyStage) projection for the supplied run ids in one query.
    /// Run ids without a work plan are omitted. Reads persisted coordinator state directly (mirrors
    /// CoordinatorStatusReader) so the board hot path does not pull the full coordinator service in.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, CoordinatorWorkPlanStage>> GetWorkPlanStagesAsync(
        IReadOnlyCollection<string> coordinatorRunIds, CancellationToken ct)
    {
        if (coordinatorRunIds.Count == 0)
            return new Dictionary<string, CoordinatorWorkPlanStage>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.WorkPlans.AsNoTracking()
            .Where(w => coordinatorRunIds.Contains(w.CoordinatorRunId))
            .Select(w => new { w.CoordinatorRunId, w.Status, w.AssemblyStage })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(
            w => w.CoordinatorRunId,
            w => new CoordinatorWorkPlanStage(w.Status, w.AssemblyStage));
    }

    /// <summary>
    /// Project-aggregate per-agent work-queue rollup for the homepage "Agents" rail. Loads every
    /// subtask (persona, status, title) across the active coordinator runs in ONE batched query
    /// (WorkPlans joined to Subtasks — no per-run round trip), then aggregates into load buckets per
    /// distinct <c>assignedAgent</c>. Status -&gt; bucket mapping mirrors the locked web Phase 1
    /// mapping (apps/web/src/api/agentQueues.ts) exactly. Returns an empty list (never null) when
    /// there are no active runs or no subtasks.
    /// </summary>
    private async Task<IReadOnlyList<AgentQueueDto>> BuildAgentQueuesAsync(
        IReadOnlyCollection<string> activeRunIds, CancellationToken ct)
    {
        if (activeRunIds.Count == 0)
            return Array.Empty<AgentQueueDto>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        // One query: every subtask of every active run's work plan, projected to the three fields the
        // rollup needs. The coordinator run id rides along so run_ids can be attributed per agent.
        var rows = await db.WorkPlans.AsNoTracking()
            .Where(w => activeRunIds.Contains(w.CoordinatorRunId))
            .Join(
                db.Subtasks.AsNoTracking(),
                w => w.Id,
                s => s.WorkPlanId,
                (w, s) => new SubtaskRow(w.CoordinatorRunId, s.AssignedAgent, s.Status, s.Title))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return Array.Empty<AgentQueueDto>();

        var byAgent = new Dictionary<string, AgentQueueAccumulator>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var agent = string.IsNullOrWhiteSpace(row.AssignedAgent) ? "Unassigned" : row.AssignedAgent;
            if (!byAgent.TryGetValue(agent, out var acc))
            {
                acc = new AgentQueueAccumulator();
                byAgent[agent] = acc;
            }

            if (!acc.ByRun.TryGetValue(row.CoordinatorRunId, out var runAcc))
            {
                runAcc = new OrchestrationAccumulator();
                acc.ByRun[row.CoordinatorRunId] = runAcc;
            }

            var bucket = SubtaskStatusToBucket(row.Status);
            switch (bucket)
            {
                case Bucket.Active: acc.Active++; runAcc.Active++; break;
                case Bucket.Queued: acc.Queued++; runAcc.Queued++; break;
                case Bucket.Blocked: acc.Blocked++; runAcc.Blocked++; break;
                case Bucket.Done: acc.Done++; runAcc.Done++; break;
            }

            acc.RunIds.Add(row.CoordinatorRunId);

            // sample_titles prefer in-flight (queued + active) work; dedup, cap at 3.
            var inFlight = bucket == Bucket.Queued || bucket == Bucket.Active;
            if (inFlight && acc.SampleTitles.Count < 3 && !string.IsNullOrWhiteSpace(row.Title))
                acc.SampleTitles.Add(row.Title);

            // Per-orchestration sample_titles: same in-flight rule, deduped within the orchestration.
            if (inFlight && runAcc.SampleTitles.Count < 3 && !string.IsNullOrWhiteSpace(row.Title)
                && runAcc.SeenTitles.Add(row.Title))
                runAcc.SampleTitles.Add(row.Title);
        }

        // One batched query for a human orchestration name per coordinator run (the run's objective).
        // Runs without an outcome spec map to null so the web can fall back to a short run id.
        var runTitles = await GetOrchestrationTitlesAsync(db, activeRunIds, ct).ConfigureAwait(false);

        var result = byAgent
            .Select(kvp => new AgentQueueDto
            {
                AgentName = kvp.Key,
                Active = kvp.Value.Active,
                Queued = kvp.Value.Queued,
                Blocked = kvp.Value.Blocked,
                Done = kvp.Value.Done,
                RunIds = kvp.Value.RunIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                SampleTitles = kvp.Value.SampleTitles.Take(3).ToList(),
                Orchestrations = kvp.Value.ByRun
                    .Select(run => new AgentOrchestrationQueueDto
                    {
                        RunId = run.Key,
                        Title = runTitles.TryGetValue(run.Key, out var title) ? title : null,
                        Active = run.Value.Active,
                        Queued = run.Value.Queued,
                        Blocked = run.Value.Blocked,
                        Done = run.Value.Done,
                        SampleTitles = run.Value.SampleTitles.Take(3).ToList(),
                    })
                    // Most-loaded orchestration first, with a stable run-id tie-break.
                    .OrderByDescending(o => o.Active * 4 + o.Queued * 2 + o.Blocked)
                    .ThenBy(o => o.RunId, StringComparer.Ordinal)
                    .ToList(),
            })
            // Most-loaded agents first (mirrors the web ordering), with a stable name tie-break.
            .OrderByDescending(a => a.Active * 4 + a.Queued * 2 + a.Blocked)
            .ThenBy(a => a.AgentName, StringComparer.Ordinal)
            .ToList();

        return result;
    }

    /// <summary>
    /// Reads a human orchestration name (the coordinator run's outcome-spec goal) for the supplied run
    /// ids in ONE batched query. Run ids without an outcome spec are omitted, so the caller resolves
    /// them to null. When a run has more than one spec row the first by id wins (deterministic).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> GetOrchestrationTitlesAsync(
        MemoryDbContext db, IReadOnlyCollection<string> coordinatorRunIds, CancellationToken ct)
    {
        var rows = await db.OutcomeSpecs.AsNoTracking()
            .Where(o => coordinatorRunIds.Contains(o.CoordinatorRunId))
            .OrderBy(o => o.Id)
            .Select(o => new { o.CoordinatorRunId, o.Goal })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Goal)) continue;
            titles.TryAdd(row.CoordinatorRunId, row.Goal);
        }
        return titles;
    }

    /// <summary>
    /// Maps a server-side WorkPlan subtask status to a load bucket. Mirrors
    /// apps/web/src/api/agentQueues.ts exactly. Server-side subtask statuses are
    /// pending | dispatched | running | rai_flagged | assemble_ready | completed | failed; the extra
    /// in_progress/merged cases are kept for forward-parity with the web mapping.
    /// </summary>
    private static Bucket SubtaskStatusToBucket(string status) =>
        status?.ToLowerInvariant() switch
        {
            "dispatched" or "running" or "in_progress" => Bucket.Active,
            "completed" or "assemble_ready" or "merged" => Bucket.Done,
            "failed" or "rai_flagged" => Bucket.Blocked,
            _ => Bucket.Queued,   // pending + anything unrecognized = queued (not yet started)
        };

    private enum Bucket { Active, Queued, Blocked, Done }

    private readonly record struct SubtaskRow(string CoordinatorRunId, string AssignedAgent, string Status, string Title);

    private sealed class AgentQueueAccumulator
    {
        public int Active;
        public int Queued;
        public int Blocked;
        public int Done;
        public readonly HashSet<string> RunIds = new(StringComparer.Ordinal);
        public readonly List<string> SampleTitles = new();
        public readonly Dictionary<string, OrchestrationAccumulator> ByRun = new(StringComparer.Ordinal);
    }

    private sealed class OrchestrationAccumulator
    {
        public int Active;
        public int Queued;
        public int Blocked;
        public int Done;
        public readonly List<string> SampleTitles = new();
        public readonly HashSet<string> SeenTitles = new(StringComparer.Ordinal);
    }
}
