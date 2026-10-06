extern alias AzureIdentity;

using Azure.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.EventsAndSessions;

internal sealed record EventsAndSessionsPostgresConnection(
    string ConnectionString,
    string TenantId,
    string ClientId,
    string TokenFilePath);

internal static class EventsAndSessionsPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static EventsAndSessionsPostgresConnection ReadRuntimeConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "EventsAndSessions", "EventsAndSessions:Database:WorkloadIdentity");

    public static EventsAndSessionsPostgresConnection ReadMigrationConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "EventsAndSessionsMigration", "EventsAndSessions:Migration:WorkloadIdentity");

    public static TokenCredential CreateCredential(EventsAndSessionsPostgresConnection connection) =>
        new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
        {
            TenantId = connection.TenantId,
            ClientId = connection.ClientId,
            TokenFilePath = connection.TokenFilePath,
        });

    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential,
        SslMode sslMode = SslMode.VerifyFull)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
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
                await GetPostgresTokenAsync(credential, cancellationToken).ConfigureAwait(false));
        return builder.Build();
    }

    internal static async ValueTask<string> GetPostgresTokenAsync(
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new InvalidOperationException("Events & Sessions PostgreSQL Entra token acquisition returned an empty token.");
        if (token.ExpiresOn <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Events & Sessions PostgreSQL Entra token acquisition returned an expired token.");
        return token.Token;
    }

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

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
