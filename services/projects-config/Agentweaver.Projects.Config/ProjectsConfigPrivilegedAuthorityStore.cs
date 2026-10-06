using System.Data;
using Agentweaver.Providers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class ProjectAuthorityConcurrencyException(string message) : Exception(message);

public sealed class ProjectsConfigPrivilegedAuthorityStore(
    DbContextOptions<ProjectsConfigDbContext> options,
    TimeProvider timeProvider)
{
    private const int MaxSerializationRetries = 3;

    public async Task<ProjectTenantMembershipRecord> GrantMembershipAsync(
        string issuer,
        string subject,
        string tenantId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(issuer, nameof(issuer));
        ValidateIdentifier(subject, nameof(subject));
        ValidateIdentifier(tenantId, nameof(tenantId));
        ValidateIdentifier(actor, nameof(actor));

        var membership = new ProjectTenantMembershipRecord
        {
            MembershipId = Guid.NewGuid(),
            Issuer = issuer,
            Subject = subject,
            TenantId = tenantId,
            State = ProjectAuthorityRecordState.Active,
            Revision = 1,
            GrantedBy = actor,
            GrantedAt = timeProvider.GetUtcNow(),
        };
        await using var db = new ProjectsConfigDbContext(options);
        db.TenantMemberships.Add(membership);
        db.AuthorityAudit.Add(ToAudit(membership, "membership_granted", actor));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return membership;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ProjectAuthorityConcurrencyException(
                "An active membership already exists for this issuer, subject, and tenant.");
        }
    }

    public async Task<ProjectRoleAssignmentRecord> AssignRoleAsync(
        Guid membershipId,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(resourceId, nameof(resourceId));
        ValidateIdentifier(actor, nameof(actor));
        if (!Enum.IsDefined(resourceType) || !Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), "Authority resource or role is not supported.");

        for (var attempt = 0; ; attempt++)
        {
            await using var db = new ProjectsConfigDbContext(options);
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var membership = await db.TenantMemberships.AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.MembershipId == membershipId &&
                        item.State == ProjectAuthorityRecordState.Active,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ProjectConfigException.NotFound();
                await ValidateAssignmentScopeAsync(
                    db, membership, resourceType, resourceId, role, cancellationToken).ConfigureAwait(false);
                if (resourceType == ProjectAuthorityResourceType.Project && role == ProjectAuthorityRole.Owner)
                    await LockProjectOwnersAsync(db, resourceId, cancellationToken).ConfigureAwait(false);

                var assignment = new ProjectRoleAssignmentRecord
                {
                    AssignmentId = Guid.NewGuid(),
                    MembershipId = membershipId,
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    Role = role,
                    State = ProjectAuthorityRecordState.Active,
                    Revision = 1,
                    GrantedBy = actor,
                    GrantedAt = timeProvider.GetUtcNow(),
                };
                db.RoleAssignments.Add(assignment);
                db.AuthorityAudit.Add(ToAudit(membership, assignment, "role_assigned", actor));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return assignment;
            }
            catch (Exception exception) when (IsSerializationFailure(exception) && attempt < MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                throw new ProjectAuthorityConcurrencyException("The active role assignment already exists.");
            }
        }
    }

    public async Task RevokeRoleAssignmentAsync(
        Guid assignmentId,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(actor, nameof(actor));
        if (expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        for (var attempt = 0; ; attempt++)
        {
            await using var db = new ProjectsConfigDbContext(options);
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var current = await db.RoleAssignments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.AssignmentId == assignmentId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ProjectConfigException.NotFound();
                if (current.ResourceType == ProjectAuthorityResourceType.Project &&
                    current.Role == ProjectAuthorityRole.Owner)
                    await LockProjectOwnersAsync(db, current.ResourceId, cancellationToken).ConfigureAwait(false);

                var assignment = await db.RoleAssignments
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM projects_config.project_role_assignments
                        WHERE assignment_id = {assignmentId}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ProjectConfigException.NotFound();
                if (assignment.State != ProjectAuthorityRecordState.Active ||
                    assignment.Revision != expectedRevision)
                    throw new ProjectAuthorityConcurrencyException("The role assignment revision has changed.");

                if (assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                    assignment.Role == ProjectAuthorityRole.Owner &&
                    !await HasOtherActiveProjectOwnerAsync(
                        db, assignment, cancellationToken).ConfigureAwait(false))
                    throw new ProjectAuthorityConcurrencyException(
                        "Cannot revoke the last explicit Owner assignment for a project.");

                var membership = await db.TenantMemberships.SingleAsync(
                    item => item.MembershipId == assignment.MembershipId,
                    cancellationToken).ConfigureAwait(false);
                assignment.State = ProjectAuthorityRecordState.Revoked;
                assignment.Revision = checked(assignment.Revision + 1);
                assignment.RevokedBy = actor;
                assignment.RevokedAt = timeProvider.GetUtcNow();
                db.AuthorityAudit.Add(ToAudit(membership, assignment, "role_revoked", actor));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsSerializationFailure(exception) && attempt < MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ProjectAuthorityConcurrencyException(
                    "The role assignment revision changed while it was being revoked.");
            }
        }
    }

    public async Task RevokeMembershipAsync(
        Guid membershipId,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(actor, nameof(actor));
        if (expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        for (var attempt = 0; ; attempt++)
        {
            await using var db = new ProjectsConfigDbContext(options);
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var current = await db.TenantMemberships.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.MembershipId == membershipId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ProjectConfigException.NotFound();
                var ownedProjects = await db.RoleAssignments.AsNoTracking()
                    .Where(item =>
                        item.MembershipId == membershipId &&
                        item.State == ProjectAuthorityRecordState.Active &&
                        item.ResourceType == ProjectAuthorityResourceType.Project &&
                        item.Role == ProjectAuthorityRole.Owner)
                    .Select(item => item.ResourceId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var projectId in ownedProjects)
                    await LockProjectOwnersAsync(db, projectId, cancellationToken).ConfigureAwait(false);

                var membership = await db.TenantMemberships
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM projects_config.tenant_memberships
                        WHERE membership_id = {membershipId}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ProjectConfigException.NotFound();
                if (membership.State != ProjectAuthorityRecordState.Active ||
                    membership.Revision != expectedRevision)
                    throw new ProjectAuthorityConcurrencyException("The membership revision has changed.");

                foreach (var projectId in ownedProjects)
                {
                    var otherOwnerExists = await (
                            from assignment in db.RoleAssignments.AsNoTracking()
                            join activeMembership in db.TenantMemberships.AsNoTracking()
                                on assignment.MembershipId equals activeMembership.MembershipId
                            where assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                                assignment.ResourceId == projectId &&
                                assignment.Role == ProjectAuthorityRole.Owner &&
                                assignment.State == ProjectAuthorityRecordState.Active &&
                                assignment.MembershipId != membershipId &&
                                activeMembership.State == ProjectAuthorityRecordState.Active
                            select assignment.AssignmentId)
                        .AnyAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!otherOwnerExists)
                        throw new ProjectAuthorityConcurrencyException(
                            "Cannot revoke a membership that holds the last explicit Owner assignment.");
                }

                membership.State = ProjectAuthorityRecordState.Revoked;
                membership.Revision = checked(membership.Revision + 1);
                membership.RevokedBy = actor;
                membership.RevokedAt = timeProvider.GetUtcNow();
                db.AuthorityAudit.Add(ToAudit(membership, "membership_revoked", actor));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsSerializationFailure(exception) && attempt < MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ProjectAuthorityConcurrencyException(
                    "The membership revision changed while it was being revoked.");
            }
        }
    }

    private async Task ValidateAssignmentScopeAsync(
        ProjectsConfigDbContext db,
        ProjectTenantMembershipRecord membership,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role,
        CancellationToken cancellationToken)
    {
        var supported = (resourceType, role) switch
        {
            (ProjectAuthorityResourceType.Platform, ProjectAuthorityRole.PlatformAdmin) =>
                resourceId == ProjectAuthorizationOwner.PlatformResourceId,
            (ProjectAuthorityResourceType.Tenant, ProjectAuthorityRole.TenantAdmin) =>
                resourceId == membership.TenantId,
            (ProjectAuthorityResourceType.Project, ProjectAuthorityRole.Owner or
                ProjectAuthorityRole.Contributor or ProjectAuthorityRole.Viewer or ProjectAuthorityRole.Orchestrator) =>
                await db.Projects.AsNoTracking().AnyAsync(project =>
                    project.ProjectId == resourceId && project.TenantId == membership.TenantId,
                    cancellationToken).ConfigureAwait(false),
            _ => false,
        };
        if (!supported)
            throw ProjectConfigException.Forbidden();
    }

    private async Task<bool> HasOtherActiveProjectOwnerAsync(
        ProjectsConfigDbContext db,
        ProjectRoleAssignmentRecord assignment,
        CancellationToken cancellationToken) =>
        await (
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
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task LockProjectOwnersAsync(
        ProjectsConfigDbContext db,
        string projectId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"project-owner:" + projectId}, 0))",
            cancellationToken).ConfigureAwait(false);
    }

    private ProjectAuthorityAuditRecord ToAudit(
        ProjectTenantMembershipRecord membership,
        string eventType,
        string actor) => new()
    {
        EventId = Guid.NewGuid(),
        EventType = eventType,
        MembershipId = membership.MembershipId,
        Issuer = membership.Issuer,
        Subject = membership.Subject,
        TenantId = membership.TenantId,
        Revision = membership.Revision,
        Actor = actor,
        CreatedAt = timeProvider.GetUtcNow(),
    };

    private ProjectAuthorityAuditRecord ToAudit(
        ProjectTenantMembershipRecord membership,
        ProjectRoleAssignmentRecord assignment,
        string eventType,
        string actor) => new()
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
        CreatedAt = timeProvider.GetUtcNow(),
    };

    private static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: "40001" })
                return true;
        return false;
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: "23505" })
                return true;
        return false;
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':' or '/')))
            throw new ArgumentException("Expected a nonempty opaque authority identifier.", parameterName);
    }
}
