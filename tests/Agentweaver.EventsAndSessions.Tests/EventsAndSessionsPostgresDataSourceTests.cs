using System.Collections.Concurrent;
using Agentweaver.EventsAndSessions;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class EventsAndSessionsPostgresDataSourceTests(SessionsPostgresFixture postgres)
{
    [Fact]
    public void RuntimeAndMigrationRequireSeparateConnectionsAndWorkloadIdentities()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EventsAndSessions"] = "Host=runtime;Username=runtime_role;Database=events",
            ["ConnectionStrings:EventsAndSessionsMigration"] = "Host=migration;Username=migration_role;Database=events",
            ["EventsAndSessions:Database:WorkloadIdentity:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["EventsAndSessions:Database:WorkloadIdentity:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["EventsAndSessions:Database:WorkloadIdentity:TokenFilePath"] =
                Path.Combine(Path.GetTempPath(), "events-runtime-token"),
            ["EventsAndSessions:Migration:WorkloadIdentity:TenantId"] = "33333333-3333-3333-3333-333333333333",
            ["EventsAndSessions:Migration:WorkloadIdentity:ClientId"] = "44444444-4444-4444-4444-444444444444",
            ["EventsAndSessions:Migration:WorkloadIdentity:TokenFilePath"] =
                Path.Combine(Path.GetTempPath(), "events-migration-token"),
        }).Build();

        var runtime = EventsAndSessionsPostgresDataSource.ReadRuntimeConnection(configuration);
        var migration = EventsAndSessionsPostgresDataSource.ReadMigrationConnection(configuration);

        Assert.NotEqual(runtime.ConnectionString, migration.ConnectionString);
        Assert.NotEqual(runtime.ClientId, migration.ClientId);
        Assert.Equal("runtime_role", new NpgsqlConnectionStringBuilder(runtime.ConnectionString).Username);
        Assert.Equal("migration_role", new NpgsqlConnectionStringBuilder(migration.ConnectionString).Username);
    }

    [Fact]
    public void RequiresExplicitConnectionAndValidWorkloadIdentitySettings()
    {
        var missing = new ConfigurationBuilder().Build();
        var connectionError = Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.ReadRuntimeConnection(missing));
        Assert.Contains("ConnectionStrings:EventsAndSessions", connectionError.Message, StringComparison.Ordinal);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EventsAndSessionsMigration"] = "Host=migration;Username=migration_role;Database=events",
            ["EventsAndSessions:Migration:WorkloadIdentity:TenantId"] = "not-a-guid",
            ["EventsAndSessions:Migration:WorkloadIdentity:ClientId"] = "44444444-4444-4444-4444-444444444444",
            ["EventsAndSessions:Migration:WorkloadIdentity:TokenFilePath"] = "relative-token-file",
        }).Build();
        var identityError = Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.ReadMigrationConnection(invalid));
        Assert.Contains("requires GUID tenant/client IDs and an absolute token-file path",
            identityError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationDoesNotBorrowRuntimeConnectionOrWorkloadIdentity()
    {
        var runtimeOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EventsAndSessions"] = "Host=runtime;Username=runtime_role;Database=events",
            ["EventsAndSessions:Database:WorkloadIdentity:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["EventsAndSessions:Database:WorkloadIdentity:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["EventsAndSessions:Database:WorkloadIdentity:TokenFilePath"] =
                Path.Combine(Path.GetTempPath(), "events-runtime-token"),
        }).Build();
        var missingMigration = Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.ReadMigrationConnection(runtimeOnly));
        Assert.Contains("ConnectionStrings:EventsAndSessionsMigration",
            missingMigration.Message, StringComparison.Ordinal);

        var mixed = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EventsAndSessions"] = "Host=runtime;Username=runtime_role;Database=events",
            ["ConnectionStrings:EventsAndSessionsMigration"] =
                "Host=migration;Username=migration_role;Database=events",
            ["EventsAndSessions:Database:WorkloadIdentity:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["EventsAndSessions:Database:WorkloadIdentity:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["EventsAndSessions:Database:WorkloadIdentity:TokenFilePath"] =
                Path.Combine(Path.GetTempPath(), "events-runtime-token"),
        }).Build();
        var missingMigrationIdentity = Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.ReadMigrationConnection(mixed));
        Assert.Contains("EventsAndSessions:Migration:WorkloadIdentity",
            missingMigrationIdentity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesPasswordFreeEntraConnectionAndRefreshesTokensForNewConnections()
    {
        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { MaxPoolSize = 2 };
        var testPassword = connection.Password
            ?? throw new InvalidOperationException("Testcontainers did not provide a PostgreSQL password.");
        connection.Remove("Password");
        var tokenRequest = 0;
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(
                testPassword,
                Interlocked.Increment(ref tokenRequest) == 1
                    ? DateTimeOffset.UtcNow.AddMinutes(-5)
                    : DateTimeOffset.UtcNow.AddMinutes(5))));
        await using var dataSource = EventsAndSessionsPostgresDataSource.Create(
            connection.ConnectionString, credential, SslMode.Disable);

        var dataSourceConnection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.True(string.IsNullOrEmpty(dataSourceConnection.Password));
        Assert.Equal(SslMode.Disable, dataSourceConnection.SslMode);

        await using (var first = await dataSource.OpenConnectionAsync())
        await using (var second = await dataSource.OpenConnectionAsync())
        {
            await using var query = new NpgsqlCommand("SELECT 1", first);
            Assert.Equal(1, await query.ExecuteScalarAsync());
            Assert.Equal(2, credential.Requests.ToArray().Length);
            await using var secondQuery = new NpgsqlCommand("SELECT 1", second);
            Assert.Equal(1, await secondQuery.ExecuteScalarAsync());
            var requests = credential.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.All(requests, request =>
                Assert.Equal(
                    "https://ossrdbms-aad.database.windows.net/.default",
                    Assert.Single(request.Scopes)));
            var tokens = credential.Tokens.ToArray();
            Assert.Equal(2, tokens.Length);
            Assert.True(tokens[0].ExpiresOn <= DateTimeOffset.UtcNow);
            Assert.True(tokens[1].ExpiresOn > DateTimeOffset.UtcNow);
        }

        await using var pooled = await dataSource.OpenConnectionAsync();
        Assert.Equal(2, credential.Requests.ToArray().Length);
    }

    [Fact]
    public async Task RejectsPasswordsAndSynchronousTokenAcquisition()
    {
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken("unused", DateTimeOffset.UtcNow.AddMinutes(5))));
        Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.Create(
                "Host=localhost;Username=runtime_role;Database=events;Password=static",
                credential));
        Assert.Throws<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.Create(
                "Host=localhost;Database=events",
                credential));

        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString);
        connection.Remove("Password");
        connection.Pooling = false;
        await using var dataSource = EventsAndSessionsPostgresDataSource.Create(
            connection.ConnectionString, credential, SslMode.Disable);
        var exception = Assert.Throws<NpgsqlException>(() => dataSource.OpenConnection());
        Assert.Contains(
            "Synchronous PostgreSQL token acquisition is disabled.",
            Assert.IsType<InvalidOperationException>(exception.InnerException).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultsToVerifiedTlsWithoutAStoredPassword()
    {
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken("unused", DateTimeOffset.UtcNow.AddMinutes(5))));
        await using var dataSource = EventsAndSessionsPostgresDataSource.Create(
            "Host=localhost;Username=runtime_role;Database=events", credential);
        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.True(string.IsNullOrEmpty(connection.Password));
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
    }

    [Fact]
    public async Task TokenAcquisitionPropagatesCancellationAndFailuresWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new FakeTokenCredential((_, token) => ValueTask.FromCanceled<AccessToken>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EventsAndSessionsPostgresDataSource.GetPostgresTokenAsync(cancelled, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, Assert.Single(cancelled.CancellationTokens));

        var failure = new InvalidOperationException("token acquisition failed");
        var failed = new FakeTokenCredential((_, _) =>
            ValueTask.FromException<AccessToken>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.GetPostgresTokenAsync(failed, CancellationToken.None).AsTask()));

        var empty = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(string.Empty, DateTimeOffset.UtcNow.AddMinutes(5))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EventsAndSessionsPostgresDataSource.GetPostgresTokenAsync(empty, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RefreshFailureDoesNotReuseAnExpiredTokenForANewConnection()
    {
        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Pooling = false };
        var testPassword = connection.Password
            ?? throw new InvalidOperationException("Testcontainers did not provide a PostgreSQL password.");
        connection.Remove("Password");
        var failure = new InvalidOperationException("token refresh failed");
        var tokenRequest = 0;
        var credential = new FakeTokenCredential((_, _) =>
            Interlocked.Increment(ref tokenRequest) == 1
                ? ValueTask.FromResult(new AccessToken(testPassword, DateTimeOffset.UtcNow.AddMinutes(-5)))
                : ValueTask.FromException<AccessToken>(failure));
        await using var dataSource = EventsAndSessionsPostgresDataSource.Create(
            connection.ConnectionString, credential, SslMode.Disable);

        await using (var initial = await dataSource.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand("SELECT 1", initial))
            Assert.Equal(1, await query.ExecuteScalarAsync());

        var exception = await Assert.ThrowsAsync<NpgsqlException>(async () =>
        {
            await using var refreshed = await dataSource.OpenConnectionAsync();
        });
        Assert.Contains("token refresh failed", exception.ToString(), StringComparison.Ordinal);
        Assert.True(credential.Requests.Count > 1);
        Assert.True(Assert.Single(credential.Tokens).ExpiresOn <= DateTimeOffset.UtcNow);
    }

    private sealed class FakeTokenCredential(
        Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire) : TokenCredential
    {
        public ConcurrentQueue<TokenRequestContext> Requests { get; } = new();
        public ConcurrentQueue<CancellationToken> CancellationTokens { get; } = new();
        public ConcurrentQueue<AccessToken> Tokens { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fake credential does not support synchronous acquisition.");

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Enqueue(requestContext);
            CancellationTokens.Enqueue(cancellationToken);
            var token = await acquire(requestContext, cancellationToken);
            Tokens.Enqueue(token);
            return token;
        }
    }
}
