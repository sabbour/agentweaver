using Scaffolder.Api.Persistence;

namespace Scaffolder.Api.Agent.Governance;

/// <summary>
/// T048: Enforces per-run step count and wall-clock duration bounds (FR-029).
///
/// The enforcer is owned by RunExecutionService and consulted on each agent
/// loop iteration. When either limit is reached:
///   - A run.bounded event is emitted via EventLogService.
///   - The run is transitioned to the Bounded terminal state via RunStateMachine.
///   - The active agent loop CancellationToken is cancelled so the loop exits.
///
/// This enforcement is not bypassable by any client or tool per FR-029.
/// </summary>
public sealed class RunBoundsEnforcer
{
    private readonly EventLogService _eventLog;
    private readonly ILogger<RunBoundsEnforcer> _logger;

    public RunBoundsEnforcer(
        EventLogService eventLog,
        ILogger<RunBoundsEnforcer> logger)
    {
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the current step count has reached the configured maximum.
    /// Returns <c>true</c> if the bound has been reached (run should be terminated).
    /// </summary>
    public bool IsStepBoundReached(int currentSteps, int maxSteps)
    {
        if (currentSteps >= maxSteps)
        {
            _logger.LogInformation(
                "RunBoundsEnforcer: step bound reached ({Current}/{Max})",
                currentSteps, maxSteps);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Emits a run.bounded event and records the reason.
    /// Called by RunExecutionService before cancelling the agent loop.
    /// </summary>
    public async Task EmitBoundedEventAsync(
        Guid runId,
        string reason,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "RunBoundsEnforcer: emitting run.bounded for run {RunId}. Reason: {Reason}",
            runId, reason);

        await _eventLog.AppendLifecycleEventAsync(
            runId,
            EventType.RunBounded,
            new { reason },
            ct);
    }

    /// <summary>
    /// Creates a CancellationTokenSource that cancels after <paramref name="maxDurationSeconds"/>.
    /// Use the returned token as the agent loop cancellation token.
    /// </summary>
    public static CancellationTokenSource CreateDurationCts(int maxDurationSeconds)
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(maxDurationSeconds));
    }
}
