using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Workflows;
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
    private readonly IRunStore _runStore;
    private readonly CoordinatorRunService _coordinatorRunService;
    private readonly ILogger<CoordinatorPickupService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorPickupService(
        IBacklogTaskStore backlogStore,
        IRunStore runStore,
        CoordinatorRunService coordinatorRunService,
        ILogger<CoordinatorPickupService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _backlogStore = backlogStore;
        _runStore = runStore;
        _coordinatorRunService = coordinatorRunService;
        _logger = logger;
        _scopeFactory = scopeFactory;
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

        // The model id is resolved the same way the project coordinator-run endpoint does: the
        // project default. The PROVIDER, however, comes from the shared resolver — a pickup run must
        // record the provider that actually serves it (BYOK or Copilot), not a hardcoded literal.
        var modelId = project.ProviderSettings.GitHubCopilotModel;
        var effectiveProvider = await ResolveEffectiveProviderAsync(project.Id, ct).ConfigureAwait(false);

        var run = new Run
        {
            Id = runId,
            RepositoryPath = project.WorkingDirectory,
            OriginatingBranch = project.DefaultBranch,
            ModelSource = effectiveProvider.ToModelSource(),
            ModelId = modelId,
            Task = goal,
            // Keep the human-facing GitHub login in CapturedBy while carrying the durable auth
            // subject into background execution. Legacy and automation tasks retain their existing
            // behavior through the fallback.
            SubmittingUser = task.CapturedByUserId ?? task.CapturedBy,
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

        var blockedReason = (string?)null;
        try
        {
            CoordinatorRosterGuard.EnsureDispatchableTeam(project.WorkingDirectory);
        }
        catch (NoTeamException)
        {
            blockedReason = NoTeamException.ErrorCode;
            run = run with
            {
                Status = RunStatus.Failed,
                EndedAt = now,
                Result = blockedReason,
            };
        }
        catch (InvalidTeamException ex)
        {
            blockedReason = InvalidTeamException.ErrorCode;
            _logger.LogError(ex, "Pickup refused: project {ProjectId} team roster is invalid; task {TaskId}", project.Id, task.Id);
            run = run with
            {
                Status = RunStatus.Failed,
                EndedAt = now,
                Result = blockedReason,
            };
        }

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

        if (blockedReason is not null)
        {
            if (blockedReason == NoTeamException.ErrorCode)
            {
                _logger.LogWarning(
                    "Pickup refused: project {ProjectId} has no dispatchable team; task {TaskId} claimed to failed run {RunId} with reason {Reason}",
                    project.Id, task.Id, runId, blockedReason);
            }
            return;
        }

        if (WorkflowTriggerBacklogFactory.IsTrustedAutomationTask(task))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var invocationService = scope.ServiceProvider.GetRequiredService<IAutomationInvocationService>();
            if (!await invocationService.TryPrepareRunAsync(project.Id, task.Id, runId.ToString(), ct).ConfigureAwait(false))
            {
                await _runStore.TrySetTerminalStatusAsync(
                    runId, RunStatus.Failed, DateTimeOffset.UtcNow, "automation_invocation_unavailable", ct)
                    .ConfigureAwait(false);
                _logger.LogWarning("Pickup refused unavailable automation invocation for task {TaskId} and run {RunId}", task.Id, runId);
                return;
            }
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

    /// <summary>
    /// Resolves the effective model provider for <paramref name="projectId"/> through the single
    /// shared <see cref="EffectiveModelProviderResolver"/>, so the reserved coordinator run row
    /// records the provider that actually serves it.
    /// </summary>
    private async Task<EffectiveModelProviderResult> ResolveEffectiveProviderAsync(
        ProjectId projectId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        return await resolver.ResolveAsync(projectId, ct).ConfigureAwait(false);
    }
}
