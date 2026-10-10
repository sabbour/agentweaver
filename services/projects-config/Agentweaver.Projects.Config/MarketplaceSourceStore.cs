using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class MarketplaceSourceStore(ProjectsConfigDbContext db) : IProjectMarketplaceSourceStore
{
    private const string ActiveNameConstraint = "ux_marketplace_sources_project_name_active";

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

    public Task<ProjectMarketplaceSourceRecord?> GetAsync(
        string projectId,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        db.MarketplaceSources.AsNoTracking()
            .SingleOrDefaultAsync(
                source => source.ProjectId == projectId && source.SourceId == sourceId,
                cancellationToken);

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

    private static bool IsActiveNameConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ActiveNameConstraint,
        };
}
