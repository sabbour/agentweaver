using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpCredentialValidityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AuthorizedCurrentCredentialIsValidAndCarriesNoSecretReferences()
    {
        var row = Connection();

        var result = Evaluate(row, Copy(row), Current(row));

        Assert.True(result.Valid);
        Assert.Equal(RemoteMcpCredentialDenial.None, result.Denial);
        Assert.Equal(row.CredentialRevision, result.CredentialRevision);
        Assert.Equal(row.ConfigurationRevision, result.ConfigurationRevision);
        Assert.Equal(row.EnvironmentConfigurationHash, result.ConfigurationSha256);
        Assert.Equal(row.AccessTokenExpiresAt, result.ExpiresAt);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(row.AccessTokenSecretId!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(row.RefreshTokenSecretId!, json, StringComparison.Ordinal);
        Assert.Contains("\"Denial\":\"None\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RemoteMcpOAuthConnectionState.NotConnected, RemoteMcpCredentialDenial.NotConnected)]
    [InlineData(RemoteMcpOAuthConnectionState.PendingConsent, RemoteMcpCredentialDenial.ConsentPending)]
    [InlineData(RemoteMcpOAuthConnectionState.RefreshInProgress, RemoteMcpCredentialDenial.RefreshPending)]
    [InlineData(RemoteMcpOAuthConnectionState.RefreshIndeterminate, RemoteMcpCredentialDenial.RefreshIndeterminate)]
    [InlineData(RemoteMcpOAuthConnectionState.Disconnected, RemoteMcpCredentialDenial.Disconnected)]
    [InlineData(RemoteMcpOAuthConnectionState.Revoked, RemoteMcpCredentialDenial.Revoked)]
    public void NonAuthorizedStatesDenyUse(
        RemoteMcpOAuthConnectionState state,
        RemoteMcpCredentialDenial expected)
    {
        var row = Connection();
        row.State = state;

        var result = Evaluate(row, Copy(row), Current(row));

        Assert.False(result.Valid);
        Assert.Equal(expected, result.Denial);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void ExpiredOrMissingAccessTokenDeniesUse()
    {
        var expired = Connection();
        expired.AccessTokenExpiresAt = Now;
        Assert.Equal(RemoteMcpCredentialDenial.Expired,
            Evaluate(expired, Copy(expired), Current(expired)).Denial);

        var missing = Connection();
        missing.AccessTokenSecretVersion = null;
        Assert.Equal(RemoteMcpCredentialDenial.NotConnected,
            Evaluate(missing, Copy(missing), Current(missing)).Denial);
    }

    [Fact]
    public void ChangedOrRemovedConfigurationDeniesUse()
    {
        var row = Connection();
        var current = Current(row);

        Assert.Equal(RemoteMcpCredentialDenial.ConnectionRemoved,
            Evaluate(row, Copy(row), null).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.ConnectionRemoved,
            Evaluate(row, Copy(row), current with { ConnectionState = "removed" }).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            Evaluate(row, Copy(row), current with { Revision = row.ConfigurationRevision + 1 }).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            Evaluate(row, Copy(row), current with { Hash = new string('c', 64) }).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            Evaluate(row, Copy(row), current with { IdentityBindingReference = null }).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            RemoteMcpOAuthManagementService.EvaluateCredentialValidity(
                row, Copy(row), current,
                new RemoteMcpCredentialValidityRequest(row.ConfigurationRevision, new string('d', 64)),
                Now).Denial);
    }

    [Fact]
    public void RowChangeDuringOwnerChecksReturnsCredentialChanged()
    {
        var row = Connection();
        var rotated = Copy(row);
        rotated.CredentialRevision++;
        rotated.AccessTokenSecretVersion = "v2";
        var disconnected = Copy(row);
        disconnected.State = RemoteMcpOAuthConnectionState.Disconnected;

        Assert.Equal(RemoteMcpCredentialDenial.CredentialChanged,
            Evaluate(row, rotated, Current(row)).Denial);
        Assert.Equal(RemoteMcpCredentialDenial.CredentialChanged,
            Evaluate(row, disconnected, Current(row)).Denial);
    }

    [Fact]
    public void AdmissionRequiresBothCurrentInvocationAndMatchingCredential()
    {
        var reference = new RemoteMcpConnectionReviewReference(
            new RemoteMcpConnectionReference("project-1", Guid.NewGuid()),
            4, new string('a', 64), 2, new string('b', 64));
        var invocation = new RemoteMcpInvocationValidity(
            true, RemoteMcpInvocationDenial.None, false, true, "binding", reference);
        var credential = new RemoteMcpCredentialValidity(
            true, RemoteMcpCredentialDenial.None, 3, 2, 4, new string('a', 64), Now.AddHours(1));

        Assert.True(RemoteMcpRemoteUse.Admit(invocation, credential).Allowed);
        Assert.True(RemoteMcpRemoteUse.Admit(
            invocation with { CredentialValidityRequired = false }, null).Allowed);
        Assert.Equal(RemoteMcpCredentialDenial.CredentialValidityMissing,
            RemoteMcpRemoteUse.Admit(invocation, null).CredentialDenial);
        Assert.Equal(RemoteMcpCredentialDenial.Expired,
            RemoteMcpRemoteUse.Admit(invocation,
                credential with { Valid = false, Denial = RemoteMcpCredentialDenial.Expired }).CredentialDenial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            RemoteMcpRemoteUse.Admit(invocation, credential with { ConfigurationRevision = 5 }).CredentialDenial);
        Assert.Equal(RemoteMcpCredentialDenial.ConfigurationChanged,
            RemoteMcpRemoteUse.Admit(invocation,
                credential with { ConfigurationSha256 = new string('c', 64) }).CredentialDenial);
        var denied = RemoteMcpRemoteUse.Admit(
            invocation with { Allowed = false, Denial = RemoteMcpInvocationDenial.CatalogChanged }, credential);
        Assert.False(denied.Allowed);
        Assert.Equal(RemoteMcpInvocationDenial.CatalogChanged, denied.InvocationDenial);
    }

    private static RemoteMcpCredentialValidity Evaluate(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConnectionRecord latest,
        RemoteMcpOAuthManagementService.CurrentConfiguration? current) =>
        RemoteMcpOAuthManagementService.EvaluateCredentialValidity(
            row, latest, current,
            new RemoteMcpCredentialValidityRequest(row.ConfigurationRevision, row.EnvironmentConfigurationHash),
            Now);

    private static RemoteMcpOAuthManagementService.CurrentConfiguration Current(
        RemoteMcpOAuthConnectionRecord row) =>
        new(row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.EndpointUri, row.ResourceUri,
            "delegatedOAuth", "streamableHttp20250618", row.IdentityBindingReference)
        {
            ConnectionState = "enabled"
        };

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
            ConfigurationRevision = 4,
            EnvironmentConfigurationHash = new string('a', 64),
            IdentityBindingReference = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            EndpointUri = "https://mcp.example.test/",
            ResourceUri = "https://mcp.example.test/resource",
            IssuerUri = "https://issuer.example.test/",
            ClientId = "registered-client",
            RedirectUri = "https://identity.example.test/oauth/callback",
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ScopesJson = "[\"tools.read\"]",
            BindingHash = new string('b', 64),
            ConnectionRevision = 3,
            CredentialRevision = 2,
            State = RemoteMcpOAuthConnectionState.Authorized,
            AccessTokenSecretId = "remote-mcp-access-secret",
            AccessTokenSecretVersion = "v1",
            AccessTokenExpiresAt = Now.AddHours(1),
            RefreshTokenSecretId = "remote-mcp-refresh-secret",
            RefreshTokenSecretVersion = "v1",
            CreatedAt = Now,
            UpdatedAt = Now
        };

    private static RemoteMcpOAuthConnectionRecord Copy(RemoteMcpOAuthConnectionRecord row) =>
        JsonSerializer.Deserialize<RemoteMcpOAuthConnectionRecord>(JsonSerializer.Serialize(row))
        ?? throw new InvalidOperationException("The OAuth row test copy could not be created.");
}
