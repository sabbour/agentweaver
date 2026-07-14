using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Polls the backlog store on a short interval for the number of ACTIVE-project backlog tasks
/// currently in Ready and still unclaimed (<c>state='ready' AND run_id IS NULL</c>) and publishes
/// it via <see cref="AgentWeaverMetrics.QueuedRuns"/> (issue #108). This is the durable worker
/// pickup backlog the coordinator heartbeat actually drains; <c>runs.status='pending'</c> is only
/// a short-lived reservation seam on other start paths and is intentionally excluded.
///
/// An <see cref="System.Diagnostics.Metrics.ObservableGauge{T}"/> callback must be synchronous, so
/// it cannot itself await a DB query. This service instead polls asynchronously on its own
/// interval and caches the latest count via <see cref="AgentWeaverMetrics.SetQueuedRunsCount"/>;
/// the gauge callback then just reads that cached value when the OTel exporter collects.
///
/// Every replica polls the same shared store and therefore exports the same GLOBAL snapshot.
/// Downstream Prometheus/KEDA queries must aggregate this instrument with <c>max</c>, never
/// <c>sum</c>, or the queue depth will be multiplied by replica count.
/// </summary>
public sealed class QueuedRunsMetricService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IBacklogTaskStore _backlogStore;
    private readonly ILogger<QueuedRunsMetricService> _logger;

    public QueuedRunsMetricService(IBacklogTaskStore backlogStore, ILogger<QueuedRunsMetricService> logger)
    {
        _backlogStore = backlogStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Executes a single poll-and-publish cycle. Extracted from <see cref="ExecuteAsync"/> so unit
    /// tests can exercise the query-and-publish behavior directly against a real
    /// <see cref="IBacklogTaskStore"/> without waiting on <see cref="PollInterval"/>.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var queued = await _backlogStore.CountReadyForPickupAsync(ct).ConfigureAwait(false);
            AgentWeaverMetrics.SetQueuedRunsCount(queued);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QueuedRunsMetricService poll failed; will retry next interval");
        }
    }
}
