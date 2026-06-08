using System.Threading.Channels;
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
    private readonly RunStreamStore _streamStore;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly CancellationToken _appStopping;

    public RunOrchestrator(
        SqliteRunStore runStore,
        IAgentRunner agentRunner,
        RunStreamStore streamStore,
        IHostApplicationLifetime lifetime,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _agentRunner = agentRunner;
        _streamStore = streamStore;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    public async Task StartRunAsync(Run run, CancellationToken ct)
    {
        var started = run with { Status = RunStatus.InProgress, StartedAt = DateTimeOffset.UtcNow };
        await _runStore.InsertAsync(started, ct).ConfigureAwait(false);
        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
        _ = Task.Run(() => RunTurnAsync(started, entry), _appStopping);
    }

    private async Task RunTurnAsync(Run run, RunStreamEntry entry)
    {
        var recordingWriter = new RecordingChannelWriter(entry);
        var ct = _appStopping;
        try
        {
            var result = await _agentRunner.ExecuteAsync(run.Task, run.RepositoryPath, run.ModelSource, run.Id.ToString(), recordingWriter, ct).ConfigureAwait(false);
            await _runStore.UpdateResultAsync(run.Id, RunStatus.Completed, result, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent turn failed for run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        finally
        {
            _streamStore.Complete(run.Id.ToString());
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

/// <summary>
/// Adapts a <see cref="RunStreamEntry"/> into a <see cref="ChannelWriter{T}"/> for
/// the agent runner. Events are recorded directly into the entry's history list;
/// there is no separate channel — clients poll the history via
/// <see cref="RunStreamEntry.GetSnapshotSince"/>.
/// </summary>
file sealed class RecordingChannelWriter(RunStreamEntry entry) : ChannelWriter<RunEvent>
{
    public override bool TryWrite(RunEvent item)
    {
        entry.Record(item);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public override bool TryComplete(Exception? error = null) => true;
}
