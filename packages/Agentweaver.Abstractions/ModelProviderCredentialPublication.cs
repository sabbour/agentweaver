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
    [JsonIgnore]
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
    [JsonIgnore]
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialPublicationPreflightRequest(
    Guid OperationId,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    string Issuer,
    string ActorId,
    string? TenantSelector,
    string? TenantClaim,
    string? BoundProjectId,
    string Mode)
{
    [JsonIgnore]
    public bool IsValid =>
        OperationId != Guid.Empty &&
        Enum.IsDefined(Scope) &&
        (Scope == ModelProviderBindingScope.Platform
            ? ProjectId is null && BoundProjectId is null
            : IsIdentifier(ProjectId, 32) && BoundProjectId == ProjectId) &&
        IsIdentifier(ProviderId, 128) &&
        IsIssuer(Issuer) &&
        IsIdentifier(ActorId, 256) &&
        (TenantSelector is null || IsIdentifier(TenantSelector, 256)) &&
        (TenantClaim is null || IsIdentifier(TenantClaim, 256)) &&
        Mode is ("draft-snapshot" or "authority-only");

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsIssuer(string value) =>
        value is { Length: > 0 and <= 2048 } &&
        value.All(c => c is >= '!' and <= '~') &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 &&
        StringComparer.Ordinal.Equals(value, uri.AbsoluteUri);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialPublicationPreflightResponse(
    ModelProviderCredentialPublicationPreflightRequest Request,
    Guid PreflightId,
    string ResolvedTenant,
    Guid MembershipId,
    long MembershipRevision,
    Guid AssignmentId,
    long AssignmentRevision,
    long ProviderRevision,
    string ProviderState,
    Guid? CredentialSlotId,
    long? CredentialRevision,
    ModelProviderCredentialPublicationReceipt? ExistingReceipt,
    bool IsCancelled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialPublicationV2Request(
    Guid OperationId,
    Guid PreflightId,
    long ExpectedProviderRevision,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    string CredentialVersion,
    ModelCredentialIdentity CredentialIdentity,
    SecretRef CredentialReference,
    string Issuer,
    string ActorId,
    string OriginalTenant,
    Guid OriginalMembershipId,
    long OriginalMembershipRevision,
    string? TenantSelector,
    string? TenantClaim)
{
    [JsonIgnore]
    public bool IsValid =>
        PreflightId != Guid.Empty &&
        new ModelProviderCredentialPublicationRequest(
            OperationId, ExpectedProviderRevision, Scope, ProjectId, ProviderId,
            CredentialVersion, CredentialIdentity, CredentialReference).IsValid &&
        IsIdentifier(Issuer, 2048) &&
        IsIdentifier(ActorId, 256) &&
        IsIdentifier(OriginalTenant, 256) &&
        OriginalMembershipId != Guid.Empty &&
        OriginalMembershipRevision > 0 &&
        (TenantSelector is null || IsIdentifier(TenantSelector, 256)) &&
        (TenantClaim is null || IsIdentifier(TenantClaim, 256));

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ':');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialCancelRequest(
    Guid OperationId,
    Guid PreflightId,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    string Issuer,
    string ActorId,
    string OriginalTenant,
    Guid OriginalMembershipId,
    long OriginalMembershipRevision,
    string? TenantSelector,
    string? TenantClaim)
{
    [JsonIgnore]
    public bool IsValid =>
        OperationId != Guid.Empty &&
        PreflightId != Guid.Empty &&
        Enum.IsDefined(Scope) &&
        (Scope == ModelProviderBindingScope.Platform
            ? ProjectId is null
            : IsIdentifier(ProjectId, 32)) &&
        IsIdentifier(ProviderId, 128) &&
        !string.IsNullOrWhiteSpace(Issuer) && Issuer.Length <= 2048 &&
        IsIdentifier(ActorId, 256) &&
        IsIdentifier(OriginalTenant, 256) &&
        OriginalMembershipId != Guid.Empty &&
        OriginalMembershipRevision > 0 &&
        (TenantSelector is null || IsIdentifier(TenantSelector, 256)) &&
        (TenantClaim is null || IsIdentifier(TenantClaim, 256));

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialCancelResponse(
    Guid OperationId,
    string Outcome,
    ModelProviderCredentialPublicationReceipt? Receipt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialDisableRequest(
    Guid OperationId,
    long? ExpectedProviderRevision,
    long OriginalCredentialRevision,
    Guid ExpectedCredentialSlotId,
    string ExpectedCredentialVersion,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    string Issuer,
    string ActorId,
    string? TenantSelector,
    string? TenantClaim)
{
    [JsonIgnore]
    public bool IsValid =>
        OperationId != Guid.Empty &&
        (ExpectedProviderRevision is null or (> 0 and < long.MaxValue)) &&
        OriginalCredentialRevision is > 0 and < long.MaxValue &&
        ExpectedCredentialSlotId != Guid.Empty &&
        ExpectedCredentialVersion is { Length: 32 } &&
        ExpectedCredentialVersion.All(Uri.IsHexDigit) &&
        Enum.IsDefined(Scope) &&
        (Scope == ModelProviderBindingScope.Platform
            ? ProjectId is null
            : IsIdentifier(ProjectId, 32)) &&
        IsIdentifier(ProviderId, 128) &&
        !string.IsNullOrWhiteSpace(Issuer) && Issuer.Length <= 2048 &&
        IsIdentifier(ActorId, 256) &&
        (TenantSelector is null || IsIdentifier(TenantSelector, 256)) &&
        (TenantClaim is null || IsIdentifier(TenantClaim, 256));

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelProviderCredentialDisableReceipt(
    Guid OperationId,
    [property: JsonConverter(typeof(ModelProviderBindingScopeJsonConverter))]
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId,
    long OriginalProviderRevision,
    long ProviderRevision,
    long OriginalCredentialRevision,
    Guid? CredentialSlotId,
    string ConfigurationHash,
    string ResolvedTenant);
