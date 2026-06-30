using Agentweaver.Api.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RepositoryMergeLockPostgresTests(PostgresFixture pg)
{
    [Fact]
    public async Task SameRepository_IsSerializedAcrossLockInstances()
    {
        var lock1 = CreateLock(pg.ConnectionString);
        var lock2 = CreateLock(pg.ConnectionString);
        var repoPath = Path.GetFullPath("repo-a");

        using var first = await lock1.TryAcquireAsync(repoPath, TimeSpan.FromSeconds(1), CancellationToken.None);
        first.Should().NotBeNull();

        var second = await lock2.TryAcquireAsync(repoPath, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        second.Should().BeNull("a second API replica must not enter the same repository merge critical section");
    }

    [Fact]
    public async Task DifferentRepositories_CanMergeConcurrentlyAcrossLockInstances()
    {
        var lock1 = CreateLock(pg.ConnectionString);
        var lock2 = CreateLock(pg.ConnectionString);

        using var first = await lock1.TryAcquireAsync(Path.GetFullPath("repo-a"), TimeSpan.FromSeconds(1), CancellationToken.None);
        using var second = await lock2.TryAcquireAsync(Path.GetFullPath("repo-b"), TimeSpan.FromSeconds(1), CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull("repository merge locks are keyed per repository");
    }

    private static RepositoryMergeLock CreateLock(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["ConnectionStrings:Postgres"] = connectionString,
            })
            .Build();

        return new RepositoryMergeLock(configuration, NullLogger<RepositoryMergeLock>.Instance);
    }
}
