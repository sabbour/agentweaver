using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

internal static class PlatformAdminInspectionCommand
{
    internal const string Argument = "--inspect-first-platform-admin";

    internal static bool IsCommandAlone(string[] args) =>
        args.Length == 1 && args[0] == Argument;

    internal static async Task<PlatformAdminInspectionSnapshot> RunAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var identity = PlatformAdminBootstrapCommand.ReadApprovedIdentity(configuration);
        await using var dataSource = ProjectsConfigPostgresDataSource.CreateMigrationDataSource(configuration);
        var dbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(
                dataSource,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(
            dataSource, dbOptions, cancellationToken).ConfigureAwait(false);

        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(dbOptions, TimeProvider.System);
        return await authorityStore.InspectPlatformAdminAsync(
            identity.Issuer,
            identity.Subject,
            identity.TenantId,
            cancellationToken).ConfigureAwait(false);
    }

    internal static IEnumerable<string> Format(PlatformAdminInspectionSnapshot snapshot)
    {
        yield return "PLATFORM_ADMIN_INSPECTION_V1";
        if (snapshot.Membership is { } membership)
            yield return $"MEMBERSHIP|Active|{membership.MembershipId:D}|{membership.Revision}";
        else
            yield return "MEMBERSHIP|Absent|-|0";

        yield return $"ACTIVE_ASSIGNMENT_COUNT|{snapshot.Assignments.Count}";
        foreach (var assignment in snapshot.Assignments)
            yield return $"ASSIGNMENT|{assignment.AssignmentId:D}|{assignment.Revision}|Active|Platform|default|PlatformAdmin|{assignment.MembershipId:D}";

        yield return $"MEMBERSHIP_AUDIT_COUNT|{snapshot.MembershipAudits.Count}";
        foreach (var audit in snapshot.MembershipAudits)
            yield return $"AUDIT|membership_granted|-|-|{FormatTimestamp(audit.CreatedAt)}|{audit.MembershipId:D}";

        yield return $"ROLE_ASSIGNMENT_AUDIT_COUNT|{snapshot.RoleAssignmentAudits.Count}";
        foreach (var audit in snapshot.RoleAssignmentAudits)
            yield return $"AUDIT|role_assigned|Platform|default|PlatformAdmin|{FormatTimestamp(audit.CreatedAt)}|{audit.MembershipId:D}|{audit.AssignmentId:D}";

        yield return $"OTHER_ACTIVE_PLATFORM_ADMIN_COUNT|{snapshot.OtherActivePlatformAdminCount}";
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

internal sealed record PlatformAdminMembershipSnapshot(Guid MembershipId, long Revision);

internal sealed record PlatformAdminAssignmentSnapshot(Guid AssignmentId, Guid MembershipId, long Revision);

internal sealed record PlatformAdminAuditSnapshot(
    DateTimeOffset CreatedAt,
    Guid MembershipId,
    Guid? AssignmentId);

internal sealed record PlatformAdminInspectionSnapshot(
    PlatformAdminMembershipSnapshot? Membership,
    IReadOnlyList<PlatformAdminAssignmentSnapshot> Assignments,
    IReadOnlyList<PlatformAdminAuditSnapshot> MembershipAudits,
    IReadOnlyList<PlatformAdminAuditSnapshot> RoleAssignmentAudits,
    long OtherActivePlatformAdminCount);
