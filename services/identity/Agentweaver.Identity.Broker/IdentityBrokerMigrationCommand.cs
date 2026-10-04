extern alias AzureIdentity;

using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.Identity.Broker;

internal sealed record IdentityBrokerMigrationIdentity(string TenantId, string ClientId, string TokenFilePath);

internal static class IdentityBrokerMigrationCommand
{
    private const string Argument = "--migrate";

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == Argument;

    public static bool ContainsArgument(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static async Task RunAsync(
        IConfiguration configuration,
        Func<IdentityBrokerMigrationIdentity, TokenCredential>? credentialFactory = null,
        SslMode sslMode = SslMode.VerifyFull,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Required(configuration.GetConnectionString("IdentityBrokerMigration"),
            "ConnectionStrings:IdentityBrokerMigration");
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
