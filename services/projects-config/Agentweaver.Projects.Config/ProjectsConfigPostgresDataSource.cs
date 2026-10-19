extern alias AzureIdentity;

using Azure.Core;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.Projects.Config;

/// <summary>Builds passwordless PostgreSQL connections using workload identity and full server-certificate validation.</summary>
internal static class ProjectsConfigPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Creates a data source that obtains PostgreSQL access tokens asynchronously and forces TLS hostname verification.
    /// </summary>
    /// <exception cref="InvalidOperationException">The role is missing, a password is configured, or token acquisition is invalid.</exception>
    public static NpgsqlDataSource Create(string connectionString, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
        var connection = CreatePasswordlessConnectionString(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            async (_, cancellationToken) =>
                await GetPostgresTokenAsync(credential, cancellationToken, TimeProvider.System).ConfigureAwait(false));
        return builder.Build();
    }

    /// <summary>
    /// Creates the separate migration connection with either a short-lived operator token or migration workload identity.
    /// </summary>
    public static NpgsqlDataSource CreateMigrationDataSource(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("ProjectsConfigMigration")
            ?? throw new InvalidOperationException(
                "Missing required configuration 'ConnectionStrings:ProjectsConfigMigration'.");
        var accessToken = configuration["ProjectsConfig:Migration:AccessToken"];
        if (accessToken is not null)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException(
                    "'ProjectsConfig:Migration:AccessToken' must not be empty.");
            if (configuration.GetSection("ProjectsConfig:Migration:WorkloadIdentity").Exists())
                throw new InvalidOperationException(
                    "Configure either 'ProjectsConfig:Migration:AccessToken' or 'ProjectsConfig:Migration:WorkloadIdentity', not both.");

            var connection = CreatePasswordlessConnectionString(connectionString);
            connection.Password = accessToken;
            return NpgsqlDataSource.Create(connection.ConnectionString);
        }

        var identity = ReadWorkloadIdentityOptions(
            configuration, "ProjectsConfig:Migration:WorkloadIdentity");
        return Create(connectionString, new WorkloadIdentityCredential(identity));
    }

    /// <summary>Loads workload-identity settings and requires GUID tenant/client IDs plus an absolute projected token path.</summary>
    public static WorkloadIdentityCredentialOptions ReadWorkloadIdentityOptions(
        IConfiguration configuration,
        string section)
    {
        var options = configuration.GetSection(section).Get<WorkloadIdentityCredentialOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section '{section}'.");
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.ClientId, out _) ||
            !Path.IsPathFullyQualified(options.TokenFilePath))
            throw new InvalidOperationException(
                $"'{section}' requires GUID tenant/client IDs and an absolute token-file path.");
        return options;
    }

    private static NpgsqlConnectionStringBuilder CreatePasswordlessConnectionString(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("The Projects & Config PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException(
                "Projects & Config PostgreSQL connection strings must not contain a password.");

        connection.SslMode = SslMode.VerifyFull;
        return connection;
    }

    /// <summary>Requests a PostgreSQL-scoped token and rejects empty or already expired credentials.</summary>
    internal static async ValueTask<string> GetPostgresTokenAsync(
        TokenCredential credential,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new InvalidOperationException("Projects & Config PostgreSQL Entra token acquisition returned an empty token.");
        if (token.ExpiresOn <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Projects & Config PostgreSQL Entra token acquisition returned an expired token.");
        return token.Token;
    }
}
