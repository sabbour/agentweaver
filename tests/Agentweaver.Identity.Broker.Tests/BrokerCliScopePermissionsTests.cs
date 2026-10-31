using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class BrokerCliScopePermissionsTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MigrationAddsOnlyWildcardMarkerOnceAndPreservesLegacyRegistration()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var provider = BrokerCliScopePermissionTestSupport.CreateStoreProvider(connectionString);
        using var scope = provider.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var prior = BrokerCliScopePermissions.CreatePreviousDescriptor();
        prior.DisplayName = "Preserved CLI registration";
        prior.Properties["operator-note"] = JsonDocument.Parse("\"keep-property\"").RootElement.Clone();
        prior.Settings["operator-note"] = "keep-setting";
        var application = await applications.CreateAsync(prior);
        var applicationId = await applications.GetIdAsync(application);

        await BrokerCliScopePermissions.MigrateAsync(applications, CancellationToken.None);
        var migrated = await ReadCliApplicationAsync(applications);

        Assert.Equal(applicationId, await applications.GetIdAsync(
            (await applications.FindByClientIdAsync(BrokerCliScopePermissions.ClientId))!));
        Assert.Equal(prior.ClientType, migrated.ClientType);
        Assert.Equal(prior.ConsentType, migrated.ConsentType);
        Assert.Equal(prior.DisplayName, migrated.DisplayName);
        Assert.True(prior.RedirectUris.SetEquals(migrated.RedirectUris));
        Assert.True(prior.Requirements.SetEquals(migrated.Requirements));
        Assert.Equal(prior.Permissions.Count + 1, migrated.Permissions.Count);
        Assert.True(prior.Permissions.IsSubsetOf(migrated.Permissions));
        Assert.Contains(BrokerCliScopePermissions.AllRegisteredScopes, migrated.Permissions);
        Assert.Equal("keep-property", migrated.Properties["operator-note"].GetString());
        Assert.Equal("keep-setting", migrated.Settings["operator-note"]);

        await BrokerCliScopePermissions.MigrateAsync(applications, CancellationToken.None);
        var migratedAgain = await ReadCliApplicationAsync(applications);
        Assert.True(migrated.Permissions.SetEquals(migratedAgain.Permissions));
        Assert.Equal(applicationId, await applications.GetIdAsync(
            (await applications.FindByClientIdAsync(BrokerCliScopePermissions.ClientId))!));
    }

    [Fact]
    public async Task MigrationRejectsUnexpectedPermissionWithoutChangingRegistration()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var provider = BrokerCliScopePermissionTestSupport.CreateStoreProvider(connectionString);
        using var scope = provider.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var prior = BrokerCliScopePermissions.CreatePreviousDescriptor();
        prior.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "unapproved.scope");
        await applications.CreateAsync(prior);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrokerCliScopePermissions.MigrateAsync(applications, CancellationToken.None));

        Assert.Contains("exact prior public CLI registration", exception.Message);
        var unchanged = await ReadCliApplicationAsync(applications);
        Assert.Equal(prior.Permissions, unchanged.Permissions);
        Assert.DoesNotContain(BrokerCliScopePermissions.AllRegisteredScopes, unchanged.Permissions);
    }

    [Fact]
    public async Task MigrationDoesNotCreateAMissingCliApplication()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var provider = BrokerCliScopePermissionTestSupport.CreateStoreProvider(connectionString);
        using var scope = provider.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrokerCliScopePermissions.MigrateAsync(applications, CancellationToken.None));

        Assert.Null(await applications.FindByClientIdAsync(BrokerCliScopePermissions.ClientId));
    }

    private static async Task<OpenIddictApplicationDescriptor> ReadCliApplicationAsync(
        IOpenIddictApplicationManager applications)
    {
        var application = await applications.FindByClientIdAsync(BrokerCliScopePermissions.ClientId)
            ?? throw new InvalidOperationException("The test CLI application was not persisted.");
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        return descriptor;
    }
}

internal static class BrokerCliScopePermissionTestSupport
{
    public static async Task ProvisionAndMigrateLegacyCliClientAsync(string connectionString)
    {
        await using var provider = CreateStoreProvider(connectionString);
        using var scope = provider.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(BrokerCliScopePermissions.ClientId) is null)
            await applications.CreateAsync(BrokerCliScopePermissions.CreatePreviousDescriptor());
        await BrokerCliScopePermissions.MigrateAsync(applications, CancellationToken.None);
    }

    public static ServiceProvider CreateStoreProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<IdentityBrokerDbContext>(options =>
            options.UseNpgsql(connectionString).UseOpenIddict());
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore()
                .UseDbContext<IdentityBrokerDbContext>());
        return services.BuildServiceProvider();
    }
}
