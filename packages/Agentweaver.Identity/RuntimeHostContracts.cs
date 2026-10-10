using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record EnvironmentRuntimeReadinessContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    DateTimeOffset LeaseCreatedAt,
    SandboxObservation Observation,
    SandboxStartupTimeBudgets StartupBudgets,
    string WorkspaceMountPath);

public sealed record EnvironmentRuntimeWorkspaceContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    WorkspaceVolumeAttachmentNegotiation Workspace,
    long TransitionRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostConfigureRequest(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    JsonElement Configuration);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSessionProof(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid SourceGrantId,
    long SourceGrantRevision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeCredentialPurpose>))]
    RuntimeCredentialPurpose Purpose);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRefreshRequest(RuntimeHostSessionProof Proof, Guid OperationId);

public sealed record RuntimeHostReadinessReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    SandboxImageIdentity Image,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    RuntimeGrantReceipt SourceGrant);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ATextPart(string Kind, string Text);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMetadata(RuntimeHostSessionProof Runtime, AddressedMessageDeliveryMode DeliveryMode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMessage(
    string Kind,
    Guid MessageId,
    string ContextId,
    string Role,
    ImmutableArray<RuntimeA2ATextPart> Parts,
    RuntimeA2AMetadata Metadata);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ASendRequest(RuntimeA2AMessage Message);

public sealed record RuntimeA2AResponse(
    string Kind, Guid MessageId, string ContextId, string Role, ImmutableArray<RuntimeA2ATextPart> Parts);
