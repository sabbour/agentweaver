using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

/// <summary>A bounded, token-free view of one authority audit event linked to a setup invocation.</summary>
public sealed record ProjectSetupAuthorityAuditEvent(
    Guid EventId,
    string EventType,
    Guid? AssignmentId,
    ProjectAuthorityResourceType? ResourceType,
    string? ResourceId,
    ProjectAuthorityRole? Role,
    long Revision,
    string Actor,
    DateTimeOffset CreatedAt);

/// <summary>The invocation-scoped authority audit read and whether its bounded result is complete.</summary>
public sealed record ProjectSetupAuthorityAuditRead(
    Guid InvocationId,
    bool Complete,
    bool Truncated,
    IReadOnlyList<ProjectSetupAuthorityAuditEvent> Records);

/// <summary>Reads invocation-linked audit rows using the existing Projects authority boundary.</summary>
public sealed class ProjectSetupAuthorityAuditService(ProjectsConfigDbContext db)
{
    public const int MaximumRecords = 32;

    public async Task<ProjectSetupAuthorityAuditRead> ReadAsync(
        ProjectAuthorizationContext caller,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        if (invocationId == Guid.Empty)
            throw ProjectConfigException.Invalid(
                "invalid_invocation_id", "A non-empty setup invocation ID is required.");

        var invocationAudit = db.AuthorityAudit.AsNoTracking()
            .Where(record => record.TenantId == caller.TenantId && record.InvocationId == invocationId);

        if (!caller.IsTenantAdmin)
        {
            var ownedProjectIds = caller.Grants
                .Where(grant =>
                    grant.ResourceType == ProjectAuthorityResourceType.Project &&
                    grant.Role == ProjectAuthorityRole.Owner)
                .Select(grant => grant.ResourceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // The invocation ID is not an authorization boundary: project Owners may see only records
            // for projects they currently own, and a missing or mixed-scope invocation is denied.
            if (ownedProjectIds.Length == 0 ||
                !await invocationAudit.AnyAsync(cancellationToken).ConfigureAwait(false) ||
                await invocationAudit.AnyAsync(record =>
                        record.ResourceType != ProjectAuthorityResourceType.Project ||
                        record.ResourceId == null ||
                        !ownedProjectIds.Contains(record.ResourceId),
                    cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
        }

        var rows = await invocationAudit
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.EventId)
            .Take(MaximumRecords + 1)
            .Select(record => new ProjectSetupAuthorityAuditEvent(
                record.EventId,
                record.EventType,
                record.AssignmentId,
                record.ResourceType,
                record.ResourceId,
                record.Role,
                record.Revision,
                record.Actor,
                record.CreatedAt))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var truncated = rows.Length > MaximumRecords;
        return new ProjectSetupAuthorityAuditRead(
            invocationId,
            Complete: !truncated,
            truncated,
            rows.Take(MaximumRecords).ToArray());
    }
}
