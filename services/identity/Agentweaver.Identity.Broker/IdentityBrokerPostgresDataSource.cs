using Azure.Core;
using Npgsql;

namespace Agentweaver.Identity.Broker;

internal static class IdentityBrokerPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential,
        SslMode sslMode = SslMode.VerifyFull)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("The Identity broker PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException("Identity broker PostgreSQL connections must use Entra token authentication, not a password.");

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
            throw new InvalidOperationException("Identity broker PostgreSQL Entra token acquisition returned an empty token.");
        return token.Token;
    }
}
