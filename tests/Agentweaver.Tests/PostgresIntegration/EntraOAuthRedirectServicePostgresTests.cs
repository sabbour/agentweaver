using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EntraOAuthRedirectServicePostgresTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task BeginAuthorizationAsync_WhenRequiredConfigurationIsMissing_DoesNotPersistState()
    {
        foreach (var configuration in new[]
        {
            CreateConfiguration(missingAuthority: true),
            CreateConfiguration(missingRedirectUri: true),
        })
        {
            using var services = CreateServices();
            var service = CreateRedirectService(services, configuration);

            await using var beforeDb = await postgres.CreateDbContextAsync();
            var statesBefore = await beforeDb.EntraOAuthStates.CountAsync();
            var begin = () => service.BeginAuthorizationAsync();

            await begin.Should().ThrowAsync<EntraNotConfiguredException>();

            await using var afterDb = await postgres.CreateDbContextAsync();
            (await afterDb.EntraOAuthStates.CountAsync()).Should().Be(statesBefore);
        }
    }

    [PostgresFact]
    public async Task BeginAuthorizationAsync_WhenConfigured_PersistsStateAndReturnsAuthorizationUrl()
    {
        using var services = CreateServices();
        var configuration = CreateConfiguration();
        var service = CreateRedirectService(services, configuration);

        await using var beforeDb = await postgres.CreateDbContextAsync();
        var statesBefore = await beforeDb.EntraOAuthStates.CountAsync();

        var authorizationUrl = await service.BeginAuthorizationAsync();

        authorizationUrl.Should().Contain("/oauth2/v2.0/authorize")
            .And.Contain("redirect_uri=");
        await using var afterDb = await postgres.CreateDbContextAsync();
        (await afterDb.EntraOAuthStates.CountAsync()).Should().Be(statesBefore + 1);
    }

    private ServiceProvider CreateServices() =>
        new ServiceCollection()
            .AddDbContext<MemoryDbContext>(options => options.UseNpgsql(
                postgres.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")))
            .AddHttpClient()
            .AddLogging()
            .BuildServiceProvider();

    private static IConfiguration CreateConfiguration(
        bool missingAuthority = false,
        bool missingRedirectUri = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Entra:ClientId"] = "11111111-2222-3333-4444-555555555555",
            ["Auth:Entra:TenantId"] = "test-tenant",
            ["Auth:Entra:Authority"] = "https://login.microsoftonline.com/test-tenant/v2.0",
            ["Auth:Entra:RedirectUri"] = "https://agentweaver.example.test/auth/entra/callback",
        };
        if (missingAuthority)
        {
            settings["Auth:Entra:TenantId"] = string.Empty;
            settings["Auth:Entra:Authority"] = string.Empty;
        }
        if (missingRedirectUri)
            settings["Auth:Entra:RedirectUri"] = string.Empty;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static EntraOAuthRedirectService CreateRedirectService(
        IServiceProvider services,
        IConfiguration configuration)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        return new EntraOAuthRedirectService(
            configuration,
            new EntraAccessTokenValidator(
                configuration,
                httpClientFactory,
                loggerFactory.CreateLogger<EntraAccessTokenValidator>()),
            httpClientFactory,
            services.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<EntraOAuthRedirectService>());
    }
}
