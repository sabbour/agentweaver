extern alias AzureIdentity;

using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.Identity.Broker;

/// <summary>Inputs required to authenticate the dedicated schema-migration workload identity.</summary>
internal sealed record IdentityBrokerMigrationIdentity(string TenantId, string ClientId, string TokenFilePath);

/// <summary>
/// Command-line entry point for running Identity broker schema migrations with its
/// separate workload identity and database connection. It does not run the web host.
/// </summary>
internal static class IdentityBrokerMigrationCommand
{
    private const string Argument = "--migrate";

    /// <summary>Returns whether the argument list consists only of the migration command.</summary>
    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == Argument;

    /// <summary>Detects the migration command so mixed maintenance and host commands can be rejected.</summary>
    public static bool ContainsArgument(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    /// <summary>
    /// Validates explicit migration credentials, creates an Entra-authenticated PostgreSQL
    /// data source, runs migrations under the schema-specific advisory lock, and applies the
    /// canonical grants to the explicitly configured runtime role. A supplied
    /// credential factory supports tests; disposable credentials are always disposed.
    /// </summary>
    public static async Task RunAsync(
        IConfiguration configuration,
        Func<IdentityBrokerMigrationIdentity, TokenCredential>? credentialFactory = null,
        SslMode sslMode = SslMode.VerifyFull,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Required(configuration.GetConnectionString("IdentityBrokerMigration"),
            "ConnectionStrings:IdentityBrokerMigration");
        var runtimeRole = Required(configuration["IdentityBroker:Migration:RuntimeRole"],
            "IdentityBroker:Migration:RuntimeRole");
        var migrationRole = Required(new NpgsqlConnectionStringBuilder(connectionString).Username,
            "ConnectionStrings:IdentityBrokerMigration Username");
        var tenantId = Required(configuration["IdentityBroker:Migration:WorkloadIdentityTenantId"],
            "IdentityBroker:Migration:WorkloadIdentityTenantId");
        var clientId = Required(configuration["IdentityBroker:Migration:WorkloadIdentityClientId"],
            "IdentityBroker:Migration:WorkloadIdentityClientId");
        var tokenFilePath = Required(configuration["IdentityBroker:Migration:WorkloadIdentityTokenFilePath"],
            "IdentityBroker:Migration:WorkloadIdentityTokenFilePath");

        if (!Guid.TryParse(tenantId, out _))
            throw new InvalidOperationException("Identity broker migration WorkloadIdentityTenantId must be a GUID.");
        if (!Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("Identity broker migration WorkloadIdentityClientId must be a GUID.");
        if (!Path.IsPathFullyQualified(tokenFilePath))
            throw new InvalidOperationException("Identity broker migration WorkloadIdentityTokenFilePath must be absolute.");

        var identity = new IdentityBrokerMigrationIdentity(tenantId, clientId, tokenFilePath);
        var credential = credentialFactory is null
            ? new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
            {
                TenantId = identity.TenantId,
                ClientId = identity.ClientId,
                TokenFilePath = identity.TokenFilePath,
            })
            : credentialFactory(identity);
        try
        {
            await using var dataSource = IdentityBrokerPostgresDataSource.Create(connectionString, credential, sslMode);
            var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
                .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", IdentityBrokerDbContext.Schema))
                .Options;

            await IdentityBrokerMigrator.MigrateAsync(dataSource, options, cancellationToken).ConfigureAwait(false);

            // The migration owner applies the reviewed table grants before the Job can succeed.
            // Repeating a completed migration also repairs missing grants without changing history.
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!await IdentityBrokerPostgresBootstrapCommand.ApplyRuntimeGrantsIfMigratedAsync(
                    connection, runtimeRole, migrationRole, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Identity broker migrations completed without migration history.");
        }
        finally
        {
            if (credential is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
