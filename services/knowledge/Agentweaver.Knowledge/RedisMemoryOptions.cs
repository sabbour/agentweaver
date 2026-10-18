using StackExchange.Redis;

namespace Agentweaver.Knowledge;

/// <summary>Validated connection and immutable resource identity for the Redis Memory namespace.</summary>
/// <param name="Endpoint">TLS Redis URI using <c>rediss</c> with an explicit port and no path, credentials, query, or fragment.</param>
/// <param name="ResourceId">Stable label for this Memory store; it must never change.</param>
/// <param name="ResourceGeneration">Positive resource generation; change it only after the store is wiped or replaced.</param>
/// <param name="OptionsRevision">Provider configuration revision used to match the catalog entry.</param>
/// <param name="OptionsSchemaVersion">Options contract version; defaults to and must equal version 1.</param>
/// <param name="Database">Redis logical database index, from 0 through 15; defaults to 0.</param>
/// <param name="KeyPrefix">Key namespace prefix, at most 128 allowed characters; defaults to <c>agentweaver:knowledge</c>.</param>
/// <param name="UserName">Optional Redis ACL username; it must be supplied together with a password.</param>
/// <param name="Password">Optional Redis ACL password; it must be supplied together with a username and is omitted from diagnostics.</param>
public sealed record RedisMemoryOptions(
    Uri Endpoint,
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion,
    int Database,
    string KeyPrefix,
    string? UserName,
    string? Password)
{
    /// <summary>The only options contract version supported by this adapter.</summary>
    public const int CurrentOptionsSchemaVersion = 1;

    /// <summary>Reads the optional Redis provider section and validates it when present.</summary>
    /// <returns><see langword="null"/> when Redis is not configured; otherwise the validated options.</returns>
    public static RedisMemoryOptions? ReadOptional(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Knowledge:RedisProvider");
        if (!section.Exists())
            return null;

        var endpoint = Required(section["Endpoint"], "Knowledge:RedisProvider:Endpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException(
                "'Knowledge:RedisProvider:Endpoint' must be an absolute rediss URI with an explicit port.");

        var options = new RedisMemoryOptions(
            endpointUri,
            Required(section["ResourceId"], "Knowledge:RedisProvider:ResourceId"),
            section.GetValue<long>("ResourceGeneration"),
            Required(section["OptionsRevision"], "Knowledge:RedisProvider:OptionsRevision"),
            section.GetValue("OptionsSchemaVersion", CurrentOptionsSchemaVersion),
            section.GetValue("Database", 0),
            section["KeyPrefix"] ?? "agentweaver:knowledge",
            section["UserName"],
            section["Password"]);
        options.Validate();
        return options;
    }

    /// <summary>
    /// Builds TLS connection settings with administrative commands enabled for provider
    /// compatibility checks and bounded connect and command timeouts.
    /// </summary>
    /// <exception cref="ArgumentException">The options do not satisfy the Redis provider contract.</exception>
    public ConfigurationOptions CreateConnectionOptions()
    {
        Validate();
        var connection = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            AllowAdmin = true,
            ConnectRetry = 1,
            ConnectTimeout = 5_000,
            DefaultDatabase = Database,
            Password = Password,
            Ssl = true,
            SslHost = Endpoint.Host,
            SyncTimeout = 5_000,
            User = UserName
        };
        connection.EndPoints.Add(Endpoint.Host, Endpoint.Port);
        return connection;
    }

    /// <summary>
    /// Requires a TLS URI with an explicit port, a positive generation, the supported options
    /// schema, a database from 0 through 15, a valid key prefix, and either both ACL credentials
    /// or neither. User names and passwords are bounded and cannot contain control characters.
    /// </summary>
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            !string.Equals(Endpoint.Scheme, "rediss", StringComparison.OrdinalIgnoreCase) ||
            Endpoint.Port is < 1 or > 65_535 ||
            Endpoint.UserInfo.Length > 0 ||
            Endpoint.Query.Length > 0 ||
            Endpoint.Fragment.Length > 0 ||
            Endpoint.AbsolutePath != "/" ||
            !IsToken(ResourceId, 256) ||
            ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion ||
            Database is < 0 or > 15 ||
            !IsKeyPrefix(KeyPrefix) ||
            (UserName is not null && !IsToken(UserName, 256)) ||
            (Password is not null && (Password.Length is < 1 or > 4096 || Password.Any(char.IsControl))) ||
            ((UserName is null) != (Password is null)))
            throw new ArgumentException("Redis Memory options are invalid.");
    }

    /// <summary>Formats non-secret connection identity; the configured password is never included.</summary>
    public override string ToString() =>
        $"RedisMemoryOptions {{ Endpoint = {Endpoint.Host}:{Endpoint.Port}, Database = {Database}, " +
        $"KeyPrefix = {KeyPrefix}, ResourceId = {ResourceId}, ResourceGeneration = {ResourceGeneration}, " +
        $"OptionsRevision = {OptionsRevision}, OptionsSchemaVersion = {OptionsSchemaVersion} }}";

    private static bool IsKeyPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
