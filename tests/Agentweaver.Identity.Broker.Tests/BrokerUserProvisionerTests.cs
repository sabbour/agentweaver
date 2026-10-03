using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class BrokerUserProvisionerTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task ConcurrentFirstLogins_ReturnSameDurableUserAndDetachLosingInserts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(connectionString).UseOpenIddict().Options;
        await using (var setup = new IdentityBrokerDbContext(options))
            await setup.Database.MigrateAsync();

        const int callers = 8;
        var barrier = new InsertBarrier(callers);
        var concurrentOptions = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(connectionString).UseOpenIddict().AddInterceptors(barrier).Options;
        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(async index =>
        {
            await using var db = new IdentityBrokerDbContext(concurrentOptions);
            var user = await new BrokerUserProvisioner(db, TimeProvider.System).ProvisionAsync(
                "https://issuer.test", "same-subject", $"Caller {index}", null, default);
            Assert.Single(db.ChangeTracker.Entries<BrokerUser>());
            Assert.Equal(EntityState.Unchanged, db.Entry(user).State);
            return user.Id;
        }));
        Assert.Single(results.Distinct());
        await using var verify = new IdentityBrokerDbContext(options);
        Assert.Equal(results[0], (await verify.Users.SingleAsync()).Id);
    }

    [Fact]
    public async Task CancellationAndUnrelatedDatabaseFailures_AreNotRecovered()
    {
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync()).UseOpenIddict().Options;
        await using var db = new IdentityBrokerDbContext(options);
        await db.Database.MigrateAsync();
        var provisioner = new BrokerUserProvisioner(db, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.ProvisionAsync("https://issuer.test", "subject", null, null, cancellation.Token));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            provisioner.ProvisionAsync("https://issuer.test", "subject", new string('x', 257), null, default));
        Assert.Empty(await db.Users.AsNoTracking().ToListAsync());
    }

    private sealed class InsertBarrier(int callers) : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BrokerUser>().Any(entry => entry.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref _arrived) == callers) _ready.SetResult();
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
