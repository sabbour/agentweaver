using Microsoft.Extensions.Logging;
using Agentweaver.Domain;
using Agentweaver.Api.Webhooks;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Entry point for an inbound event to start a run for every project workflow whose trigger declares
/// <c>type: event</c> with a matching event name (issue #53, first pass). Mirrors the schedule
/// trigger's mechanism exactly: firing creates a Ready backlog task bound to the workflow
/// (<see cref="WorkflowTriggerBacklogFactory"/>) so the run starts via the EXISTING
/// <see cref="Coordinator.CoordinatorHeartbeatService"/> pickup path — an event firing is an
/// accountable run visible on the board, claimed exactly once, same as every other backlog task.
///
/// <para><b>SCOPE:</b> this class is the trigger MECHANISM. Event sources call
/// <see cref="FireEventAsync"/>: the project-scoped GitHub webhook receiver
/// (<c>Endpoints.GitHubWebhookEndpoints</c>, HMAC-verified before it reaches here) and the explicit
/// <c>POST /api/projects/{id}/workflow-events</c> endpoint (<c>Endpoints.WorkflowTriggerEndpoints</c>).
/// This service treats the event name as an opaque, already-authenticated routing key. The only raw
/// user text it may see is <c>comment.body</c> for the boolean-only <c>commentMatches</c> predicate;
/// that body is never logged, stored, parsed for arguments, or forwarded to any downstream prompt.</para>
/// </summary>
public sealed class WorkflowEventTriggerService
{
    public const string CapturedBy = "automation:event-trigger";

    private readonly IBacklogTaskStore _backlogStore;
    private readonly WorkflowRegistry _registry;
    private readonly ILogger<WorkflowEventTriggerService> _logger;
    private readonly TimeProvider _timeProvider;

    public WorkflowEventTriggerService(
        IBacklogTaskStore backlogStore,
        WorkflowRegistry registry,
        ILogger<WorkflowEventTriggerService> logger,
        TimeProvider? timeProvider = null)
    {
        _backlogStore = backlogStore;
        _registry = registry;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Fires <paramref name="eventName"/> for <paramref name="project"/>: creates one Ready backlog
    /// task per matching, valid workflow (WorkflowOverrideId bound). When <paramref name="dedupeKey"/>
    /// is supplied (e.g. a webhook delivery id) a repeated call with the SAME event name + dedupe key
    /// is a no-op, so a retried delivery never double-fires; omitting it means every call fires (the
    /// caller is the source of dedupe truth for at-most-once delivery semantics). When a matching
    /// event-triggered workflow declares structured predicates in <c>trigger.if</c>, they are
    /// evaluated against <paramref name="payload"/> before firing. Returns the ids of the workflows
    /// that fired.
    /// </summary>
    public async Task<IReadOnlyList<string>> FireEventAsync(
        Project project, string eventName, string? dedupeKey, GitHubWebhookPayload? payload, CancellationToken ct)
    {
        if (project.State != ProjectState.Active) return [];
        if (string.IsNullOrWhiteSpace(eventName)) return [];

        var set = _registry.GetOrLoad(project);
        var now = _timeProvider.GetUtcNow();
        var fired = new List<string>();

        foreach (var result in set.Available)
        {
            ct.ThrowIfCancellationRequested();
            var def = result.Definition;
            if (def?.Trigger is not { Type: WorkflowTriggerType.Event } trigger) continue;
            if (!string.Equals(trigger.EventName, eventName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!WorkflowTriggerPredicateEvaluator.EvaluateAll(trigger.If, eventName, payload)) continue;

            var idempotencyKey = string.IsNullOrWhiteSpace(dedupeKey)
                ? $"workflow-event-trigger:{def.Id}:{eventName}:{now:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}"
                : $"workflow-event-trigger:{def.Id}:{eventName}:{dedupeKey}";

            if (!string.IsNullOrWhiteSpace(dedupeKey))
            {
                var alreadyFired = await _backlogStore
                    .GetExistingTitlesFromSourceAsync(project.Id, idempotencyKey, ct)
                    .ConfigureAwait(false);
                if (alreadyFired.Count > 0)
                    continue;   // already fired for this dedupe key
            }

            var task = await WorkflowTriggerBacklogFactory.CreateReadyTaskAsync(
                _backlogStore,
                project,
                def,
                title: $"Event run: {def.Name}",
                description: $"Automatically triggered by event '{eventName}' for the '{def.Id}' workflow.",
                capturedBy: CapturedBy,
                idempotencyKey: idempotencyKey,
                now: now,
                ct: ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Workflow event trigger: fired workflow {WorkflowId} for project {ProjectId} on event {EventName} (task {TaskId})",
                def.Id, project.Id, eventName, task.Id);
            fired.Add(def.Id);
        }

        return fired;
    }
}
