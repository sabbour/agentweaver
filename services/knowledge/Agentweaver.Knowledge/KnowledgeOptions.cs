extern alias AzureIdentity;

using Agentweaver.Abstractions;
using Azure.Core;
using Npgsql;

namespace Agentweaver.Knowledge;

/// <summary>Native PostgreSQL resource identity and schema version selected for Memory.</summary>
/// <param name="ResourceId">Stable label stored with Memory data; it must never change.</param>
/// <param name="DatabaseName">PostgreSQL database name, checked against the live connection during negotiation.</param>
/// <param name="ResourceGeneration">Positive resource generation; change it only after the store is wiped or replaced.</param>
/// <param name="Schema">PostgreSQL schema name; defaults to <c>knowledge</c> and cannot be a reserved schema.</param>
/// <param name="OptionsRevision">Provider configuration revision; defaults to <c>native-postgres-memory-v1</c>.</param>
/// <param name="OptionsSchemaVersion">Provider options contract version; defaults to and must equal version 1.</param>
public sealed record NativePostgresMemoryOptions(
    string ResourceId,
    string DatabaseName,
    long ResourceGeneration,
    string Schema,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    public const int CurrentOptionsSchemaVersion = 1;

    /// <summary>
    /// Reads the required native Memory settings. Defaults are schema <c>knowledge</c>,
    /// revision <c>native-postgres-memory-v1</c>, and options schema version 1.
    /// </summary>
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

    /// <summary>
    /// Requires resource and database tokens of at most 256 characters, a positive generation,
    /// a lowercase schema name of at most 63 characters that is not reserved, and a supported
    /// revision and options schema version.
    /// </summary>
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

/// <summary>Validated endpoints, audiences, storage settings, and context limits for the Knowledge service.</summary>
/// <param name="IdentityAuthority">HTTPS issuer URI used to validate service identity tokens.</param>
/// <param name="Audience">Nonempty audience expected on service identity tokens.</param>
/// <param name="ProjectsConfigBaseAddress">HTTPS base URI for the Projects Config service, without user info, query, or fragment.</param>
/// <param name="EventsBaseAddress">HTTPS base URI for the Events service, without user info, query, or fragment.</param>
/// <param name="EventsAudience">Nonempty audience sent when calling the Events service.</param>
/// <param name="MemoryProvider">Validated native PostgreSQL Memory resource and provider settings.</param>
/// <param name="MaximumContextCandidates">Maximum candidate records examined; defaults to 1000 and must be 1 through 10,000.</param>
/// <param name="DefaultContextItems">Default context item limit; defaults to 20 and must be 1 through 100.</param>
/// <param name="DefaultContextTokens">Default context token limit; defaults to 4000 and must be 1 through 16,000.</param>
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
    /// <summary>Reads and validates the complete runtime configuration before service registration.</summary>
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

    /// <summary>
    /// Requires HTTPS endpoints, nonempty control-free audiences, context limits within
    /// their documented ranges, and valid native Memory provider options.
    /// </summary>
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

/// <summary>Connection string and workload-identity inputs used to authenticate to PostgreSQL.</summary>
internal sealed record KnowledgePostgresConnection(
    string ConnectionString,
    string TenantId,
    string ClientId,
    string TokenFilePath);

/// <summary>Creates password-provider-backed PostgreSQL data sources using workload identity access tokens.</summary>
internal static class KnowledgePostgresDataSource
{
    private const string TokenScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Reads the service's runtime connection and workload identity settings.</summary>
    public static KnowledgePostgresConnection ReadRuntimeConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "Knowledge", "Knowledge:Database:WorkloadIdentity");

    /// <summary>Reads the separately scoped migration connection and workload identity settings.</summary>
    public static KnowledgePostgresConnection ReadMigrationConnection(IConfiguration configuration) =>
        ReadConnection(configuration, "KnowledgeMigration", "Knowledge:Migration:WorkloadIdentity");

    /// <summary>Creates an Entra workload-identity credential from the selected identity file and IDs.</summary>
    public static TokenCredential CreateCredential(KnowledgePostgresConnection connection) =>
        new AzureIdentity::Azure.Identity.WorkloadIdentityCredential(
            new AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions
            {
                TenantId = connection.TenantId,
                ClientId = connection.ClientId,
                TokenFilePath = connection.TokenFilePath,
            });

    /// <summary>
    /// Builds a full-verification TLS data source and disables synchronous token acquisition.
    /// The connection-string username must be the PostgreSQL Entra role whose name matches
    /// the Knowledge managed identity; password authentication is not accepted.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string with the matching Entra role as its username and no password.</param>
    /// <param name="credential">Workload-identity credential used to acquire PostgreSQL audience tokens.</param>
    /// <param name="timeProvider">Optional clock used to check token expiry.</param>
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

    /// <summary>Requests a fresh PostgreSQL audience token and rejects empty or already-expired results.</summary>
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
