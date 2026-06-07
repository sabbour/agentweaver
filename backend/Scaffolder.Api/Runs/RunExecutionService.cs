using Microsoft.Extensions.Options;
using Scaffolder.Api.Agent;
using Scaffolder.Api.Configuration;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;
using Scaffolder.Api.Worktrees;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Orchestrates the full lifecycle of a run: worktree creation, agent loop,
/// state machine transitions, diff retrieval, and operational record writing.
/// </summary>
public sealed class RunExecutionService
{
    private readonly IRunRepository _runs;
    private readonly IWorktreeService _worktrees;
    private readonly IDiffService _diff;
    private readonly IAgentLoopHost _agentLoop;
    private readonly RunStateMachine _stateMachine;
    private readonly EventLogService _eventLog;
    private readonly OperationalRecordWriter _opRecordWriter;
    private readonly ScaffolderOptions _options;
    private readonly ILogger<RunExecutionService> _logger;

    public RunExecutionService(
        IRunRepository runs,
        IWorktreeService worktrees,
        IDiffService diff,
        IAgentLoopHost agentLoop,
        RunStateMachine stateMachine,
        EventLogService eventLog,
        OperationalRecordWriter opRecordWriter,
        IOptions<ScaffolderOptions> options,
        ILogger<RunExecutionService> logger)
    {
        _runs = runs;
        _worktrees = worktrees;
        _diff = diff;
        _agentLoop = agentLoop;
        _stateMachine = stateMachine;
        _eventLog = eventLog;
        _opRecordWriter = opRecordWriter;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Executes a run from start to completion/failure/bounded.
    /// Should be called on a background thread (not blocking the HTTP request).
    /// </summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetByIdAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run {runId} not found.");

        SessionEntity? session = null;
        var stepCount = 0;

        try
        {
            // Transition to Running
            await _stateMachine.TransitionToRunningAsync(runId, ct);

            // Create worktree session
            session = await _worktrees.CreateWorktreeAsync(runId, run.OriginatingBranch, ct);
            await _runs.UpdateSessionIdAsync(runId, session.Id, ct);

            // Build bounds-enforcing cancellation token
            using var durationCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.DefaultMaxDurationSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, durationCts.Token);

            // Execute agent loop
            var loopContext = new AgentLoopContext
            {
                RunId = runId,
                TaskPrompt = run.TaskPrompt,
                ArtifactDir = session.ArtifactDir,
                ModelSource = run.ModelSource == ModelSource.CopilotSdk
                    ? ModelSourceType.CopilotSdk
                    : ModelSourceType.MicrosoftFoundry
            };

            try
            {
                await _agentLoop.ExecuteAsync(loopContext, linkedCts.Token);
                stepCount = _options.DefaultMaxSteps; // Placeholder; AgentLoopHost will report real count in T040+
            }
            catch (OperationCanceledException) when (durationCts.IsCancellationRequested)
            {
                await _stateMachine.TransitionToBoundedAsync(
                    runId,
                    $"Run exceeded maximum duration of {_options.DefaultMaxDurationSeconds} seconds.",
                    CancellationToken.None);
                await WriteOperationalRecord(run, stepCount, CancellationToken.None);
                return;
            }

            // Get diff and transition to Completed
            var diffText = await _diff.GetDiffAsync(
                session.WorktreePath, session.OriginatingCommit, ct);
            await _runs.UpdateDiffSummaryAsync(runId, diffText[..Math.Min(diffText.Length, 5000)], ct);

            await _stateMachine.TransitionToCompletedAsync(runId, ct);
            await _stateMachine.TransitionToAwaitingReviewAsync(runId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Run {RunId} failed with unexpected error", runId);
            try
            {
                await _stateMachine.TransitionToFailedAsync(
                    runId,
                    $"Unexpected error: {ex.Message}",
                    CancellationToken.None);
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Failed to transition run {RunId} to Failed state", runId);
            }
        }
        finally
        {
            await WriteOperationalRecord(run, stepCount, CancellationToken.None);

            // Worktree cleanup is deferred until after human review (US4)
        }
    }

    private async Task WriteOperationalRecord(RunEntity run, int stepCount, CancellationToken ct)
    {
        try
        {
            // Re-fetch run to get updated status
            var latest = await _runs.GetByIdAsync(run.Id, ct) ?? run;
            await _opRecordWriter.WriteAsync(latest, stepCount, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write operational record for run {RunId}", run.Id);
        }
    }
}
