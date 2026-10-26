using System.Data;
using System.Net.Mail;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Strict JSON request for assigning a project role to a member of the caller's tenant.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignProjectRoleRequest
{
    /// <summary>The subject of an active membership in the caller's tenant, under the caller's issuer.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>The project role to assign: owner, contributor, viewer, or orchestrator.</summary>
    public required ProjectAuthorityRole Role { get; init; }
}

/// <summary>A project role assignment as returned by the public role-assignment API.</summary>
public sealed record ProjectRoleAssignmentView(
    Guid AssignmentId,
    string ProjectId,
    string PrincipalId,
    ProjectAuthorityRole Role,
    ProjectAuthorityRecordState State,
    long Revision,
    string GrantedBy,
    DateTimeOffset GrantedAt,
    string? RevokedBy,
    DateTimeOffset? RevokedAt);

/// <summary>One active project role assignment in the access list.</summary>
public sealed record ProjectRoleAssignmentSummary(
    Guid AssignmentId,
    string PrincipalId,
    ProjectAuthorityRole Role,
    long Revision,
    string CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>The active role assignments of one project.</summary>
public sealed record ProjectRoleAssignmentList(
    string ProjectId,
    IReadOnlyList<ProjectRoleAssignmentSummary> Assignments);

/// <summary>
/// Lists, assigns, and revokes project roles through the public API.
/// </summary>
/// <remarks>
/// The runtime database principal writes only new assignment rows, the four revoke columns, and audit rows.
/// Each change locks the caller's current TenantAdmin authority, runs in a serializable transaction,
/// and writes a matching <c>authority_audit</c> row in the same transaction.
/// </remarks>
public sealed class ProjectRoleAssignmentService(
    IDbContextFactory<ProjectsConfigDbContext> dbFactory,
    TimeProvider timeProvider)
{
    /// <summary>Lists the active role assignments of one project for a tenant admin or a project Owner.</summary>
    /// <remarks>
    /// The caller needs the api.read scope and an unbound request token. The check uses the grants that were
    /// resolved for this request, so it runs before any project data is read.
    /// </remarks>
    /// <exception cref="ProjectConfigException">The caller is not a tenant admin or project Owner, or the project is not in the caller's tenant.</exception>
    public async Task<ProjectRoleAssignmentList> ListAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        RequireProjectId(projectId);
        if (!caller.IsTenantAdmin && !caller.HasRole(
                ProjectAuthorityResourceType.Project, projectId, ProjectAuthorityRole.Owner))
            throw ProjectConfigException.Forbidden();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var exists = await db.Projects.AsNoTracking()
            .AnyAsync(item => item.ProjectId == projectId && item.TenantId == caller.TenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            throw ProjectConfigException.NotFound();

        var rows = await (
                from assignment in db.RoleAssignments.AsNoTracking()
                join membership in db.TenantMemberships.AsNoTracking()
                    on assignment.MembershipId equals membership.MembershipId
                where assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                    assignment.ResourceId == projectId &&
                    assignment.State == ProjectAuthorityRecordState.Active &&
                    membership.TenantId == caller.TenantId
                select new
                {
                    assignment.AssignmentId,
                    membership.Subject,
                    assignment.Role,
                    assignment.Revision,
                    assignment.GrantedBy,
                    assignment.GrantedAt,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ProjectRoleAssignmentList(
            projectId,
            rows
                .OrderBy(row => row.Role)
                .ThenBy(row => row.Subject, StringComparer.Ordinal)
                .ThenBy(row => row.AssignmentId)
                .Select(row => new ProjectRoleAssignmentSummary(
                    row.AssignmentId, row.Subject, row.Role, row.Revision, row.GrantedBy, row.GrantedAt))
                .ToArray());
    }

    /// <summary>Assigns one project role to an active member of the caller's tenant.</summary>
    /// <exception cref="ProjectConfigException">The caller, project, principal, role, or current state does not allow the assignment.</exception>
    public async Task<ProjectRoleAssignmentView> AssignAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        AssignProjectRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adminGrant = RequireTenantAdminCaller(caller, projectId);
        if (!ProjectAuthorityRules.IsProjectRole(request.Role))
            throw ProjectConfigException.Invalid(
                "invalid_project_role",
                "The role must be owner, contributor, viewer, or orchestrator.");
        var principalId = request.PrincipalId;
        if (string.IsNullOrWhiteSpace(principalId) || principalId.Length > 256 ||
            principalId.Any(char.IsControl) || principalId.Trim().Length != principalId.Length)
            throw ProjectConfigException.Invalid(
                "invalid_principal_id",
                "The principal ID must be the nonempty subject of a tenant member.");
        // Broker email is profile data, not a stable identity key; never resolve it to a local subject here.
        if (MailAddress.TryCreate(principalId, out var email) &&
            string.Equals(email.Address, principalId, StringComparison.OrdinalIgnoreCase))
            throw ProjectConfigException.Invalid(
                "email_principal_not_supported",
                "Email addresses cannot identify project members. Sign in to Agentweaver and use the member's Broker subject ID; email is not mapped to an identity.");

        return await RunSerializableAsync(async db =>
        {
            await LockCurrentTenantAdminAsync(db, caller, adminGrant, projectId, cancellationToken)
                .ConfigureAwait(false);
            var project = await db.Projects.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.TenantId == caller.TenantId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (project.State != ProjectLifecycleState.Active)
                throw ProjectConfigException.Conflict(
                    "project_archived",
                    "Roles cannot be assigned on an archived project.");

            var membership = await db.TenantMemberships.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Issuer == caller.Issuer &&
                        item.Subject == principalId &&
                        item.TenantId == caller.TenantId &&
                        item.State == ProjectAuthorityRecordState.Active,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ProjectConfigException(
                    "principal_not_found",
                    "The principal has no active membership in the current tenant.",
                    StatusCodes.Status404NotFound);
            if (request.Role == ProjectAuthorityRole.Owner)
                await ProjectAuthorityRules.LockProjectOwnersAsync(db, projectId, cancellationToken)
                    .ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var assignment = new ProjectRoleAssignmentRecord
            {
                AssignmentId = Guid.NewGuid(),
                MembershipId = membership.MembershipId,
                ResourceType = ProjectAuthorityResourceType.Project,
                ResourceId = projectId,
                Role = request.Role,
                State = ProjectAuthorityRecordState.Active,
                Revision = 1,
                GrantedBy = caller.ActorId,
                GrantedAt = now,
            };
            db.RoleAssignments.Add(assignment);
            // The P1 fixture name carries its invocation ID; ordinary project role audits remain unlinked.
            db.AuthorityAudit.Add(ProjectAuthorityRules.CreateRoleAudit(
                membership,
                assignment,
                "role_assigned",
                caller.ActorId,
                now,
                ProjectAuthorityRules.P1SetupInvocationId(project.Name)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToView(assignment, membership.Subject);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes one active project role assignment at its expected revision.</summary>
    /// <remarks>
    /// The last active Owner of an active project cannot be revoked. After a project is archived,
    /// its last Owner can be revoked because a tenant admin keeps authority over the project.
    /// </remarks>
    /// <exception cref="ProjectConfigException">The caller, assignment, revision, or Owner rule does not allow the revocation.</exception>
    public async Task<ProjectRoleAssignmentView> RevokeAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid assignmentId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var adminGrant = RequireTenantAdminCaller(caller, projectId);
        if (expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw ProjectConfigException.Invalid(
                "invalid_expected_revision",
                "The expected revision must be a positive integer.");

        return await RunSerializableAsync(async db =>
        {
            await LockCurrentTenantAdminAsync(db, caller, adminGrant, projectId, cancellationToken)
                .ConfigureAwait(false);
            var project = await db.Projects.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.TenantId == caller.TenantId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var current = await db.RoleAssignments.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.AssignmentId == assignmentId &&
                        item.ResourceType == ProjectAuthorityResourceType.Project &&
                        item.ResourceId == projectId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (current.Role == ProjectAuthorityRole.Owner)
                await ProjectAuthorityRules.LockProjectOwnersAsync(db, projectId, cancellationToken)
                    .ConfigureAwait(false);

            var assignment = await db.RoleAssignments
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM projects_config.project_role_assignments
                    WHERE assignment_id = {assignmentId}
                      AND resource_type = 'Project'
                      AND resource_id = {projectId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (assignment.State != ProjectAuthorityRecordState.Active ||
                assignment.Revision != expectedRevision)
                throw ProjectConfigException.Conflict("The role assignment state or revision has changed.");
            if (assignment.Role == ProjectAuthorityRole.Owner &&
                project.State == ProjectLifecycleState.Active &&
                !await ProjectAuthorityRules.HasOtherActiveProjectOwnerAsync(db, assignment, cancellationToken)
                    .ConfigureAwait(false))
                throw ProjectConfigException.Conflict(
                    "last_project_owner",
                    "The last Owner of an active project cannot be revoked.");

            var membership = await db.TenantMemberships.AsNoTracking()
                .SingleAsync(item => item.MembershipId == assignment.MembershipId, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            assignment.State = ProjectAuthorityRecordState.Revoked;
            assignment.Revision = checked(assignment.Revision + 1);
            assignment.RevokedBy = caller.ActorId;
            assignment.RevokedAt = now;
            db.AuthorityAudit.Add(ProjectAuthorityRules.CreateRoleAudit(
                membership,
                assignment,
                "role_revoked",
                caller.ActorId,
                now,
                ProjectAuthorityRules.P1SetupInvocationId(project.Name)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToView(assignment, membership.Subject);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Requires an unbound api.read and projects.admin request from a tenant admin in the resolved snapshot.</summary>
    private static ProjectAuthorizationGrant RequireTenantAdminCaller(
        ProjectAuthorizationContext caller,
        string projectId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        RequireProjectId(projectId);
        return caller.Grants.FirstOrDefault(grant =>
                grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                string.Equals(grant.ResourceId, caller.TenantId, StringComparison.Ordinal) &&
                grant.Role == ProjectAuthorityRole.TenantAdmin)
            ?? throw ProjectConfigException.Forbidden();
    }

    private static void RequireProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 32 ||
            !projectId.All(char.IsAsciiLetterOrDigit))
            throw ProjectConfigException.NotFound();
    }

    /// <summary>
    /// Share-locks the caller's membership and authority through the definer function, then confirms
    /// that the exact TenantAdmin assignment from the request snapshot is still active at the same revision.
    /// </summary>
    private static async Task LockCurrentTenantAdminAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        ProjectAuthorizationGrant adminGrant,
        string projectId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using (var authorityLock = new NpgsqlCommand(
            """
            SELECT projects_config.lock_casting_authority(
                @membership_id, @issuer, @subject, @tenant_id, @membership_revision, @project_id, true)
            """,
            connection,
            transaction))
        {
            authorityLock.Parameters.AddWithValue("membership_id", caller.MembershipId);
            authorityLock.Parameters.AddWithValue("issuer", caller.Issuer);
            authorityLock.Parameters.AddWithValue("subject", caller.ActorId);
            authorityLock.Parameters.AddWithValue("tenant_id", caller.TenantId);
            authorityLock.Parameters.AddWithValue("membership_revision", caller.MembershipRevision);
            authorityLock.Parameters.AddWithValue("project_id", projectId);
            var result = await authorityLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result as string, "authorized", StringComparison.Ordinal))
                throw ProjectConfigException.Forbidden();
        }

        // The definer function already share-locked the TenantAdmin row, so a plain read is stable here.
        var current = await db.RoleAssignments.AsNoTracking()
            .AnyAsync(item =>
                item.AssignmentId == adminGrant.AssignmentId &&
                item.MembershipId == caller.MembershipId &&
                item.ResourceType == ProjectAuthorityResourceType.Tenant &&
                item.ResourceId == caller.TenantId &&
                item.Role == ProjectAuthorityRole.TenantAdmin &&
                item.State == ProjectAuthorityRecordState.Active &&
                item.Revision == adminGrant.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        if (!current)
            throw ProjectConfigException.Forbidden();
    }

    /// <summary>
    /// Runs one change in a new serializable transaction, retrying only serialization failures that happen
    /// before commit. Missing runtime grants map to a stable unavailable response.
    /// </summary>
    private async Task<T> RunSerializableAsync<T>(
        Func<ProjectsConfigDbContext, Task<T>> change,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var result = await change(db).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception) when (
                ProjectAuthorityRules.IsSerializationFailure(exception) &&
                attempt < ProjectAuthorityRules.MaxSerializationRetries - 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (ProjectAuthorityRules.IsSerializationFailure(exception))
            {
                throw ProjectConfigException.Conflict(
                    "The role assignment changed concurrently. Read the current state before you try again.");
            }
            catch (DbUpdateException exception) when (ProjectAuthorityRules.IsUniqueViolation(exception))
            {
                throw ProjectConfigException.Conflict(
                    "role_assignment_exists",
                    "The principal already has an active assignment for this project role.");
            }
            catch (DbUpdateConcurrencyException)
            {
                throw ProjectConfigException.Conflict("The role assignment revision changed during the request.");
            }
            catch (Exception exception) when (ProjectAuthorityRules.IsPermissionDenied(exception))
            {
                throw new ProjectConfigException(
                    "role_assignment_unavailable",
                    "Project role assignment is not enabled for this deployment.",
                    StatusCodes.Status503ServiceUnavailable);
            }
        }
    }

    private static ProjectRoleAssignmentView ToView(ProjectRoleAssignmentRecord assignment, string principalId) =>
        new(
            assignment.AssignmentId,
            assignment.ResourceId,
            principalId,
            assignment.Role,
            assignment.State,
            assignment.Revision,
            assignment.GrantedBy,
            assignment.GrantedAt,
            assignment.RevokedBy,
            assignment.RevokedAt);
}
