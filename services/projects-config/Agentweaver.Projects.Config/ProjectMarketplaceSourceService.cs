namespace Agentweaver.Projects.Config;

/// <summary>Applies current project authority and revision rules to marketplace source persistence operations.</summary>
public sealed class ProjectMarketplaceSourceService(
    IProjectMarketplaceSourceStore store,
    ProjectsConfigService projects,
    TimeProvider timeProvider)
{
    /// <summary>Lists source metadata only for a project the caller can currently read.</summary>
    public async Task<IReadOnlyList<ProjectMarketplaceSourceRecord>> ListAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        bool includeRemoved,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetMarketplaceProjectAsync(
                caller, projectId, requireWrite: false, cancellationToken)
            .ConfigureAwait(false);
        return await store.ListByProjectAsync(
                project.ProjectId, includeRemoved, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns an active source only when the expected revision still matches for browsing.</summary>
    public async Task<ProjectMarketplaceSourceRecord> GetForBrowseAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetMarketplaceProjectAsync(
                caller, projectId, requireWrite: false, cancellationToken)
            .ConfigureAwait(false);
        if (expectedRevision <= 0)
            throw MarketplaceSourceException.InvalidRequest("Expected source revision must be positive.");
        var source = await store.GetAsync(project.ProjectId, sourceId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || source.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();
        if (source.Revision != expectedRevision)
            throw MarketplaceSourceException.RevisionConflict();
        return source;
    }

    /// <summary>Returns an active source at the expected revision after checking current project write authority.</summary>
    public async Task<ProjectMarketplaceSourceRecord> GetForImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetMarketplaceProjectAsync(
                caller, projectId, requireWrite: true, cancellationToken)
            .ConfigureAwait(false);
        if (expectedRevision <= 0)
            throw MarketplaceSourceException.InvalidRequest("Expected source revision must be positive.");
        var source = await store.GetAsync(project.ProjectId, sourceId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || source.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();
        if (source.Revision != expectedRevision)
            throw MarketplaceSourceException.RevisionConflict();
        return source;
    }

    /// <summary>Normalizes source details and creates revision 1 inside the project's serialized write transaction.</summary>
    public async Task<ProjectMarketplaceSourceRecord> CreateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CreateMarketplaceSourceRequest request,
        CancellationToken cancellationToken)
    {
        return await projects.ExecuteMarketplaceWriteAsync(
                caller,
                projectId,
                async project =>
                {
                    var definition = MarketplaceSourceDefinition.Normalize(
                        request.Name, request.Repository, request.RequestedRef, request.Subpath);
                    var now = timeProvider.GetUtcNow();
                    return await store.CreateAsync(
                        new ProjectMarketplaceSourceRecord
                        {
                            ProjectId = project.ProjectId,
                            SourceId = Guid.NewGuid(),
                            Name = definition.Name,
                            NormalizedName = MarketplaceSourceDefinition.NormalizeName(definition.Name),
                            Repository = definition.Repository,
                            RequestedRef = definition.RequestedRef,
                            Subpath = definition.Subpath,
                            Revision = 1,
                            State = ProjectMarketplaceSourceState.Active,
                            CreatedByActorId = caller.ActorId,
                            UpdatedByActorId = caller.ActorId,
                            CreatedAt = now,
                            UpdatedAt = now,
                        },
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Updates a source at its expected revision inside the project authority transaction.</summary>
    public async Task<ProjectMarketplaceSourceRecord> UpdateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        UpdateMarketplaceSourceRequest request,
        CancellationToken cancellationToken)
    {
        return await projects.ExecuteMarketplaceWriteAsync(
                caller,
                projectId,
                async project =>
                {
                    if (request.ExpectedRevision <= 0)
                        throw MarketplaceSourceException.InvalidRequest("Expected source revision must be positive.");
                    var definition = MarketplaceSourceDefinition.Normalize(
                        request.Name, request.Repository, request.RequestedRef, request.Subpath);
                    return await store.UpdateAsync(
                        project.ProjectId,
                        sourceId,
                        request.ExpectedRevision,
                        definition,
                        caller.ActorId,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Marks a source removed at its expected revision without physically deleting its history.</summary>
    public async Task<ProjectMarketplaceSourceRecord> RemoveAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid sourceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        return await projects.ExecuteMarketplaceWriteAsync(
                caller,
                projectId,
                project =>
                {
                    if (expectedRevision <= 0)
                        throw MarketplaceSourceException.InvalidRequest("Expected source revision must be positive.");
                    return store.RemoveAsync(
                        project.ProjectId,
                        sourceId,
                        expectedRevision,
                        caller.ActorId,
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
