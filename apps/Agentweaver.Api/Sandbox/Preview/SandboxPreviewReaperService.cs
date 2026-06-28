using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Background sweep (~60 s) that reaps expired or orphaned previews. Entirely driven by
/// HTTPRoute annotations + live pod existence (no in-memory registry), so it is replica-safe:
/// both API replicas run identical reconciliation against the same cluster state.
///
/// <para>No-ops when <c>Sandbox:Preview:Enabled=false</c> (default), preserving ship-dark behaviour.</para>
/// </summary>
public sealed class SandboxPreviewReaperService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly ISandboxPreviewService _preview;
    private readonly ILogger<SandboxPreviewReaperService> _logger;

    public SandboxPreviewReaperService(
        ISandboxPreviewService preview,
        ILogger<SandboxPreviewReaperService> logger)
    {
        _preview = preview;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_preview.Enabled)
        {
            _logger.LogInformation(
                "SandboxPreviewReaperService: preview disabled — reaper idle (set Sandbox:Preview:Enabled=true to activate).");
            return;
        }

        _logger.LogInformation(
            "SandboxPreviewReaperService: starting preview reaper (sweep every {Seconds}s)",
            SweepInterval.TotalSeconds);

        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reaped = await _preview.ReapAsync(stoppingToken).ConfigureAwait(false);
                if (reaped > 0)
                    _logger.LogInformation("SandboxPreviewReaperService: reaped {Count} preview(s)", reaped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SandboxPreviewReaperService: sweep failed (best-effort, will retry)");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
