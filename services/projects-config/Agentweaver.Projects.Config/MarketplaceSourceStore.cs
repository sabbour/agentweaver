using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>
/// Persists project-scoped marketplace sources and translates database uniqueness and revision races into domain conflicts.
/// </summary>
public sealed class MarketplaceSourceStore(ProjectsConfigDbContext db) : IProjectMarketplaceSourceStore
{
    private const string ActiveNameConstraint = "ux_marketplace_sources_project_name_active";

    /// <summary>Lists a project's marketplace sources in stable name order, optionally including removed records.</summary>
    public async Task<IReadOnlyList<ProjectMarketplaceSourceRecord>> ListByProjectAsync(
        string projectId,
        bool includeRemoved,
        CancellationToken cancellationToken)
    {
        IQueryable<ProjectMarketplaceSourceRecord> query = db.MarketplaceSources.AsNoTracking()
            .Where(source => source.ProjectId == projectId);
        if (!includeRemoved)
            query = query.Where(source => source.State == ProjectMarketplaceSourceState.Active);
        return await query.OrderBy(source => source.NormalizedName)
            .ThenBy(source => source.SourceId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a source only when both its project and source IDs match.</summary>
    public Task<ProjectMarketplaceSourceRecord?> GetAsync(
        string projectId,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        db.MarketplaceSources.AsNoTracking()
            .SingleOrDefaultAsync(
                source => source.ProjectId == projectId && source.SourceId == sourceId,
                cancellationToken);

    /// <summary>
    /// Inserts a source at its supplied initial revision; a duplicate active project name becomes a name conflict.
    /// </summary>
    public async Task<ProjectMarketplaceSourceRecord> CreateAsync(
        ProjectMarketplaceSourceRecord source,
        CancellationToken cancellationToken)
    {
        db.MarketplaceSources.Add(source);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.Entry(source).State = EntityState.Detached;
            return source;
        }
        catch (DbUpdateException exception) when (IsActiveNameConflict(exception))
        {
            db.Entry(source).State = EntityState.Detached;
            throw MarketplaceSourceException.NameConflict();
        }
    }

    /// <summary>
    /// Replaces source details only at the expected active revision and advances the revision atomically.
    /// </summary>
    /// <exception cref="MarketplaceSourceException">The source is missing, stale, removed, or conflicts by active name.</exception>
    public async Task<ProjectMarketplaceSourceRecord> UpdateAsync(
        string projectId,
        Guid sourceId,
        long expectedRevision,
        ProjectMarketplaceSourceDefinition definition,
        string actorId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var current = await db.MarketplaceSources.SingleOrDefaultAsync(
                source => source.ProjectId == projectId && source.SourceId == sourceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            throw MarketplaceSourceException.NotFound();
        if (current.Revision != expectedRevision)
            throw MarketplaceSourceException.RevisionConflict();
        if (current.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();

        var updated = current with
        {
            Name = definition.Name,
            NormalizedName = MarketplaceSourceDefinition.NormalizeName(definition.Name),
            Repository = definition.Repository,
            RequestedRef = definition.RequestedRef,
            Subpath = definition.Subpath,
            Revision = current.Revision + 1,
            UpdatedByActorId = actorId,
            UpdatedAt = updatedAt,
        };
        var entry = db.Entry(current);
        entry.CurrentValues.SetValues(updated);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (DbUpdateConcurrencyException)
        {
            await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            throw MarketplaceSourceException.RevisionConflict();
        }
        catch (DbUpdateException exception) when (IsActiveNameConflict(exception))
        {
            await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            throw MarketplaceSourceException.NameConflict();
        }
    }

    /// <summary>
    /// Tombstones an active source at the expected revision; source history remains stored for audit and references.
    /// </summary>
    /// <exception cref="MarketplaceSourceException">The source is missing, removed, or has a different revision.</exception>
    public async Task<ProjectMarketplaceSourceRecord> RemoveAsync(
        string projectId,
        Guid sourceId,
        long expectedRevision,
        string actorId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var current = await db.MarketplaceSources.SingleOrDefaultAsync(
                source => source.ProjectId == projectId && source.SourceId == sourceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            throw MarketplaceSourceException.NotFound();
        if (current.Revision != expectedRevision)
            throw MarketplaceSourceException.RevisionConflict();
        if (current.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();

        var updated = current with
        {
            State = ProjectMarketplaceSourceState.Removed,
            Revision = current.Revision + 1,
            UpdatedByActorId = actorId,
            UpdatedAt = updatedAt,
        };
        var entry = db.Entry(current);
        entry.CurrentValues.SetValues(updated);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (DbUpdateConcurrencyException)
        {
            await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            throw MarketplaceSourceException.RevisionConflict();
        }
    }

    /// <summary>Recognizes only the active-name unique constraint so unrelated database failures remain visible.</summary>
    private static bool IsActiveNameConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ActiveNameConstraint,
        };
}
