using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

/// <summary>Runs the operator-only first-platform-admin bootstrap with the approved identity and migration connection.</summary>
internal static class PlatformAdminBootstrapCommand
{
    internal const string Argument = "--bootstrap-first-platform-admin";

    internal static bool IsCommandAlone(string[] args) =>
        args.Length == 1 && args[0] == Argument;

    internal static ApprovedTenantAdminIdentity ReadApprovedIdentity(IConfiguration configuration) =>
        TenantAdminBootstrapCommand.ReadApprovedIdentity(configuration);

    public static async Task<bool> RunAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var identity = ReadApprovedIdentity(configuration);
        await using var dataSource = ProjectsConfigPostgresDataSource.CreateMigrationDataSource(configuration);
        var dbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(
                dataSource,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        // Fail closed when runtime configuration uses a schema that the operator has not migrated yet.
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(
            dataSource, dbOptions, cancellationToken).ConfigureAwait(false);

        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(dbOptions, TimeProvider.System);
        return await authorityStore.BootstrapFirstPlatformAdminAsync(
            identity.Issuer,
            identity.Subject,
            identity.TenantId,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class PlatformAdminBootstrapConflictException()
    : InvalidOperationException("An active PlatformAdmin already exists.");
