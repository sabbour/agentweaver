using Agentweaver.Projects.Config;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class PlatformAdminBootstrapCommandTests
{
    private const string TestIssuer = "https://projects-config.test/";

    [Fact]
    public async Task RefusesWhenApprovedSetupConfigurationIsMissing()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PlatformAdminBootstrapCommand.RunAsync(configuration));

        Assert.Contains("ProjectsConfig:Authentication:Authority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenApprovedSetupConfigurationIsPartial()
    {
        var configuration = CreateConfiguration(
            ("ProjectsConfig:Authentication:Authority", TestIssuer),
            ("ProjectsConfig:PrivilegedSetup:ApprovedIssuer", TestIssuer),
            ("ProjectsConfig:PrivilegedSetup:ApprovedSubject", "approved-subject"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PlatformAdminBootstrapCommand.RunAsync(configuration));

        Assert.Contains("ProjectsConfig:PrivilegedSetup:ApprovedTenantId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsOnlyTheApprovedIssuerSubjectAndTenant()
    {
        var configuration = CreateConfiguration(
            ("ProjectsConfig:Authentication:Authority", TestIssuer),
            ("ProjectsConfig:PrivilegedSetup:ApprovedIssuer", TestIssuer),
            ("ProjectsConfig:PrivilegedSetup:ApprovedSubject", "approved-subject"),
            ("ProjectsConfig:PrivilegedSetup:ApprovedTenantId", "approved-tenant"));

        var identity = PlatformAdminBootstrapCommand.ReadApprovedIdentity(configuration);

        Assert.Equal(TestIssuer, identity.Issuer);
        Assert.Equal("approved-subject", identity.Subject);
        Assert.Equal("approved-tenant", identity.TenantId);
    }

    [Fact]
    public void BootstrapArgumentMustBeUsedAlone()
    {
        Assert.True(PlatformAdminBootstrapCommand.IsCommandAlone(
            ["--bootstrap-first-platform-admin"]));
        Assert.False(PlatformAdminBootstrapCommand.IsCommandAlone(
            ["--bootstrap-first-platform-admin", "--migrate"]));
        Assert.False(PlatformAdminBootstrapCommand.IsCommandAlone(["--migrate"]));
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();
}
