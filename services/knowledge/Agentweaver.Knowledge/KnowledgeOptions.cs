extern alias AzureIdentity;

using Agentweaver.Abstractions;
using Azure.Core;
using Npgsql;

namespace Agentweaver.Knowledge;

public sealed record NativePostgresMemoryOptions(
    string ResourceId,
    string DatabaseName,
    long ResourceGeneration,
    string Schema,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    public const int CurrentOptionsSchemaVersion = 1;

    public static NativePostgresMemoryOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Knowledge:Provider");
        var options = new NativePostgresMemoryOptions(
            Required(section["ResourceId"], "Knowledge:Provider:ResourceId"),
            Required(section["DatabaseName"], "Knowledge:Provider:DatabaseName"),
            section.GetValue<long>("ResourceGeneration"),
            section["Schema"] ?? "knowledge",
            section["OptionsRevision"] ?? "native-postgres-memory-v1",
            section.GetValue("OptionsSchemaVersion", CurrentOptionsSchemaVersion));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!IsToken(ResourceId) ||
            !IsToken(DatabaseName) ||
            ResourceGeneration < 1 ||
            !IsSchema(Schema) ||
            !IsToken(OptionsRevision) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Native PostgreSQL Memory options are invalid.");
    }

    private static bool IsSchema(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 &&
        char.IsAsciiLetterLower(value[0]) &&
        value.All(character => char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) || character == '_') &&
        value is not ("public" or "pg_catalog" or "information_schema") &&
        !value.StartsWith("pg_", StringComparison.Ordinal);

    private static bool IsToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}

public sealed record KnowledgeRuntimeOptions(
    Uri IdentityAuthority,
    string Audience,
    Uri ProjectsConfigBaseAddress,
    Uri EventsBaseAddress,
    string EventsAudience,
    NativePostgresMemoryOptions MemoryProvider,
    int MaximumContextCandidates,
    int DefaultContextItems,
    int DefaultContextTokens)
{
    public static KnowledgeRuntimeOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var identityAuthority = ReadHttpsUri(configuration["Identity:Issuer"], "Identity:Issuer");
        var projectsBaseAddress = ReadHttpsUri(
            configuration["ProjectsConfig:BaseAddress"], "ProjectsConfig:BaseAddress");
        var eventsBaseAddress = ReadHttpsUri(
            configuration["Knowledge:Events:BaseAddress"], "Knowledge:Events:BaseAddress");
        var audience = Required(configuration["Identity:Audience"], "Identity:Audience");
        var eventsAudience = Required(
            configuration["Knowledge:Events:Audience"], "Knowledge:Events:Audience");
        var provider = NativePostgresMemoryOptions.Read(configuration);

        var result = new KnowledgeRuntimeOptions(
            identityAuthority,
            audience,
            projectsBaseAddress,
            eventsBaseAddress,
            eventsAudience,
            provider,
            configuration.GetValue("Knowledge:Context:MaximumCandidates", 1000),
            configuration.GetValue("Knowledge:Context:MaximumItems", 20),
            configuration.GetValue("Knowledge:Context:MaximumTokens", 4000));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (!IdentityAuthority.IsAbsoluteUri || IdentityAuthority.Scheme != Uri.UriSchemeHttps ||
            !ProjectsConfigBaseAddress.IsAbsoluteUri ||
            ProjectsConfigBaseAddress.Scheme != Uri.UriSchemeHttps ||
            ProjectsConfigBaseAddress.UserInfo.Length > 0 ||
            ProjectsConfigBaseAddress.Query.Length > 0 ||
            ProjectsConfigBaseAddress.Fragment.Length > 0 ||
            !EventsBaseAddress.IsAbsoluteUri ||
            EventsBaseAddress.Scheme != Uri.UriSchemeHttps ||
            EventsBaseAddress.UserInfo.Length > 0 ||
            EventsBaseAddress.Query.Length > 0 ||
            EventsBaseAddress.Fragment.Length > 0 ||
            string.IsNullOrWhiteSpace(Audience) ||
            Audience.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(EventsAudience) ||
            EventsAudience.Any(char.IsControl) ||
            MaximumContextCandidates is < 1 or > 10_000 ||
            DefaultContextItems is < 1 or > 100 ||
            DefaultContextTokens is < 1 or > 16_000)
            throw new InvalidOperationException("Knowledge runtime configuration is invalid.");
        MemoryProvider.Validate();
    }

    private static Uri ReadHttpsUri(string? value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
            throw new InvalidOperationException($"'{name}' must be an absolute HTTPS URI without user info, query, or fragment.");
        return uri;
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}

internal sealed record KnowledgePostgresConnection(
    string ConnectionString,
    string TenantId,
    string ClientId,
    string TokenFilePath);

internal static class KnowledgePostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static KnowledgePostgresConnection ReadRuntimeConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "Knowledge", "Knowledge:Database:WorkloadIdentity");

    public static KnowledgePostgresConnection ReadMigrationConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "KnowledgeMigration", "Knowledge:Migration:WorkloadIdentity");

    public static TokenCredential CreateCredential(KnowledgePostgresConnection connection) =>
        new AzureIdentity::Azure.Identity.WorkloadIdentityCredential(
            new AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions
            {
                TenantId = connection.TenantId,
                ClientId = connection.ClientId,
                TokenFilePath = connection.TokenFilePath,
            });

    public static NpgsqlDataSource Create(
        string connectionString,
        TokenCredential credential,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Username))
            throw new InvalidOperationException("The Knowledge PostgreSQL Entra role is required.");
        if (!string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                "Knowledge PostgreSQL connections must use Entra token authentication, not a password.");
        builder.SslMode = SslMode.VerifyFull;

        var dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dataSource.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            async (_, cancellationToken) =>
                await GetPostgresTokenAsync(credential, cancellationToken, timeProvider).ConfigureAwait(false));
        return dataSource.Build();
    }

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
            throw new InvalidOperationException("Knowledge PostgreSQL Entra token acquisition returned an empty token.");
        if (token.ExpiresOn <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Knowledge PostgreSQL Entra token acquisition returned an expired token.");
        return token.Token;
    }

    private static KnowledgePostgresConnection ReadConnection(
        IConfiguration configuration,
        string connectionName,
        string identitySection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException($"Missing required configuration 'ConnectionStrings:{connectionName}'.");
        var identity = configuration.GetSection(identitySection)
            .Get<AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section '{identitySection}'.");
        if (!Guid.TryParse(identity.TenantId, out _) ||
            !Guid.TryParse(identity.ClientId, out _) ||
            !Path.IsPathFullyQualified(identity.TokenFilePath))
            throw new InvalidOperationException(
                $"'{identitySection}' requires GUID tenant/client IDs and an absolute token-file path.");
        return new KnowledgePostgresConnection(
            connectionString, identity.TenantId, identity.ClientId, identity.TokenFilePath);
    }
}
