using Agentweaver.Domain;
using Agentweaver.Api.Auth;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

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
        var task = await CreateBacklogTaskAsync(
            backlogStore, project, definition, title, description, capturedBy, idempotencyKey, now, ct)
            .ConfigureAwait(false);
        var readyKeys = (await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false))
            .Where(t => t.State == BacklogTaskState.Ready)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .Select(t => t.OrderKey)
            .ToList();
        var orderKey = OrderKey.Between(readyKeys.Count == 0 ? null : readyKeys[^1], null);
        if (!await backlogStore.TryMoveToReadyAsync(project.Id, task.Id, orderKey, now, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to publish workflow backlog task.");
        return task with { State = BacklogTaskState.Ready, CommittedAt = now };
    }

    public static Task<BacklogTask> CreateProvisionalAutomationTaskAsync(
        IBacklogTaskStore backlogStore,
        Project project,
        WorkflowDefinition definition,
        string title,
        string description,
        string capturedBy,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken ct,
        BacklogTaskId? taskId = null) =>
        CreateBacklogTaskAsync(
            backlogStore, project, definition, title, description, capturedBy, idempotencyKey, now, ct,
            isAutomationInvocationPending: true, taskId: taskId);

    private static async Task<BacklogTask> CreateBacklogTaskAsync(
        IBacklogTaskStore backlogStore,
        Project project,
        WorkflowDefinition definition,
        string title,
        string description,
        string capturedBy,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken ct,
        bool isAutomationInvocationPending = false,
        BacklogTaskId? taskId = null)
    {
        if (taskId.HasValue)
        {
            var existingTask = await backlogStore.GetAsync(project.Id, taskId.Value, ct).ConfigureAwait(false);
            if (existingTask is not null)
                return existingTask;
        }

        var existing = await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false);
        var backlogKeys = existing
            .Where(t => t.State == BacklogTaskState.Backlog)
            .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
            .Select(t => t.OrderKey)
            .ToList();
        var orderKey = OrderKey.Between(backlogKeys.Count == 0 ? null : backlogKeys[^1], null);

        var task = new BacklogTask
        {
            Id = taskId ?? BacklogTaskId.New(),
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
            IsAutomationInvocationPending = isAutomationInvocationPending,
        };

        try
        {
            await backlogStore.InsertAsync(task, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (taskId.HasValue && !ct.IsCancellationRequested)
        {
            var existingTask = await backlogStore.GetAsync(project.Id, taskId.Value, CancellationToken.None).ConfigureAwait(false);
            if (existingTask is not null)
                return existingTask;
            throw;
        }
        catch (DbException) when (taskId.HasValue && !ct.IsCancellationRequested)
        {
            var existingTask = await backlogStore.GetAsync(project.Id, taskId.Value, CancellationToken.None).ConfigureAwait(false);
            if (existingTask is not null)
                return existingTask;
            throw;
        }
        return task;
    }

    /// <summary>
    /// Resumes the one durable task handoff for an invocation. The reservation remains until
    /// publication completes, allowing a later scheduler period to finish an interrupted handoff.
    /// </summary>
    public static async Task<BacklogTask> RecoverAndPublishAutomationTaskAsync(
        IBacklogTaskStore backlogStore,
        IAutomationInvocationService invocations,
        AutomationInvocationClaim invocation,
        Project project,
        WorkflowDefinition definition,
        string title,
        string description,
        string capturedBy,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var reservation = await invocations.TryReserveBacklogTaskAsync(invocation.InvocationId, project.Id, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unable to reserve a trusted automation invocation backlog task.");
        var task = await backlogStore.GetAsync(project.Id, reservation.BacklogTaskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            try
            {
                task = await CreateProvisionalAutomationTaskAsync(
                    backlogStore, project, definition, title, description, capturedBy, idempotencyKey, now, ct,
                    reservation.BacklogTaskId).ConfigureAwait(false);
            }
            catch
            {
                throw;
            }
        }

        if (!IsTrustedAutomationTask(task) ||
            !string.Equals(task.SourceFilePath, idempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Reserved automation invocation task does not match its occurrence.");

        if (task.State == BacklogTaskState.Ready && !task.IsAutomationInvocationPending)
        {
            if (!await invocations.TryCompleteBacklogTaskReservationAsync(
                    invocation.InvocationId, project.Id, task.Id, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Unable to complete trusted automation invocation task reservation.");
            return task;
        }

        if (task.State != BacklogTaskState.Backlog || !task.IsAutomationInvocationPending)
            throw new InvalidOperationException("Reserved automation invocation task is not publishable.");

        if (!reservation.IsBound &&
            !await invocations.TryBindBacklogTaskAsync(invocation.InvocationId, project.Id, task.Id, ct)
                .ConfigureAwait(false))
            throw new InvalidOperationException("Unable to bind trusted automation invocation to its backlog task.");

        if (!await TryPublishAsync(backlogStore, project, task, now, ct).ConfigureAwait(false))
        {
            var published = await backlogStore.GetAsync(project.Id, task.Id, ct).ConfigureAwait(false);
            if (published?.State != BacklogTaskState.Ready || published.IsAutomationInvocationPending)
                throw new InvalidOperationException("Unable to publish trusted automation invocation backlog task.");
            task = published;
        }

        if (!await invocations.TryCompleteBacklogTaskReservationAsync(
                invocation.InvocationId, project.Id, task.Id, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to complete trusted automation invocation task reservation.");
        return task with { State = BacklogTaskState.Ready, CommittedAt = now, IsAutomationInvocationPending = false };
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
        return await backlogStore.TryPublishAutomationInvocationTaskAsync(project.Id, task.Id, orderKey, now, ct)
            .ConfigureAwait(false);
    }
}
