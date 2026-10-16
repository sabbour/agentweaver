using System.Collections.Immutable;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public static class CanvasSchemaDialects
{
    public const string Draft202012SubsetV1 =
        "urn:agentweaver:canvas-schema:2020-12-subset:v1";
}

public sealed record CanvasProviderProfile(
    string ProviderId,
    Version AdapterVersion,
    string ProfileId,
    string ProfileRevision,
    string FormatVersion);

public sealed record CanvasJsonSchema(string Dialect, JsonElement Definition);

public sealed record CanvasActionDescriptor(
    string ActionId,
    CanvasJsonSchema InputSchema,
    CanvasJsonSchema OutputSchema);

public sealed record CanvasTypeDescriptor(
    string TypeId,
    string DescriptorRevision,
    CanvasProviderProfile ProviderProfile,
    CanvasJsonSchema? OpenInputSchema,
    ImmutableArray<CanvasActionDescriptor> Actions,
    ImmutableArray<string> ContentRequirements);

public sealed record CanvasRequestScope(string ProjectId, string SessionId);

public sealed record CanvasContentReference(string Revision, string Sha256);

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

public sealed record CanvasInstanceBinding(
    CanvasRequestScope Scope,
    string InstanceId,
    CanvasProviderProfile ProviderProfile,
    string TypeId,
    string DescriptorRevision,
    CanvasContentReference Content,
    string ConfigurationRevision,
    long StateRevision);

public sealed record CanvasReadRequest(CanvasRequestScope Scope, string InstanceId);

public sealed record CanvasActionRequest(
    CanvasInstanceBinding ExpectedBinding,
    string ActionId,
    string IdempotencyKey,
    JsonElement Input);

public sealed record CanvasCloseRequest(
    CanvasInstanceBinding ExpectedBinding,
    string IdempotencyKey);
