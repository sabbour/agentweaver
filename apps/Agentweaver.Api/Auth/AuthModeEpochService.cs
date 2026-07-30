using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

public sealed record AuthModeEpochSnapshot(AuthMode AuthMode, long Epoch);

public sealed class AuthModeEpochService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AuthModeEpochService> logger)
{
    private const string SingletonKey = "current";

    private readonly AuthMode _configuredMode = AuthModeResolver.Resolve(configuration);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private AuthModeEpochSnapshot? _startupSnapshot;

    public async Task<AuthModeEpochSnapshot> EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_startupSnapshot is not null)
            return _startupSnapshot;

        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_startupSnapshot is not null)
                return _startupSnapshot;

            _startupSnapshot = await InitializeCoreAsync(ct).ConfigureAwait(false);
            return _startupSnapshot;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<bool> IsCurrentInstanceActiveAsync(CancellationToken ct = default)
    {
        var startup = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var current = await GetCurrentSnapshotAsync(ct).ConfigureAwait(false);
        return current.Epoch == startup.Epoch && current.AuthMode == startup.AuthMode;
    }

    public async Task<AuthModeEpochSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var current = await db.Set<AuthModeEpochRecord>()
            .AsNoTracking()
            .SingleAsync(x => x.Key == SingletonKey, ct)
            .ConfigureAwait(false);
        return new AuthModeEpochSnapshot(AuthModeResolver.Parse(current.AuthMode), current.Epoch);
    }

    private async Task<AuthModeEpochSnapshot> InitializeCoreAsync(CancellationToken ct)
    {
        while (true)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var current = await db.Set<AuthModeEpochRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Key == SingletonKey, ct)
                .ConfigureAwait(false);

            if (current is null)
            {
                db.Set<AuthModeEpochRecord>().Add(new AuthModeEpochRecord
                {
                    Key = SingletonKey,
                    AuthMode = AuthModeResolver.Normalize(_configuredMode),
                    Epoch = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });

                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    logger.LogInformation(
                        "Initialized shared auth mode epoch at {AuthMode}#{Epoch}.",
                        _configuredMode,
                        1);
                    return new AuthModeEpochSnapshot(_configuredMode, 1);
                }
                catch (DbUpdateException)
                {
                    continue;
                }
            }

            var currentMode = AuthModeResolver.Parse(current.AuthMode);
            if (currentMode == _configuredMode)
                return new AuthModeEpochSnapshot(currentMode, current.Epoch);

            var nextEpoch = current.Epoch + 1;
            var now = DateTimeOffset.UtcNow;
            var updated = await db.Set<AuthModeEpochRecord>()
                .Where(x => x.Key == SingletonKey && x.Epoch == current.Epoch && x.AuthMode == current.AuthMode)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.AuthMode, AuthModeResolver.Normalize(_configuredMode))
                    .SetProperty(x => x.Epoch, nextEpoch)
                    .SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);

            if (updated == 1)
            {
                logger.LogWarning(
                    "Bumped shared auth mode epoch from {OldMode}#{OldEpoch} to {NewMode}#{NewEpoch}.",
                    currentMode,
                    current.Epoch,
                    _configuredMode,
                    nextEpoch);
                return new AuthModeEpochSnapshot(_configuredMode, nextEpoch);
            }
        }
    }
}

public sealed class AuthModeEpochStartupService(
    AuthModeEpochService authModeEpochService,
    ILogger<AuthModeEpochStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var snapshot = await authModeEpochService.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Shared auth mode epoch active at {AuthMode}#{Epoch}.",
            snapshot.AuthMode,
            snapshot.Epoch);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
