using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class SkillAdmissionContractTests
{
    [Theory]
    [InlineData(false, false, 20, 500, SkillTrustTier.Standard)]
    [InlineData(true, true, 20, 650, SkillTrustTier.Standard)]
    [InlineData(false, true, 19, 500, SkillTrustTier.Standard)]
    [InlineData(false, false, 19, 450, SkillTrustTier.Probationary)]
    public void ScoreUsesReviewedHeuristicBoundaries(
        bool signature,
        bool capability,
        int descriptionLength,
        int expectedScore,
        SkillTrustTier expectedTier)
    {
        var score = SkillTrustScore.Compute(
            new string('d', descriptionLength), signature, capability);

        Assert.Equal(SkillTrustScoreStatus.Available, score.Status);
        Assert.Equal(expectedScore, score.Value);
        Assert.Equal(expectedTier, score.Tier);
    }

    [Fact]
    public void FailedScoringRemainsSeparateFromTier()
    {
        var score = SkillTrustScore.Compute(
            "Known description.", signatureVerified: true, capabilityDeclarationValid: true,
            scoringAvailable: false);

        Assert.Equal(SkillTrustScoreStatus.Unavailable, score.Status);
        Assert.Null(score.Value);
        Assert.Null(score.Tier);
    }

    [Fact]
    public void RuntimeAdmissionRejectsEvidenceOlderThanThirtyDays()
    {
        var now = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<SkillContentServiceException>(() =>
            SkillContentService.RequireFreshRuntimeEvidence(
                now - TimeSpan.FromDays(31), now));

        Assert.Equal("skill_admission_unavailable", exception.Code);
    }

    [Fact]
    public void CapabilityDeclarationMustBeAClosedStringList()
    {
        var valid = Encoding.UTF8.GetBytes(
            "---\nname: declared\ndescription: A declared Skill for admission.\n" +
            "capabilities:\n  - files.read\n  - tools.search\n---\nUse the declared requests.");
        var forged = Encoding.UTF8.GetBytes(
            "---\nname: forged\ndescription: A forged capability declaration.\n" +
            "capabilities: files.read, tools.search\n---\nUse the requested tools.");

        Assert.True(SkillContentValidator.HasValidCapabilityDeclaration(valid));
        Assert.False(SkillContentValidator.HasValidCapabilityDeclaration(forged));
    }

    [Fact]
    public void ForgedBuiltInAndChangedPolicyFailClosed()
    {
        var policy = new SkillAdmissionPolicySnapshot(
            "canonical", 7, new string('a', 64), null, null, null, 500, true);
        var evidence = new ProjectSkillTrustEvidenceRecord
        {
            EvidenceId = Guid.NewGuid(),
            ProjectId = "project",
            SkillId = "skill",
            Revision = 1,
            ActorIssuer = "issuer",
            ActorId = "actor",
            IdempotencyKey = "key",
            RequestFingerprint = new string('b', 64),
            ContentDigest = new string('c', 64),
            ReportSetJson = [1],
            ReportSetSha256 = new string('d', 64),
            ScoreStatus = SkillTrustScoreStatus.Available,
            HeuristicScore = 500,
            TrustTier = SkillTrustTier.Standard,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var forged = Admission(policy, evidence);
        forged.Classification = ProjectSkillAdmissionClassification.BuiltInExempt;
        forged.ReleaseId = Guid.NewGuid();
        var unavailable = Assert.Throws<SkillContentServiceException>(() =>
            SkillAdmissionService.RequirePassingAdmission(
                forged, evidence, policy, null, null, false));
        Assert.Equal("skill_admission_unavailable", unavailable.Code);

        var tightened = policy with { PlatformPolicyRevision = 8, PlatformPolicySha256 = new string('e', 64) };
        var changed = Assert.Throws<SkillContentServiceException>(() =>
            SkillAdmissionService.RequirePassingAdmission(
                Admission(policy, evidence), evidence, tightened, null, null, false));
        Assert.Equal(StatusCodes.Status409Conflict, changed.StatusCode);
    }

    [Fact]
    public void BuiltInExemptionFailsAfterVerifiedSourceRemoval()
    {
        var policy = new SkillAdmissionPolicySnapshot(
            "canonical", 1, new string('a', 64), null, null, null, 500, true);
        var evidence = new ProjectSkillTrustEvidenceRecord
        {
            EvidenceId = Guid.NewGuid(),
            ProjectId = "project",
            SkillId = "skill",
            Revision = 1,
            ActorIssuer = "issuer",
            ActorId = "actor",
            IdempotencyKey = "key",
            RequestFingerprint = new string('b', 64),
            ContentDigest = new string('c', 64),
            ReportSetJson = [1],
            ReportSetSha256 = new string('d', 64),
            ScoreStatus = SkillTrustScoreStatus.Available,
            HeuristicScore = 600,
            TrustTier = SkillTrustTier.Standard,
            SignatureVerified = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var releaseId = Guid.NewGuid();
        var admission = Admission(policy, evidence);
        admission.Classification = ProjectSkillAdmissionClassification.BuiltInExempt;
        admission.SourceAuthorityKind = ProjectSkillSourceAuthorityKind.MarketplaceVerified;
        admission.VerifiedSourceId = Guid.NewGuid().ToString("D");
        admission.VerifiedSourceRevision = "1";
        admission.VerifiedRequestedRef = "main";
        admission.ReleaseId = releaseId;
        admission.TrustedPublisherId = "publisher";
        admission.SignatureSha256 = new string('e', 64);
        var release = new ProjectSkillBuiltInReleaseRecord
        {
            ReleaseId = releaseId,
            InventoryRevision = "inventory",
            Repository = "owner/skills",
            ResolvedCommitSha = new string('f', 40),
            SelectedPath = "skills/one",
            ContentDigest = new string('c', 64),
            TrustedPublisherId = "publisher",
            SignatureSha256 = new string('e', 64),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var unavailable = Assert.Throws<SkillContentServiceException>(() =>
            SkillAdmissionService.RequirePassingAdmission(
                admission, evidence, policy, currentSource: null, release, false));

        Assert.Equal("skill_admission_unavailable", unavailable.Code);
    }

    [Fact]
    public async Task ReleaseInventoryRequiresAPinnedPublisherSignature()
    {
        using var publisher = RSA.Create(2048);
        var publisherId = "publisher";
        var keys = new SkillReleasePublisherKeys(
            [new SkillReleasePublisherKey(
                publisherId, publisher.ExportSubjectPublicKeyInfoPem())]);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql("Host=localhost;Database=skill_admission_signature")
            .Options;
        await using var db = new ProjectsConfigDbContext(options);
        var releases = ImmutableArray.Create(new SkillReleaseInventoryEntry(
            "owner/skills", new string('a', 40), "skills/one", new string('b', 64)));
        var unsigned = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                inventoryRevision = "inventory-1",
                publisherId,
                releases,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifest = new SkillReleaseInventoryManifest(
            1,
            "inventory-1",
            publisherId,
            releases,
            Convert.ToBase64String(publisher.SignData(
                unsigned, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        var tampered = manifest with { InventoryRevision = "inventory-2" };

        var service = new SkillAdmissionService(db, TimeProvider.System, keys);

        await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            service.RegisterVerifiedInventoryAsync(tampered, CancellationToken.None));
    }

    [Fact]
    public async Task CutoverRequiresPlatformAdminAuthority()
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql("Host=localhost;Database=skill_admission_cutover")
            .Options;
        await using var db = new ProjectsConfigDbContext(options);
        var caller = new ProjectAuthorizationContext(
            "issuer",
            "actor",
            "tenant",
            Guid.NewGuid(),
            1,
            [],
            [ProjectAuthorizationOwner.ApiReadScope],
            null,
            null,
            null);
        var service = new SkillAdmissionService(db, TimeProvider.System);
        var inventory = new SkillReleaseInventoryManifest(
            1, "inventory", "publisher", [], "signature");

        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ActivateAsync(caller, inventory, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    private static ProjectSkillAdmissionRecord Admission(
        SkillAdmissionPolicySnapshot policy,
        ProjectSkillTrustEvidenceRecord evidence) => new()
    {
        AdmissionId = Guid.NewGuid(),
        ProjectId = evidence.ProjectId,
        SkillId = evidence.SkillId,
        Revision = evidence.Revision,
        EvidenceId = evidence.EvidenceId,
        CutoverGeneration = 2,
        Classification = ProjectSkillAdmissionClassification.ThirdParty,
        SourceAuthorityKind = ProjectSkillSourceAuthorityKind.CallerClaimed,
        PlatformPolicyId = policy.PlatformPolicyId,
        PlatformPolicyRevision = policy.PlatformPolicyRevision,
        PlatformPolicySha256 = policy.PlatformPolicySha256,
        CreatedAt = evidence.CreatedAt,
    };
}
