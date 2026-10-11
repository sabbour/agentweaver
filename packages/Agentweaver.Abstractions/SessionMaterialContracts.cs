using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public enum SessionMaterialKind { TurnContent, SdkCache, RemoteToolResult, RuntimeConfiguration }

public enum SessionMaterialResultContentClass { TextOnly, Structured, NonText, Mixed }

public sealed record RemoteToolResultSnapshotBinding(
    string ConnectionId,
    long ConnectionRowRevision,
    long ConnectionConfigurationRevision,
    long? ConnectionDiscoveryRevision,
    string ConnectionState,
    string ConfigurationSha256,
    string EndpointUri,
    string? ResourceUri,
    string AuthenticationMode,
    string? IdentityBindingReference,
    string TransportProfile,
    string? RegistryServerName,
    string? RegistryServerVersion,
    string? RegistryServerMetadataSha256,
    string CatalogRevision,
    string CatalogDigest,
    string ToolId,
    string ToolRevision,
    string ToolDigest,
    string ToolSchemaRevision,
    string ToolSchemaDigest,
    string PermissionActionId,
    string PermissionPurpose,
    string PermissionMetadataDigest);

public sealed record RemoteToolResultProof(
    int ContractVersion,
    Guid CallEventId,
    string CallInputHash,
    string NativeCallId,
    ReviewedRemoteToolSnapshotReference Snapshot,
    RemoteToolResultSnapshotBinding SnapshotBinding,
    string ToolName,
    string CanonicalArgumentsJson,
    string ArgumentsDigest,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string StepId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    string ActionId,
    string Purpose,
    int ActionContractVersion,
    bool IsToolInvocation,
    string GrantId,
    string GrantRevision,
    Guid PolicyReceiptId,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    SessionMaterialResultContentClass ContentClass)
{
    public override string ToString() => nameof(RemoteToolResultProof) + " [REDACTED]";
}

public sealed record RemoteToolResultVerificationReceipt(
    int ContractVersion, Guid ResultEventId, string ProofSha256);

public sealed record SessionMaterialBinding(
    int ContractVersion,
    SessionMaterialKind Kind,
    string TenantId,
    string Sha256,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string SdkVersion,
    string RuntimeVersion,
    string ModelSelectionReference,
    string ModelId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteToolResultProof? RemoteToolResultProof { get; init; }
}

public sealed record SessionMaterialWriteRequest(
    int ContractVersion,
    Guid EventId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    SessionMaterialKind Kind,
    byte[] Bytes,
    string? Role = null,
    string? SdkVersion = null,
    string? ModelId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteToolResultProof? RemoteToolResultProof { get; init; }

    public override string ToString() => nameof(SessionMaterialWriteRequest) + " [REDACTED]";
}

public sealed record SessionMaterialAcknowledgment(
    int ContractVersion,
    SessionIdentity Identity,
    Guid EventId,
    long Position,
    SessionObjectReference Reference);

public sealed record SessionMaterialReadResult(SessionMaterialAcknowledgment Material, byte[] Bytes)
{
    public override string ToString() => nameof(SessionMaterialReadResult) + " [REDACTED]";
}

public static class SessionMaterialValidation
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumRemoteToolArgumentsBytes = 256 * 1024;

    public static void Validate(SessionMaterialWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.EventId == Guid.Empty ||
            request.RuntimeInstanceId == Guid.Empty || request.RegistrationRevision <= 0 ||
            request.ExecutionFence <= 0 || !Enum.IsDefined(request.Kind) ||
            request.Bytes is not { Length: > 0 and <= MaximumBytes } ||
            request.Kind == SessionMaterialKind.TurnContent && request.Role is not ("user" or "assistant") ||
            request.Kind != SessionMaterialKind.TurnContent && request.Role is not null ||
            request.Kind == SessionMaterialKind.RemoteToolResult &&
                (request.RemoteToolResultProof is null ||
                    request.EventId != RemoteToolResultEventId(request.RemoteToolResultProof)) ||
            request.Kind != SessionMaterialKind.RemoteToolResult && request.RemoteToolResultProof is not null ||
            request.MaxPromptTokens is < 1 or > 200000)
            throw new ArgumentException("A bounded typed execution session material request is required.");
        if (request.Kind != SessionMaterialKind.RemoteToolResult)
        {
            RequireIdentifier(request.SdkVersion);
            RequireIdentifier(request.ModelId);
        }
        else
        {
            ValidateRemoteToolResultProof(request.RemoteToolResultProof!);
            if (request.SdkVersion is not null)
                RequireIdentifier(request.SdkVersion);
            if (request.ModelId is not null)
                RequireIdentifier(request.ModelId);
        }
        if (request.Kind == SessionMaterialKind.RuntimeConfiguration)
        {
            using var document = System.Text.Json.JsonDocument.Parse(request.Bytes);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                document.RootElement.GetRawText().Length > AddressedMessageValidation.MaximumPayloadLength)
                throw new ArgumentException("A bounded accepted Runtime configuration object is required.");
        }
    }

    public static void Validate(SessionObjectReference reference)
    {
        var material = reference.Material ?? throw new ArgumentException("Recorded session material is required.");
        if (material.ContractVersion != 1 || !Enum.IsDefined(material.Kind) ||
            material.RuntimeInstanceId == Guid.Empty || material.RegistrationRevision <= 0 ||
            material.ExecutionFence <= 0 || reference.ByteLength is not (> 0 and <= MaximumBytes) ||
            reference.Purpose != Purpose(material.Kind) || material.MaxPromptTokens is < 1 or > 200000)
            throw new ArgumentException("The recorded session material binding is invalid.");
        if (material.Kind == SessionMaterialKind.RemoteToolResult)
        {
            if (material.RemoteToolResultProof is null)
                throw new ArgumentException("The recorded remote tool result proof is required.");
            ValidateRemoteToolResultProof(material.RemoteToolResultProof);
        }
        else if (material.RemoteToolResultProof is not null)
            throw new ArgumentException("Only remote tool result material may contain a result proof.");
        RequireHash(material.Sha256);
        RequireHash(material.AcceptedSelectionHash);
        RequireIdentifier(material.TenantId);
        RequireIdentifier(material.SdkVersion);
        RequireIdentifier(material.RuntimeVersion);
        RequireIdentifier(material.ModelSelectionReference);
        RequireIdentifier(material.ModelId);
    }

    public static string Purpose(SessionMaterialKind kind) => kind switch
    {
        SessionMaterialKind.TurnContent => "runtime-turn-content",
        SessionMaterialKind.SdkCache => "runtime-sdk-cache",
        SessionMaterialKind.RemoteToolResult => "runtime-remote-tool-result",
        SessionMaterialKind.RuntimeConfiguration => "runtime-configuration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static Guid RemoteToolResultEventId(RemoteToolResultProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.CallEventId == Guid.Empty)
            throw new ArgumentException("A reviewed tool call event ID is required.", nameof(proof));
        RequireHash(proof.CallInputHash);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("remote-tool-result.v1");
            writer.Write(proof.CallEventId.ToByteArray());
            writer.Write(Convert.FromHexString(proof.CallInputHash));
        }
        var digest = System.Security.Cryptography.SHA256.HashData(stream.ToArray());
        var eventId = new Guid(digest.AsSpan(0, 16));
        return eventId == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : eventId;
    }

    public static string RemoteToolResultCallId(RemoteToolResultProof proof) =>
        "remote_tool_call_" + HashUtf8(
            $"remote-tool-call-id.v1\0{proof.CallEventId:N}\0{proof.CallInputHash}");

    public static string RemoteToolResultToolName(RemoteToolResultProof proof) =>
        "remote_tool_" + HashUtf8(
            $"remote-tool-name.v1\0{proof.Snapshot.SnapshotId:N}\0{proof.Snapshot.SnapshotDigest}\0{proof.ToolName}");

    public static bool SameRemoteToolResultProof(
        RemoteToolResultProof? first, RemoteToolResultProof? second) =>
        first is not null && second is not null &&
        first.Snapshot.ProjectId == second.Snapshot.ProjectId &&
        first.Snapshot.SnapshotId == second.Snapshot.SnapshotId &&
        first.Snapshot.SnapshotDigest == second.Snapshot.SnapshotDigest &&
        first.Snapshot.AgentId == second.Snapshot.AgentId &&
        first.Snapshot.NodeId == second.Snapshot.NodeId &&
        (first with { Snapshot = second.Snapshot }) == second;

    public static string RemoteToolResultProofHash(RemoteToolResultProof proof)
    {
        ValidateRemoteToolResultProof(proof);
        return HashUtf8(System.Text.Json.JsonSerializer.Serialize(proof));
    }

    public static void ValidateRemoteToolResultProof(RemoteToolResultProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var snapshot = proof.SnapshotBinding;
        if (proof.ContractVersion != 1 || proof.CallEventId == Guid.Empty ||
            proof.RuntimeInstanceId == Guid.Empty || proof.RegistrationRevision <= 0 ||
            proof.ExecutionFence <= 0 || proof.ProjectRevision <= 0 ||
            proof.ProjectConfigurationRevision <= 0 || proof.PlatformRuntimeRevision <= 0 ||
            proof.ActionContractVersion != 1 || !proof.IsToolInvocation ||
            proof.PolicyReceiptId != proof.CallEventId ||
            proof.Outcome != PolicyEvaluationOutcome.Allow ||
            proof.ReasonCode != PolicyEvaluationReasonCode.Allowed ||
            proof.GrantId != proof.CallEventId.ToString("N") ||
            proof.GrantRevision != "1" ||
            proof.Snapshot is null ||
            snapshot is null ||
            proof.Snapshot.ProjectId != proof.ProjectId ||
            proof.ActionId is not ("tool.read" or "tool.write") ||
            proof.Purpose != "runtime.execution" ||
            snapshot.ConnectionRowRevision <= 0 ||
            snapshot.ConnectionConfigurationRevision <= 0 ||
            snapshot.ConnectionDiscoveryRevision is <= 0 ||
            snapshot.ConnectionState != "Enabled" ||
            snapshot.ToolId != proof.ToolName ||
            snapshot.PermissionActionId != proof.ActionId ||
            snapshot.PermissionPurpose != proof.Purpose ||
            !Enum.IsDefined(proof.ContentClass) ||
            proof.CanonicalArgumentsJson is null ||
            System.Text.Encoding.UTF8.GetByteCount(proof.CanonicalArgumentsJson) is <= 0 or > MaximumRemoteToolArgumentsBytes)
            throw new ArgumentException("The remote tool result proof is invalid.");
        RequireHash(proof.CallInputHash);
        RequireHash(proof.ArgumentsDigest);
        RequireHash(proof.AcceptedSelectionHash);
        RequireHash(snapshot.ConfigurationSha256);
        RequireHash(snapshot.CatalogDigest);
        RequireHash(snapshot.ToolDigest);
        RequireHash(snapshot.ToolSchemaDigest);
        RequireHash(snapshot.PermissionMetadataDigest);
        if (snapshot.RegistryServerMetadataSha256 is { } registryDigest)
            RequireHash(registryDigest);
        RequireText(proof.NativeCallId, nameof(proof.NativeCallId));
        RequireText(proof.ToolName, nameof(proof.ToolName));
        RequireText(proof.ActorIssuer, nameof(proof.ActorIssuer));
        RequireText(proof.ActorId, nameof(proof.ActorId));
        RequireText(proof.TenantId, nameof(proof.TenantId));
        RequireText(proof.ProjectId, nameof(proof.ProjectId));
        RequireText(proof.RunId, nameof(proof.RunId));
        RequireText(proof.SessionId, nameof(proof.SessionId));
        RequireText(proof.StepId, nameof(proof.StepId));
        RequireText(proof.ContextRevision, nameof(proof.ContextRevision));
        RequireText(snapshot.ConnectionId, nameof(snapshot.ConnectionId));
        RequireText(snapshot.ConnectionState, nameof(snapshot.ConnectionState));
        RequireText(snapshot.EndpointUri, nameof(snapshot.EndpointUri));
        RequireText(snapshot.AuthenticationMode, nameof(snapshot.AuthenticationMode));
        RequireText(snapshot.TransportProfile, nameof(snapshot.TransportProfile));
        RequireText(snapshot.CatalogRevision, nameof(snapshot.CatalogRevision));
        RequireText(snapshot.ToolRevision, nameof(snapshot.ToolRevision));
        RequireText(snapshot.ToolSchemaRevision, nameof(snapshot.ToolSchemaRevision));
        RequireText(snapshot.PermissionMetadataDigest, nameof(snapshot.PermissionMetadataDigest));
        RequireText(snapshot.ToolId, nameof(snapshot.ToolId));
        if (!Uri.TryCreate(snapshot.EndpointUri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Fragment.Length != 0 ||
            snapshot.ResourceUri is { } resource &&
                (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) ||
                    resourceUri.Scheme != Uri.UriSchemeHttps || resourceUri.UserInfo.Length != 0 ||
                    resourceUri.Fragment.Length != 0))
            throw new ArgumentException("A pinned HTTPS remote tool endpoint is required.");
        var registryPinParts = new[]
        {
            snapshot.RegistryServerName,
            snapshot.RegistryServerVersion,
            snapshot.RegistryServerMetadataSha256
        };
        if (registryPinParts.Any(value => value is not null) &&
            registryPinParts.Any(value => string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("A complete pinned registry server identity is required.");
        if (snapshot.RegistryServerName is { } registryName)
        {
            RequireText(registryName, nameof(snapshot.RegistryServerName));
            RequireText(snapshot.RegistryServerVersion, nameof(snapshot.RegistryServerVersion));
        }
        using var arguments = System.Text.Json.JsonDocument.Parse(proof.CanonicalArgumentsJson);
        if (arguments.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("The remote tool result proof must contain canonical object arguments.");
    }

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A valid remote tool result proof value is required.", parameterName);
    }

    private static string HashUtf8(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireHash(string? hash)
    {
        if (hash is not { Length: 64 } ||
            hash.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("A SHA-256 material digest is required.");
    }

    private static void RequireIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':' or '+')))
            throw new ArgumentException("An exact SDK and model binding is required.");
    }
}
