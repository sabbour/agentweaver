using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<OAuthMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OAuth maintenance failed; protocol validation remains active.");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (!await TryAcquireLeaseAsync(db, ct).ConfigureAwait(false))
            return;

        var now = DateTimeOffset.UtcNow;
        await OAuthDynamicClientLifecycle.DisableExpiredAsync(
            db,
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(),
            now,
            ct).ConfigureAwait(false);

        var cutoff = now.Subtract(OAuthServerConfiguration.RefreshReplayRetention);
        await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
            .PruneAsync(cutoff, ct).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
            .PruneAsync(cutoff, ct).ConfigureAwait(false);

        var staleTransactions = (await db.OAuthAuthorizationTransactions.AsNoTracking()
            .Select(x => new { x.HandleHash, x.ExpiresAt }).ToListAsync(ct).ConfigureAwait(false))
            .Where(x => x.ExpiresAt < now).Select(x => x.HandleHash).ToArray();
        var staleRegistrations = (await db.OAuthDynamicRegistrations.AsNoTracking()
            .Select(x => new { x.Id, x.DisabledAt }).ToListAsync(ct).ConfigureAwait(false))
            .Where(x => x.DisabledAt < now.AddDays(-90)).Select(x => x.Id).ToArray();
        var staleConsents = (await db.OAuthConsents.AsNoTracking()
            .Select(x => new { x.Id, x.RevokedAt }).ToListAsync(ct).ConfigureAwait(false))
            .Where(x => x.RevokedAt < now.AddDays(-90)).Select(x => x.Id).ToArray();
        await db.OAuthAuthorizationTransactions.Where(x => staleTransactions.Contains(x.HandleHash))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.OAuthDynamicRegistrations.Where(x => staleRegistrations.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.OAuthConsents.Where(x => staleConsents.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> TryAcquireLeaseAsync(MemoryDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return true;

        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(15);
        var updated = await db.OAuthMaintenanceLeases
            .Where(x => x.Name == "oauth-pruning"
                && (x.LeaseExpiresAt < now || x.Owner == _owner))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Owner, _owner)
                .SetProperty(x => x.LeaseExpiresAt, expires), ct)
            .ConfigureAwait(false);
        if (updated == 1)
            return true;

        db.OAuthMaintenanceLeases.Add(new OAuthMaintenanceLease
        {
            Name = "oauth-pruning",
            Owner = _owner,
            LeaseExpiresAt = expires,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
