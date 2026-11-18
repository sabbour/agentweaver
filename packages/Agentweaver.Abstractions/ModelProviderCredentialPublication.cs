using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public sealed class ModelProviderBindingScopeJsonConverter
    : JsonStringEnumConverter<ModelProviderBindingScope>
{
    public ModelProviderBindingScopeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialPublicationRequest(
    Guid OperationId,
    long ExpectedProviderRevision,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    string CredentialVersion,
    ModelCredentialIdentity CredentialIdentity,
    SecretRef CredentialReference)
{
    public bool IsValid =>
        OperationId != Guid.Empty &&
        ExpectedProviderRevision is > 0 and < long.MaxValue &&
        Enum.IsDefined(Scope) &&
        (Scope == ModelProviderBindingScope.Platform
            ? ProjectId is null
            : IsIdentifier(ProjectId, 32)) &&
        IsIdentifier(ProviderId, 128) &&
        CredentialIdentity is not null &&
        CredentialIdentity.CredentialSlotId != Guid.Empty &&
        CredentialIdentity.Scope == Scope &&
        CredentialIdentity.ProjectId == ProjectId &&
        CredentialIdentity.ProviderId == ProviderId &&
        CredentialReference is not null &&
        IsSecretIdentifier(CredentialReference.Id) &&
        CredentialReference.Version == CredentialVersion &&
        CredentialVersion is { Length: 32 } &&
        CredentialVersion.All(Uri.IsHexDigit);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsSecretIdentifier(string value) =>
        value is { Length: > 0 and <= 256 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialPublicationReceipt(
    Guid OperationId,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    long ProviderRevision,
    string CredentialVersion,
    string ConfigurationHash,
    ModelCredentialIdentity CredentialIdentity,
    SecretRef CredentialReference)
{
    public bool IsValid =>
        OperationId != Guid.Empty &&
        Enum.IsDefined(Scope) &&
        (Scope == ModelProviderBindingScope.Platform
            ? ProjectId is null
            : IsIdentifier(ProjectId, 32)) &&
        IsIdentifier(ProviderId, 128) &&
        ProviderRevision > 0 &&
        CredentialIdentity is not null &&
        CredentialIdentity.CredentialSlotId != Guid.Empty &&
        CredentialIdentity.Scope == Scope &&
        CredentialIdentity.ProjectId == ProjectId &&
        CredentialIdentity.ProviderId == ProviderId &&
        CredentialReference is not null &&
        IsSecretIdentifier(CredentialReference.Id) &&
        CredentialReference.Version == CredentialVersion &&
        CredentialVersion is { Length: 32 } &&
        CredentialVersion.All(Uri.IsHexDigit) &&
        ConfigurationHash is { Length: 64 } &&
        ConfigurationHash.All(Uri.IsHexDigit);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsSecretIdentifier(string value) =>
        value is { Length: > 0 and <= 256 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
