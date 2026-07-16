using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Backlog;

public sealed record BacklogTaskReadModel(
    BacklogTask Task,
    IReadOnlyList<string> DependsOnTaskIds,
    bool IsBlocked,
    string? BlockedReason,
    bool IsReadyToStart,
    IReadOnlyList<BlockingDependencyDto> BlockingDependencies);

public sealed class BacklogTaskReadModelFactory(IBacklogTaskStore backlogStore)
{
    public async Task<IReadOnlyDictionary<BacklogTaskId, BacklogTaskReadModel>> BuildAsync(
        ProjectId projectId,
        IReadOnlyList<BacklogTask> tasks,
        CancellationToken ct = default)
    {
        if (tasks.Count == 0)
            return new Dictionary<BacklogTaskId, BacklogTaskReadModel>();

        var statuses = await backlogStore.ListDependencyStatusesAsync(projectId, tasks.Select(t => t.Id).ToList(), ct)
            .ConfigureAwait(false);

        var grouped = statuses
            .GroupBy(s => s.TaskId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.DependsOnTaskId.ToString(), StringComparer.Ordinal).ToList());

        return tasks.ToDictionary(task => task.Id, task =>
        {
            var taskStatuses = grouped.TryGetValue(task.Id, out var values) ? values : [];
            var blocking = taskStatuses
                .Where(s => !s.IsSatisfied)
                .Select(s => new BlockingDependencyDto
                {
                    TaskId = s.DependsOnTaskId.ToString(),
                    Title = s.DependsOnTitle,
                    RunId = s.DependsOnRunId?.ToString(),
                    RunStatus = s.DependsOnRunStatus?.ToApiString(),
                })
                .OrderBy(s => s.TaskId, StringComparer.Ordinal)
                .ToList();
            var blockedCount = blocking.Count;
            var isBlocked = blockedCount > 0;
            return new BacklogTaskReadModel(
                task,
                taskStatuses.Select(s => s.DependsOnTaskId.ToString()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                isBlocked,
                isBlocked ? BuildBlockedReason(blockedCount) : null,
                task.State == BacklogTaskState.Ready && task.RunId is null && task.ArchivedAt is null && !isBlocked,
                blocking);
        });
    }

    public async Task<BacklogTaskDto> BuildTaskDtoAsync(BacklogTask task, CancellationToken ct = default)
    {
        var map = await BuildAsync(task.ProjectId, [task], ct).ConfigureAwait(false);
        return ToTaskDto(map[task.Id]);
    }

    public static BacklogTaskDto ToTaskDto(BacklogTaskReadModel model) => new()
    {
        TaskId = model.Task.Id.ToString(),
        ProjectId = model.Task.ProjectId.ToString(),
        Title = model.Task.Title,
        Description = model.Task.Description,
        State = model.Task.State.ToApiString(),
        OrderKey = model.Task.OrderKey,
        CapturedBy = model.Task.CapturedBy,
        CreatedAt = model.Task.CreatedAt,
        CommittedAt = model.Task.CommittedAt,
        ClaimedAt = model.Task.ClaimedAt,
        RunId = model.Task.RunId?.ToString(),
        WorkflowOverrideId = model.Task.WorkflowOverrideId,
        ArchivedAt = model.Task.ArchivedAt,
        ExternalId = model.Task.SourceFilePath,
        ParentPrdRunId = model.Task.ParentPrdRunId?.ToString(),
        PromotionKey = model.Task.PromotionKey,
        PromotionReason = model.Task.PromotionReason,
        DependsOnTaskIds = model.DependsOnTaskIds,
        IsBlocked = model.IsBlocked,
        BlockedReason = model.BlockedReason,
        IsReadyToStart = model.IsReadyToStart,
        BlockingDependencies = model.BlockingDependencies,
    };

    public static TaskCardDto ToTaskCardDto(BacklogTaskReadModel model) => new()
    {
        TaskId = model.Task.Id.ToString(),
        Title = model.Task.Title,
        Description = model.Task.Description,
        State = model.Task.State.ToApiString(),
        OrderKey = model.Task.OrderKey,
        CapturedBy = model.Task.CapturedBy,
        CreatedAt = model.Task.CreatedAt,
        CommittedAt = model.Task.CommittedAt,
        WorkflowOverrideId = model.Task.WorkflowOverrideId,
        ArchivedAt = model.Task.ArchivedAt,
        ParentPrdRunId = model.Task.ParentPrdRunId?.ToString(),
        PromotionKey = model.Task.PromotionKey,
        PromotionReason = model.Task.PromotionReason,
        DependsOnTaskIds = model.DependsOnTaskIds,
        IsBlocked = model.IsBlocked,
        BlockedReason = model.BlockedReason,
        IsReadyToStart = model.IsReadyToStart,
        BlockingDependencies = model.BlockingDependencies,
    };

    public static string BuildBlockedReason(int dependencyCount) =>
        dependencyCount == 1
            ? "Waiting for 1 prerequisite task to merge."
            : $"Waiting for {dependencyCount} prerequisite tasks to merge.";
}

