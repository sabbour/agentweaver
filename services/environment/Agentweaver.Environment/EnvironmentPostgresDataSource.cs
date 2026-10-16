extern alias AzureIdentity;

using Azure.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.Environment;

/// <summary>Validated PostgreSQL connection settings and workload identity inputs for one Environment database role.</summary>
internal sealed record EnvironmentPostgresConnection(
    string ConnectionString,
    string TenantId,
    string ClientId,
    string TokenFilePath);

/// <summary>Builds passwordless, Entra-authenticated PostgreSQL data sources for Environment operations.</summary>
internal static class EnvironmentPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Reads the runtime database connection and its dedicated workload identity settings.</summary>
    public static EnvironmentPostgresConnection ReadRuntimeConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "Environment", "Environment:Database:WorkloadIdentity");

    /// <summary>Reads the migration database connection and its separate workload identity settings.</summary>
    public static EnvironmentPostgresConnection ReadMigrationConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "EnvironmentMigration", "Environment:Migration:WorkloadIdentity");

    /// <summary>Creates an Azure workload identity credential from the validated connection settings.</summary>
    public static TokenCredential CreateCredential(EnvironmentPostgresConnection connection) =>
        new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
        {
            TenantId = connection.TenantId,
            ClientId = connection.ClientId,
            TokenFilePath = connection.TokenFilePath,
        });

    /// <summary>
    /// Builds a PostgreSQL data source that requires an Entra role, full TLS verification, and asynchronous token retrieval.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection uses a password or omits its PostgreSQL role.</exception>
    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
        timeProvider ??= TimeProvider.System;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("The Environment PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException(
                "Environment PostgreSQL connections must use Entra token authentication, not a password.");

        connection.SslMode = SslMode.VerifyFull;
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            async (_, cancellationToken) =>
                await GetPostgresTokenAsync(credential, cancellationToken, timeProvider).ConfigureAwait(false));
        return builder.Build();
    }

    /// <summary>Requests a fresh PostgreSQL Entra token and rejects empty or expired credentials.</summary>
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
            throw new InvalidOperationException(
                "Environment PostgreSQL Entra token acquisition returned an empty token.");
        if (token.ExpiresOn <= timeProvider.GetUtcNow())
            throw new InvalidOperationException(
                "Environment PostgreSQL Entra token acquisition returned an expired token.");
        return token.Token;
    }

    /// <summary>Loads connection and workload identity settings, rejecting incomplete or malformed configuration.</summary>
    private static EnvironmentPostgresConnection ReadConnection(
        IConfiguration configuration,
        string connectionName,
        string identitySection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = Required(
            configuration.GetConnectionString(connectionName),
            $"ConnectionStrings:{connectionName}");
        var options = configuration.GetSection(identitySection).Get<WorkloadIdentityCredentialOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section '{identitySection}'.");
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.ClientId, out _) ||
            !Path.IsPathFullyQualified(options.TokenFilePath))
            throw new InvalidOperationException(
                $"'{identitySection}' requires GUID tenant/client IDs and an absolute token-file path.");
        return new EnvironmentPostgresConnection(
            connectionString,
            options.TenantId,
            options.ClientId,
            options.TokenFilePath);
    }

    /// <summary>Returns a required configuration value or reports its missing setting.</summary>
    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
