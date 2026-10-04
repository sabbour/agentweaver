using System.Collections.Concurrent;
using Azure.Core;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerPostgresDataSourceTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task AcquiresEntraPasswordForEachPhysicalConnectionAndReusesPooledConnections()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync())
        {
            MaxPoolSize = 2,
        };
        connection.Remove("Password");
        var generatedPassword = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Password
            ?? throw new InvalidOperationException("Testcontainers did not provide a generated PostgreSQL password.");
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(generatedPassword, DateTimeOffset.UtcNow.AddMinutes(5))));
        await using var dataSource = IdentityBrokerPostgresDataSource.Create(
            connection.ConnectionString, credential, SslMode.Disable);

        await using (var first = await dataSource.OpenConnectionAsync())
        await using (var second = await dataSource.OpenConnectionAsync())
        {
            Assert.Equal(2, credential.Requests.Count);
            Assert.All(credential.Requests, request =>
                Assert.Equal("https://ossrdbms-aad.database.windows.net/.default", Assert.Single(request.Scopes)));
        }

        await using var pooledCheckout = await dataSource.OpenConnectionAsync();
        Assert.Equal(2, credential.Requests.Count);
    }

    [Fact]
    public async Task TokenAcquisitionPropagatesCancellationAndFailuresWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCredential = new FakeTokenCredential((_, token) =>
            ValueTask.FromCanceled<AccessToken>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IdentityBrokerPostgresDataSource.GetPostgresTokenAsync(cancelledCredential, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, Assert.Single(cancelledCredential.CancellationTokens));

        var failure = new InvalidOperationException("generated token failure");
        var failedCredential = new FakeTokenCredential((_, _) =>
            ValueTask.FromException<AccessToken>(failure));
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerPostgresDataSource.GetPostgresTokenAsync(failedCredential, CancellationToken.None).AsTask());
        Assert.Same(failure, observed);

        var emptyCredential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(string.Empty, DateTimeOffset.UtcNow.AddMinutes(5))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerPostgresDataSource.GetPostgresTokenAsync(emptyCredential, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RejectsStaticPasswordsAndSynchronousConnections()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var password = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Password
            ?? throw new InvalidOperationException("Testcontainers did not provide a generated PostgreSQL password.");
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(password, DateTimeOffset.UtcNow.AddMinutes(5))));

        connection.Password = "generated-test-password";
        Assert.Throws<InvalidOperationException>(() =>
            IdentityBrokerPostgresDataSource.Create(connection.ConnectionString, credential, SslMode.Disable));

        connection.Remove("Password");
        connection.Pooling = false;
        await using var dataSource = IdentityBrokerPostgresDataSource.Create(
            connection.ConnectionString, credential, SslMode.Disable);
        var exception = Assert.Throws<NpgsqlException>(() =>
        {
            using var opened = dataSource.OpenConnection();
        });
        Assert.Contains(
            "Synchronous PostgreSQL token acquisition is disabled.",
            Assert.IsType<InvalidOperationException>(exception.InnerException).Message,
            StringComparison.Ordinal);
    }

    private sealed class FakeTokenCredential(
        Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire) : TokenCredential
    {
        public ConcurrentQueue<TokenRequestContext> Requests { get; } = new();
        public ConcurrentQueue<CancellationToken> CancellationTokens { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fake credential does not support synchronous acquisition.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Enqueue(requestContext);
            CancellationTokens.Enqueue(cancellationToken);
            return acquire(requestContext, cancellationToken);
        }
    }
}
