using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config;

internal sealed record PrivilegedProjectSetupApproval(
    Uri Authority,
    string Audience,
    string ApprovedSubject,
    string ApprovedTenantId)
{
    public static PrivilegedProjectSetupApproval Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var authentication = configuration.GetSection("ProjectsConfig:Authentication");
        var authority = RequireHttpsUri(
            authentication["Authority"],
            "ProjectsConfig:Authentication:Authority");
        var audience = authentication["Audience"];
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException(
                "Missing required configuration 'ProjectsConfig:Authentication:Audience'.");

        var setup = configuration.GetSection("ProjectsConfig:PrivilegedSetup");
        var approvedIssuer = RequireHttpsUri(
            setup["ApprovedIssuer"],
            "ProjectsConfig:PrivilegedSetup:ApprovedIssuer");
        var approvedSubject = RequireIdentifier(
            setup["ApprovedSubject"],
            "ProjectsConfig:PrivilegedSetup:ApprovedSubject");
        var approvedTenantId = RequireIdentifier(
            setup["ApprovedTenantId"],
            "ProjectsConfig:PrivilegedSetup:ApprovedTenantId");
        if (!string.Equals(authority.AbsoluteUri, approvedIssuer.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The privileged setup issuer must match the configured Projects & Config authentication authority.");

        return new PrivilegedProjectSetupApproval(
            authority,
            audience,
            approvedSubject,
            approvedTenantId);
    }

    public void RequireApprovedCaller(ProjectAuthorizationContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!string.Equals(caller.Issuer, Authority.AbsoluteUri, StringComparison.Ordinal) ||
            !string.Equals(caller.ActorId, ApprovedSubject, StringComparison.Ordinal) ||
            !string.Equals(caller.TenantId, ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();
    }

    private static Uri RequireHttpsUri(string? value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) ||
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

internal sealed record P1ProjectSetupAuthorityConfiguration(
    string ConnectionString,
    PrivilegedProjectSetupApproval Approval)
{
    private const string AuthorityConnectionStringKey = "ConnectionStrings:ProjectsConfigAuthority";
    private const string AuthorityIdentitySection = "ProjectsConfig:AuthorityDatabase:WorkloadIdentity";
    private const string RuntimeIdentitySection = "ProjectsConfig:Database:WorkloadIdentity";
    private const string MigrationIdentitySection = "ProjectsConfig:Migration:WorkloadIdentity";
    private static readonly Regex AuthorityRolePattern = new(
        "^[a-z_][a-z0-9_-]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AuthorityHostPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static P1ProjectSetupAuthorityConfiguration? ReadOptional(
        IConfiguration configuration,
        string runtimeConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConnectionString);

        var authorityConnectionString = configuration[AuthorityConnectionStringKey];
        var authorityIdentitySection = configuration.GetSection(AuthorityIdentitySection);
        var hasAuthorityIdentity = authorityIdentitySection.Exists();
        if (authorityConnectionString is null && !hasAuthorityIdentity)
            return null;
        if (string.IsNullOrWhiteSpace(authorityConnectionString) || !hasAuthorityIdentity)
            throw new InvalidOperationException(
                "The P1 setup authority requires both its separate connection string and explicit workload identity.");

        var approval = PrivilegedProjectSetupApproval.Read(configuration);
        var runtimeIdentity = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
            configuration, RuntimeIdentitySection);
        var identity = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
            configuration, AuthorityIdentitySection);
        var authorityConnection = RequireAuthorityConnectionString(
            authorityConnectionString,
            AuthorityConnectionStringKey);
        var runtimeConnection = ParseConnectionString(runtimeConnectionString, "ConnectionStrings:ProjectsConfig");
        var runtimeTenantId = RequireGuid(runtimeIdentity.TenantId, RuntimeIdentitySection + ":TenantId");
        var authorityTenantId = RequireGuid(identity.TenantId, AuthorityIdentitySection + ":TenantId");
        var runtimeClientId = RequireGuid(runtimeIdentity.ClientId, RuntimeIdentitySection + ":ClientId");
        var authorityClientId = RequireGuid(identity.ClientId, AuthorityIdentitySection + ":ClientId");

        if (authorityTenantId != runtimeTenantId)
            throw new InvalidOperationException(
                "The P1 setup authority workload identity must use the Projects runtime tenant.");
        if (!string.Equals(identity.TokenFilePath, runtimeIdentity.TokenFilePath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The P1 setup authority workload identity must use the existing projected token file.");
        RequireSameDatabase(authorityConnection, runtimeConnection, AuthorityConnectionStringKey);
        RequireDistinctIdentity(
            authorityConnection,
            authorityClientId,
            runtimeConnection,
            runtimeClientId,
            "runtime");

        var migrationConnectionString = configuration["ConnectionStrings:ProjectsConfigMigration"];
        var migrationIdentitySection = configuration.GetSection(MigrationIdentitySection);
        var migrationAccessToken = configuration["ProjectsConfig:Migration:AccessToken"];
        if (migrationConnectionString is null)
        {
            if (migrationIdentitySection.Exists() || migrationAccessToken is not null)
                throw new InvalidOperationException(
                    "Projects & Config migration identity settings require their separate migration connection string.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(migrationConnectionString) ||
                migrationIdentitySection.Exists() && migrationAccessToken is not null ||
                !migrationIdentitySection.Exists() && migrationAccessToken is null)
                throw new InvalidOperationException(
                    "Projects & Config migration configuration must contain one complete connection and credential source.");
            var migrationConnection = RequirePasswordlessVerifyFull(
                migrationConnectionString,
                "ConnectionStrings:ProjectsConfigMigration");
            RequireSameDatabase(
                authorityConnection,
                migrationConnection,
                "ConnectionStrings:ProjectsConfigAuthority");
            if (string.Equals(
                    authorityConnection.Username,
                    migrationConnection.Username,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The P1 setup authority PostgreSQL role must differ from the migration role.");

            if (migrationIdentitySection.Exists())
            {
                var migrationIdentity = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
                    configuration, MigrationIdentitySection);
                var migrationTenantId = RequireGuid(
                    migrationIdentity.TenantId,
                    MigrationIdentitySection + ":TenantId");
                var migrationClientId = RequireGuid(
                    migrationIdentity.ClientId,
                    MigrationIdentitySection + ":ClientId");
                if (migrationTenantId != runtimeTenantId)
                    throw new InvalidOperationException(
                        "The Projects & Config migration workload identity must use the Projects runtime tenant.");
                if (authorityClientId == migrationClientId || runtimeClientId == migrationClientId)
                    throw new InvalidOperationException(
                        "The Projects runtime, setup authority, and migration workload identities must be distinct.");
            }
        }

        return new P1ProjectSetupAuthorityConfiguration(
            authorityConnectionString,
            approval);
    }

    private static NpgsqlConnectionStringBuilder RequirePasswordlessVerifyFull(
        string connectionString,
        string setting)
    {
        var connection = ParseConnectionString(connectionString, setting);
        if (string.IsNullOrWhiteSpace(connection.Username) ||
            !string.IsNullOrEmpty(connection.Password) ||
            connection.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException(
                $"'{setting}' requires a password-free PostgreSQL role and SSL Mode=VerifyFull.");
        return connection;
    }

    private static NpgsqlConnectionStringBuilder RequireAuthorityConnectionString(
        string connectionString,
        string setting)
    {
        var connection = RequirePasswordlessVerifyFull(connectionString, setting);
        var host = connection.Host;
        var username = connection.Username;
        if (string.IsNullOrWhiteSpace(host) ||
            host.Contains(',', StringComparison.Ordinal) ||
            !AuthorityHostPattern.IsMatch(host) ||
            string.IsNullOrWhiteSpace(username) ||
            !AuthorityRolePattern.IsMatch(username) ||
            string.Equals(username, "postgres", StringComparison.OrdinalIgnoreCase) ||
            username.Contains("migration", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{setting}' requires a single approved host and a separate non-postgres, non-migration role.");

        var canonical = $"Host={connection.Host};Port={connection.Port};Database={connection.Database};" +
            $"Username={username};SSL Mode=VerifyFull";
        if (!string.Equals(connectionString, canonical, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{setting}' may contain only unique Host, Port, Database, Username, and SSL Mode fields.");
        return connection;
    }

    private static NpgsqlConnectionStringBuilder ParseConnectionString(
        string connectionString,
        string setting)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"'{setting}' is not a valid PostgreSQL connection string.");
        }
    }

    private static void RequireSameDatabase(
        NpgsqlConnectionStringBuilder authority,
        NpgsqlConnectionStringBuilder other,
        string setting)
    {
        if (string.IsNullOrWhiteSpace(authority.Host) ||
            string.IsNullOrWhiteSpace(authority.Database) ||
            string.IsNullOrWhiteSpace(other.Host) ||
            string.IsNullOrWhiteSpace(other.Database) ||
            !string.Equals(authority.Host, other.Host, StringComparison.OrdinalIgnoreCase) ||
            authority.Port != other.Port ||
            !string.Equals(authority.Database, other.Database, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{setting}' must target the same PostgreSQL host, port, and database as the Projects runtime.");
    }

    private static void RequireDistinctIdentity(
        NpgsqlConnectionStringBuilder authorityConnection,
        Guid authorityClientId,
        NpgsqlConnectionStringBuilder otherConnection,
        Guid otherClientId,
        string otherName)
    {
        if (string.Equals(
                authorityConnection.Username,
                otherConnection.Username,
                StringComparison.Ordinal) ||
            authorityClientId == otherClientId)
            throw new InvalidOperationException(
                $"The P1 setup authority PostgreSQL role and workload identity must differ from the {otherName} identity.");
    }

    private static Guid RequireGuid(string? value, string setting) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException($"'{setting}' must be a nonempty GUID.");

}
