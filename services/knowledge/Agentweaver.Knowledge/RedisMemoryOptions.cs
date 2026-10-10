using StackExchange.Redis;

namespace Agentweaver.Knowledge;

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
    public const int CurrentOptionsSchemaVersion = 1;

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
