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
/// <para>Idempotency (fire-at-most-once per occurrence) piggybacks on the SAME "does a task with this
/// synthetic source path already exist" check the backlog-capture endpoint uses for external-id
/// idempotency (<see cref="IBacklogTaskStore.GetExistingTitlesFromSourceAsync"/>): the source path
/// encodes the workflow id + the due occurrence's period key, so a fresh occurrence always gets a
/// fresh key and a repeated tick within the same occurrence is a no-op. As with that existing
/// idempotency check, this is a check-then-insert (not a DB-enforced unique constraint), so an
/// extremely narrow multi-replica race remains theoretically possible — the same known limitation the
/// existing capture-by-external-id path already has.</para>
///
/// <para>A workflow with no triggers is entirely unaffected, so on-demand/manual start behavior is
/// unchanged.</para>
/// </summary>
public sealed class WorkflowScheduleTriggerService : BackgroundService
{
    public const string CapturedBy = "automation:schedule-trigger";

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

            var scheduleOrdinal = 0;
            foreach (var trigger in def.Triggers.Where(t => t.Type == WorkflowTriggerType.Schedule))
            {
                if (!WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out _))
                {
                    scheduleOrdinal++;
                    continue;
                }

                var idempotencyKey = BuildIdempotencyKey(def.Id, periodKey, scheduleOrdinal);
                scheduleOrdinal++;
                var alreadyFired = await backlogStore
                    .GetExistingTitlesFromSourceAsync(project.Id, idempotencyKey, ct)
                    .ConfigureAwait(false);
                if (alreadyFired.Count > 0)
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

                var task = await WorkflowTriggerBacklogFactory.CreateProvisionalAutomationTaskAsync(
                    backlogStore,
                    project,
                    def,
                    title: $"Scheduled run: {def.Name}",
                    description: $"Automatically triggered by the '{def.Id}' workflow's schedule trigger (occurrence {periodKey}).",
                    capturedBy: CapturedBy,
                    idempotencyKey: idempotencyKey,
                    now: now,
                    ct: ct).ConfigureAwait(false);
                try
                {
                    if (!await invocations.TryBindBacklogTaskAsync(invocation.InvocationId, project.Id, task.Id, ct)
                            .ConfigureAwait(false))
                        throw new InvalidOperationException("Unable to bind trusted automation invocation to its backlog task.");
                    if (!await WorkflowTriggerBacklogFactory.TryPublishAsync(backlogStore, project, task, now, ct)
                            .ConfigureAwait(false))
                        throw new InvalidOperationException("Unable to publish trusted automation invocation backlog task.");
                }
                catch
                {
                    await DiscardUnpublishedTaskAsync(invocations, backlogStore, invocation, project, task)
                        .ConfigureAwait(false);
                    throw;
                }

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

    private static async Task DiscardUnpublishedTaskAsync(
        IAutomationInvocationService invocations,
        IBacklogTaskStore backlogStore,
        AutomationInvocationClaim invocation,
        Project project,
        BacklogTask task)
    {
        var deleted = await backlogStore.TryDeleteProvisionalAutomationTaskAsync(project.Id, task.Id, CancellationToken.None)
            .ConfigureAwait(false);
        if (!deleted)
            throw new InvalidOperationException("Unable to discard an unbound trusted automation invocation.");
        var released = await invocations.TryDiscardInvocationForTaskAsync(
                invocation.InvocationId, project.Id, task.Id, CancellationToken.None)
            .ConfigureAwait(false);
        if (!released)
            throw new InvalidOperationException("Unable to discard an unbound trusted automation invocation.");
    }
}
