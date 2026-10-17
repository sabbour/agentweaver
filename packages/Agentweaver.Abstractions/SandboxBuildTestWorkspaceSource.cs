using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>
/// Exact accepted repository seed reference used by a BuildTest operation; it must refer
/// to the originating checkpoint and match the accepted source byte length and digest.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SandboxBuildTestWorkspaceSeedReference(
    SandboxBuildTestCheckpointReference Origin,
    Guid OperationId,
    string SeedSha256,
    long SeedByteLength)
{
    /// <summary>Checks that the seed reference is the one admitted for this checkpoint and operation.</summary>
    /// <exception cref="ArgumentException">The checkpoint, operation, source digest, or source length differs.</exception>
    public SandboxBuildTestWorkspaceSeedReference Validate(
        SandboxBuildTestCheckpointReference checkpoint,
        Guid operationId,
        SandboxBuildTestWorkspaceSource source)
    {
        ArgumentNullException.ThrowIfNull(Origin);
        ArgumentNullException.ThrowIfNull(source);
        _ = Origin.Validate();
        _ = source.Validate();
        if (Origin != checkpoint || OperationId != operationId || OperationId == Guid.Empty ||
            SeedSha256 != source.SeedSha256 || SeedByteLength != source.SeedByteLength)
            throw new ArgumentException("The BuildTest seed reference differs from its originating intent.");
        return this;
    }
}

/// <summary>
/// Immutable Source Control and workspace identity accepted for a BuildTest run.
/// The seed and baseline hashes bind repository input, while the provider generation
/// and workspace incarnation prevent reuse after resource replacement.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SandboxBuildTestWorkspaceSource(
    int ContractVersion,
    SessionIdentity Identity,
    string TenantId,
    string ActorIssuer,
    string ActorSubject,
    string SourceControlPinId,
    string AcceptedSelectionHash,
    ProviderResourceRef SourceControlResource,
    string WorkspaceId,
    Guid WorkspaceIncarnationId,
    string BranchName,
    string BaseSha,
    string BaselineTreeSha,
    string SeedReference,
    string SeedSha256,
    long SeedByteLength,
    string BundleSha256,
    long BundleByteLength,
    string BaselineManifestSha256,
    long BaselineManifestByteLength)
{
    /// <summary>Deterministically names the repository directory prepared for this run and workspace.</summary>
    [JsonIgnore]
    public string PreparedRepositoryRelativePath =>
        "buildtest-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            TenantId + "\0" + Identity.ProjectId + "\0" + Identity.RunId + "\0" + WorkspaceId)))[..32] +
        "/repository";

    /// <summary>Returns whether all identity, hash, generation, and size limits are valid.</summary>
    public bool IsValid() =>
        ContractVersion == 2 &&
        IsText(Identity.ProjectId, 256) &&
        IsText(Identity.RunId, 256) &&
        IsText(Identity.SessionId, 256) &&
        IsText(TenantId, 256) &&
        IsText(ActorIssuer, 512) &&
        IsText(ActorSubject, 256) &&
        IsText(SourceControlPinId, 128) &&
        AcceptedSelectionHash is { Length: 64 } && AcceptedSelectionHash.All(Uri.IsHexDigit) &&
        SourceControlResource is not null &&
        SourceControlResource.Seam == ProviderSeam.SourceControl &&
        IsText(SourceControlResource.ProviderId, 128) &&
        IsText(SourceControlResource.ResourceId, 256) &&
        SourceControlResource.Generation > 0 &&
        WorkspaceId is { Length: > 0 and <= 128 } &&
        WorkspaceId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
        WorkspaceIncarnationId != Guid.Empty &&
        IsText(BranchName, 255) &&
        IsGitObjectId(BaseSha) &&
        IsGitObjectId(BaselineTreeSha) &&
        IsText(SeedReference, 256) &&
        SandboxBuildTestHashes.IsSha256(SeedSha256) &&
        SeedByteLength is > 0 and <= ProducedRunCaptureLimits.MaximumPackageBytes &&
        SandboxBuildTestHashes.IsSha256(BundleSha256) &&
        BundleByteLength > 0 && BundleByteLength <= SeedByteLength &&
        SandboxBuildTestHashes.IsSha256(BaselineManifestSha256) &&
        BaselineManifestByteLength is > 0 and <= ProducedRunCaptureLimits.MaximumManifestBytes &&
        BaselineManifestByteLength <= SeedByteLength - BundleByteLength;

    /// <summary>Returns this source after validating its accepted repository binding.</summary>
    /// <exception cref="ArgumentException">The source identity, hashes, generation, or lengths are invalid.</exception>
    public SandboxBuildTestWorkspaceSource Validate()
    {
        if (!IsValid())
            throw new ArgumentException("The accepted BuildTest repository seed binding is invalid.");
        return this;
    }

    /// <summary>Writes the source fields in the stable order used by BuildTest canonical fingerprints.</summary>
    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("workspaceSource");
        writer.WriteStartObject();
        writer.WriteNumber("contractVersion", ContractVersion);
        writer.WriteString("projectId", Identity.ProjectId);
        writer.WriteString("runId", Identity.RunId);
        writer.WriteString("sessionId", Identity.SessionId);
        writer.WriteString("tenantId", TenantId);
        writer.WriteString("actorIssuer", ActorIssuer);
        writer.WriteString("actorSubject", ActorSubject);
        writer.WriteString("sourceControlPinId", SourceControlPinId);
        writer.WriteString("acceptedSelectionHash", AcceptedSelectionHash);
        writer.WriteString("providerId", SourceControlResource.ProviderId);
        writer.WriteString("repositoryId", SourceControlResource.ResourceId);
        writer.WriteNumber("resourceGeneration", SourceControlResource.Generation);
        writer.WriteString("workspaceId", WorkspaceId);
        writer.WriteString("workspaceIncarnationId", WorkspaceIncarnationId.ToString("D"));
        writer.WriteString("branchName", BranchName);
        writer.WriteString("baseSha", BaseSha);
        writer.WriteString("baselineTreeSha", BaselineTreeSha);
        writer.WriteString("seedReference", SeedReference);
        writer.WriteString("seedSha256", SeedSha256);
        writer.WriteNumber("seedByteLength", SeedByteLength);
        writer.WriteString("bundleSha256", BundleSha256);
        writer.WriteNumber("bundleByteLength", BundleByteLength);
        writer.WriteString("baselineManifestSha256", BaselineManifestSha256);
        writer.WriteNumber("baselineManifestByteLength", BaselineManifestByteLength);
        writer.WriteEndObject();
    }

    /// <summary>Checks required text for length and control characters.</summary>
    private static bool IsText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsGitObjectId(string? value) =>
        value is { Length: 40 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
