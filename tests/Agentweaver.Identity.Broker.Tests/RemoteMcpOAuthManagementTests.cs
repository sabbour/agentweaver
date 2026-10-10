using System.Text.Json;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpOAuthManagementTests
{
    [Fact]
    public void CurrentConfigurationIsOnlyCorrelationAndManagementStatusNeverAuthorizesCredentialUse()
    {
        var row = Connection();
        var configuration = new RemoteMcpOAuthManagementService.CurrentConfiguration(
            row.ConfigurationRevision,
            row.EnvironmentConfigurationHash,
            row.EndpointUri,
            row.ResourceUri,
            "delegatedOAuth",
            "streamableHttp20250618",
            row.IdentityBindingReference)
        {
            ConnectionState = "Draft"
        };

        var status = RemoteMcpOAuthManagementService.ToStatus(row, configuration);

        Assert.True(status.CurrentConfigurationMatches);
        Assert.False(status.CredentialUseAvailable);
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain(row.AccessTokenSecretId!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(row.AccessTokenSecretVersion!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(row.RefreshTokenSecretId!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(row.RefreshTokenSecretVersion!, json, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrChangedEnvironmentReferenceDoesNotMatchCurrentConfiguration()
    {
        var row = Connection();
        var current = new RemoteMcpOAuthManagementService.CurrentConfiguration(
            row.ConfigurationRevision,
            row.EnvironmentConfigurationHash,
            row.EndpointUri,
            row.ResourceUri,
            "delegatedOAuth",
            "streamableHttp20250618",
            row.IdentityBindingReference)
        {
            ConnectionState = "Draft"
        };

        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { IdentityBindingReference = null }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { IdentityBindingReference = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { ConnectionState = "Removed" }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { ConnectionState = "unknown" }));
    }

    private static RemoteMcpOAuthConnectionRecord Connection() =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OwnerIssuer = "https://identity.example.test/",
            OwnerActorId = "actor-1",
            TenantId = "tenant-1",
            ProjectId = "project-1",
            ConnectionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ConfigurationRevision = 1,
            EnvironmentConfigurationHash = new string('a', 64),
            IdentityBindingReference = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            EndpointUri = "https://mcp.example.test/",
            ResourceUri = "https://mcp.example.test/resource",
            IssuerUri = "https://issuer.example.test/",
            RedirectUri = "https://identity.example.test/oauth/callback",
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ScopesJson = "[\"tools.read\"]",
            BindingHash = new string('b', 64),
            ConnectionRevision = 3,
            CredentialRevision = 2,
            State = RemoteMcpOAuthConnectionState.Authorized,
            AccessTokenSecretId = "remote-mcp-access",
            AccessTokenSecretVersion = "v1",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            RefreshTokenSecretId = "remote-mcp-refresh",
            RefreshTokenSecretVersion = "v1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
