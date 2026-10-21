using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>
/// Shares project authority rules between the privileged authority store and the runtime role-assignment service.
/// </summary>
internal static class ProjectAuthorityRules
{
    public const int MaxSerializationRetries = 3;

    /// <summary>Whether a role can be assigned on a project resource.</summary>
    public static bool IsProjectRole(ProjectAuthorityRole role) =>
        role is ProjectAuthorityRole.Owner or ProjectAuthorityRole.Contributor or
            ProjectAuthorityRole.Viewer or ProjectAuthorityRole.Orchestrator;

    /// <summary>Serializes concurrent Owner mutations for one project within the current transaction.</summary>
    public static Task LockProjectOwnersAsync(
        ProjectsConfigDbContext db,
        string projectId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"project-owner:" + projectId}, 0))",
            cancellationToken);

    /// <summary>Checks for another active Owner grant backed by an active membership.</summary>
    public static Task<bool> HasOtherActiveProjectOwnerAsync(
        ProjectsConfigDbContext db,
        ProjectRoleAssignmentRecord assignment,
        CancellationToken cancellationToken) =>
        (
            from other in db.RoleAssignments.AsNoTracking()
            join membership in db.TenantMemberships.AsNoTracking()
                on other.MembershipId equals membership.MembershipId
            where other.ResourceType == ProjectAuthorityResourceType.Project &&
                other.ResourceId == assignment.ResourceId &&
                other.Role == ProjectAuthorityRole.Owner &&
                other.State == ProjectAuthorityRecordState.Active &&
                other.AssignmentId != assignment.AssignmentId &&
                membership.State == ProjectAuthorityRecordState.Active
            select other.AssignmentId)
        .AnyAsync(cancellationToken);

    /// <summary>Creates the role-assignment audit row with the exact resource, role, and assignment revision.</summary>
    public static ProjectAuthorityAuditRecord CreateRoleAudit(
        ProjectTenantMembershipRecord membership,
        ProjectRoleAssignmentRecord assignment,
        string eventType,
        string actor,
        DateTimeOffset createdAt) => new()
    {
        EventId = Guid.NewGuid(),
        EventType = eventType,
        MembershipId = membership.MembershipId,
        AssignmentId = assignment.AssignmentId,
        Issuer = membership.Issuer,
        Subject = membership.Subject,
        TenantId = membership.TenantId,
        ResourceType = assignment.ResourceType,
        ResourceId = assignment.ResourceId,
        Role = assignment.Role,
        Revision = assignment.Revision,
        Actor = actor,
        CreatedAt = createdAt,
    };

    /// <summary>Recognizes PostgreSQL serialization failures through nested provider exceptions.</summary>
    public static bool IsSerializationFailure(Exception exception) => HasSqlState(exception, "40001");

    /// <summary>Recognizes PostgreSQL unique-constraint failures through nested provider exceptions.</summary>
    public static bool IsUniqueViolation(Exception exception) => HasSqlState(exception, "23505");

    /// <summary>Recognizes PostgreSQL permission failures, such as a missing runtime grant.</summary>
    public static bool IsPermissionDenied(Exception exception) => HasSqlState(exception, "42501");

    private static bool HasSqlState(Exception exception, string sqlState)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && postgres.SqlState == sqlState)
                return true;
        return false;
    }
}
