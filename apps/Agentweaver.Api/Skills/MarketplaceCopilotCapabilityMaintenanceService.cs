using Agentweaver.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Independently reclaims a bounded batch of expired marketplace capabilities. This is deliberately
/// not tied to browse activity: a disconnected browser or a crashed request must not retain an
/// unconsumed authority indefinitely.
/// </summary>
internal sealed class MarketplaceCopilotCapabilityMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<MarketplaceCopilotCapabilityMaintenanceService> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Marketplace capability expiry maintenance failed; will retry next interval");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Performs one bounded sweep; extracted for deterministic lifecycle tests.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<TwoAppPersistenceStore>();
        return await persistence
            .PruneMarketplaceCopilotCapabilitiesAsync(DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }
}
