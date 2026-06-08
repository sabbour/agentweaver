using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Coordinates the run lifecycle: persistence and the agent turn.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly SqliteRunStore _runStore;
    private readonly IAgentRunner _agentRunner;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly CancellationToken _appStopping;

    public RunOrchestrator(
        SqliteRunStore runStore,
        IAgentRunner agentRunner,
        IHostApplicationLifetime lifetime,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _agentRunner = agentRunner;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    public async Task StartRunAsync(Run run, CancellationToken ct)
    {
        var started = run with { Status = RunStatus.InProgress, StartedAt = DateTimeOffset.UtcNow };
        await _runStore.InsertAsync(started, ct).ConfigureAwait(false);
        _ = Task.Run(() => RunTurnAsync(started), _appStopping);
    }

    private async Task RunTurnAsync(Run run)
    {
        var ct = _appStopping;
        try
        {
            var result = await _agentRunner.ExecuteAsync(run.Task, run.RepositoryPath, ct).ConfigureAwait(false);
            await _runStore.UpdateResultAsync(run.Id, RunStatus.Completed, result, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent turn failed for run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fails any run still marked InProgress from a previous process on startup.
    /// </summary>
    public async Task RestartRecoveryAsync(CancellationToken ct)
    {
        var stranded = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in stranded)
        {
            _logger.LogWarning("Failing stranded run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
