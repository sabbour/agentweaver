using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Composes the Kanban board read model in one response: the intake columns (Backlog + Ready tasks
/// in priority order) and the descriptor-driven workflow columns (coordinator-run cards placed in
/// their current stage). Reads only committed server state (Principle VII). The terminal column
/// collapses older runs unless the full history is requested (FR-016a).
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

        // FR-019 fallback: if the coordinator topology cannot be resolved into stages, expose only the
        // intake columns and signal unavailability.
        IReadOnlyList<WorkflowStage> stages;
        try
        {
            stages = _stageProjector.GetStages();
        }
        catch
        {
            stages = Array.Empty<WorkflowStage>();
        }
        var workflowAvailable = stages.Count > 0;

        var columns = new List<BoardColumnDto>
        {
            BuildIntakeColumn("backlog", "Backlog", tasks, BacklogTaskState.Backlog),
            BuildIntakeColumn("ready", "Ready", tasks, BacklogTaskState.Ready),
        };

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
                });
            }

            foreach (var stage in stages)
            {
                var cards = byStage[stage.Id];
                if (string.Equals(stage.Id, WorkflowStageProjector.TerminalStageId, StringComparison.Ordinal))
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

        return new BoardDto
        {
            ProjectId = projectId.ToString(),
            WorkflowStagesAvailable = workflowAvailable,
            Columns = columns,
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
}
