using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace Agentweaver.Api.Infrastructure.Ef;

public sealed class EfProjectRoleAssignmentStore(IDbContextFactory<MemoryDbContext> factory) : IProjectRoleAssignmentStore
{
    private const int MaxSerializationRetries = 3;

    public async Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.ProjectRoleAssignments
            .FirstOrDefaultAsync(
                x => x.ProjectId == assignment.ProjectId.ToString() && x.PrincipalId == assignment.PrincipalId,
                ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.ProjectRoleAssignments.Add(ToRecord(assignment));
        }
        else
        {
            existing.Role = assignment.Role.ToApiString();
            existing.GrantedBy = assignment.GrantedBy;
            existing.GrantedAt = assignment.GrantedAt;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(
        ProjectRoleAssignment assignment,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            try
            {
                var existing = await db.ProjectRoleAssignments
                    .FirstOrDefaultAsync(
                        x => x.ProjectId == assignment.ProjectId.ToString() && x.PrincipalId == assignment.PrincipalId,
                        ct)
                    .ConfigureAwait(false);

                if (existing is not null
                    && ProjectRoleExtensions.Parse(existing.Role) == ProjectRole.Owner
                    && assignment.Role != ProjectRole.Owner)
                {
                    var otherOwners = await db.ProjectRoleAssignments
                        .CountAsync(
                            x => x.ProjectId == assignment.ProjectId.ToString()
                                 && x.Role == ProjectRole.Owner.ToApiString()
                                 && x.PrincipalId != assignment.PrincipalId,
                            ct)
                        .ConfigureAwait(false);
                    if (otherOwners == 0)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict);
                    }
                }

                if (existing is null)
                {
                    db.ProjectRoleAssignments.Add(ToRecord(assignment));
                }
                else
                {
                    existing.Role = assignment.Role.ToApiString();
                    existing.GrantedBy = assignment.GrantedBy;
                    existing.GrantedAt = assignment.GrantedAt;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.Ok, assignment);
            }
            catch (Exception ex) when (IsSerializationFailure(ex) && attempt < MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<ProjectRoleAssignment?> GetAsync(ProjectId projectId, string principalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.ProjectRoleAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ProjectId == projectId.ToString() && x.PrincipalId == principalId,
                ct)
            .ConfigureAwait(false);
        return record is null ? null : FromRecord(record);
    }

    public async Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ProjectRoleAssignments
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId.ToString())
            .OrderByDescending(x => x.Role)
            .ThenBy(x => x.PrincipalId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(FromRecord).ToList();
    }

    public async Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ProjectRoleAssignments
            .AsNoTracking()
            .Where(x => x.PrincipalId == principalId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(FromRecord).ToList();
    }

    public async Task<bool> DeleteAsync(ProjectId projectId, string principalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.ProjectRoleAssignments
            .Where(x => x.ProjectId == projectId.ToString() && x.PrincipalId == principalId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(
        ProjectId projectId,
        string principalId,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            try
            {
                var existing = await db.ProjectRoleAssignments
                    .FirstOrDefaultAsync(
                        x => x.ProjectId == projectId.ToString() && x.PrincipalId == principalId,
                        ct)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.NotFound);
                }

                var existingAssignment = FromRecord(existing);
                if (existingAssignment.Role == ProjectRole.Owner)
                {
                    var otherOwners = await db.ProjectRoleAssignments
                        .CountAsync(
                            x => x.ProjectId == projectId.ToString()
                                 && x.Role == ProjectRole.Owner.ToApiString()
                                 && x.PrincipalId != principalId,
                            ct)
                        .ConfigureAwait(false);
                    if (otherOwners == 0)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict, existingAssignment);
                    }
                }

                db.ProjectRoleAssignments.Remove(existing);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.Ok, existingAssignment);
            }
            catch (Exception ex) when (IsSerializationFailure(ex) && attempt < MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static ProjectRoleAssignmentRecord ToRecord(ProjectRoleAssignment assignment) => new()
    {
        ProjectId = assignment.ProjectId.ToString(),
        PrincipalId = assignment.PrincipalId,
        Role = assignment.Role.ToApiString(),
        GrantedBy = assignment.GrantedBy,
        GrantedAt = assignment.GrantedAt,
    };

    private static ProjectRoleAssignment FromRecord(ProjectRoleAssignmentRecord record) => new()
    {
        ProjectId = ProjectId.Parse(record.ProjectId),
        PrincipalId = record.PrincipalId,
        Role = ProjectRoleExtensions.Parse(record.Role),
        GrantedBy = record.GrantedBy,
        GrantedAt = record.GrantedAt,
    };

    private static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "40001" })
                return true;
        }

        return false;
    }
}
