using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Background service that periodically sweeps checkpoint directories for runs that
/// have reached a terminal state in SQLite (Guardrail 8). Covers crashed or abandoned
/// runs whose inline cleanup never ran.
/// </summary>
public sealed class CheckpointGcService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);
    private static readonly RunStatus[] TerminalStatuses =
    [
        RunStatus.Merged,
        RunStatus.Declined,
        RunStatus.Failed,
        RunStatus.Completed,
        RunStatus.MergeFailed
    ];

    private readonly SqliteRunStore _runStore;
    private readonly RunWorkflowFactory _factory;
    private readonly ILogger<CheckpointGcService> _logger;

    public CheckpointGcService(
        SqliteRunStore runStore,
        RunWorkflowFactory factory,
        ILogger<CheckpointGcService> logger)
    {
        _runStore = runStore;
        _factory = factory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
                await SweepAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task SweepAsync(CancellationToken ct)
    {
        var checkpointDir = _factory.CheckpointDirectory;
        if (!Directory.Exists(checkpointDir)) return;

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
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GC: failed to check/delete checkpoint for run {RunId}", runId);
            }
        }
    }

    private static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Merged or RunStatus.Declined or RunStatus.Failed
            or RunStatus.Completed or RunStatus.MergeFailed;
}
