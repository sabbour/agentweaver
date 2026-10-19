using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Runs the operator-only first-admin bootstrap with the approved identity and migration connection.</summary>
internal static class TenantAdminBootstrapCommand
{
    private const string AuthoritySetting = "ProjectsConfig:Authentication:Authority";
    private const string SetupSetting = "ProjectsConfig:PrivilegedSetup";

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
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(
            dataSource, dbOptions, cancellationToken).ConfigureAwait(false);

        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(dbOptions, TimeProvider.System);
        return await authorityStore.BootstrapFirstTenantAdminAsync(
            identity.Issuer,
            identity.Subject,
            identity.TenantId,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ApprovedTenantAdminIdentity ReadApprovedIdentity(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var authority = RequireHttpsUri(configuration[AuthoritySetting], AuthoritySetting);
        var setup = configuration.GetSection(SetupSetting);
        var issuer = RequireHttpsUri(
            setup["ApprovedIssuer"], $"{SetupSetting}:ApprovedIssuer");
        var subject = RequireIdentifier(
            setup["ApprovedSubject"], $"{SetupSetting}:ApprovedSubject");
        var tenantId = RequireIdentifier(
            setup["ApprovedTenantId"], $"{SetupSetting}:ApprovedTenantId");
        if (!string.Equals(authority.AbsoluteUri, issuer.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The approved setup issuer must match the Projects & Config authentication authority.");

        return new ApprovedTenantAdminIdentity(issuer.AbsoluteUri, subject, tenantId);
    }

    private static Uri RequireHttpsUri(string? value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"'{setting}' requires an absolute HTTPS URI without user information, query, or fragment.");
        return uri;
    }

    private static string RequireIdentifier(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':'))
            throw new InvalidOperationException(
                $"'{setting}' requires a nonempty opaque identifier.");
        return value;
    }
}

internal sealed record ApprovedTenantAdminIdentity(string Issuer, string Subject, string TenantId);

internal sealed class TenantAdminBootstrapConflictException()
    : InvalidOperationException("An active TenantAdmin already exists for this tenant.");
