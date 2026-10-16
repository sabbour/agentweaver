using System.Security.Cryptography;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public sealed record RemoteMcpManagedGatewayGateDescriptor(
    int ContractVersion,
    RemoteMcpCurrentGatewayPolicyBinding GatewayPolicy,
    RemoteToolResultSnapshotBinding SnapshotBinding)
{
    public override string ToString() => nameof(RemoteMcpManagedGatewayGateDescriptor) + " [REDACTED]";
}

public enum RemoteMcpManagedGatewayOperation { Apply, Verify, Revoke }
public enum RemoteMcpManagedGatewayGateState { Active, Revoked }

// This receipt proves only the state of an in-process handler, not forced network mediation.
public sealed record RemoteMcpManagedGatewayPolicyReceipt(
    int ContractVersion,
    RemoteMcpManagedGatewayOperation Operation,
    string DescriptorSha256,
    Guid HandlerInstanceId,
    RemoteMcpManagedGatewayGateState State);

public interface IRemoteMcpManagedGatewayPolicy
{
    Task<RemoteMcpManagedGatewayPolicyReceipt> ApplyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);
    Task<RemoteMcpManagedGatewayPolicyReceipt> VerifyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);
    Task<RemoteMcpManagedGatewayPolicyReceipt> RevokeAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor, CancellationToken cancellationToken);
}

public static class RemoteMcpManagedGatewayContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public static string DescriptorHash(RemoteMcpManagedGatewayGateDescriptor descriptor)
    {
        Validate(descriptor);
        return Hash(JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions));
    }

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
