using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public enum ProjectSkillAdmissionFenceState
{
    Open,
    Draining,
    Active
}

public enum ProjectSkillAdmissionClassification
{
    LegacyThirdParty,
    ThirdParty,
    BuiltInExempt
}

public enum ProjectSkillSourceAuthorityKind
{
    CallerClaimed,
    MarketplaceVerified
}

public enum SkillTrustTier
{
    Revoked,
    Probationary,
    Standard,
    Trusted,
    Verified
}

public enum SkillTrustScoreStatus
{
    Unavailable,
    Available
}

public sealed class ProjectSkillAdmissionFenceRecord
{
    public int Id { get; set; }
    public ProjectSkillAdmissionFenceState State { get; set; }
    public long Generation { get; set; }
    public string? ReleaseInventoryRevision { get; set; }
    public string? PlatformSkillsPolicySha256 { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectSkillBuiltInReleaseRecord
{
    public Guid ReleaseId { get; set; }
    public required string InventoryRevision { get; set; }
    public required string Repository { get; set; }
    public required string ResolvedCommitSha { get; set; }
    public required string SelectedPath { get; set; }
    public required string ContentDigest { get; set; }
    public required string TrustedPublisherId { get; set; }
    public string? SignatureSha256 { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectSkillAdmissionRecord
{
    public Guid AdmissionId { get; set; }
    public required string ProjectId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public Guid EvidenceId { get; set; }
    public long CutoverGeneration { get; set; }
    public ProjectSkillAdmissionClassification Classification { get; set; }
    public ProjectSkillSourceAuthorityKind SourceAuthorityKind { get; set; }
    public string? VerifiedSourceId { get; set; }
    public string? VerifiedSourceRevision { get; set; }
    public string? VerifiedRequestedRef { get; set; }
    public string? VerifiedResolvedCommitSha { get; set; }
    public string? VerifiedSelectedPath { get; set; }
    public Guid? ReleaseId { get; set; }
    public string? TrustedPublisherId { get; set; }
    public string? SignatureSha256 { get; set; }
    public string? PlatformPolicyId { get; set; }
    public long? PlatformPolicyRevision { get; set; }
    public string? PlatformPolicySha256 { get; set; }
    public string? ProjectPolicyId { get; set; }
    public long? ProjectPolicyRevision { get; set; }
    public string? ProjectPolicySha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed record SkillContentOwnerProvenance(
    ProjectMarketplaceSourceRecord Source,
    string ResolvedCommitSha,
    string SelectedPath,
    Func<CancellationToken, Task> VerifyCurrentSourceAsync);

public sealed record SkillTrustScore(
    SkillTrustScoreStatus Status,
    int? Value,
    SkillTrustTier? Tier,
    bool SignatureVerified,
    bool CapabilityDeclarationValid,
    bool ShortDescription)
{
    public static SkillTrustScore Compute(
        string description,
        bool signatureVerified,
        bool capabilityDeclarationValid,
        bool scoringAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (!scoringAvailable)
        {
            return new(
                SkillTrustScoreStatus.Unavailable,
                null,
                null,
                signatureVerified,
                capabilityDeclarationValid,
                description.Length < 20);
        }

        var shortDescription = description.Length < 20;
        var score = 500 +
            (signatureVerified ? 100 : 0) +
            (capabilityDeclarationValid ? 50 : 0) -
            (shortDescription ? 50 : 0);
        score = Math.Clamp(score, 0, 1000);
        var tier = score switch
        {
            >= 900 => SkillTrustTier.Verified,
            >= 700 => SkillTrustTier.Trusted,
            >= 500 => SkillTrustTier.Standard,
            >= 300 => SkillTrustTier.Probationary,
            _ => SkillTrustTier.Revoked
        };
        return new(
            SkillTrustScoreStatus.Available,
            score,
            tier,
            signatureVerified,
            capabilityDeclarationValid,
            shortDescription);
    }
}

internal sealed record SkillAdmissionPolicySnapshot(
    string PlatformPolicyId,
    long PlatformPolicyRevision,
    string PlatformPolicySha256,
    string? ProjectPolicyId,
    long? ProjectPolicyRevision,
    string? ProjectPolicySha256,
    int MinimumScore,
    bool RequireCompletedEvidence,
    NativeAcsArtifact PlatformArtifact = null!,
    NativeAcsArtifact? ProjectArtifact = null);

internal sealed record SkillSourceAuthorityBinding(
    ProjectSkillSourceAuthorityKind Kind,
    ProjectMarketplaceSourceRecord? MarketplaceSource,
    string? ResolvedCommitSha,
    string? SelectedPath,
    ProjectSkillBuiltInReleaseRecord? Release,
    string? TrustedPublisherId,
    string? SignatureSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillAdmissionPolicyDataV1(
    int SchemaVersion,
    int MinimumScore,
    bool RequireCompletedEvidence)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillReleaseInventoryManifest(
    int SchemaVersion,
    string InventoryRevision,
    string PublisherId,
    ImmutableArray<SkillReleaseInventoryEntry> Releases,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillReleaseInventoryEntry(
    string Repository,
    string ResolvedCommitSha,
    string SelectedPath,
    string ContentDigest);

public sealed record SkillReleasePublisherKey(string PublisherId, string PublicKeyPem);

public sealed class SkillReleasePublisherKeys
{
    public const string SectionName = "ProjectsConfig:SkillReleasePublisherKeys";

    public SkillReleasePublisherKeys(IEnumerable<SkillReleasePublisherKey> keys)
    {
        var entries = keys ?? throw new ArgumentNullException(nameof(keys));
        try
        {
            Pinned = entries.ToDictionary(item => item.PublisherId, item => item.PublicKeyPem,
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Skill release publisher IDs must be unique.", exception);
        }
        if (Pinned.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Value)))
            throw new InvalidOperationException(
                "Each Skill release publisher requires an ID and public key.");
    }

    public static SkillReleasePublisherKeys FromConfiguration(
        Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        new(configuration.GetSection(SectionName)
            .Get<SkillReleasePublisherKey[]>() ?? []);

    public IReadOnlyDictionary<string, string> Pinned { get; }
}
