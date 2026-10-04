using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerConfigurationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MissingRuntimeConnectionStringFailsHostStartupWithoutFallback()
    {
        await using var idp = await FakeIdentityProvider.StartAsync();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateMigratedDatabaseAsync(),
            idp,
            configure: settings => settings["ConnectionStrings__IdentityBroker"] = null);

        var startupFailure = await Record.ExceptionAsync(() =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://broker.test.local"),
            });
            return Task.CompletedTask;
        });

        Assert.NotNull(startupFailure);
        Assert.Contains("ConnectionStrings:IdentityBroker", startupFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRedemptionAudienceFailsHostStartupWithoutFallback()
    {
        await using var idp = await FakeIdentityProvider.StartAsync();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateMigratedDatabaseAsync(),
            idp,
            configure: settings => settings.Remove("IdentityBroker__SecretRedemption__Audience"));

        var startupFailure = await Record.ExceptionAsync(() =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://broker.test.local"),
            });
            return Task.CompletedTask;
        });

        Assert.NotNull(startupFailure);
        Assert.Contains("Audience", startupFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDatabaseSchemaFailsHostStartupAndDoesNotAutoMigrate()
    {
        await using var idp = await FakeIdentityProvider.StartAsync();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateDatabaseAsync(),
            idp);

        var startupFailure = await Record.ExceptionAsync(() =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://broker.test.local"),
            });
            return Task.CompletedTask;
        });

        Assert.NotNull(startupFailure);
        Assert.Contains("approved Identity broker migration Job", startupFailure.ToString(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(factory.ConnectionStringForRestartTest);
        await connection.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT pg_catalog.to_regnamespace('identity_broker') IS NULL", connection);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
    }
}
