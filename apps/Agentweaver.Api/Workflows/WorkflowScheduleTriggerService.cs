using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Background scheduler for schedule-triggered workflows (issue #53). On each tick, for every active
/// project, checks each of the project's valid workflows that declare a <see cref="WorkflowTrigger"/>
/// of type <see cref="WorkflowTriggerType.Schedule"/> and, when the cadence's current occurrence is
/// due (<see cref="WorkflowScheduleEvaluator"/>) and has not already fired, creates a Ready backlog
/// task bound to that workflow (<see cref="WorkflowTriggerBacklogFactory"/>) so the existing
/// <see cref="Coordinator.CoordinatorHeartbeatService"/> pickup path claims it and starts a run —
/// there is no bespoke run-start path here, so the same pickup-capacity bounds, exactly-once claim,
/// and board visibility apply as for every other backlog task.
///
/// <para>Idempotency (fire-at-most-once per occurrence) is held by the server-owned automation
/// invocation claim and its task reservation. The source path encodes the workflow id + due period,
/// so a completed Ready task is a fast repeat-tick no-op while an incomplete handoff is resumed
/// through that same claimed invocation.</para>
///
/// <para>Before evaluating the current period, each configured schedule trigger resumes its bounded
/// set of incomplete, durably reserved task handoffs. This prevents an interruption in a previous
/// daily, weekly, or monthly period from becoming permanently stranded when its occurrence key rolls
/// over. A recovery set over the safety limit fails the project tick rather than dropping an occurrence.</para>
///
/// <para>A workflow with no triggers is entirely unaffected, so on-demand/manual start behavior is
/// unchanged.</para>
/// </summary>
public sealed class WorkflowScheduleTriggerService : BackgroundService
{
    public const string CapturedBy = "automation:schedule-trigger";
    private const int MaximumOutstandingInvocationsPerTrigger = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowScheduleTriggerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public WorkflowScheduleTriggerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<WorkflowScheduleTriggerService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Master enable flag (default true), mirroring Coordinator:HeartbeatEnabled so hermetic web
        // tests can disable it to stay deterministic.
        _enabled = configuration.GetValue("Workflows:ScheduleTriggerEnabled", true);

        var seconds = configuration.GetValue("Workflows:ScheduleTriggerIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Workflow schedule trigger disabled (Workflows:ScheduleTriggerEnabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunTickAsync(_timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one tick against an explicit <paramref name="now"/> — the test seam that keeps this fully
    /// unit-testable without any wall-clock dependency or sleeping.
    /// </summary>
    public async Task RunTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var projectStore = sp.GetRequiredService<IProjectStore>();
        var backlogStore = sp.GetRequiredService<IBacklogTaskStore>();
        var registry = sp.GetRequiredService<WorkflowRegistry>();
        var invocations = sp.GetRequiredService<IAutomationInvocationService>();

        IReadOnlyList<Project> projects = await projectStore.ListAsync(ct).ConfigureAwait(false);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.State != ProjectState.Active)
                continue;

            try
            {
                await TickProjectAsync(project, now, registry, backlogStore, invocations, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // shutdown — stop the service cleanly
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workflow schedule trigger: project {ProjectId} tick failed", project.Id);
                // Isolated; next project still processed.
            }
        }
    }

    private async Task TickProjectAsync(
        Project project,
        DateTimeOffset now,
        WorkflowRegistry registry,
        IBacklogTaskStore backlogStore,
        IAutomationInvocationService invocations,
        CancellationToken ct)
    {
        var set = registry.GetOrLoad(project);
        foreach (var result in set.Available)
        {
            ct.ThrowIfCancellationRequested();
            var def = result.Definition;
            if (def is null)
                continue;

            var schedules = def.Triggers
                .Where(t => t.Type == WorkflowTriggerType.Schedule)
                .Select((trigger, ordinal) => (Trigger: trigger, Ordinal: ordinal))
                .ToList();
            foreach (var schedule in schedules)
            {
                ct.ThrowIfCancellationRequested();
                var occurrenceKeyPrefix = BuildIdempotencyKey(def.Id, string.Empty, schedule.Ordinal);
                var legacyProvisionalOccurrences = (await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false))
                    .Where(task => task.State == BacklogTaskState.Backlog &&
                                   task.RunId is null &&
                                   task.IsAutomationInvocationPending &&
                                   string.Equals(task.WorkflowOverrideId, def.Id, StringComparison.Ordinal) &&
                                   string.Equals(task.CapturedBy, CapturedBy, StringComparison.Ordinal) &&
                                   task.SourceFilePath?.StartsWith(occurrenceKeyPrefix, StringComparison.Ordinal) == true)
                    .Select(task => task.SourceFilePath!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaximumOutstandingInvocationsPerTrigger + 1)
                    .ToList();
                if (legacyProvisionalOccurrences.Count > MaximumOutstandingInvocationsPerTrigger)
                    throw new InvalidOperationException(
                        $"Outstanding schedule invocation recovery exceeds the safe limit of {MaximumOutstandingInvocationsPerTrigger}.");

                var outstanding = await invocations.ListOutstandingScheduleInvocationsAsync(
                    project.Id, occurrenceKeyPrefix, legacyProvisionalOccurrences,
                    MaximumOutstandingInvocationsPerTrigger, ct)
                    .ConfigureAwait(false);
                foreach (var pending in outstanding)
                {
                    var periodKey = pending.OccurrenceKey[occurrenceKeyPrefix.Length..];
                    await RecoverAndPublishAsync(
                        invocations, backlogStore, new(pending.InvocationId), project, def, periodKey,
                        pending.OccurrenceKey, now, ct).ConfigureAwait(false);
                }
            }

            foreach (var schedule in schedules)
            {
                ct.ThrowIfCancellationRequested();
                if (!WorkflowScheduleEvaluator.TryGetDueOccurrence(schedule.Trigger, now, out var periodKey, out _))
                    continue;

                var idempotencyKey = BuildIdempotencyKey(def.Id, periodKey, schedule.Ordinal);
                var alreadyPublished = (await backlogStore.ListByProjectAsync(project.Id, ct).ConfigureAwait(false))
                    .Any(task => string.Equals(task.SourceFilePath, idempotencyKey, StringComparison.Ordinal) &&
                                 task.State == BacklogTaskState.Ready &&
                                 !task.IsAutomationInvocationPending);
                if (alreadyPublished)
                    continue;

                var invocation = await invocations.TryClaimForProjectAsync(
                    project.Id, idempotencyKey, deliveryId: null, eventName: "schedule", ct).ConfigureAwait(false);
                if (invocation is null)
                {
                    _logger.LogWarning(
                        "Workflow schedule trigger refused workflow {WorkflowId} for project {ProjectId}: automation activation unavailable",
                        def.Id, project.Id);
                    continue;
                }

                var task = await RecoverAndPublishAsync(
                    invocations, backlogStore, invocation, project, def, periodKey, idempotencyKey, now, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow schedule trigger: fired workflow {WorkflowId} for project {ProjectId} (task {TaskId}, occurrence {PeriodKey})",
                    def.Id, project.Id, task.Id, periodKey);
            }
        }
    }

    internal static string BuildIdempotencyKey(string workflowId, string periodKey, int scheduleOrdinal = 0) =>
        scheduleOrdinal == 0
            ? $"workflow-schedule-trigger:{workflowId}:{periodKey}"
            : $"workflow-schedule-trigger:{workflowId}:{scheduleOrdinal}:{periodKey}";

    private static Task<BacklogTask> RecoverAndPublishAsync(
        IAutomationInvocationService invocations,
        IBacklogTaskStore backlogStore,
        AutomationInvocationClaim invocation,
        Project project,
        WorkflowDefinition definition,
        string periodKey,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken ct) =>
        WorkflowTriggerBacklogFactory.RecoverAndPublishAutomationTaskAsync(
            backlogStore, invocations, invocation, project, definition,
            title: $"Scheduled run: {definition.Name}",
            description: $"Automatically triggered by the '{definition.Id}' workflow's schedule trigger (occurrence {periodKey}).",
            capturedBy: CapturedBy, idempotencyKey: idempotencyKey, now: now, ct: ct);
}
