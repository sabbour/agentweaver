using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerConfigurationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MissingRedemptionAudienceFailsHostStartupWithoutFallback()
    {
        await using var idp = await FakeIdentityProvider.StartAsync();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateDatabaseAsync(),
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
}
