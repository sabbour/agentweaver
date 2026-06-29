using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Background service that periodically sweeps checkpoint directories for runs that
/// have reached a terminal state in SQLite (Guardrail 8). Covers crashed or abandoned
/// runs whose inline cleanup never ran.
/// </summary>
public sealed class CheckpointGcService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    private readonly IRunStore _runStore;
    private readonly RunWorkflowFactory _factory;
    private readonly ICheckpointStoreFactory _checkpointStoreFactory;
    private readonly HeartbeatStatusStore _statusStore;
    private readonly ILogger<CheckpointGcService> _logger;

    public CheckpointGcService(
        IRunStore runStore,
        RunWorkflowFactory factory,
        ICheckpointStoreFactory checkpointStoreFactory,
        HeartbeatStatusStore statusStore,
        ILogger<CheckpointGcService> logger)
    {
        _runStore = runStore;
        _factory = factory;
        _checkpointStoreFactory = checkpointStoreFactory;
        _statusStore = statusStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
                var tickStart = DateTimeOffset.UtcNow;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int purged = 0;
                string? error = null;
                try
                {
                    purged = await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                    throw;
                }
                finally
                {
                    sw.Stop();
                    _statusStore.RecordTickOutcome(tickStart, "Checkpoint GC", purged, error is null ? 0 : 1, sw.Elapsed.TotalMilliseconds, error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Checkpoint GC sweep failed; will retry next interval");
            }
        }
    }

    private async Task<int> SweepAsync(CancellationToken ct)
    {
        // Postgres: checkpoints live in the shared workflow_checkpoints table — purge rows for terminal
        // runs (the "runs" store). The coordinator store is not GC'd today (matches the file behaviour).
        if (_checkpointStoreFactory.IsDatabaseBacked)
        {
            var purged = await _checkpointStoreFactory
                .PurgeTerminalAsync("runs", IsTerminalSessionAsync, ct)
                .ConfigureAwait(false);
            if (purged > 0)
                _logger.LogInformation("GC: deleted {Count} checkpoint rows for terminal runs", purged);
            return purged;
        }

        var checkpointDir = _factory.CheckpointDirectory;
        if (!Directory.Exists(checkpointDir)) return 0;

        int deletedCount = 0;
        foreach (var dir in Directory.GetDirectories(checkpointDir))
        {
            var runId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(runId)) continue;

            if (!RunId.TryParse(runId, out var parsedId)) continue;

            try
            {
                var run = await _runStore.GetAsync(parsedId, ct).ConfigureAwait(false);
                if (run is null || IsTerminal(run.Status))
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("GC: deleted orphaned checkpoint directory for run {RunId}", runId);
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GC: failed to check/delete checkpoint for run {RunId}", runId);
            }
        }
        return deletedCount;
    }

    private async ValueTask<bool> IsTerminalSessionAsync(string sessionId, CancellationToken ct)
    {
        if (!RunId.TryParse(sessionId, out var parsedId)) return false; // unknown id -> keep
        var run = await _runStore.GetAsync(parsedId, ct).ConfigureAwait(false);
        return run is null || IsTerminal(run.Status);
    }

    private static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Merged or RunStatus.Declined or RunStatus.Failed
            or RunStatus.Completed or RunStatus.MergeFailed or RunStatus.AssembleReady;
}
