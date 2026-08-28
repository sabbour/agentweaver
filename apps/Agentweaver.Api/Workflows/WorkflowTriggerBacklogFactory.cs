using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Shared trigger-task staging and publication helper used by both
/// <see cref="WorkflowScheduleTriggerService"/> and <see cref="WorkflowEventTriggerService"/> (issue
/// #53). A trigger firing is deliberately NOT a bespoke run-start path: once its invocation is
/// durably bound, it lands as an ordinary Ready backlog task (WorkflowOverrideId bound to the triggered
/// workflow) so the EXISTING
/// <see cref="Coordinator.CoordinatorHeartbeatService"/> pickup path claims it — reusing the same
/// pickup-capacity bounds (<c>Project.MaxReadyPerHeartbeat</c>), exactly-once claim, and board
/// visibility as every other backlog task. The task remains Backlog while the invocation binding is
/// written, so coordinator pickup cannot observe an unbound trusted task.
/// </summary>
internal static class WorkflowTriggerBacklogFactory
{
    internal static bool IsTrustedAutomationTask(BacklogTask task) =>
        task.SourceFilePath?.StartsWith("workflow-schedule-trigger:", StringComparison.Ordinal) == true ||
        task.SourceFilePath?.StartsWith("workflow-event-trigger:", StringComparison.Ordinal) == true;

    /// <summary>
    /// Creates an immediately-published task for manual workflow starts, which do not have an
    /// automation invocation to bind. Trusted automation triggers must use the unpublished path.
    /// </summary>
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
        var task = await CreateUnpublishedTaskAsync(
            backlogStore, project, definition, title, description, capturedBy, idempotencyKey, now, ct)
            .ConfigureAwait(false);
        if (!await TryPublishAsync(backlogStore, project, task, now, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to publish workflow backlog task.");
        return task with { State = BacklogTaskState.Ready, CommittedAt = now };
    }

    public static async Task<BacklogTask> CreateUnpublishedTaskAsync(
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
        var backlogKeys = existing
            .Where(t => t.State == BacklogTaskState.Backlog)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .Select(t => t.OrderKey)
            .ToList();
        var orderKey = OrderKey.Between(backlogKeys.Count == 0 ? null : backlogKeys[^1], null);

        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = project.Id,
            Title = title,
            Description = description,
            State = BacklogTaskState.Backlog,
            OrderKey = orderKey,
            CapturedBy = capturedBy,
            CreatedAt = now,
            CommittedAt = null,
            WorkflowOverrideId = definition.Id,
            SourceFilePath = idempotencyKey,
        };

        await backlogStore.InsertAsync(task, ct).ConfigureAwait(false);
        return task;
    }

    public static async Task<bool> TryPublishAsync(
        IBacklogTaskStore backlogStore,
        Project project,
        BacklogTask task,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var readyKeys = (await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false))
            .Where(t => t.State == BacklogTaskState.Ready)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .Select(t => t.OrderKey)
            .ToList();
        var orderKey = OrderKey.Between(readyKeys.Count == 0 ? null : readyKeys[^1], null);
        return await backlogStore.TryMoveToReadyAsync(project.Id, task.Id, orderKey, now, ct)
            .ConfigureAwait(false);
    }
}
