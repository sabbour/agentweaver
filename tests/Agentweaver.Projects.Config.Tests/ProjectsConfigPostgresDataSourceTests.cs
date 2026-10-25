using System.Collections.Concurrent;
using Agentweaver.Projects.Config;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigPostgresDataSourceTests
{
    [Fact]
    public async Task MigrationConnectionAcceptsShortLivedTokenWithoutWorkloadIdentity()
    {
        const string token = "test-postgres-access-token";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProjectsConfigMigration"] =
                    "Host=postgres.example.test;Port=5432;Database=agentweaver;Username=projects_config_owner;" +
                    "GSS Encryption Mode=Disable",
                ["ProjectsConfig:Migration:AccessToken"] = token,
            })
            .Build();

        await using var dataSource =
            ProjectsConfigPostgresDataSource.CreateMigrationDataSource(configuration);

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Equal("projects_config_owner", connection.Username);
        Assert.Null(connection.Password);
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
        Assert.Equal("Disable", connection["GSS Encryption Mode"].ToString());
    }

    [Fact]
    public async Task MigrationConnectionUsesExplicitWorkloadIdentityWhenNoOperatorTokenIsSet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProjectsConfigMigration"] =
                    "Host=postgres.example.test;Port=5432;Database=agentweaver;Username=projects_config_owner",
                ["ProjectsConfig:Migration:WorkloadIdentity:TenantId"] =
                    "11111111-1111-1111-1111-111111111111",
                ["ProjectsConfig:Migration:WorkloadIdentity:ClientId"] =
                    "22222222-2222-2222-2222-222222222222",
                ["ProjectsConfig:Migration:WorkloadIdentity:TokenFilePath"] =
                    Path.GetFullPath(Path.Combine(Path.GetTempPath(), "projects-config-token")),
            })
            .Build();

        await using var dataSource =
            ProjectsConfigPostgresDataSource.CreateMigrationDataSource(configuration);

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Equal("projects_config_owner", connection.Username);
        Assert.True(string.IsNullOrEmpty(connection.Password));
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
    }

    [Fact]
    public void MigrationConnectionRejectsBothCredentialSources()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProjectsConfigMigration"] =
                    "Host=postgres.example.test;Port=5432;Database=agentweaver;Username=projects_config_owner",
                ["ProjectsConfig:Migration:AccessToken"] = "test-postgres-access-token",
                ["ProjectsConfig:Migration:WorkloadIdentity:TenantId"] =
                    "11111111-1111-1111-1111-111111111111",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectsConfigPostgresDataSource.CreateMigrationDataSource(configuration));

        Assert.Contains("Configure either", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsCurrentEntraTokenForPostgresScope()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var expiration = now.AddMinutes(5);
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken("postgres-token", expiration)));

        var token = await ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
            credential, CancellationToken.None, clock);

        Assert.Equal("postgres-token", token);
        Assert.Equal(
            "https://ossrdbms-aad.database.windows.net/.default",
            Assert.Single(Assert.Single(credential.Requests).Scopes));
    }

    [Fact]
    public async Task RejectsEmptyAndExpiredEntraTokens()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var emptyCredential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken(string.Empty, now.AddMinutes(5))));
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                emptyCredential, CancellationToken.None, clock).AsTask());
        Assert.Contains("empty token", empty.Message, StringComparison.Ordinal);

        var expiredCredential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken("expired-token", now.AddTicks(-1))));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                expiredCredential, CancellationToken.None, clock).AsTask());
        Assert.Contains("expired token", expired.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsPreviouslyValidTokenAfterClockAdvancesPastExpiry()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var expiration = now.AddMinutes(5);
        var credential = new FakeTokenCredential((_, _) =>
            ValueTask.FromResult(new AccessToken("postgres-token", expiration)));

        Assert.Equal(
            "postgres-token",
            await ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                credential, CancellationToken.None, clock));

        clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                credential, CancellationToken.None, clock).AsTask());

        Assert.Contains("expired token", expired.Message, StringComparison.Ordinal);
        Assert.Equal(2, credential.Requests.Count);
    }

    [Fact]
    public async Task PropagatesCredentialFailuresAndCancellation()
    {
        var expected = new InvalidOperationException("credential unavailable");
        var failedCredential = new FakeTokenCredential((_, _) =>
            ValueTask.FromException<AccessToken>(expected));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                failedCredential, CancellationToken.None, TimeProvider.System).AsTask());
        Assert.Same(expected, failure);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCredential = new FakeTokenCredential((_, token) =>
            ValueTask.FromCanceled<AccessToken>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProjectsConfigPostgresDataSource.GetPostgresTokenAsync(
                cancelledCredential, cancellation.Token, TimeProvider.System).AsTask());
        Assert.Equal(cancellation.Token, Assert.Single(cancelledCredential.CancellationTokens));
    }

    private sealed class FakeTokenCredential(
        Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire) : TokenCredential
    {
        public ConcurrentQueue<TokenRequestContext> Requests { get; } = new();
        public ConcurrentQueue<CancellationToken> CancellationTokens { get; } = new();

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fake credential does not support synchronous acquisition.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(requestContext);
            CancellationTokens.Enqueue(cancellationToken);
            return acquire(requestContext, cancellationToken);
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }
}
