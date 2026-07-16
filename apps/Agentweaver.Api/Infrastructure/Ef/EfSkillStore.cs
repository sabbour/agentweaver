using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed <see cref="ISkillStore"/>. Used when Database:Provider = postgres. Semantics are
/// identical to <see cref="SqliteSkillStore"/>, dialect-neutral.
/// </summary>
public sealed class EfSkillStore : ISkillStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public EfSkillStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    private static readonly JsonSerializerOptions JsonOpts = new();

    public async Task InsertAsync(Skill skill, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Skills.Add(ToRecord(skill));
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Skill skill, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var sid = skill.Id.ToString();
        var pid = skill.ProjectId.ToString();
        await db.Skills
            .Where(s => s.SkillId == sid && s.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, skill.Name)
                .SetProperty(x => x.Description, skill.Description)
                .SetProperty(x => x.Instructions, skill.Instructions)
                .SetProperty(x => x.Resources, SerializeResources(skill.Resources))
                .SetProperty(x => x.Provenance, skill.Provenance.ToApiString())
                .SetProperty(x => x.SourceRepository, skill.SourceRepository)
                .SetProperty(x => x.SourceLocation, skill.SourceLocation)
                .SetProperty(x => x.ContentHash, skill.ContentHash)
                .SetProperty(x => x.Status, skill.Status.ToApiString())
                .SetProperty(x => x.UpdatedAt, skill.UpdatedAt), ct);
    }

    public async Task<Skill?> GetAsync(ProjectId projectId, SkillId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var sid = id.ToString();
        var rec = await db.Skills.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SkillId == sid && s.ProjectId == pid, ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<Skill?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var rec = await db.Skills.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == pid && s.Name.ToLower() == name.ToLower(), ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<IReadOnlyList<Skill>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.Skills.AsNoTracking()
            .Where(s => s.ProjectId == pid)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<bool> DeleteAsync(ProjectId projectId, SkillId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var sid = id.ToString();
        await db.SkillAssignments
            .Where(a => a.ProjectId == pid && a.SkillId == sid)
            .ExecuteDeleteAsync(ct);
        var rows = await db.Skills
            .Where(s => s.SkillId == sid && s.ProjectId == pid)
            .ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task AssignAsync(ProjectId projectId, SkillId skillId, string agentName, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var sid = skillId.ToString();
        var exists = await db.SkillAssignments
            .AnyAsync(a => a.ProjectId == pid && a.SkillId == sid && a.AgentName == agentName, ct);
        if (exists) return;
        db.SkillAssignments.Add(new SkillAssignmentRecord
        {
            ProjectId = pid,
            SkillId = sid,
            AgentName = agentName,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UnassignAsync(ProjectId projectId, SkillId skillId, string agentName, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var sid = skillId.ToString();
        var rows = await db.SkillAssignments
            .Where(a => a.ProjectId == pid && a.SkillId == sid && a.AgentName == agentName)
            .ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task<IReadOnlyList<SkillAssignment>> ListAssignmentsByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.SkillAssignments.AsNoTracking()
            .Where(a => a.ProjectId == pid)
            .ToListAsync(ct);
        return recs.Select(r => new SkillAssignment
        {
            ProjectId = ProjectId.Parse(r.ProjectId),
            SkillId = SkillId.Parse(r.SkillId),
            AgentName = r.AgentName,
            CreatedAt = r.CreatedAt,
        }).ToList();
    }

    public async Task<IReadOnlyList<Skill>> ListActiveSkillsForAgentAsync(ProjectId projectId, string agentName, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await (
            from s in db.Skills.AsNoTracking()
            join a in db.SkillAssignments.AsNoTracking()
                on new { s.ProjectId, s.SkillId } equals new { a.ProjectId, a.SkillId }
            where s.ProjectId == pid && a.AgentName == agentName && s.Status == "active"
            orderby s.Name
            select s).ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<SkillDefaultsStoreApplyResult> ApplyDefaultsAsync(
        SkillDefaultsStorePlan plan,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        try
        {
            var pid = plan.ProjectId.ToString();
            var records = await db.Skills
                .Where(s => s.ProjectId == pid)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var assignments = await db.SkillAssignments
                .Where(a => a.ProjectId == pid)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var currentSkills = records.Select(FromRecord).ToList();
            var currentAssignments = assignments.Select(r => new SkillAssignment
            {
                ProjectId = ProjectId.Parse(r.ProjectId),
                SkillId = SkillId.Parse(r.SkillId),
                AgentName = r.AgentName,
                CreatedAt = r.CreatedAt,
            }).ToList();

            if (!string.Equals(
                    SkillCatalogStateFingerprint.Compute(currentSkills, currentAssignments),
                    plan.ExpectedStateFingerprint,
                    StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SkillDefaultsStoreApplyResult.Stale;
            }

            foreach (var skill in plan.SkillsToInsert)
                db.Skills.Add(ToRecord(skill));

            foreach (var skill in plan.SkillsToActivate)
            {
                var record = records.SingleOrDefault(s => s.SkillId == skill.Id.ToString());
                if (record is null)
                    throw new InvalidOperationException("A guarded built-in skill was not found during apply.");
                record.Status = skill.Status.ToApiString();
                record.UpdatedAt = skill.UpdatedAt;
            }

            foreach (var assignment in plan.AssignmentsToAdd)
            {
                db.SkillAssignments.Add(new SkillAssignmentRecord
                {
                    ProjectId = assignment.ProjectId.ToString(),
                    SkillId = assignment.SkillId.ToString(),
                    AgentName = assignment.AgentName,
                    CreatedAt = assignment.CreatedAt,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return SkillDefaultsStoreApplyResult.Applied;
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return SkillDefaultsStoreApplyResult.Stale;
        }
    }

    private static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "40001" })
                return true;
        }
        return false;
    }

    private static SkillRecord ToRecord(Skill s) => new()
    {
        SkillId = s.Id.ToString(),
        ProjectId = s.ProjectId.ToString(),
        Name = s.Name,
        Description = s.Description,
        Instructions = s.Instructions,
        Resources = SerializeResources(s.Resources),
        Provenance = s.Provenance.ToApiString(),
        SourceRepository = s.SourceRepository,
        SourceLocation = s.SourceLocation,
        ContentHash = s.ContentHash,
        Status = s.Status.ToApiString(),
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };

    private static Skill FromRecord(SkillRecord r) => new()
    {
        Id = SkillId.Parse(r.SkillId),
        ProjectId = ProjectId.Parse(r.ProjectId),
        Name = r.Name,
        Description = r.Description,
        Instructions = r.Instructions,
        Resources = DeserializeResources(r.Resources),
        Provenance = SkillProvenanceExtensions.ParseProvenance(r.Provenance),
        SourceRepository = r.SourceRepository,
        SourceLocation = r.SourceLocation,
        ContentHash = r.ContentHash,
        Status = SkillStatusExtensions.ParseStatus(r.Status),
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static string SerializeResources(IReadOnlyList<SkillResource> resources) =>
        JsonSerializer.Serialize(resources, JsonOpts);

    private static IReadOnlyList<SkillResource> DeserializeResources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SkillResource>();
        return JsonSerializer.Deserialize<List<SkillResource>>(json, JsonOpts) ?? new List<SkillResource>();
    }
}
