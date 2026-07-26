using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed <see cref="IProjectMarketplaceSourceStore"/>. Used when Database:Provider = postgres.
/// Semantics are identical to <see cref="SqliteProjectMarketplaceSourceStore"/>, dialect-neutral.
/// </summary>
public sealed class EfProjectMarketplaceSourceStore : IProjectMarketplaceSourceStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public EfProjectMarketplaceSourceStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<ProjectMarketplaceSource>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.SkillMarketplaceSources.AsNoTracking()
            .Where(s => s.ProjectId == pid)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<ProjectMarketplaceSource?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var rec = await db.SkillMarketplaceSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == pid && s.Name.ToLower() == name.ToLower(), ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<bool> InsertAsync(ProjectMarketplaceSource source, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.SkillMarketplaceSources.Add(ToRecord(source));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public async Task<bool> DeleteByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var lowered = name.ToLower();
        var rows = await db.SkillMarketplaceSources
            .Where(s => s.ProjectId == pid && s.Name.ToLower() == lowered)
            .ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    private static SkillMarketplaceSourceRecord ToRecord(ProjectMarketplaceSource s) => new()
    {
        SourceId = s.SourceId,
        ProjectId = s.ProjectId.ToString(),
        Name = s.Name,
        Repository = s.Repository,
        Branch = s.Branch,
        Subpath = s.Subpath,
        ParseStrategy = s.ParseStrategy,
        Enabled = s.Enabled,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };

    private static ProjectMarketplaceSource FromRecord(SkillMarketplaceSourceRecord r) => new()
    {
        SourceId = r.SourceId,
        ProjectId = ProjectId.Parse(r.ProjectId),
        Name = r.Name,
        Repository = r.Repository,
        Branch = r.Branch,
        Subpath = r.Subpath,
        ParseStrategy = r.ParseStrategy,
        Enabled = r.Enabled,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}
