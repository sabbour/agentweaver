using System.Collections.Immutable;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>Names the JSON Schema subset accepted by Canvas contracts and providers.</summary>
public static class CanvasSchemaDialects
{
    public const string Draft202012SubsetV1 =
        "urn:agentweaver:canvas-schema:2020-12-subset:v1";
}

/// <summary>Pins a Canvas provider adapter and its compatibility profile for discovery and calls.</summary>
public sealed record CanvasProviderProfile(
    string ProviderId,
    Version AdapterVersion,
    string ProfileId,
    string ProfileRevision,
    string FormatVersion);

/// <summary>Pairs a supported schema dialect with the JSON schema definition it describes.</summary>
public sealed record CanvasJsonSchema(string Dialect, JsonElement Definition);

/// <summary>Describes one callable Canvas action and the JSON shapes of its input and output.</summary>
public sealed record CanvasActionDescriptor(
    string ActionId,
    CanvasJsonSchema InputSchema,
    CanvasJsonSchema OutputSchema);

/// <summary>
/// Provider-produced description of a Canvas type; callers use it to validate open input,
/// available actions, and required content before creating an instance.
/// </summary>
public sealed record CanvasTypeDescriptor(
    string TypeId,
    string DescriptorRevision,
    CanvasProviderProfile ProviderProfile,
    CanvasJsonSchema? OpenInputSchema,
    ImmutableArray<CanvasActionDescriptor> Actions,
    ImmutableArray<string> ContentRequirements);

/// <summary>Scopes Canvas operations to one project and session.</summary>
public sealed record CanvasRequestScope(string ProjectId, string SessionId);

/// <summary>Pins a content revision to its SHA-256 digest so the provider reads exact content.</summary>
public sealed record CanvasContentReference(string Revision, string Sha256);

/// <summary>
/// Request from a Canvas caller to open an instance with an exact provider, type, content,
/// and configuration revision; the idempotency key makes retries identifiable.
/// </summary>
public sealed record CanvasOpenRequest(
    CanvasRequestScope Scope,
    string InstanceId,
    CanvasProviderProfile ProviderProfile,
    string TypeId,
    string DescriptorRevision,
    CanvasContentReference Content,
    string ConfigurationRevision,
    string IdempotencyKey,
    JsonElement? Input);

/// <summary>
/// Provider-returned binding for an opened instance. Callers send it back as an expected
/// binding so the provider can reject stale actions or close requests.
/// </summary>
public sealed record CanvasInstanceBinding(
    CanvasRequestScope Scope,
    string InstanceId,
    CanvasProviderProfile ProviderProfile,
    string TypeId,
    string DescriptorRevision,
    CanvasContentReference Content,
    string ConfigurationRevision,
    long StateRevision);

/// <summary>Requests current state for a Canvas instance within its original scope.</summary>
public sealed record CanvasReadRequest(CanvasRequestScope Scope, string InstanceId);

/// <summary>Requests one advertised action against the exact state and provider binding.</summary>
public sealed record CanvasActionRequest(
    CanvasInstanceBinding ExpectedBinding,
    string ActionId,
    string IdempotencyKey,
    JsonElement Input);

/// <summary>Requests idempotent closure of the instance represented by the expected binding.</summary>
public sealed record CanvasCloseRequest(
    CanvasInstanceBinding ExpectedBinding,
    string IdempotencyKey);
