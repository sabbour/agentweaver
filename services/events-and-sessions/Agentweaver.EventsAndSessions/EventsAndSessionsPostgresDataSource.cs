extern alias AzureIdentity;

using Azure.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.EventsAndSessions;

/// <summary>Holds one PostgreSQL endpoint and its explicit Entra workload-identity inputs.</summary>
internal sealed record EventsAndSessionsPostgresConnection(
    string ConnectionString,
    string TenantId,
    string ClientId,
    string TokenFilePath);

/// <summary>Builds runtime or migration PostgreSQL data sources using short-lived Entra tokens.</summary>
internal static class EventsAndSessionsPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Reads the runtime database connection and workload identity configuration.</summary>
    public static EventsAndSessionsPostgresConnection ReadRuntimeConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "EventsAndSessions", "EventsAndSessions:Database:WorkloadIdentity");

    /// <summary>Reads the separately scoped migration database connection and workload identity.</summary>
    public static EventsAndSessionsPostgresConnection ReadMigrationConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "EventsAndSessionsMigration", "EventsAndSessions:Migration:WorkloadIdentity");

    /// <summary>Creates the Azure credential from the configured tenant, client, and projected token file.</summary>
    public static TokenCredential CreateCredential(EventsAndSessionsPostgresConnection connection) =>
        new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
        {
            TenantId = connection.TenantId,
            ClientId = connection.ClientId,
            TokenFilePath = connection.TokenFilePath,
        });

    /// <summary>Creates a PostgreSQL source that obtains Entra passwords asynchronously for new connections.</summary>
    /// <exception cref="InvalidOperationException">The connection is missing a database role, uses a password, or token acquisition fails.</exception>
    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential,
        SslMode sslMode = SslMode.VerifyFull,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
        timeProvider ??= TimeProvider.System;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("The Events & Sessions PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException(
                "Events & Sessions PostgreSQL connections must use Entra token authentication, not a password.");

        connection.SslMode = sslMode;
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            async (_, cancellationToken) =>
                await GetPostgresTokenAsync(credential, cancellationToken, timeProvider).ConfigureAwait(false));
        return builder.Build();
    }

    /// <summary>Gets a non-empty, unexpired token for the PostgreSQL Entra scope.</summary>
    /// <exception cref="InvalidOperationException">The credential returns an empty or expired token.</exception>
    internal static async ValueTask<string> GetPostgresTokenAsync(
        TokenCredential credential,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        timeProvider ??= TimeProvider.System;
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new InvalidOperationException("Events & Sessions PostgreSQL Entra token acquisition returned an empty token.");
        if (token.ExpiresOn <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Events & Sessions PostgreSQL Entra token acquisition returned an expired token.");
        return token.Token;
    }

    /// <summary>Validates the named connection string and requires explicit workload identity configuration.</summary>
    private static EventsAndSessionsPostgresConnection ReadConnection(
        IConfiguration configuration,
        string connectionName,
        string identitySection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = Required(configuration.GetConnectionString(connectionName),
            $"ConnectionStrings:{connectionName}");
        var options = configuration.GetSection(identitySection).Get<WorkloadIdentityCredentialOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section '{identitySection}'.");
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.ClientId, out _) ||
            !Path.IsPathFullyQualified(options.TokenFilePath))
            throw new InvalidOperationException(
                $"'{identitySection}' requires GUID tenant/client IDs and an absolute token-file path.");
        return new EventsAndSessionsPostgresConnection(
            connectionString, options.TenantId, options.ClientId, options.TokenFilePath);
    }

    /// <summary>Returns a configured nonblank value or stops startup with the missing key named.</summary>
    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
