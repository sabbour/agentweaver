using System.Security.Cryptography;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>
/// Immutable descriptor for one managed-gateway check, bound to the applied policy and
/// reviewed remote-tool snapshot that authorize the target.
/// </summary>
public sealed record RemoteMcpManagedGatewayGateDescriptor(
    int ContractVersion,
    RemoteMcpCurrentGatewayPolicyBinding GatewayPolicy,
    RemoteToolResultSnapshotBinding SnapshotBinding)
{
    /// <summary>Prevents descriptor details from being emitted by default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpManagedGatewayGateDescriptor) + " [REDACTED]";
}

/// <summary>Names the state-changing or verification operation performed on a managed gateway.</summary>
public enum RemoteMcpManagedGatewayOperation { Apply, Verify, Revoke }

/// <summary>Reports whether the in-process managed-gateway handler is active or revoked.</summary>
public enum RemoteMcpManagedGatewayGateState { Active, Revoked }

// This receipt proves only the state of an in-process handler, not forced network mediation.
/// <summary>Handler receipt tied to a descriptor hash and one handler instance.</summary>
public sealed record RemoteMcpManagedGatewayPolicyReceipt(
    int ContractVersion,
    RemoteMcpManagedGatewayOperation Operation,
    string DescriptorSha256,
    Guid HandlerInstanceId,
    RemoteMcpManagedGatewayGateState State);

/// <summary>Owner interface for applying, verifying, and revoking a managed gateway policy.</summary>
public interface IRemoteMcpManagedGatewayPolicy
{
    /// <summary>Activates handling for the supplied exact policy and snapshot descriptor.</summary>
    Task<RemoteMcpManagedGatewayPolicyReceipt> ApplyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>Reports whether the matching handler instance remains active.</summary>
    Task<RemoteMcpManagedGatewayPolicyReceipt> VerifyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>Revokes the matching handler instance and returns its resulting state.</summary>
    Task<RemoteMcpManagedGatewayPolicyReceipt> RevokeAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>Builds and checks the descriptor hash and operation receipt for managed-gateway policy.</summary>
public static class RemoteMcpManagedGatewayContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a descriptor only from a validated current-call acknowledgment.</summary>
    /// <exception cref="ArgumentException">The acknowledgment does not match the request or pins are invalid.</exception>
    public static RemoteMcpManagedGatewayGateDescriptor CreateDescriptor(
        RemoteMcpCurrentCallVerificationRequest request,
        RemoteMcpCurrentCallVerificationAcknowledgment acknowledgment)
    {
        RemoteMcpCurrentCallContract.ValidateAcknowledgment(request, acknowledgment);
        var descriptor = new RemoteMcpManagedGatewayGateDescriptor(
            1, acknowledgment.GatewayPolicy, acknowledgment.SnapshotBinding);
        Validate(descriptor);
        return descriptor;
    }

    /// <summary>Rejects descriptors with missing, stale, noncanonical, or malformed pins.</summary>
    /// <exception cref="ArgumentException">The descriptor is not an exact current reviewed-call binding.</exception>
    public static void Validate(RemoteMcpManagedGatewayGateDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var policy = descriptor.GatewayPolicy;
        var snapshot = descriptor.SnapshotBinding;
        if (descriptor.ContractVersion != 1 ||
            policy?.AppliedPolicy?.EnvironmentFence is not { } fence ||
            fence.LifecycleGeneration <= 0 || policy.AppliedPolicy.AppliedGeneration <= 0 ||
            policy.PolicyResource is not { Seam: ProviderSeam.NetworkPolicy, Generation: > 0 } resource ||
            !Version.TryParse(policy.AdapterVersion, out _) || policy.OptionsSchemaVersion <= 0 ||
            snapshot is null || snapshot.ConnectionRowRevision <= 0 ||
            snapshot.ConnectionConfigurationRevision <= 0 || snapshot.ConnectionDiscoveryRevision is not > 0 ||
            snapshot.ConnectionState != "Enabled" ||
            policy.TargetEndpointSha256 != Hash(System.Text.Encoding.UTF8.GetBytes(snapshot.EndpointUri)))
            throw new ArgumentException("An exact current reviewed-call gate descriptor is required.", nameof(descriptor));
        foreach (var text in new[]
        {
            resource.ProviderId, resource.ResourceId, policy.OptionsRevision, policy.AppliedPolicy.Reference,
            snapshot.ConnectionId, snapshot.CatalogRevision, snapshot.ToolId, snapshot.ToolRevision,
            snapshot.ToolSchemaRevision, snapshot.PermissionActionId, snapshot.PermissionPurpose
        })
            RequireText(text);
        foreach (var hash in new[]
        {
            policy.AppliedPolicy.EgressIntentSha256, policy.OptionsSha256, policy.GatewayEndpointSha256,
            policy.TargetEndpointSha256, snapshot.ConfigurationSha256, snapshot.CatalogDigest,
            snapshot.ToolDigest, snapshot.ToolSchemaDigest, snapshot.PermissionMetadataDigest
        })
            RequireHash(hash);
        if (RemoteMcpUri.CanonicalizeHttps(snapshot.EndpointUri) != snapshot.EndpointUri)
            throw new ArgumentException("A canonical target endpoint is required.", nameof(descriptor));
    }

    /// <summary>Computes the stable lowercase SHA-256 hash of a validated serialized descriptor.</summary>
    public static string DescriptorHash(RemoteMcpManagedGatewayGateDescriptor descriptor)
    {
        Validate(descriptor);
        return Hash(JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions));
    }

    /// <summary>Checks that a receipt proves the requested operation on the descriptor's handler instance.</summary>
    /// <exception cref="ArgumentException">The receipt has a different operation, hash, instance, or state.</exception>
    public static void ValidateReceipt(
        RemoteMcpManagedGatewayGateDescriptor descriptor,
        RemoteMcpManagedGatewayOperation operation,
        RemoteMcpManagedGatewayPolicyReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!Enum.IsDefined(operation) || receipt.ContractVersion != 1 || receipt.Operation != operation ||
            receipt.DescriptorSha256 != DescriptorHash(descriptor) || receipt.HandlerInstanceId == Guid.Empty ||
            receipt.State != (operation == RemoteMcpManagedGatewayOperation.Revoke
                ? RemoteMcpManagedGatewayGateState.Revoked : RemoteMcpManagedGatewayGateState.Active))
            throw new ArgumentException("An exact active-handler receipt for this operation is required.", nameof(receipt));
    }

    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A bounded current gate pin is required.");
    }

    private static void RequireHash(string? value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("An exact SHA-256 gate pin is required.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
