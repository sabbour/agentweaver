extern alias AzureIdentity;

using Azure.Core;
using Npgsql;
using WorkloadIdentityCredentialOptions =
    AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.Orchestrator;

internal sealed record CoordinationPostgresConnection(
    string ConnectionString,
    WorkloadIdentityCredentialOptions Identity);

internal static class CoordinationPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static CoordinationPostgresConnection ReadConnection(
        IConfiguration configuration,
        string connectionName,
        string identitySection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(connectionName);
        var identity = configuration.GetSection(identitySection).Get<WorkloadIdentityCredentialOptions>();
        if (string.IsNullOrWhiteSpace(connectionString) || identity is null ||
            !Guid.TryParse(identity.TenantId, out _) || !Guid.TryParse(identity.ClientId, out _) ||
            !Path.IsPathFullyQualified(identity.TokenFilePath))
            throw new InvalidOperationException(
                $"'{identitySection}' and 'ConnectionStrings:{connectionName}' are required.");
        return new CoordinationPostgresConnection(connectionString, identity);
    }

    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("The Orchestrator PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException(
                "Orchestrator PostgreSQL connections must use Entra token authentication, not a password.");

        connection.SslMode = SslMode.VerifyFull;
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            async (_, cancellationToken) =>
            {
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext([TokenScope]), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token.Token) || token.ExpiresOn <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException(
                        "Orchestrator PostgreSQL Entra token acquisition returned an invalid token.");
                return token.Token;
            });
        return builder.Build();
    }
}
