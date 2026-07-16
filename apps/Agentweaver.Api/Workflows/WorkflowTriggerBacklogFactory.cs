using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Shared "create a Ready backlog task bound to a triggered workflow" helper used by both
/// <see cref="WorkflowScheduleTriggerService"/> and <see cref="WorkflowEventTriggerService"/> (issue
/// #53). A trigger firing is deliberately NOT a bespoke run-start path: it lands as an ordinary Ready
/// backlog task (WorkflowOverrideId bound to the triggered workflow) so the EXISTING
/// <see cref="Coordinator.CoordinatorHeartbeatService"/> pickup path claims it — reusing the same
/// pickup-capacity bounds (<c>Project.MaxReadyPerHeartbeat</c>), exactly-once claim, and board
/// visibility as every other backlog task ("trigger firings are accountable runs visible on the
/// board").
/// </summary>
internal static class WorkflowTriggerBacklogFactory
{
    public static async Task<BacklogTask> CreateReadyTaskAsync(
        IBacklogTaskStore backlogStore,
        Project project,
        WorkflowDefinition definition,
        string title,
        string description,
        string capturedBy,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false);
        var readyKeys = existing
            .Where(t => t.State == BacklogTaskState.Ready)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .Select(t => t.OrderKey)
            .ToList();
        var orderKey = OrderKey.Between(readyKeys.Count == 0 ? null : readyKeys[^1], null);

        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = project.Id,
            Title = title,
            Description = description,
            State = BacklogTaskState.Ready,
            OrderKey = orderKey,
            CapturedBy = capturedBy,
            CreatedAt = now,
            CommittedAt = now,
            WorkflowOverrideId = definition.Id,
            SourceFilePath = idempotencyKey,
        };

        await backlogStore.InsertAsync(task, ct).ConfigureAwait(false);
        return task;
    }
}
