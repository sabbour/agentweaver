using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Path A pickup: turns a single top-of-Ready backlog task into a running, unattended COORDINATOR
/// run. Owns the Feature 009 section 1.5 flow — it builds the reserved coordinator <see cref="Run"/>
/// (with a caller-supplied <see cref="RunId"/> so the claim binds it atomically and the durable
/// <see cref="RunOrigin.BacklogPickup"/> marker is stamped in the same transaction), executes the
/// atomic claim+reserve, and — only on a won claim — activates the coordinator workflow and schedules
/// the unattended outcome-spec confirmation via <see cref="CoordinatorRunService"/> (FR-021).
/// </summary>
public sealed class CoordinatorPickupService
{
    private readonly IBacklogTaskStore _backlogStore;
    private readonly SqliteRunStore _runStore;
    private readonly CoordinatorRunService _coordinatorRunService;
    private readonly ILogger<CoordinatorPickupService> _logger;

    public CoordinatorPickupService(
        IBacklogTaskStore backlogStore,
        SqliteRunStore runStore,
        CoordinatorRunService coordinatorRunService,
        ILogger<CoordinatorPickupService> logger)
    {
        _backlogStore = backlogStore;
        _runStore = runStore;
        _coordinatorRunService = coordinatorRunService;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to claim <paramref name="task"/> and start its coordinator run. On a lost claim
    /// (another heartbeat won, or the task was moved back to Backlog) nothing is persisted and the
    /// method returns. On <see cref="ClaimReserveResult.ProjectUnavailable"/> the task is left in
    /// Ready with its priority preserved. Only on a won claim is the coordinator workflow activated;
    /// if activation throws post-commit, the reserved run is terminalized Failed and the task stays
    /// Claimed (no silent re-queue, FR-012).
    /// </summary>
    public async Task TryPickupAsync(Project project, BacklogTask task, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var goal = string.IsNullOrWhiteSpace(task.Description)
            ? task.Title
            : $"{task.Title}\n\n{task.Description}";
        if (!string.IsNullOrWhiteSpace(task.WorkflowOverrideId))
            goal = $"use {task.WorkflowOverrideId.Trim()}\n\n{goal}";

        // The coordinator provider is fixed to GitHub Copilot (Constitution Principle II). The model
        // id is resolved the same way the project coordinator-run endpoint does: the project default.
        var modelId = project.ProviderSettings.GitHubCopilotModel;

        var run = new Run
        {
            Id = runId,
            RepositoryPath = project.WorkingDirectory,
            OriginatingBranch = project.DefaultBranch,
            ModelSource = ModelSource.GitHubCopilot,
            ModelId = modelId,
            Task = goal,
            SubmittingUser = task.CapturedBy,         // accountable human (Principle IX)
            Status = RunStatus.InProgress,
            StartedAt = now,
            ProjectId = project.Id,
            AgentName = "Coordinator",                // parent coordinator run
            ParentRunId = null,
            SubtaskId = null,
            WorkflowRunId = null,                     // identity parity with interactive coordinator runs:
                                                      // detail page + endpoints resolve by run_id (no envelope)
            Origin = RunOrigin.BacklogPickup,         // durable origin marker; persisted atomically in step (b)
        };

        var result = await _backlogStore
            .TryClaimAndReserveCoordinatorRunAsync(project.Id, task.Id, run, now, ct)
            .ConfigureAwait(false);

        switch (result)
        {
            case ClaimReserveResult.Lost:
                // Another heartbeat/instance won, or the task moved back to Backlog. Nothing persisted.
                return;
            case ClaimReserveResult.ProjectUnavailable:
                _logger.LogInformation(
                    "Pickup: project {ProjectId} not active; task {TaskId} left Ready", project.Id, task.Id);
                return;
        }

        // Reservation committed. Activate the coordinator workflow + unattended confirm post-commit.
        // CancellationToken.None: the run must outlive the heartbeat tick that spawned it.
        try
        {
            await _coordinatorRunService.StartReservedCoordinatorRunAsync(
                run,
                autoApproveTools: project.PickupAutoApproveTools,
                autopilot: project.PickupAutopilot,
                confirmedBy: task.CapturedBy,         // named human accountable for the auto-confirm (Principle IX)
                ct: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pickup: coordinator start failed for run {RunId}", runId);
            var terminalized = await _runStore.TrySetTerminalStatusAsync(
                    runId, RunStatus.Failed, DateTimeOffset.UtcNow, "coordinator_start_failed", CancellationToken.None)
                .ConfigureAwait(false);
            if (!terminalized)
            {
                _logger.LogWarning(
                    "Pickup: failed to terminalize coordinator run {RunId} after activation failure; claimed task {TaskId} may be tied to a non-terminal run",
                    runId, task.Id);
            }

            // Task stays Claimed -> Failed coordinator run shown in the terminal column. No silent re-queue (FR-012).
        }
    }
}
