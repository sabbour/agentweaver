using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using System.Text.Json;

namespace Agentweaver.Api.Backlog;

public sealed record BacklogTaskReadModel(
    BacklogTask Task,
    IReadOnlyList<string> DependsOnTaskIds,
    IReadOnlyList<string> DependentsTaskIds,
    IReadOnlyList<BlockingDependencyDto> Prerequisites,
    long GraphRevision,
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
        var dependents = statuses.GroupBy(s => s.DependsOnTaskId)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)g.Select(s => s.TaskId.ToString())
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        var revision = await backlogStore.GetDependencyRevisionAsync(projectId, ct).ConfigureAwait(false);

        return tasks.ToDictionary(task => task.Id, task =>
        {
            var taskStatuses = grouped.TryGetValue(task.Id, out var values) ? values : [];
            var prerequisites = taskStatuses
                .Select(s => new BlockingDependencyDto
                {
                    TaskId = s.DependsOnTaskId.ToString(),
                    Title = s.DependsOnTitle,
                    RunId = s.DependsOnRunId?.ToString(),
                    RunStatus = s.DependsOnRunStatus?.ToApiString(),
                    Reason = s.Reason,
                    IsSatisfied = s.IsSatisfied,
                })
                .OrderBy(s => s.TaskId, StringComparer.Ordinal)
                .ToList();
            var blocking = prerequisites.Where(s => !s.IsSatisfied).ToList();
            var blockedCount = blocking.Count;
            var isBlocked = blockedCount > 0;
            return new BacklogTaskReadModel(
                task,
                taskStatuses.Select(s => s.DependsOnTaskId.ToString()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                dependents.TryGetValue(task.Id, out var downstream) ? downstream : [],
                prerequisites,
                revision,
                isBlocked,
                isBlocked ? BuildBlockedReason(blockedCount) : null,
                task.State == BacklogTaskState.Ready && task.RunId is null && task.ArchivedAt is null && !isBlocked,
                blocking);
        });
    }

    public async Task<BacklogTaskDto> BuildTaskDtoAsync(BacklogTask task, CancellationToken ct = default)
    {
        var active = await backlogStore.ListByProjectAsync(task.ProjectId, ct).ConfigureAwait(false);
        var tasks = active.Where(t => t.Id != task.Id).Append(task).ToList();
        var map = await BuildAsync(task.ProjectId, tasks, ct).ConfigureAwait(false);
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
        DependentsTaskIds = model.DependentsTaskIds,
        Prerequisites = model.Prerequisites,
        GraphRevision = model.GraphRevision,
        ClaimedGraphRevision = model.Task.ClaimedGraphRevision,
        ClaimedPrerequisites = model.Task.ClaimedPrerequisitesJson is null
            ? null
            : JsonSerializer.Deserialize<BacklogClaimedPrerequisite[]>(model.Task.ClaimedPrerequisitesJson),
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
        DependentsTaskIds = model.DependentsTaskIds,
        Prerequisites = model.Prerequisites,
        GraphRevision = model.GraphRevision,
        IsBlocked = model.IsBlocked,
        BlockedReason = model.BlockedReason,
        IsReadyToStart = model.IsReadyToStart,
        BlockingDependencies = model.BlockingDependencies,
    };

    public static string BuildBlockedReason(int dependencyCount) =>
        dependencyCount == 1
            ? "Waiting for 1 prerequisite task to finish successfully."
            : $"Waiting for {dependencyCount} prerequisite tasks to finish successfully.";
}
