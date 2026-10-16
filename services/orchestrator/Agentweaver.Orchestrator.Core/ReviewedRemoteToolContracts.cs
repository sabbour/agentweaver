using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public sealed class ReviewedRemoteToolPermissionMetadata
{
    public ReviewedRemoteToolPermissionMetadata(
        string actionId,
        string purpose,
        string metadataJson)
    {
        ActionId = ReviewedRemoteToolContractValidation.RequireText(actionId, nameof(actionId));
        Purpose = ReviewedRemoteToolContractValidation.RequireText(purpose, nameof(purpose));
        CanonicalMetadataJson = ReviewedRemoteToolJson.CanonicalizeObject(metadataJson, nameof(metadataJson));
        Digest = ReviewedRemoteToolJson.Hash(Encoding.UTF8.GetBytes(CanonicalMetadataJson));
    }

    public string ActionId { get; }
    public string Purpose { get; }
    public string CanonicalMetadataJson { get; }
    public string Digest { get; }
}

public sealed class ReviewedRemoteToolRegistryServerPin
{
    public ReviewedRemoteToolRegistryServerPin(
        string serverName,
        string exactVersion,
        string metadataSha256)
    {
        ServerName = ReviewedRemoteToolContractValidation.RequireText(serverName, nameof(serverName));
        ExactVersion = ReviewedRemoteToolContractValidation.RequireText(exactVersion, nameof(exactVersion));
        MetadataSha256 =
            ReviewedRemoteToolContractValidation.RequireDigest(metadataSha256, nameof(metadataSha256));
    }

    public string ServerName { get; }
    public string ExactVersion { get; }
    public string MetadataSha256 { get; }
}

public sealed class ReviewedRemoteToolSnapshot
{
    public ReviewedRemoteToolSnapshot(
        Guid snapshotId,
        string projectId,
        string agentId,
        string nodeId,
        string connectionId,
        long connectionRowRevision,
        long connectionConfigurationRevision,
        long? connectionDiscoveryRevision,
        string connectionState,
        string configurationSha256,
        string endpointUri,
        string? resourceUri,
        string authenticationMode,
        string? identityBindingReference,
        string transportProfile,
        ReviewedRemoteToolRegistryServerPin? registryServerPin,
        string catalogRevision,
        string catalogDigest,
        string toolId,
        string toolRevision,
        string toolDigest,
        string toolSchemaRevision,
        string toolSchemaDigest,
        string toolMetadataJson,
        string toolSchemaJson,
        ReviewedRemoteToolPermissionMetadata permission)
    {
        SnapshotId = snapshotId != Guid.Empty
            ? snapshotId
            : throw new ArgumentException("A reviewed snapshot ID is required.", nameof(snapshotId));
        ProjectId = ReviewedRemoteToolContractValidation.RequireText(projectId, nameof(projectId));
        AgentId = ReviewedRemoteToolContractValidation.RequireText(agentId, nameof(agentId));
        NodeId = ReviewedRemoteToolContractValidation.RequireText(nodeId, nameof(nodeId));
        ConnectionId = ReviewedRemoteToolContractValidation.RequireText(connectionId, nameof(connectionId));
        ConnectionRowRevision = connectionRowRevision > 0
            ? connectionRowRevision
            : throw new ArgumentOutOfRangeException(nameof(connectionRowRevision));
        ConnectionConfigurationRevision = connectionConfigurationRevision > 0
            ? connectionConfigurationRevision
            : throw new ArgumentOutOfRangeException(nameof(connectionConfigurationRevision));
        ConnectionDiscoveryRevision = connectionDiscoveryRevision is null or > 0
            ? connectionDiscoveryRevision
            : throw new ArgumentOutOfRangeException(nameof(connectionDiscoveryRevision));
        ConnectionState = ReviewedRemoteToolContractValidation.RequireConnectionState(
            connectionState, nameof(connectionState));
        ConfigurationSha256 =
            ReviewedRemoteToolContractValidation.RequireDigest(configurationSha256, nameof(configurationSha256));
        EndpointUri = ReviewedRemoteToolContractValidation.RequireText(endpointUri, nameof(endpointUri));
        ResourceUri = ReviewedRemoteToolContractValidation.RequireOptionalText(resourceUri, nameof(resourceUri));
        AuthenticationMode = ReviewedRemoteToolContractValidation.RequireAuthenticationMode(
            authenticationMode, nameof(authenticationMode));
        IdentityBindingReference = ReviewedRemoteToolContractValidation.RequireOptionalText(
            identityBindingReference, nameof(identityBindingReference));
        TransportProfile = ReviewedRemoteToolContractValidation.RequireTransportProfile(
            transportProfile, nameof(transportProfile));
        RegistryServerPin = registryServerPin;
        CatalogRevision = ReviewedRemoteToolContractValidation.RequireText(catalogRevision, nameof(catalogRevision));
        CatalogDigest = ReviewedRemoteToolContractValidation.RequireDigest(catalogDigest, nameof(catalogDigest));
        ToolId = ReviewedRemoteToolContractValidation.RequireText(toolId, nameof(toolId));
        ToolRevision = ReviewedRemoteToolContractValidation.RequireText(toolRevision, nameof(toolRevision));
        ToolDigest = ReviewedRemoteToolContractValidation.RequireDigest(toolDigest, nameof(toolDigest));
        ToolSchemaRevision = ReviewedRemoteToolContractValidation.RequireText(
            toolSchemaRevision, nameof(toolSchemaRevision));
        ToolSchemaDigest = ReviewedRemoteToolContractValidation.RequireDigest(
            toolSchemaDigest, nameof(toolSchemaDigest));
        CanonicalToolMetadataJson = ReviewedRemoteToolJson.CanonicalizeObject(
            toolMetadataJson, nameof(toolMetadataJson));
        CanonicalToolSchemaJson = ReviewedRemoteToolJson.CanonicalizeObject(
            toolSchemaJson, nameof(toolSchemaJson));
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
        SnapshotDigest = ReviewedRemoteToolJson.Hash(WriteSnapshotDigestInput());
        Reference = new(ProjectId, SnapshotId, SnapshotDigest, AgentId, NodeId);
    }

    public Guid SnapshotId { get; }
    public string ProjectId { get; }
    public string AgentId { get; }
    public string NodeId { get; }
    public string ConnectionId { get; }
    public long ConnectionRowRevision { get; }
    public long ConnectionConfigurationRevision { get; }
    public long? ConnectionDiscoveryRevision { get; }
    public string ConnectionState { get; }
    public string ConfigurationSha256 { get; }
    public string EndpointUri { get; }
    public string? ResourceUri { get; }
    public string AuthenticationMode { get; }
    public string? IdentityBindingReference { get; }
    public string TransportProfile { get; }
    public ReviewedRemoteToolRegistryServerPin? RegistryServerPin { get; }
    public string CatalogRevision { get; }
    public string CatalogDigest { get; }
    public string ToolId { get; }
    public string ToolRevision { get; }
    public string ToolDigest { get; }
    public string ToolSchemaRevision { get; }
    public string ToolSchemaDigest { get; }
    public string CanonicalToolMetadataJson { get; }
    public string CanonicalToolSchemaJson { get; }
    public ReviewedRemoteToolPermissionMetadata Permission { get; }
    public string SnapshotDigest { get; }
    public ReviewedRemoteToolSnapshotReference Reference { get; }

    private byte[] WriteSnapshotDigestInput()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshotId", SnapshotId);
            writer.WriteString("projectId", ProjectId);
            writer.WriteString("agentId", AgentId);
            writer.WriteString("nodeId", NodeId);
            writer.WriteString("connectionId", ConnectionId);
            writer.WriteNumber("connectionRowRevision", ConnectionRowRevision);
            writer.WriteNumber("connectionConfigurationRevision", ConnectionConfigurationRevision);
            if (ConnectionDiscoveryRevision is { } discoveryRevision)
                writer.WriteNumber("connectionDiscoveryRevision", discoveryRevision);
            else
                writer.WriteNull("connectionDiscoveryRevision");
            writer.WriteString("connectionState", ConnectionState);
            writer.WriteString("configurationSha256", ConfigurationSha256);
            writer.WriteString("endpointUri", EndpointUri);
            if (ResourceUri is null)
                writer.WriteNull("resourceUri");
            else
                writer.WriteString("resourceUri", ResourceUri);
            writer.WriteString("authenticationMode", AuthenticationMode);
            if (IdentityBindingReference is null)
                writer.WriteNull("identityBindingReference");
            else
                writer.WriteString("identityBindingReference", IdentityBindingReference);
            writer.WriteString("transportProfile", TransportProfile);
            if (RegistryServerPin is null)
                writer.WriteNull("registryServerPin");
            else
            {
                writer.WritePropertyName("registryServerPin");
                writer.WriteStartObject();
                writer.WriteString("serverName", RegistryServerPin.ServerName);
                writer.WriteString("exactVersion", RegistryServerPin.ExactVersion);
                writer.WriteString("metadataSha256", RegistryServerPin.MetadataSha256);
                writer.WriteEndObject();
            }
            writer.WriteString("catalogRevision", CatalogRevision);
            writer.WriteString("catalogDigest", CatalogDigest);
            writer.WriteString("toolId", ToolId);
            writer.WriteString("toolRevision", ToolRevision);
            writer.WriteString("toolDigest", ToolDigest);
            writer.WriteString("toolSchemaRevision", ToolSchemaRevision);
            writer.WriteString("toolSchemaDigest", ToolSchemaDigest);
            writer.WriteString("toolMetadata", CanonicalToolMetadataJson);
            writer.WriteString("toolSchema", CanonicalToolSchemaJson);
            writer.WriteString("permissionActionId", Permission.ActionId);
            writer.WriteString("permissionPurpose", Permission.Purpose);
            writer.WriteString("permissionMetadataDigest", Permission.Digest);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

public sealed class ReviewedRemoteToolCall
{
    private ReviewedRemoteToolCall(
        ReviewedRemoteToolSnapshotReference snapshot,
        RemoteToolCallExecutionBinding executionBinding,
        string nativeCallId,
        string canonicalArgumentsJson,
        string argumentsDigest,
        Guid eventId,
        string inputHash,
        string actionId,
        string purpose,
        string toolId,
        string toolSchemaDigest,
        string permissionMetadataDigest)
    {
        Snapshot = snapshot;
        ExecutionBinding = executionBinding;
        NativeCallId = nativeCallId;
        CanonicalArgumentsJson = canonicalArgumentsJson;
        ArgumentsDigest = argumentsDigest;
        EventId = eventId;
        InputHash = inputHash;
        ActionId = actionId;
        Purpose = purpose;
        ToolId = toolId;
        ToolSchemaDigest = toolSchemaDigest;
        PermissionMetadataDigest = permissionMetadataDigest;
    }

    public ReviewedRemoteToolSnapshotReference Snapshot { get; }
    public RemoteToolCallExecutionBinding ExecutionBinding { get; }
    public string ProjectId => ExecutionBinding.ProjectId;
    public string RunId => ExecutionBinding.RunId;
    public string SessionId => ExecutionBinding.SessionId;
    public string StepId => ExecutionBinding.StepId;
    public string NativeCallId { get; }
    public string CanonicalArgumentsJson { get; }
    public string ArgumentsDigest { get; }
    public Guid EventId { get; }
    public string InputHash { get; }
    public string ActionId { get; }
    public string Purpose { get; }
    public string ToolId { get; }
    public string ToolSchemaDigest { get; }
    public string PermissionMetadataDigest { get; }

    public static ReviewedRemoteToolCall Create(
        ReviewedRemoteToolSnapshot snapshot,
        RemoteToolCallExecutionBinding executionBinding,
        string nativeCallId,
        string agentId,
        string nodeId,
        string toolId,
        string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(executionBinding);
        nativeCallId = ReviewedRemoteToolContractValidation.RequireText(nativeCallId, nameof(nativeCallId));
        agentId = ReviewedRemoteToolContractValidation.RequireText(agentId, nameof(agentId));
        nodeId = ReviewedRemoteToolContractValidation.RequireText(nodeId, nameof(nodeId));
        toolId = ReviewedRemoteToolContractValidation.RequireText(toolId, nameof(toolId));

        if (snapshot.ProjectId != executionBinding.ProjectId ||
            snapshot.AgentId != agentId ||
            snapshot.NodeId != nodeId ||
            snapshot.ToolId != toolId)
            throw new ArgumentException("The tool call does not match the reviewed agent, node, and tool snapshot.");

        var canonicalArguments = ReviewedRemoteToolJson.CanonicalizeObject(argumentsJson, nameof(argumentsJson));
        var argumentsDigest = ReviewedRemoteToolJson.Hash(Encoding.UTF8.GetBytes(canonicalArguments));
        var eventId = CreateEventId(
            executionBinding.ProjectId, executionBinding.RunId, executionBinding.SessionId,
            executionBinding.StepId, nativeCallId);
        var inputHash = ReviewedRemoteToolJson.Hash(WriteCallInput(
            snapshot, executionBinding, nativeCallId, canonicalArguments));
        return new(
            snapshot.Reference,
            executionBinding,
            nativeCallId,
            canonicalArguments,
            argumentsDigest,
            eventId,
            inputHash,
            snapshot.Permission.ActionId,
            snapshot.Permission.Purpose,
            snapshot.ToolId,
            snapshot.ToolSchemaDigest,
            snapshot.Permission.Digest);
    }

    private static Guid CreateEventId(
        string projectId,
        string runId,
        string sessionId,
        string stepId,
        string nativeCallId)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("agentweaver.remote-tool-call.v1");
            writer.Write(projectId);
            writer.Write(runId);
            writer.Write(sessionId);
            writer.Write(stepId);
            writer.Write(nativeCallId);
        }

        var digest = SHA256.HashData(stream.ToArray());
        var eventId = new Guid(digest.AsSpan(0, 16));
        return eventId == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : eventId;
    }

    private static byte[] WriteCallInput(
        ReviewedRemoteToolSnapshot snapshot,
        RemoteToolCallExecutionBinding binding,
        string nativeCallId,
        string canonicalArgumentsJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("runtimeInstanceId", binding.RuntimeInstanceId);
            writer.WriteNumber("registrationRevision", binding.RegistrationRevision);
            writer.WriteString("actorIssuer", binding.ActorIssuer);
            writer.WriteString("actorId", binding.ActorId);
            writer.WriteString("tenantId", binding.TenantId);
            writer.WriteString("projectId", binding.ProjectId);
            writer.WriteString("runId", binding.RunId);
            writer.WriteString("sessionId", binding.SessionId);
            writer.WriteString("stepId", binding.StepId);
            writer.WriteNumber("projectRevision", binding.ProjectRevision);
            writer.WriteNumber("projectConfigurationRevision", binding.ProjectConfigurationRevision);
            writer.WriteNumber("platformRuntimeRevision", binding.PlatformRuntimeRevision);
            writer.WriteString("contextRevision", binding.ContextRevision);
            writer.WriteString("acceptedSelectionHash", binding.AcceptedSelectionHash);
            writer.WriteNumber("executionFence", binding.ExecutionFence);
            writer.WriteString("nativeCallId", nativeCallId);
            writer.WriteString("snapshotId", snapshot.SnapshotId);
            writer.WriteString("snapshotDigest", snapshot.SnapshotDigest);
            writer.WriteString("agentId", snapshot.AgentId);
            writer.WriteString("nodeId", snapshot.NodeId);
            writer.WriteString("connectionId", snapshot.ConnectionId);
            writer.WriteNumber("connectionRowRevision", snapshot.ConnectionRowRevision);
            writer.WriteNumber("connectionConfigurationRevision", snapshot.ConnectionConfigurationRevision);
            if (snapshot.ConnectionDiscoveryRevision is { } discoveryRevision)
                writer.WriteNumber("connectionDiscoveryRevision", discoveryRevision);
            else
                writer.WriteNull("connectionDiscoveryRevision");
            writer.WriteString("connectionState", snapshot.ConnectionState);
            writer.WriteString("configurationSha256", snapshot.ConfigurationSha256);
            writer.WriteString("endpointUri", snapshot.EndpointUri);
            if (snapshot.ResourceUri is null)
                writer.WriteNull("resourceUri");
            else
                writer.WriteString("resourceUri", snapshot.ResourceUri);
            writer.WriteString("authenticationMode", snapshot.AuthenticationMode);
            if (snapshot.IdentityBindingReference is null)
                writer.WriteNull("identityBindingReference");
            else
                writer.WriteString("identityBindingReference", snapshot.IdentityBindingReference);
            writer.WriteString("transportProfile", snapshot.TransportProfile);
            if (snapshot.RegistryServerPin is null)
                writer.WriteNull("registryServerPin");
            else
            {
                writer.WritePropertyName("registryServerPin");
                writer.WriteStartObject();
                writer.WriteString("serverName", snapshot.RegistryServerPin.ServerName);
                writer.WriteString("exactVersion", snapshot.RegistryServerPin.ExactVersion);
                writer.WriteString("metadataSha256", snapshot.RegistryServerPin.MetadataSha256);
                writer.WriteEndObject();
            }
            writer.WriteString("catalogRevision", snapshot.CatalogRevision);
            writer.WriteString("catalogDigest", snapshot.CatalogDigest);
            writer.WriteString("toolId", snapshot.ToolId);
            writer.WriteString("toolRevision", snapshot.ToolRevision);
            writer.WriteString("toolDigest", snapshot.ToolDigest);
            writer.WriteString("toolSchemaRevision", snapshot.ToolSchemaRevision);
            writer.WriteString("toolSchemaDigest", snapshot.ToolSchemaDigest);
            writer.WriteString("actionId", snapshot.Permission.ActionId);
            writer.WriteString("purpose", snapshot.Permission.Purpose);
            writer.WriteString("permissionMetadataDigest", snapshot.Permission.Digest);
            writer.WritePropertyName("arguments");
            writer.WriteRawValue(canonicalArgumentsJson);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

public sealed class RemoteToolCallExecutionBinding
{
    public RemoteToolCallExecutionBinding(
        Guid runtimeInstanceId,
        long registrationRevision,
        string actorIssuer,
        string actorId,
        string tenantId,
        string projectId,
        string runId,
        string sessionId,
        string stepId,
        long projectRevision,
        long projectConfigurationRevision,
        long platformRuntimeRevision,
        string contextRevision,
        string acceptedSelectionHash,
        long executionFence)
    {
        RuntimeInstanceId = runtimeInstanceId != Guid.Empty
            ? runtimeInstanceId
            : throw new ArgumentException("A runtime instance ID is required.", nameof(runtimeInstanceId));
        RegistrationRevision = registrationRevision > 0
            ? registrationRevision
            : throw new ArgumentOutOfRangeException(nameof(registrationRevision));
        ActorIssuer = ReviewedRemoteToolContractValidation.RequireText(actorIssuer, nameof(actorIssuer));
        ActorId = ReviewedRemoteToolContractValidation.RequireText(actorId, nameof(actorId));
        TenantId = ReviewedRemoteToolContractValidation.RequireText(tenantId, nameof(tenantId));
        ProjectId = ReviewedRemoteToolContractValidation.RequireText(projectId, nameof(projectId));
        RunId = ReviewedRemoteToolContractValidation.RequireText(runId, nameof(runId));
        SessionId = ReviewedRemoteToolContractValidation.RequireText(sessionId, nameof(sessionId));
        StepId = ReviewedRemoteToolContractValidation.RequireText(stepId, nameof(stepId));
        ProjectRevision = projectRevision > 0 ? projectRevision : throw new ArgumentOutOfRangeException(nameof(projectRevision));
        ProjectConfigurationRevision = projectConfigurationRevision > 0
            ? projectConfigurationRevision
            : throw new ArgumentOutOfRangeException(nameof(projectConfigurationRevision));
        PlatformRuntimeRevision = platformRuntimeRevision > 0
            ? platformRuntimeRevision
            : throw new ArgumentOutOfRangeException(nameof(platformRuntimeRevision));
        ContextRevision = ReviewedRemoteToolContractValidation.RequireText(contextRevision, nameof(contextRevision));
        AcceptedSelectionHash =
            ReviewedRemoteToolContractValidation.RequireDigest(acceptedSelectionHash, nameof(acceptedSelectionHash));
        ExecutionFence = executionFence > 0
            ? executionFence
            : throw new ArgumentOutOfRangeException(nameof(executionFence));
    }

    public Guid RuntimeInstanceId { get; }
    public long RegistrationRevision { get; }
    public string ActorIssuer { get; }
    public string ActorId { get; }
    public string TenantId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string SessionId { get; }
    public string StepId { get; }
    public long ProjectRevision { get; }
    public long ProjectConfigurationRevision { get; }
    public long PlatformRuntimeRevision { get; }
    public string ContextRevision { get; }
    public string AcceptedSelectionHash { get; }
    public long ExecutionFence { get; }
}

public enum RemoteToolResultContentClass
{
    Unknown,
    TextOnly,
    Structured,
    NonText,
    Mixed
}

public sealed class RemoteToolCallAuthorityBinding
{
    public RemoteToolCallAuthorityBinding(
        Guid runtimeInstanceId,
        long registrationRevision,
        string actorIssuer,
        string actorId,
        string tenantId,
        string projectId,
        string runId,
        string sessionId,
        string stepId,
        long projectRevision,
        long projectConfigurationRevision,
        long platformRuntimeRevision,
        string contextRevision,
        string acceptedSelectionHash,
        long executionFence,
        string grantId,
        string grantRevision)
    {
        RuntimeInstanceId = runtimeInstanceId != Guid.Empty
            ? runtimeInstanceId
            : throw new ArgumentException("A runtime instance ID is required.", nameof(runtimeInstanceId));
        RegistrationRevision = registrationRevision > 0
            ? registrationRevision
            : throw new ArgumentOutOfRangeException(nameof(registrationRevision));
        ActorIssuer = ReviewedRemoteToolContractValidation.RequireText(actorIssuer, nameof(actorIssuer));
        ActorId = ReviewedRemoteToolContractValidation.RequireText(actorId, nameof(actorId));
        TenantId = ReviewedRemoteToolContractValidation.RequireText(tenantId, nameof(tenantId));
        ProjectId = ReviewedRemoteToolContractValidation.RequireText(projectId, nameof(projectId));
        RunId = ReviewedRemoteToolContractValidation.RequireText(runId, nameof(runId));
        SessionId = ReviewedRemoteToolContractValidation.RequireText(sessionId, nameof(sessionId));
        StepId = ReviewedRemoteToolContractValidation.RequireText(stepId, nameof(stepId));
        ProjectRevision = projectRevision > 0 ? projectRevision : throw new ArgumentOutOfRangeException(nameof(projectRevision));
        ProjectConfigurationRevision = projectConfigurationRevision > 0
            ? projectConfigurationRevision
            : throw new ArgumentOutOfRangeException(nameof(projectConfigurationRevision));
        PlatformRuntimeRevision = platformRuntimeRevision > 0
            ? platformRuntimeRevision
            : throw new ArgumentOutOfRangeException(nameof(platformRuntimeRevision));
        ContextRevision = ReviewedRemoteToolContractValidation.RequireText(contextRevision, nameof(contextRevision));
        AcceptedSelectionHash =
            ReviewedRemoteToolContractValidation.RequireDigest(acceptedSelectionHash, nameof(acceptedSelectionHash));
        ExecutionFence = executionFence > 0
            ? executionFence
            : throw new ArgumentOutOfRangeException(nameof(executionFence));
        GrantId = ReviewedRemoteToolContractValidation.RequireText(grantId, nameof(grantId));
        GrantRevision = ReviewedRemoteToolContractValidation.RequireText(grantRevision, nameof(grantRevision));
    }

    public Guid RuntimeInstanceId { get; }
    public long RegistrationRevision { get; }
    public string ActorIssuer { get; }
    public string ActorId { get; }
    public string TenantId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string SessionId { get; }
    public string StepId { get; }
    public long ProjectRevision { get; }
    public long ProjectConfigurationRevision { get; }
    public long PlatformRuntimeRevision { get; }
    public string ContextRevision { get; }
    public string AcceptedSelectionHash { get; }
    public long ExecutionFence { get; }
    public string GrantId { get; }
    public string GrantRevision { get; }

    internal bool Matches(RemoteToolCallExecutionBinding binding) =>
        RuntimeInstanceId == binding.RuntimeInstanceId &&
        RegistrationRevision == binding.RegistrationRevision &&
        ActorIssuer == binding.ActorIssuer &&
        ActorId == binding.ActorId &&
        TenantId == binding.TenantId &&
        ProjectId == binding.ProjectId &&
        RunId == binding.RunId &&
        SessionId == binding.SessionId &&
        StepId == binding.StepId &&
        ProjectRevision == binding.ProjectRevision &&
        ProjectConfigurationRevision == binding.ProjectConfigurationRevision &&
        PlatformRuntimeRevision == binding.PlatformRuntimeRevision &&
        ContextRevision == binding.ContextRevision &&
        AcceptedSelectionHash == binding.AcceptedSelectionHash &&
        ExecutionFence == binding.ExecutionFence;
}

public enum RemoteToolGuardrailStage
{
    ToolResult
}

public sealed class RemoteToolResultEnvelope<TContent>
{
    public RemoteToolResultEnvelope(
        ReviewedRemoteToolCall call,
        RemoteToolCallAuthorityBinding authority,
        RemoteToolResultContentClass contentClass,
        TContent content)
    {
        ArgumentNullException.ThrowIfNull(call);
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (!Enum.IsDefined(contentClass))
            throw new ArgumentOutOfRangeException(nameof(contentClass));
        if (!authority.Matches(call.ExecutionBinding))
            throw new ArgumentException("The current authority does not match the remote tool call.", nameof(authority));

        OperationId = call.EventId;
        Snapshot = call.Snapshot;
        AgentId = call.Snapshot.AgentId;
        NodeId = call.Snapshot.NodeId;
        ToolId = call.ToolId;
        ToolSchemaDigest = call.ToolSchemaDigest;
        PermissionMetadataDigest = call.PermissionMetadataDigest;
        ArgumentsDigest = call.ArgumentsDigest;
        ContentClass = contentClass;
        Content = content;
    }

    public RemoteToolGuardrailStage Stage => RemoteToolGuardrailStage.ToolResult;
    public Guid OperationId { get; }
    public ReviewedRemoteToolSnapshotReference Snapshot { get; }
    public string AgentId { get; }
    public string NodeId { get; }
    public string ToolId { get; }
    public string ToolSchemaDigest { get; }
    public string PermissionMetadataDigest { get; }
    public string ArgumentsDigest { get; }
    public RemoteToolCallAuthorityBinding Authority { get; }
    public RemoteToolResultContentClass ContentClass { get; }
    public TContent Content { get; }
}

internal static class ReviewedRemoteToolJson
{
    public static string CanonicalizeObject(string json, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json, parameterName);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A JSON object is required.", parameterName);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(document.RootElement, writer, parameterName);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The JSON object is invalid.", parameterName, exception);
        }
    }

    public static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, string parameterName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (properties.Any(property => !names.Add(property.Name)))
                    throw new ArgumentException("Duplicate JSON object properties are not allowed.", parameterName);
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, parameterName);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer, parameterName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("The JSON value is not supported.", parameterName);
        }
    }
}

internal static class ReviewedRemoteToolContractValidation
{
    public static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A valid value is required.", parameterName);
        return value;
    }

    public static string RequireDigest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        return value;
    }

    public static string? RequireOptionalText(string? value, string parameterName) =>
        value is null ? null : RequireText(value, parameterName);

    public static string RequireConnectionState(string value, string parameterName)
    {
        value = RequireText(value, parameterName);
        return value is "Draft" or "Enabled" or "Disabled" or "Removed"
            ? value
            : throw new ArgumentException("A recognized remote MCP connection state is required.", parameterName);
    }

    public static string RequireAuthenticationMode(string value, string parameterName)
    {
        value = RequireText(value, parameterName);
        return value is "None" or "DelegatedOAuth"
            ? value
            : throw new ArgumentException("A supported remote MCP authentication mode is required.", parameterName);
    }

    public static string RequireTransportProfile(string value, string parameterName)
    {
        value = RequireText(value, parameterName);
        return value is "StreamableHttp20250618"
            ? value
            : throw new ArgumentException("A supported remote MCP transport profile is required.", parameterName);
    }
}
