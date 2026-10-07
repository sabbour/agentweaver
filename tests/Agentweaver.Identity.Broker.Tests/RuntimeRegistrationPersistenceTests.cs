extern alias OrchestratorHost;

using Agentweaver.Identity;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class RuntimeRegistrationPersistenceTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task RegistrationCreatesItsOwnIdentityDeduplicatesAndRetainsImmutableRevocationAcrossRestart()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await CreateSchemaAsync(source);
        var store = new RuntimeRegistrationStore(source, "runtime_test", TimeProvider.System);
        var binding = Binding();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        RuntimeRegistration registered = await store.RegisterAsync(binding, expiresAt, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, registered.RuntimeInstanceId);
        Assert.Equal(registered, await store.RegisterAsync(binding, expiresAt, CancellationToken.None));
        Assert.Equal(registered, await new RuntimeRegistrationStore(source, "runtime_test", TimeProvider.System)
            .ReadAsync(registered.RuntimeInstanceId, CancellationToken.None));
        var revoked = await store.RevokeAsync(registered.RuntimeInstanceId, 1, CancellationToken.None);
        Assert.Equal(RuntimeRegistrationState.Revoked, revoked.State);
        Assert.Equal(2, revoked.Revision);
        Assert.Equal(revoked, await new RuntimeRegistrationStore(source, "runtime_test", TimeProvider.System)
            .ReadAsync(registered.RuntimeInstanceId, CancellationToken.None));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.RevokeAsync(registered.RuntimeInstanceId, 1, CancellationToken.None));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.RegisterAsync(binding, expiresAt, CancellationToken.None));

        await using var connection = await source.OpenConnectionAsync();
        await using var mutate = new NpgsqlCommand("""
            UPDATE runtime_test.runtime_registration_revisions SET state = 1 WHERE revision = 1
            """, connection);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
        Assert.Contains("append-only", immutable.MessageText);
        await using var skip = new NpgsqlCommand("""
            UPDATE runtime_test.runtime_registration_heads SET current_revision = 9
            """, connection);
        var skipped = await Assert.ThrowsAsync<PostgresException>(() => skip.ExecuteNonQueryAsync());
        Assert.Contains("advance by one", skipped.MessageText);
    }

    [Fact]
    public async Task ConcurrentExactRegistrationReturnsOneServerIdentity()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await CreateSchemaAsync(source);
        var binding = Binding();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var registrations = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ =>
                new RuntimeRegistrationStore(source, "runtime_test", TimeProvider.System)
                    .RegisterAsync(binding, expiresAt, CancellationToken.None)));
        Assert.All(registrations, registration => Assert.Equal(registrations[0], registration));
        await using var connection = await source.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM runtime_test.runtime_registration_revisions", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    private static async Task CreateSchemaAsync(NpgsqlDataSource source)
    {
        await using var resource = typeof(RuntimeRegistrationPersistenceTests).Assembly
            .GetManifestResourceStream("Agentweaver.Identity.Broker.Tests.runtime_registration.sql")
            ?? throw new InvalidOperationException("The owned runtime registration schema is missing.");
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace("{schema}", "\"runtime_test\"", StringComparison.Ordinal);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("CREATE SCHEMA runtime_test;" + sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static RuntimeBinding Binding() => new(
        "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
        "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
        "environment", "placement-uid", 1, "profile",
        new Uri("https://runtime.test/configure"), new Uri("https://orchestrator.test/runtime/observations"))
    {
        EnvironmentCurrentFencingGeneration = 4,
        EnvironmentProviderFencingGeneration = 7
    };
}
