using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public sealed record RemoteMcpToolCallSemanticBinding(
    string ProtocolProfile,
    string Method,
    string CanonicalTarget,
    string Operation,
    string NativeCallId,
    Guid CallEventId,
    string CallInputHash,
    string ToolName,
    string ArgumentsDigest)
{
    public override string ToString() => nameof(RemoteMcpToolCallSemanticBinding) + " [REDACTED]";
}

public sealed record RemoteMcpCurrentCallVerificationRequest(
    int ContractVersion,
    RemoteToolResultProof Proof,
    RemoteMcpToolCallSemanticBinding CallBinding)
{
    public override string ToString() => nameof(RemoteMcpCurrentCallVerificationRequest) + " [REDACTED]";
}

public sealed record RemoteMcpCurrentGatewayPolicyBinding(
    RemoteMcpAppliedNetworkPolicyReference AppliedPolicy,
    ProviderResourceRef PolicyResource,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string OptionsSha256,
    string GatewayEndpointSha256,
    string TargetEndpointSha256)
{
    public override string ToString() => nameof(RemoteMcpCurrentGatewayPolicyBinding) + " [REDACTED]";
}

public sealed record RemoteMcpCurrentCallVerificationAcknowledgment(
    int ContractVersion,
    string ProofSha256,
    string CallBindingSha256,
    RemoteToolResultVerificationReceipt CallVerification,
    RemoteToolResultSnapshotBinding SnapshotBinding,
    RemoteMcpCurrentGatewayPolicyBinding GatewayPolicy)
{
    public override string ToString() => nameof(RemoteMcpCurrentCallVerificationAcknowledgment) + " [REDACTED]";
}

public static class RemoteMcpCurrentCallContract
{
    public const string VerificationPath = "/internal/remote-mcp/current-call/verify";
    private static readonly JsonSerializerOptions BindingJsonOptions = new(JsonSerializerDefaults.Web);

    public static RemoteMcpCurrentCallVerificationRequest CreateRequest(RemoteToolResultProof proof) =>
        new(1, proof, CreateBinding(proof));

    public static RemoteMcpToolCallSemanticBinding CreateBinding(RemoteToolResultProof proof)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        if (proof.SnapshotBinding.TransportProfile is not ("StreamableHttp20250618" or "mcp-2025-06-18") ||
            proof.ArgumentsDigest != Sha256(Encoding.UTF8.GetBytes(proof.CanonicalArgumentsJson)))
            throw new ArgumentException("The reviewed MCP profile and exact argument digest are required.", nameof(proof));
        return new(
            proof.SnapshotBinding.TransportProfile,
            "POST",
            proof.SnapshotBinding.EndpointUri,
            "tools/call",
            proof.NativeCallId,
            proof.CallEventId,
            proof.CallInputHash,
            proof.ToolName,
            proof.ArgumentsDigest);
    }

    public static void Validate(RemoteMcpCurrentCallVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.CallBinding is null ||
            request.CallBinding != CreateBinding(request.Proof))
            throw new ArgumentException("The exact reviewed tools/call semantic binding is required.", nameof(request));
    }

    public static string CallBindingHash(RemoteMcpToolCallSemanticBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(binding, BindingJsonOptions));
    }

    public static void ValidateAcknowledgment(
        RemoteMcpCurrentCallVerificationRequest request,
        RemoteMcpCurrentCallVerificationAcknowledgment acknowledgment)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(acknowledgment);
        var proof = request.Proof;
        var expectedHash = SessionMaterialValidation.RemoteToolResultProofHash(proof);
        var policy = acknowledgment.GatewayPolicy;
        if (acknowledgment.ContractVersion != 1 ||
            acknowledgment.ProofSha256 != expectedHash ||
            acknowledgment.CallBindingSha256 != CallBindingHash(request.CallBinding) ||
            acknowledgment.CallVerification is not { ContractVersion: 1 } receipt ||
            receipt.ProofSha256 != expectedHash ||
            receipt.ResultEventId != SessionMaterialValidation.RemoteToolResultEventId(proof) ||
            acknowledgment.SnapshotBinding != proof.SnapshotBinding ||
            policy?.AppliedPolicy?.EnvironmentFence is not { } fence ||
            fence.Owner is not { } owner ||
            owner.TenantId != proof.TenantId || owner.ProjectId != proof.ProjectId || owner.RunId != proof.RunId ||
            fence.LifecycleGeneration <= 0 || policy.AppliedPolicy.AppliedGeneration <= 0 ||
            policy.PolicyResource is not { Seam: ProviderSeam.NetworkPolicy, Generation: > 0 } resource ||
            !Version.TryParse(policy.AdapterVersion, out _) ||
            policy.OptionsSchemaVersion <= 0 ||
            policy.TargetEndpointSha256 != Sha256(Encoding.UTF8.GetBytes(request.CallBinding.CanonicalTarget)))
            throw new ArgumentException("A current owner acknowledgment for the exact reviewed call is required.", nameof(acknowledgment));
        RequireText(resource.ProviderId);
        RequireText(resource.ResourceId);
        RequireText(policy.OptionsRevision);
        RequireText(policy.AppliedPolicy.Reference);
        RequireHash(policy.AppliedPolicy.EgressIntentSha256);
        RequireHash(policy.OptionsSha256);
        RequireHash(policy.GatewayEndpointSha256);
        RequireHash(policy.TargetEndpointSha256);
    }

    public static void RequireSameAuthority(
        RemoteMcpCurrentCallVerificationRequest request,
        RemoteMcpCurrentCallVerificationAcknowledgment before,
        RemoteMcpCurrentCallVerificationAcknowledgment after)
    {
        ValidateAcknowledgment(request, before);
        ValidateAcknowledgment(request, after);
        if (before != after)
            throw new ArgumentException("The current Remote MCP owner authority changed during the wait.");
    }

    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A bounded current policy binding is required.");
    }

    private static void RequireHash(string? value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("An exact SHA-256 policy binding is required.");
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
