using Azure.Core;
using Npgsql;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Creates the broker's PostgreSQL data source using asynchronous Microsoft Entra token
/// authentication and full certificate verification. Password-based connection settings
/// and synchronous token acquisition are rejected.
/// </summary>
internal static class IdentityBrokerPostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Builds a validated data source whose password provider obtains Entra tokens asynchronously.</summary>
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

    /// <summary>Gets a nonempty access token for the PostgreSQL Entra resource.</summary>
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
