using System.Collections.Immutable;
using System.Globalization;

namespace Agentweaver.Projects.Config;

/// <summary>
/// Bridges a commit-pinned marketplace source to the existing Skills preview/import authority.
/// </summary>
public sealed class ProjectMarketplaceSkillContentService(
    ProjectMarketplaceSourceService sources,
    SkillMarketplaceBrowseService marketplace,
    ISkillContentService skills)
{
    /// <summary>Reads the exact selected commit/path and delegates byte validation to the Skills owner.</summary>
    public async Task<SkillContentPreview> PreviewAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        MarketplaceSkillPreviewRequest request,
        Func<CancellationToken, Task> verifyCurrentReadAuthorityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifyCurrentReadAuthorityAsync);

        var source = await sources.GetForBrowseAsync(
                caller, projectId, sourceId, request.ExpectedSourceRevision, cancellationToken)
            .ConfigureAwait(false);
        var files = await marketplace.ReadSelectedSkillFilesAsync(
                projectId,
                source,
                request.ExpectedSourceRevision,
                request.ResolvedCommitSha,
                request.SelectedPath,
                verifyCurrentReadAuthorityAsync,
                cancellationToken)
            .ConfigureAwait(false);
        return skills.Preview(ToCandidate(files));
    }

    /// <summary>
    /// Rechecks write authority and the exact source revision around the read before importing with source provenance.
    /// </summary>
    /// <exception cref="MarketplaceSourceException">The source revision changes or the source becomes unavailable.</exception>
    public async Task<SkillContentImportReceipt> ImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        MarketplaceSkillImportRequest request,
        Func<CancellationToken, Task> verifyCurrentWriteAuthorityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifyCurrentWriteAuthorityAsync);

        var source = await sources.GetForImportAsync(
                caller, projectId, sourceId, request.ExpectedSourceRevision, cancellationToken)
            .ConfigureAwait(false);
        var files = await marketplace.ReadSelectedSkillFilesAsync(
                projectId,
                source,
                request.ExpectedSourceRevision,
                request.ResolvedCommitSha,
                request.SelectedPath,
                verifyCurrentWriteAuthorityAsync,
                cancellationToken)
            .ConfigureAwait(false);
        var candidate = ToCandidate(files);

        await verifyCurrentWriteAuthorityAsync(cancellationToken).ConfigureAwait(false);
        var currentSource = await sources.GetForImportAsync(
                caller, projectId, sourceId, request.ExpectedSourceRevision, cancellationToken)
            .ConfigureAwait(false);
        if (currentSource != source)
            throw MarketplaceSourceException.RevisionConflict();
        await verifyCurrentWriteAuthorityAsync(cancellationToken).ConfigureAwait(false);

        return await skills.ImportAsync(
                caller,
                projectId,
                new SkillContentImportRequest(
                    request.IdempotencyKey,
                    request.ExpectedContentDigest,
                    request.SkillId,
                    request.ExpectedRevision,
                    new SkillContentSourceSelection(
                        source.SourceId.ToString("D"),
                        source.Revision.ToString(CultureInfo.InvariantCulture),
                        source.RequestedRef,
                        request.ResolvedCommitSha.ToLowerInvariant(),
                        request.SelectedPath),
                    candidate),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Maps source-selected file bytes to the existing validator input without changing their contents.</summary>
    private static SkillContentCandidate ToCandidate(MarketplaceSkillSourceFiles files) =>
        new(
            files.SkillMarkdown,
            files.Resources
                .Select(resource => new SkillContentResourceInput(resource.RelativePath, resource.Content))
                .ToImmutableArray());
}
