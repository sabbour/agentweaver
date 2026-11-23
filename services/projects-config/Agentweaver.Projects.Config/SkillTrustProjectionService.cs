using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

/// <summary>Projects the current K2a decision contract without changing evidence or access.</summary>
public sealed class SkillTrustProjectionService(
    ProjectsConfigDbContext db,
    TimeProvider timeProvider)
{
    public async Task<SkillTrustProjection> ForMarketplacePreviewAsync(
        string projectId,
        ProjectMarketplaceSourceRecord source,
        MarketplaceSkillPreviewRequest request,
        SkillContentPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        var fence = await db.SkillAdmissionFences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        var builtIn = fence is
            {
                State: ProjectSkillAdmissionFenceState.Active,
                ReleaseInventoryRevision: not null
            } &&
            await db.SkillBuiltInReleases.AsNoTracking().AnyAsync(
                release =>
                    release.IsActive &&
                    release.InventoryRevision == fence.ReleaseInventoryRevision &&
                    release.Repository == source.Repository &&
                    release.ResolvedCommitSha == request.ResolvedCommitSha.ToLowerInvariant() &&
                    release.SelectedPath == request.SelectedPath &&
                    release.ContentDigest == preview.ContentDigest,
                cancellationToken).ConfigureAwait(false);

        return new SkillTrustProjection(
            projectId,
            null,
            null,
            preview.ContentDigest,
            source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ResolvedCommitSha,
            request.SelectedPath,
            builtIn ? "builtInExempt" : "unknown",
            builtIn ? "BuiltInExempt" : null,
            null,
            null,
            false,
            null,
            builtIn
                ? "The skill matches the owner-verified built-in release inventory. Import will check the current admission policy."
                : "Content validation passed. Preview does not establish admission readiness.");
    }

    public SkillTrustProjection Error(
        string projectId,
        ProjectSkillContentRevisionRecord revision,
        DateTimeOffset? evidenceCreatedAt,
        bool revoked,
        SkillContentServiceException exception) =>
        new(
            projectId,
            revision.SkillId,
            revision.Revision,
            revision.ContentDigest,
            revision.SourceRevision,
            revision.ResolvedCommitSha,
            revision.SelectedPath,
            exception.Code == "skill_admission_denied" ? "denied"
                : revoked ? "revoked"
                    : "error",
            revision.AdmissionClassification.ToString(),
            evidenceCreatedAt,
            evidenceCreatedAt is { } observed
                ? Math.Max(0, checked((long)(timeProvider.GetUtcNow() - observed).TotalSeconds))
                : null,
            revoked,
            exception.Code,
            exception.Message);
}
