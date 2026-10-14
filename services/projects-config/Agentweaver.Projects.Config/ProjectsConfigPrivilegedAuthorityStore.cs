using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class ProjectAuthorityConcurrencyException(string message) : Exception(message);

public sealed class ProjectsConfigPrivilegedAuthorityStore(
    DbContextOptions<ProjectsConfigDbContext> options,
    TimeProvider timeProvider)
{
    private const int MaxSerializationRetries = 3;
    private const string ProjectSetupInvocationLockSql =
        "SELECT pg_advisory_lock(hashtextextended(@invocation_id::text, 0))";
    private const string ProjectSetupInvocationUnlockSql =
        "SELECT pg_advisory_unlock(hashtextextended(@invocation_id::text, 0))";
    private static readonly JsonSerializerOptions ProjectSetupJsonOptions = CreateProjectSetupJsonOptions();

    public async Task<ProjectSetupCommandResult> ExecuteProjectSetupOperationAsync(
        ProjectAuthorizationContext caller,
        ProjectSetupOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidateProjectSetupRequest(request);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireUnboundRequest();
        if (!string.Equals(caller.TenantId, request.TenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        var tenantAdminGrants = caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            string.Equals(grant.ResourceId, caller.TenantId, StringComparison.Ordinal) &&
            grant.Role == ProjectAuthorityRole.TenantAdmin).ToArray();
        if (tenantAdminGrants.Length != 1)
            throw ProjectConfigException.Forbidden();

        var requestHash = ComputeProjectSetupRequestHash(request, caller.Issuer, caller.ActorId);
        var candidateProjectId = Guid.NewGuid().ToString("N");
        await using var db = new ProjectsConfigDbContext(options);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var invocationLockAcquired = false;
        try
        {
            await using (var invocationLock = new NpgsqlCommand(ProjectSetupInvocationLockSql, connection))
            {
                invocationLock.Parameters.AddWithValue("invocation_id", request.InvocationId);
                await invocationLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                invocationLockAcquired = true;
            }

            for (var attempt = 0; ; attempt++)
            {
                await using var transaction = await db.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var authorityProjectId = await db.ProjectSetupOperations.AsNoTracking()
                        .Where(operation =>
                            operation.InvocationId == request.InvocationId &&
                            operation.OperationKind == ProjectSetupOperationKind.Setup)
                        .Select(operation => operation.ProjectId)
                        .SingleOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false) ?? candidateProjectId;
                    await LockCurrentTenantAdminAsync(
                        db,
                        caller,
                        tenantAdminGrants[0],
                        authorityProjectId,
                        cancellationToken).ConfigureAwait(false);
                    var result = await ExecuteProjectSetupOperationInTransactionAsync(
                        db, caller, request, requestHash, candidateProjectId, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (Exception exception) when (
                    IsSerializationFailure(exception) && attempt < MaxSerializationRetries - 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                }
            }
        }
        finally
        {
            try
            {
                await using var invocationUnlock = new NpgsqlCommand(
                    ProjectSetupInvocationUnlockSql, connection);
                invocationUnlock.Parameters.AddWithValue("invocation_id", request.InvocationId);
                var released = await invocationUnlock.ExecuteScalarAsync(CancellationToken.None)
                    .ConfigureAwait(false) is true;
                if (invocationLockAcquired && !released)
                    throw new InvalidOperationException(
                        "The project setup invocation lock was not held by this connection.");
            }
            catch
            {
                NpgsqlConnection.ClearPool(connection);
                throw;
            }
        }
    }

    internal static string ComputeProjectSetupRequestHash(
        ProjectSetupOperationRequest request,
        string issuer,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new
        {
            version = request.Version,
            operationKind = request.OperationKind.ToString().ToLowerInvariant(),
            invocationId = request.InvocationId.ToString("D"),
            tenantId = request.TenantId,
            issuer,
            subject,
            name = ProjectSetupProjectName(request.InvocationId),
            expectedProjectRevision = request.ExpectedProjectRevision,
            expectedOrchestratorAssignmentRevision = request.ExpectedAssignmentRevision,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, ProjectSetupJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task<ProjectSetupCommandResult> ExecuteProjectSetupOperationInTransactionAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        ProjectSetupOperationRequest request,
        string requestHash,
        string candidateProjectId,
        CancellationToken cancellationToken)
    {
        var operations = await db.ProjectSetupOperations.AsNoTracking()
            .Where(operation => operation.InvocationId == request.InvocationId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (operations.Any(operation =>
            !SameOperationOwner(operation, caller) ||
            operation.MembershipId != caller.MembershipId ||
            operation.MembershipRevision != caller.MembershipRevision))
            throw ProjectConfigException.Forbidden();

        var setup = operations.SingleOrDefault(operation =>
            operation.OperationKind == ProjectSetupOperationKind.Setup);
        var cleanup = operations.SingleOrDefault(operation =>
            operation.OperationKind == ProjectSetupOperationKind.Cleanup);

        if (request.OperationKind == ProjectSetupOperationKind.Setup)
        {
            if (setup is not null)
            {
                if (!string.Equals(setup.RequestHash, requestHash, StringComparison.Ordinal))
                    throw ProjectConfigException.Conflict(
                        "setup_invocation_conflict",
                        "The setup invocation was already used with different input.");
                return await ReadSetupReplayAsync(db, setup, cancellationToken).ConfigureAwait(false);
            }
            if (cleanup is not null)
                throw ProjectConfigException.Forbidden();

            return await CreateProjectSetupAsync(
                db, caller, request, requestHash, candidateProjectId, cancellationToken).ConfigureAwait(false);
        }

        if (setup is null)
            throw ProjectConfigException.Forbidden();
        if (cleanup is not null)
        {
            if (!string.Equals(cleanup.RequestHash, requestHash, StringComparison.Ordinal))
                throw ProjectConfigException.Conflict(
                    "cleanup_invocation_conflict",
                    "The cleanup invocation was already used with different input.");
            return await ReadCleanupReplayAsync(db, cleanup, cancellationToken).ConfigureAwait(false);
        }
        if (request.ExpectedProjectRevision != setup.ProjectRevision ||
            request.ExpectedAssignmentRevision != setup.AssignmentRevision)
            throw ProjectConfigException.Conflict(
                "cleanup_revision_conflict",
                "Cleanup revisions do not match the immutable setup receipt.");

        return await CleanupProjectSetupAsync(
            db, caller, request, requestHash, setup, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectSetupCommandResult> CreateProjectSetupAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        ProjectSetupOperationRequest request,
        string requestHash,
        string projectId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var project = new ProjectRecord
        {
            ProjectId = projectId,
            TenantId = caller.TenantId,
            CreatedByActorId = caller.ActorId,
            Name = ProjectSetupProjectName(request.InvocationId),
            State = ProjectLifecycleState.Active,
            Revision = 1,
            ConfigurationRevision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var configuration = new ProjectConfigurationRevisionRecord
        {
            ProjectId = projectId,
            Revision = 1,
            ConfigurationJson = ProjectsConfigService.SerializeProjectConfiguration(new ProjectConfiguration()),
            UpdatedByActorId = caller.ActorId,
            CreatedAt = now,
        };
        var assignment = new ProjectRoleAssignmentRecord
        {
            AssignmentId = Guid.NewGuid(),
            MembershipId = caller.MembershipId,
            ResourceType = ProjectAuthorityResourceType.Project,
            ResourceId = projectId,
            Role = ProjectAuthorityRole.Orchestrator,
            State = ProjectAuthorityRecordState.Active,
            Revision = 1,
            GrantedBy = caller.ActorId,
            GrantedAt = now,
        };
        var ownerAssignment = new ProjectRoleAssignmentRecord
        {
            AssignmentId = Guid.NewGuid(),
            MembershipId = caller.MembershipId,
            ResourceType = ProjectAuthorityResourceType.Project,
            ResourceId = projectId,
            Role = ProjectAuthorityRole.Owner,
            State = ProjectAuthorityRecordState.Active,
            Revision = 1,
            GrantedBy = caller.ActorId,
            GrantedAt = now,
        };
        var audit = ToAudit(
            new ProjectTenantMembershipRecord
            {
                MembershipId = caller.MembershipId,
                Issuer = caller.Issuer,
                Subject = caller.ActorId,
                TenantId = caller.TenantId,
                State = ProjectAuthorityRecordState.Active,
                Revision = caller.MembershipRevision,
                GrantedBy = caller.ActorId,
                GrantedAt = now,
            },
            assignment,
            "role_assigned",
            caller.ActorId);
        var ownerAudit = ToAudit(
            new ProjectTenantMembershipRecord
            {
                MembershipId = caller.MembershipId,
                Issuer = caller.Issuer,
                Subject = caller.ActorId,
                TenantId = caller.TenantId,
                State = ProjectAuthorityRecordState.Active,
                Revision = caller.MembershipRevision,
                GrantedBy = caller.ActorId,
                GrantedAt = now,
            },
            ownerAssignment,
            "role_assigned",
            caller.ActorId);
        var receipt = new ProjectSetupReceipt(
            request.InvocationId,
            caller.Issuer,
            caller.ActorId,
            caller.TenantId,
            caller.MembershipId,
            caller.MembershipRevision,
            project.ProjectId,
            project.CreatedByActorId,
            project.Revision,
            project.ConfigurationRevision,
            assignment.AssignmentId,
            assignment.Revision,
            audit.EventId,
            now)
        {
            OwnerAssignmentId = ownerAssignment.AssignmentId,
            OwnerAssignmentRevision = ownerAssignment.Revision,
            OwnerAuthorityAuditEventId = ownerAudit.EventId,
        };
        var operation = new ProjectSetupOperationRecord
        {
            InvocationId = request.InvocationId,
            OperationKind = ProjectSetupOperationKind.Setup,
            RequestHash = requestHash,
            Issuer = caller.Issuer,
            Subject = caller.ActorId,
            TenantId = caller.TenantId,
            MembershipId = caller.MembershipId,
            MembershipRevision = caller.MembershipRevision,
            ProjectId = project.ProjectId,
            ProjectRevision = project.Revision,
            ProjectConfigurationRevision = project.ConfigurationRevision,
            AssignmentId = assignment.AssignmentId,
            AssignmentRevision = assignment.Revision,
            OwnerAssignmentId = ownerAssignment.AssignmentId,
            OwnerAssignmentRevision = ownerAssignment.Revision,
            OwnerAuthorityAuditEventId = ownerAudit.EventId,
            AuthorityAuditEventId = audit.EventId,
            ResultState = ProjectSetupOperationResultState.Created,
            ResultJson = JsonSerializer.Serialize(receipt, ProjectSetupJsonOptions),
            CreatedAt = now,
        };

        db.Projects.Add(project);
        db.ProjectConfigurationRevisions.Add(configuration);
        db.RoleAssignments.Add(assignment);
        db.RoleAssignments.Add(ownerAssignment);
        db.AuthorityAudit.Add(audit);
        db.AuthorityAudit.Add(ownerAudit);
        db.ProjectSetupOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectSetupCommandResult(
            ProjectSetupOperationKind.Setup,
            Replayed: false,
            receipt,
            new ProjectSetupReadback(
                ProjectExists: true,
                ProjectLifecycleState.Active,
                project.Revision,
                project.ConfigurationRevision,
                InitialConfigurationExists: true,
                AssignmentExists: true,
                ProjectAuthorityRecordState.Active,
                assignment.Revision,
                AuthorityAuditExists: true,
                ownerAssignment.AssignmentId,
                OwnerAssignmentExists: true,
                ProjectAuthorityRecordState.Active,
                ownerAssignment.Revision,
                OwnerAuthorityAuditExists: true),
            Cleanup: null);
    }

    private async Task<ProjectSetupCommandResult> CleanupProjectSetupAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        ProjectSetupOperationRequest request,
        string requestHash,
        ProjectSetupOperationRecord setup,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .FromSqlInterpolated($"""
                SELECT *
                FROM projects_config.projects
                WHERE project_id = {setup.ProjectId}
                  AND tenant_id = {setup.TenantId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.CreatedByActorId != setup.Subject ||
            project.ProjectId != setup.ProjectId ||
            project.Revision != request.ExpectedProjectRevision ||
            project.ConfigurationRevision != setup.ProjectConfigurationRevision ||
            project.State != ProjectLifecycleState.Active)
            throw new ProjectAuthorityConcurrencyException(
                "The project no longer matches the immutable setup receipt.");

        var assignment = await db.RoleAssignments
            .FromSqlInterpolated($"""
                SELECT *
                FROM projects_config.project_role_assignments
                WHERE assignment_id = {setup.AssignmentId}
                  AND membership_id = {setup.MembershipId}
                  AND resource_type = 'Project'
                  AND resource_id = {setup.ProjectId}
                  AND role = 'Orchestrator'
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null ||
            assignment.State != ProjectAuthorityRecordState.Active ||
            assignment.Revision != request.ExpectedAssignmentRevision)
            throw new ProjectAuthorityConcurrencyException(
                "The Orchestrator assignment no longer matches the immutable setup receipt.");

        ProjectRoleAssignmentRecord? ownerAssignment = null;
        if (setup.OwnerAssignmentId is { } ownerAssignmentId)
        {
            if (setup.OwnerAssignmentRevision is not > 0 || setup.OwnerAuthorityAuditEventId is null)
                throw new InvalidOperationException("The project setup Owner assignment binding is incomplete.");
            ownerAssignment = await db.RoleAssignments
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM projects_config.project_role_assignments
                    WHERE assignment_id = {ownerAssignmentId}
                      AND membership_id = {setup.MembershipId}
                      AND resource_type = 'Project'
                      AND resource_id = {setup.ProjectId}
                      AND role = 'Owner'
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ownerAssignment is null ||
                ownerAssignment.State != ProjectAuthorityRecordState.Active ||
                ownerAssignment.Revision != setup.OwnerAssignmentRevision)
                throw new ProjectAuthorityConcurrencyException(
                    "The Owner assignment no longer matches the immutable setup receipt.");
        }
        else if (setup.OwnerAssignmentRevision is not null || setup.OwnerAuthorityAuditEventId is not null)
        {
            throw new InvalidOperationException("The project setup Owner assignment binding is incomplete.");
        }

        var hasAnySelection = await db.RunSelections.AsNoTracking()
            .AnyAsync(selection => selection.ProjectId == setup.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var resultState = hasAnySelection
            ? ProjectSetupOperationResultState.RetainedPending
            : ProjectSetupOperationResultState.Cleaned;
        ProjectAuthorityAuditRecord? audit = null;
        ProjectAuthorityAuditRecord? ownerAudit = null;
        if (!hasAnySelection)
        {
            assignment.State = ProjectAuthorityRecordState.Revoked;
            assignment.Revision = checked(assignment.Revision + 1);
            assignment.RevokedBy = caller.ActorId;
            assignment.RevokedAt = now;
            project.State = ProjectLifecycleState.Archived;
            project.Revision = checked(project.Revision + 1);
            project.UpdatedAt = now;
            audit = ToAudit(
                new ProjectTenantMembershipRecord
                {
                    MembershipId = setup.MembershipId,
                    Issuer = setup.Issuer,
                    Subject = setup.Subject,
                    TenantId = setup.TenantId,
                    State = ProjectAuthorityRecordState.Active,
                    Revision = setup.MembershipRevision,
                    GrantedBy = caller.ActorId,
                    GrantedAt = now,
                },
                assignment,
                "role_revoked",
                caller.ActorId);
            db.AuthorityAudit.Add(audit);
            if (ownerAssignment is not null)
            {
                ownerAssignment.State = ProjectAuthorityRecordState.Revoked;
                ownerAssignment.Revision = checked(ownerAssignment.Revision + 1);
                ownerAssignment.RevokedBy = caller.ActorId;
                ownerAssignment.RevokedAt = now;
                ownerAudit = ToAudit(
                    new ProjectTenantMembershipRecord
                    {
                        MembershipId = setup.MembershipId,
                        Issuer = setup.Issuer,
                        Subject = setup.Subject,
                        TenantId = setup.TenantId,
                        State = ProjectAuthorityRecordState.Active,
                        Revision = setup.MembershipRevision,
                        GrantedBy = caller.ActorId,
                        GrantedAt = now,
                    },
                    ownerAssignment,
                    "role_revoked",
                    caller.ActorId);
                db.AuthorityAudit.Add(ownerAudit);
            }
        }

        var receipt = new ProjectCleanupReceipt(
            request.InvocationId,
            setup.TenantId,
            setup.ProjectId,
            setup.AssignmentId,
            resultState,
            project.Revision,
            project.ConfigurationRevision,
            assignment.Revision,
            audit?.EventId,
            now)
        {
            OwnerAssignmentId = ownerAssignment?.AssignmentId,
            OwnerAssignmentRevision = ownerAssignment?.Revision,
            OwnerRevocationAuditEventId = ownerAudit?.EventId,
        };
        var operation = new ProjectSetupOperationRecord
        {
            InvocationId = request.InvocationId,
            OperationKind = ProjectSetupOperationKind.Cleanup,
            RequestHash = requestHash,
            Issuer = caller.Issuer,
            Subject = caller.ActorId,
            TenantId = caller.TenantId,
            MembershipId = caller.MembershipId,
            MembershipRevision = caller.MembershipRevision,
            ProjectId = setup.ProjectId,
            ProjectRevision = receipt.ProjectRevision,
            ProjectConfigurationRevision = setup.ProjectConfigurationRevision,
            AssignmentId = setup.AssignmentId,
            AssignmentRevision = receipt.AssignmentRevision,
            OwnerAssignmentId = setup.OwnerAssignmentId,
            OwnerAssignmentRevision = receipt.OwnerAssignmentRevision,
            OwnerAuthorityAuditEventId = setup.OwnerAuthorityAuditEventId,
            OwnerRevocationAuditEventId = receipt.OwnerRevocationAuditEventId,
            ExpectedProjectRevision = request.ExpectedProjectRevision,
            ExpectedAssignmentRevision = request.ExpectedAssignmentRevision,
            AuthorityAuditEventId = audit?.EventId,
            ResultState = resultState,
            ResultJson = JsonSerializer.Serialize(receipt, ProjectSetupJsonOptions),
            CreatedAt = now,
        };
        db.ProjectSetupOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectSetupCommandResult(
            ProjectSetupOperationKind.Cleanup,
            Replayed: false,
            Setup: null,
            SetupReadback: null,
            receipt);
    }

    private async Task<ProjectSetupCommandResult> ReadSetupReplayAsync(
        ProjectsConfigDbContext db,
        ProjectSetupOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var receipt = DeserializeOperationResult<ProjectSetupReceipt>(operation.ResultJson);
        if (receipt.InvocationId != operation.InvocationId ||
            receipt.ProjectId != operation.ProjectId ||
            receipt.AssignmentId != operation.AssignmentId ||
            receipt.ProjectRevision != operation.ProjectRevision ||
            receipt.ProjectConfigurationRevision != operation.ProjectConfigurationRevision ||
            receipt.AssignmentRevision != operation.AssignmentRevision ||
            receipt.AuthorityAuditEventId != operation.AuthorityAuditEventId ||
            receipt.MembershipId != operation.MembershipId ||
            receipt.MembershipRevision != operation.MembershipRevision ||
            receipt.Issuer != operation.Issuer ||
            receipt.Subject != operation.Subject ||
            receipt.TenantId != operation.TenantId ||
            receipt.OwnerAssignmentId != operation.OwnerAssignmentId ||
            receipt.OwnerAssignmentRevision != operation.OwnerAssignmentRevision ||
            receipt.OwnerAuthorityAuditEventId != operation.OwnerAuthorityAuditEventId)
            throw new InvalidOperationException("The project setup operation receipt does not match its immutable binding.");

        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == operation.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        var configurationExists = await db.ProjectConfigurationRevisions.AsNoTracking()
            .AnyAsync(item =>
                item.ProjectId == operation.ProjectId &&
                item.Revision == operation.ProjectConfigurationRevision,
                cancellationToken)
            .ConfigureAwait(false);
        var assignment = await db.RoleAssignments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AssignmentId == operation.AssignmentId, cancellationToken)
            .ConfigureAwait(false);
        var auditExists = operation.AuthorityAuditEventId is { } auditId &&
            await db.AuthorityAudit.AsNoTracking()
                .AnyAsync(item =>
                    item.EventId == auditId &&
                    item.AssignmentId == operation.AssignmentId &&
                    item.EventType == "role_assigned" &&
                    item.Revision == operation.AssignmentRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        ProjectRoleAssignmentRecord? ownerAssignment = null;
        if (operation.OwnerAssignmentId is { } ownerAssignmentId)
        {
            ownerAssignment = await db.RoleAssignments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.AssignmentId == ownerAssignmentId, cancellationToken)
                .ConfigureAwait(false);
        }
        var ownerAuditExists = operation.OwnerAuthorityAuditEventId is { } ownerAuditId &&
            await db.AuthorityAudit.AsNoTracking()
                .AnyAsync(item =>
                    item.EventId == ownerAuditId &&
                    item.AssignmentId == operation.OwnerAssignmentId &&
                    item.EventType == "role_assigned" &&
                    item.Revision == operation.OwnerAssignmentRevision &&
                    item.ResourceType == ProjectAuthorityResourceType.Project &&
                    item.ResourceId == operation.ProjectId &&
                    item.Role == ProjectAuthorityRole.Owner,
                    cancellationToken)
                .ConfigureAwait(false);
        return new ProjectSetupCommandResult(
            ProjectSetupOperationKind.Setup,
            Replayed: true,
            receipt,
            new ProjectSetupReadback(
                project is not null &&
                    project.TenantId == operation.TenantId &&
                    project.CreatedByActorId == operation.Subject,
                project?.State,
                project?.Revision,
                project?.ConfigurationRevision,
                configurationExists,
                assignment is not null &&
                    assignment.MembershipId == operation.MembershipId &&
                    assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                    assignment.ResourceId == operation.ProjectId &&
                    assignment.Role == ProjectAuthorityRole.Orchestrator,
                assignment?.State,
                assignment?.Revision,
                auditExists,
                operation.OwnerAssignmentId,
                ownerAssignment is not null &&
                    ownerAssignment.MembershipId == operation.MembershipId &&
                    ownerAssignment.ResourceType == ProjectAuthorityResourceType.Project &&
                    ownerAssignment.ResourceId == operation.ProjectId &&
                    ownerAssignment.Role == ProjectAuthorityRole.Owner,
                ownerAssignment?.State,
                ownerAssignment?.Revision,
                ownerAuditExists),
            Cleanup: null);
    }

    private async Task<ProjectSetupCommandResult> ReadCleanupReplayAsync(
        ProjectsConfigDbContext db,
        ProjectSetupOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var receipt = DeserializeOperationResult<ProjectCleanupReceipt>(operation.ResultJson);
        if (receipt.InvocationId != operation.InvocationId ||
            receipt.ProjectId != operation.ProjectId ||
            receipt.AssignmentId != operation.AssignmentId ||
            receipt.ProjectRevision != operation.ProjectRevision ||
            receipt.ProjectConfigurationRevision != operation.ProjectConfigurationRevision ||
            receipt.AssignmentRevision != operation.AssignmentRevision ||
            receipt.AuthorityAuditEventId != operation.AuthorityAuditEventId ||
            receipt.TenantId != operation.TenantId ||
            receipt.State != operation.ResultState ||
            receipt.OwnerAssignmentId != operation.OwnerAssignmentId ||
            receipt.OwnerAssignmentRevision != operation.OwnerAssignmentRevision ||
            receipt.OwnerRevocationAuditEventId != operation.OwnerRevocationAuditEventId)
            throw new InvalidOperationException("The project cleanup operation receipt does not match its immutable binding.");

        _ = await db.Projects.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == operation.ProjectId &&
                    item.TenantId == operation.TenantId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The project referenced by the cleanup receipt is missing.");
        _ = await db.RoleAssignments.AsNoTracking().SingleOrDefaultAsync(
                item => item.AssignmentId == operation.AssignmentId &&
                    item.MembershipId == operation.MembershipId &&
                    item.ResourceId == operation.ProjectId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The assignment referenced by the cleanup receipt is missing.");
        if (operation.OwnerAssignmentId is { } ownerAssignmentId)
        {
            var ownerAssignment = await db.RoleAssignments.AsNoTracking().SingleOrDefaultAsync(
                    item => item.AssignmentId == ownerAssignmentId &&
                        item.MembershipId == operation.MembershipId &&
                        item.ResourceType == ProjectAuthorityResourceType.Project &&
                        item.ResourceId == operation.ProjectId &&
                        item.Role == ProjectAuthorityRole.Owner,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Owner assignment referenced by the cleanup receipt is missing.");
            var expectedOwnerState = operation.ResultState == ProjectSetupOperationResultState.Cleaned
                ? ProjectAuthorityRecordState.Revoked
                : ProjectAuthorityRecordState.Active;
            if (ownerAssignment.State != expectedOwnerState ||
                ownerAssignment.Revision != operation.OwnerAssignmentRevision)
                throw new InvalidOperationException("The Owner assignment no longer matches the cleanup receipt.");
        }
        if (operation.OwnerRevocationAuditEventId is { } ownerRevocationAuditId &&
            !await db.AuthorityAudit.AsNoTracking().AnyAsync(
                item => item.EventId == ownerRevocationAuditId &&
                    item.AssignmentId == operation.OwnerAssignmentId &&
                    item.EventType == "role_revoked" &&
                    item.Revision == operation.OwnerAssignmentRevision,
                cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("The Owner revocation audit referenced by the cleanup receipt is missing.");
        return new ProjectSetupCommandResult(
            ProjectSetupOperationKind.Cleanup,
            Replayed: true,
            Setup: null,
            SetupReadback: null,
            receipt);
    }

    private async Task LockCurrentTenantAdminAsync(
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

        await using var exactAdmin = new NpgsqlCommand(
            """
            SELECT assignment_id, revision
            FROM projects_config.project_role_assignments
            WHERE assignment_id = @assignment_id
              AND membership_id = @membership_id
              AND resource_type = 'Tenant'
              AND resource_id = @tenant_id
              AND role = 'TenantAdmin'
              AND state = 'Active'
            FOR SHARE
            """,
            connection,
            transaction);
        exactAdmin.Parameters.AddWithValue("assignment_id", adminGrant.AssignmentId);
        exactAdmin.Parameters.AddWithValue("membership_id", caller.MembershipId);
        exactAdmin.Parameters.AddWithValue("tenant_id", caller.TenantId);
        await using var reader = await exactAdmin.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetGuid(0) != adminGrant.AssignmentId ||
            reader.GetInt64(1) != adminGrant.Revision)
            throw ProjectConfigException.Forbidden();
    }

    private static bool SameOperationOwner(
        ProjectSetupOperationRecord operation,
        ProjectAuthorizationContext caller) =>
        string.Equals(operation.Issuer, caller.Issuer, StringComparison.Ordinal) &&
        string.Equals(operation.Subject, caller.ActorId, StringComparison.Ordinal) &&
        string.Equals(operation.TenantId, caller.TenantId, StringComparison.Ordinal);

    private static void ValidateProjectSetupRequest(ProjectSetupOperationRequest request)
    {
        if (request.Version != 1 || request.InvocationId == Guid.Empty ||
            !Enum.IsDefined(request.OperationKind) ||
            string.IsNullOrWhiteSpace(request.TenantId) || request.TenantId.Length > 256 ||
            !request.TenantId.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':'))
            throw ProjectConfigException.Invalid(
                "invalid_project_setup_request",
                "The project setup request version, invocation ID, operation, or tenant ID is invalid.");

        var setupRequest = request.OperationKind == ProjectSetupOperationKind.Setup;
        if (setupRequest
                ? request.ExpectedProjectRevision is not null || request.ExpectedAssignmentRevision is not null
                : request.ExpectedProjectRevision is not > 0 ||
                    request.ExpectedProjectRevision == long.MaxValue ||
                    request.ExpectedAssignmentRevision is not > 0 ||
                    request.ExpectedAssignmentRevision == long.MaxValue)
            throw ProjectConfigException.Invalid(
                "invalid_project_setup_request",
                "Setup must omit cleanup revisions; cleanup must supply positive project and assignment revisions.");
    }

    private static string ProjectSetupProjectName(Guid invocationId) =>
        ProjectsConfigService.NormalizeProjectName($"p1-{invocationId:D}");

    private static T DeserializeOperationResult<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ProjectSetupJsonOptions)
        ?? throw new InvalidOperationException($"The stored project setup receipt is not valid {typeof(T).Name} JSON.");

    private static JsonSerializerOptions CreateProjectSetupJsonOptions()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return json;
    }

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
