using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Builds a secret-redemption request only from a current authorized remote OAuth record
/// that exactly matches Environment's current-call proof. The before/after check prevents
/// credential use when the identity, configuration, connection or token revision changes.
/// </summary>
internal static class RemoteMcpOAuthCredentialUseBinding
{
    public const string AccessTokenPurpose = "remote-mcp-tools-call-access-token";

    /// <summary>Rejects any change to the connection or redemption reference across the authorization check.</summary>
    public static void RequireSameIdentityAuthority(
        RemoteMcpOAuthConnectionRecord before,
        RemoteMcpOAuthConnectionRecord after,
        RemoteMcpCurrentCallVerificationRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeRequest = CreateRedemptionRequest(before, request, now);
        var afterRequest = CreateRedemptionRequest(after, request, now);
        if (before.Id != after.Id || before.OwnerId != after.OwnerId ||
            before.OwnerIssuer != after.OwnerIssuer || before.OwnerActorId != after.OwnerActorId ||
            before.TenantId != after.TenantId || before.ProjectId != after.ProjectId ||
            before.ConnectionId != after.ConnectionId ||
            before.ConfigurationRevision != after.ConfigurationRevision ||
            before.EnvironmentConfigurationHash != after.EnvironmentConfigurationHash ||
            before.IdentityBindingReference != after.IdentityBindingReference ||
            before.EndpointUri != after.EndpointUri || before.ResourceUri != after.ResourceUri ||
            before.BindingHash != after.BindingHash ||
            before.ConnectionRevision != after.ConnectionRevision ||
            before.CredentialRevision != after.CredentialRevision || before.State != after.State ||
            before.AccessTokenSecretId != after.AccessTokenSecretId ||
            before.AccessTokenSecretVersion != after.AccessTokenSecretVersion ||
            before.AccessTokenExpiresAt != after.AccessTokenExpiresAt ||
            before.RefreshTokenSecretId != after.RefreshTokenSecretId ||
            before.RefreshTokenSecretVersion != after.RefreshTokenSecretVersion ||
            before.RefreshTokenExpiresAt != after.RefreshTokenExpiresAt ||
            before.RefreshAttemptId != after.RefreshAttemptId ||
            before.RefreshStartedAt != after.RefreshStartedAt ||
            beforeRequest.Secret.Id != afterRequest.Secret.Id ||
            beforeRequest.Secret.Version != afterRequest.Secret.Version ||
            beforeRequest.Purpose != afterRequest.Purpose ||
            beforeRequest.RunId != afterRequest.RunId)
            throw Denied();
    }

    /// <summary>
    /// Validates owner, project, Environment snapshot, connection revision, authorized
    /// state and token expiry, then returns the exact access-token reference and run-bound
    /// redemption purpose. Invalid bindings throw an authorization exception.
    /// </summary>
    public static SecretRedemptionRequest CreateRedemptionRequest(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpCurrentCallVerificationRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        RemoteMcpCurrentCallContract.Validate(request);

        var proof = request.Proof;
        var snapshot = proof.SnapshotBinding;
        if (row.OwnerId == Guid.Empty || row.OwnerIssuer != proof.ActorIssuer ||
            row.OwnerActorId != proof.ActorId || row.TenantId != proof.TenantId ||
            row.ProjectId != proof.ProjectId ||
            !Guid.TryParseExact(snapshot.ConnectionId, "D", out var connectionId) ||
            connectionId != row.ConnectionId || row.ConnectionId == Guid.Empty ||
            row.ConnectionRevision <= 0 || row.CredentialRevision < 0 ||
            row.ConfigurationRevision != snapshot.ConnectionConfigurationRevision ||
            row.EnvironmentConfigurationHash != snapshot.ConfigurationSha256 ||
            row.IdentityBindingReference != snapshot.IdentityBindingReference ||
            row.EndpointUri != snapshot.EndpointUri || row.ResourceUri != snapshot.ResourceUri ||
            snapshot.AuthenticationMode != "delegatedOAuth" ||
            row.TransportProfile != RemoteMcpOAuthConnectionBinding.SupportedTransportProfile ||
            !Guid.TryParseExact(row.IdentityBindingReference, "N", out var identityBinding) ||
            identityBinding == Guid.Empty ||
            row.State != RemoteMcpOAuthConnectionState.Authorized ||
            row.RefreshAttemptId is not null || row.RefreshStartedAt is not null ||
            row.AccessTokenSecretId is not { Length: > 0 } ||
            row.AccessTokenSecretVersion is not { Length: > 0 } ||
            row.AccessTokenExpiresAt is not { } expiresAt || expiresAt <= now)
            throw Denied();

        RemoteMcpOAuthConnectionBinding binding;
        try
        {
            binding = RemoteMcpOAuthManagementService.CreateBinding(
                row, row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.IdentityBindingReference);
        }
        catch (Exception error) when (
            error is ArgumentException or JsonException or UriFormatException or InvalidOperationException)
        {
            throw Denied();
        }

        if (binding.BindingHash != row.BindingHash)
            throw Denied();

        return new(
            new SecretRef(row.AccessTokenSecretId, row.AccessTokenSecretVersion),
            AccessTokenPurpose,
            proof.RunId);
    }

    private static RuntimeAuthorizationException Denied() =>
        new("remote_mcp_credential_use_identity_binding_invalid");
}
