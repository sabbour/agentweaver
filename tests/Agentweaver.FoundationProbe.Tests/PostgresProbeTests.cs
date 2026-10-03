using System.Security.Cryptography;
using Agentweaver.FoundationProbe;
using Agentweaver.Persistence.Postgres;
using Azure.Core;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class PostgresProbeTests(ProbePostgresFixture fixture) : IClassFixture<ProbePostgresFixture>
{
    [Fact]
    public async Task UsesAsyncTokenProviderForEachPhysicalConnectionAndCommitsOwnedInboxOutboxEffects()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await fixture.PrepareRuntimeRoleAsync(password);
        var adminConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var target = ProbeFixtures.Target() with
        {
            Runtime = ProbeFixtures.Target().Runtime with { DatabaseName = adminConnection.Database! },
            FoundationResources = ProbeFixtures.Target().FoundationResources with { PostgresHost = adminConnection.Host! },
        };
        var credential = new RecordingTokenCredential(password);
        await using var dataSource = AzureProbeOperations.CreatePostgresDataSource(
            target, credential, pooling: false, sslMode: SslMode.Disable, port: adminConnection.Port);

        await using (await dataSource.OpenConnectionAsync()) { }
        await using (await dataSource.OpenConnectionAsync()) { }
        Assert.Equal(2, credential.Calls);
        Assert.All(credential.Scopes, scopes =>
            Assert.Contains("https://ossrdbms-aad.database.windows.net/.default", scopes));

        var probe = new PostgresProbe(dataSource, new PostgresOutbox(dataSource, target.Runtime.SchemaName));
        var nonce = new string('e', 32);
        var result = await probe.RunAsync(target, ProbeFixtures.Source, nonce, CancellationToken.None);

        Assert.True(result.TransactionCommitted);
        Assert.Equal("foundation_probe_runtime", result.RuntimeRole);
        Assert.Equal("foundation_probe", result.SchemaName);
        Assert.Equal(nonce, result.InboxMessageId);
        Assert.Equal("Admitted", result.InboxDisposition);
        Assert.True(result.OutboxSequence > 0);
        Assert.Equal(3, credential.Calls);

        await using var verify = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM foundation_probe.probe_effects WHERE nonce = @nonce) = 1
                AND (SELECT count(*) FROM foundation_probe.consumer_inbox_receipts
                    WHERE consumer_id = 'foundation-probe' AND message_id = @nonce) = 1
                AND (SELECT count(*) FROM foundation_probe.outbox_events
                    WHERE id = @event_id) = 1
            """, verify);
        command.Parameters.AddWithValue("nonce", NpgsqlDbType.Char, nonce);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, Guid.Parse(result.OutboxEventId));
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AsyncTokenFailureAndCancellationArePropagatedWithoutFallback()
    {
        var target = ProbeFixtures.Target();
        var failure = new RecordingTokenCredential(null) { Failure = new InvalidOperationException("token unavailable") };
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AzureProbeOperations.GetPostgresTokenAsync(failure, CancellationToken.None));
        Assert.Equal("token unavailable", rejected.Message);
        Assert.Equal(1, failure.Calls);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new RecordingTokenCredential(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AzureProbeOperations.GetPostgresTokenAsync(cancelled, cancellation.Token));
        Assert.Equal(0, cancelled.Calls);
    }
}

public sealed class ProbePostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public string ConnectionString => _container.GetConnectionString();
    public NpgsqlDataSource AdminDataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        AdminDataSource = NpgsqlDataSource.Create(ConnectionString);
        var outbox = new PostgresOutbox(AdminDataSource, "foundation_probe");
        await outbox.InitializeAsync();

        await using var connection = await AdminDataSource.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            CREATE ROLE foundation_probe_runtime;
            GRANT USAGE ON SCHEMA foundation_probe TO foundation_probe_runtime;
            GRANT SELECT, INSERT, UPDATE ON TABLE foundation_probe.outbox_streams,
                foundation_probe.outbox_events TO foundation_probe_runtime;
            GRANT SELECT, INSERT ON TABLE foundation_probe.consumer_inbox_receipts TO foundation_probe_runtime;
            REVOKE ALL ON TABLE foundation_probe.outbox_schema_migrations FROM foundation_probe_runtime;
            """);
        var migration = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "schema", "001_probe_effects.sql"));
        await ExecuteAsync(connection, migration);
    }

    public async Task PrepareRuntimeRoleAsync(string password)
    {
        await using var connection = await AdminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER ROLE foundation_probe_runtime LOGIN PASSWORD '{password}'", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (AdminDataSource is not null)
            await AdminDataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (statement.Length == 0) continue;
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

internal sealed class RecordingTokenCredential(string? token) : TokenCredential
{
    public int Calls { get; private set; }
    public List<string[]> Scopes { get; } = [];
    public Exception? Failure { get; init; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The PostgreSQL probe must use the asynchronous token callback.");

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Scopes.Add(requestContext.Scopes.ToArray());
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(new AccessToken(token ?? "", DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
