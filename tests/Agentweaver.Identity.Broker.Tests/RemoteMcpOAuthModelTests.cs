using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpOAuthModelTests
{
    [Fact]
    public void DedicatedConnectionAndConsentTablesKeepOnlyVersionedSecretReferences()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var connection = model.FindEntityType(typeof(RemoteMcpOAuthConnectionRecord))!;
        var consent = model.FindEntityType(typeof(RemoteMcpOAuthConsentRecord))!;

        Assert.Equal("remote_mcp_oauth_connections", connection.GetTableName());
        Assert.Equal(IdentityBrokerDbContext.Schema, connection.GetSchema());
        Assert.Equal("remote_mcp_oauth_consents", consent.GetTableName());
        Assert.Equal(IdentityBrokerDbContext.Schema, consent.GetSchema());
        Assert.True(connection.FindProperty(nameof(RemoteMcpOAuthConnectionRecord.ConnectionRevision))!
            .IsConcurrencyToken);
        Assert.True(consent.FindProperty(nameof(RemoteMcpOAuthConsentRecord.Revision))!
            .IsConcurrencyToken);
        Assert.Contains(connection.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(BrokerUser) &&
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(RemoteMcpOAuthConnectionRecord.OwnerId)]));
        Assert.Contains(consent.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(RemoteMcpOAuthConnectionRecord));
        Assert.Contains(connection.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
            [
                nameof(RemoteMcpOAuthConnectionRecord.OwnerId),
                nameof(RemoteMcpOAuthConnectionRecord.TenantId),
                nameof(RemoteMcpOAuthConnectionRecord.ProjectId),
                nameof(RemoteMcpOAuthConnectionRecord.ConnectionId)
            ]));
        Assert.Contains(connection.GetCheckConstraints(), check =>
            check.Name == "ck_remote_mcp_oauth_connection_state");
        Assert.Contains(consent.GetCheckConstraints(), check =>
            check.Name == "ck_remote_mcp_oauth_consent_state");

        Assert.DoesNotContain(connection.GetProperties(), property =>
            property.Name is "AccessToken" or "RefreshToken");
        Assert.DoesNotContain(consent.GetProperties(), property =>
            property.Name is "Verifier" or "AuthorizationCode");
        Assert.Contains(connection.GetProperties(), property =>
            property.Name == nameof(RemoteMcpOAuthConnectionRecord.AccessTokenSecretVersion));
        Assert.Contains(consent.GetProperties(), property =>
            property.Name == nameof(RemoteMcpOAuthConsentRecord.VerifierSecretVersion));
    }

    private static IdentityBrokerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql("Host=localhost;Database=identity_broker_model;Username=unused;Password=unused")
            .Options;
        return new IdentityBrokerDbContext(options);
    }
}
