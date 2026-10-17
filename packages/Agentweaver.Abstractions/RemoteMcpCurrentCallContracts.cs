using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>
/// Semantic identity of the outgoing native tools/call request. It binds HTTP method,
/// canonical endpoint, tool operation, SDK call event, and exact argument digest.
/// </summary>
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
    /// <summary>Prevents call details from appearing in logs through default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpToolCallSemanticBinding) + " [REDACTED]";
}

/// <summary>Request for current owner verification of a reviewed remote-tool result proof and call binding.</summary>
public sealed record RemoteMcpCurrentCallVerificationRequest(
    int ContractVersion,
    RemoteToolResultProof Proof,
    RemoteMcpToolCallSemanticBinding CallBinding)
{
    /// <summary>Prevents the reviewed proof from appearing in logs through default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpCurrentCallVerificationRequest) + " [REDACTED]";
}

/// <summary>
/// Network Policy pins acknowledged by the current-call owner. The endpoint hashes and
/// applied-policy generation bind the gateway and target used for this specific call.
/// </summary>
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
    /// <summary>Prevents policy details from appearing in logs through default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpCurrentGatewayPolicyBinding) + " [REDACTED]";
}

/// <summary>
/// Owner acknowledgment that the reviewed proof and exact call binding are current,
/// including a fresh tool snapshot and applied gateway policy.
/// </summary>
public sealed record RemoteMcpCurrentCallVerificationAcknowledgment(
    int ContractVersion,
    string ProofSha256,
    string CallBindingSha256,
    RemoteToolResultVerificationReceipt CallVerification,
    RemoteToolResultSnapshotBinding SnapshotBinding,
    RemoteMcpCurrentGatewayPolicyBinding GatewayPolicy)
{
    /// <summary>Prevents proof and current authority pins from appearing in default logs.</summary>
    public override string ToString() => nameof(RemoteMcpCurrentCallVerificationAcknowledgment) + " [REDACTED]";
}

/// <summary>Creates and validates exact current-call bindings and owner acknowledgments for Remote MCP.</summary>
public static class RemoteMcpCurrentCallContract
{
    /// <summary>Internal route used to verify current Remote MCP authority.</summary>
    public const string VerificationPath = "/internal/remote-mcp/current-call/verify";
    private static readonly JsonSerializerOptions BindingJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the semantic binding from a validated reviewed-result proof.</summary>
    public static RemoteMcpCurrentCallVerificationRequest CreateRequest(RemoteToolResultProof proof) =>
        new(1, proof, CreateBinding(proof));

    /// <summary>Derives the only supported POST tools/call binding for the proof's endpoint and arguments.</summary>
    /// <exception cref="ArgumentException">The proof, protocol profile, or argument digest is invalid.</exception>
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

    /// <summary>Checks request version and requires its semantic binding to match the proof exactly.</summary>
    /// <exception cref="ArgumentException">The request does not contain the exact reviewed tools/call binding.</exception>
    public static void Validate(RemoteMcpCurrentCallVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.CallBinding is null ||
            request.CallBinding != CreateBinding(request.Proof))
            throw new ArgumentException("The exact reviewed tools/call semantic binding is required.", nameof(request));
    }

    /// <summary>
    /// Validates actual outgoing bytes against the reviewed method, endpoint, tool, and
    /// canonical argument JSON, rejecting duplicate or unsupported JSON properties.
    /// </summary>
    /// <exception cref="ArgumentException">The method, target, bytes, JSON-RPC shape, tool, or arguments differ.</exception>
    public static void ValidateToolCallBytes(
        RemoteMcpCurrentCallVerificationRequest request,
        string actualMethod,
        string actualTarget,
        ReadOnlyMemory<byte> requestBytes)
    {
        Validate(request);
        if (actualMethod != request.CallBinding.Method || actualTarget != request.CallBinding.CanonicalTarget ||
            requestBytes.Length is <= 0 or > SessionMaterialValidation.MaximumBytes)
            throw new ArgumentException("The bounded request must use the reviewed method and target.", nameof(requestBytes));
        if (!System.Text.Unicode.Utf8.IsValid(requestBytes.Span))
            throw new ArgumentException("The MCP request must contain valid UTF-8.", nameof(requestBytes));
        try
        {
            using var document = JsonDocument.Parse(requestBytes);
            var root = document.RootElement;
            RequireUniqueProperties(root);
            RequireObjectShape(root, ["jsonrpc", "id", "method", "params"]);
            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() != "2.0" ||
                !root.TryGetProperty("id", out var id) ||
                id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
                !root.TryGetProperty("method", out var operation) || operation.ValueKind != JsonValueKind.String ||
                operation.GetString() != request.CallBinding.Operation ||
                !root.TryGetProperty("params", out var parameters))
                throw new ArgumentException("A JSON-RPC tools/call request with an explicit request ID is required.");
            RequireObjectShape(parameters, ["name", "arguments", "_meta"]);
            if (!parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() != request.CallBinding.ToolName)
                throw new ArgumentException("The request must name the exact reviewed tool.");
            if (parameters.TryGetProperty("_meta", out var metadata) &&
                (metadata.ValueKind != JsonValueKind.Object ||
                    metadata.TryGetProperty("progressToken", out var progress) &&
                    progress.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))
                throw new ArgumentException("MCP request metadata and progress tokens must use their protocol types.");

            using var canonicalArguments = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonicalArguments, SandboxBuildTestCanonicalJson.WriterOptions))
            {
                if (parameters.TryGetProperty("arguments", out var arguments))
                {
                    if (arguments.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("MCP tool arguments must be an object.");
                    SandboxBuildTestCanonicalJson.WriteJsonElement(writer, arguments);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
            }
            var argumentBytes = canonicalArguments.ToArray();
            if (argumentBytes.Length > SessionMaterialValidation.MaximumRemoteToolArgumentsBytes ||
                Encoding.UTF8.GetString(argumentBytes) != request.Proof.CanonicalArgumentsJson ||
                Sha256(argumentBytes) != request.CallBinding.ArgumentsDigest)
                throw new ArgumentException("The request arguments must match the exact reviewed arguments and digest.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The MCP request must contain one valid bounded JSON object.", nameof(requestBytes), exception);
        }
    }

    /// <summary>Returns a deterministic hash of a semantic call binding.</summary>
    public static string CallBindingHash(RemoteMcpToolCallSemanticBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(binding, BindingJsonOptions));
    }

    /// <summary>Checks that the acknowledgment proves the exact proof, call, snapshot, and current network pins.</summary>
    /// <exception cref="ArgumentException">Any acknowledgment field is stale, missing, or bound to another call.</exception>
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

    /// <summary>Rejects a wait if the owner's current call authority changed between two acknowledgments.</summary>
    /// <exception cref="ArgumentException">Either acknowledgment is invalid or their authority differs.</exception>
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

    /// <summary>Requires a bounded, nonempty policy pin without control characters.</summary>
    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A bounded current policy binding is required.");
    }

    /// <summary>Requires a canonical lowercase SHA-256 policy pin.</summary>
    private static void RequireHash(string? value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("An exact SHA-256 policy binding is required.");
    }

    /// <summary>Rejects non-object JSON or properties outside the protocol's allowed shape.</summary>
    private static void RequireObjectShape(JsonElement value, string[] allowedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("The MCP request contains an unsupported object shape.");
    }

    /// <summary>Walks nested JSON values and rejects duplicate property names before interpretation.</summary>
    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Duplicate MCP request properties are not allowed.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RequireUniqueProperties(item);
        }
    }

    /// <summary>Returns lowercase SHA-256 for the exact supplied bytes.</summary>
    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
