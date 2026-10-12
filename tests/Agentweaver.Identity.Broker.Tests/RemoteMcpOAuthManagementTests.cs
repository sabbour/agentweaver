using System.Text.Json;
using Agentweaver.Abstractions;
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
            ConnectionState = "draft"
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
            ConnectionState = "draft"
        };

        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { IdentityBindingReference = null }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { IdentityBindingReference = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { ConnectionState = "removed" }));
        Assert.False(RemoteMcpOAuthManagementService.IsCurrentConfiguration(
            row, current with { ConnectionState = "unknown" }));
    }

    [Fact]
    public void CredentialUseRequestSelectsOnlyTheCurrentIdentityAccessTokenAndFixedToolsCallPurpose()
    {
        var now = DateTimeOffset.UtcNow;
        var (row, request) = CurrentCall();

        var redemption = RemoteMcpOAuthCredentialUseBinding.CreateRedemptionRequest(row, request, now);

        Assert.Equal(
            new SecretRef(row.AccessTokenSecretId!, row.AccessTokenSecretVersion!),
            redemption.Secret);
        Assert.Equal("remote-mcp-tools-call-access-token", redemption.Purpose);
        Assert.Equal(request.Proof.RunId, redemption.RunId);
    }

    [Fact]
    public void CredentialUseIdentityAuthorityMustRemainUnchangedAcrossWaits()
    {
        var now = DateTimeOffset.UtcNow;
        var (before, request) = CurrentCall();
        var unchanged = Copy(before);
        RemoteMcpOAuthCredentialUseBinding.RequireSameIdentityAuthority(before, unchanged, request, now);

        var mutations = new Action<RemoteMcpOAuthConnectionRecord>[]
        {
            row => row.ConnectionRevision++,
            row => row.CredentialRevision++,
            row => row.AccessTokenSecretVersion = "v2",
            row => row.AccessTokenExpiresAt = row.AccessTokenExpiresAt!.Value.AddMinutes(-1),
            row => row.RefreshAttemptId = Guid.NewGuid()
        };
        foreach (var mutate in mutations)
        {
            var after = Copy(before);
            mutate(after);
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RemoteMcpOAuthCredentialUseBinding.RequireSameIdentityAuthority(
                    before, after, request, now));
        }
    }

    [Fact]
    public void CredentialUseRequestRejectsStaleIdentityBindingsAndEveryNonAuthorizedState()
    {
        var now = DateTimeOffset.UtcNow;
        var mutations = new Action<RemoteMcpOAuthConnectionRecord>[]
        {
            row => row.OwnerIssuer = "https://other-identity.example.test/",
            row => row.OwnerActorId = "22222222-2222-2222-2222-222222222222",
            row => row.TenantId = "tenant-2",
            row => row.ProjectId = "project-2",
            row => row.ConnectionId = Guid.NewGuid(),
            row => row.ConfigurationRevision++,
            row => row.EnvironmentConfigurationHash = new string('c', 64),
            row => row.IdentityBindingReference = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            row => row.EndpointUri = "https://other-mcp.example.test/",
            row => row.ResourceUri = "https://other-mcp.example.test/resource",
            row => row.BindingHash = new string('c', 64),
            row => row.AccessTokenExpiresAt = now,
            row => row.AccessTokenSecretId = null,
            row => row.AccessTokenSecretVersion = null
        };

        foreach (var mutate in mutations)
        {
            var (row, request) = CurrentCall();
            mutate(row);
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RemoteMcpOAuthCredentialUseBinding.CreateRedemptionRequest(row, request, now));
        }

        foreach (var state in Enum.GetValues<RemoteMcpOAuthConnectionState>()
                     .Where(state => state != RemoteMcpOAuthConnectionState.Authorized))
        {
            var (row, request) = CurrentCall();
            row.State = state;
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RemoteMcpOAuthCredentialUseBinding.CreateRedemptionRequest(row, request, now));
        }
    }

    [Fact]
    public void CredentialUseRequestRejectsAlteredToolsCallSemantics()
    {
        var (row, request) = CurrentCall();
        var altered = request with { CallBinding = request.CallBinding with { Method = "GET" } };

        Assert.Throws<ArgumentException>(() =>
            RemoteMcpOAuthCredentialUseBinding.CreateRedemptionRequest(row, altered, DateTimeOffset.UtcNow));
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
            ClientId = "registered-client",
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

    private static (RemoteMcpOAuthConnectionRecord Row, RemoteMcpCurrentCallVerificationRequest Request)
        CurrentCall()
    {
        var proof = RemoteMcpCurrentCallVerificationClientTests.Proof();
        var row = Connection();
        row.OwnerIssuer = proof.ActorIssuer;
        row.OwnerActorId = proof.ActorId;
        row.TenantId = proof.TenantId;
        row.ProjectId = proof.ProjectId;
        row.ConnectionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        row.ConfigurationRevision = 2;
        row.EnvironmentConfigurationHash = proof.SnapshotBinding.ConfigurationSha256;
        row.IdentityBindingReference = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        row.EndpointUri = proof.SnapshotBinding.EndpointUri;
        row.ResourceUri = proof.SnapshotBinding.ResourceUri!;
        row.BindingHash = RemoteMcpOAuthManagementService.CreateBinding(
            row, row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.IdentityBindingReference)
            .BindingHash;
        var snapshot = proof.SnapshotBinding with
        {
            ConnectionId = row.ConnectionId.ToString("D"),
            ConnectionConfigurationRevision = 2,
            ConfigurationSha256 = row.EnvironmentConfigurationHash,
            EndpointUri = row.EndpointUri,
            ResourceUri = row.ResourceUri,
            AuthenticationMode = "delegatedOAuth",
            IdentityBindingReference = row.IdentityBindingReference,
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile
        };
        return (row, RemoteMcpCurrentCallContract.CreateRequest(proof with { SnapshotBinding = snapshot }));
    }

    private static RemoteMcpOAuthConnectionRecord Copy(RemoteMcpOAuthConnectionRecord row) =>
        JsonSerializer.Deserialize<RemoteMcpOAuthConnectionRecord>(JsonSerializer.Serialize(row))
        ?? throw new InvalidOperationException("The OAuth row test copy could not be created.");
}
