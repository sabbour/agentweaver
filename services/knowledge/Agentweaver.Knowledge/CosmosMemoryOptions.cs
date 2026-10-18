using Microsoft.Extensions.Configuration;

namespace Agentweaver.Knowledge;

/// <summary>Validated identity and version information for the configured Cosmos Memory container.</summary>
/// <param name="Endpoint">Root HTTPS endpoint of the Cosmos account, without user info, query, or fragment.</param>
/// <param name="DatabaseId">Cosmos database name using letters, digits, hyphens, or underscores.</param>
/// <param name="ContainerId">Cosmos container name; the provider also checks its partition key, index, and retention policy.</param>
/// <param name="ResourceId">Stable label for this Memory store; it must never change.</param>
/// <param name="ResourceGeneration">Positive resource generation; change it only after the store is wiped or replaced.</param>
/// <param name="OptionsRevision">Provider configuration revision used to match the catalog entry.</param>
/// <param name="OptionsSchemaVersion">Options contract version; defaults to and must equal version 1.</param>
public sealed record CosmosMemoryOptions(
    Uri Endpoint,
    string DatabaseId,
    string ContainerId,
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    /// <summary>The only options contract version supported by this adapter.</summary>
    public const int CurrentOptionsSchemaVersion = 1;
    /// <summary>Required container partition key for project-scoped Memory documents.</summary>
    public const string PartitionKeyPath = "/projectId";

    /// <summary>Reads the optional Cosmos provider section and validates it when present.</summary>
    /// <returns><see langword="null"/> when Cosmos is not configured; otherwise the validated options.</returns>
    /// <exception cref="InvalidOperationException">A required setting is absent or the endpoint is malformed.</exception>
    public static CosmosMemoryOptions? ReadOptional(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Knowledge:CosmosProvider");
        if (!section.Exists())
            return null;

        var endpoint = Required(section["Endpoint"], "Knowledge:CosmosProvider:Endpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException(
                "'Knowledge:CosmosProvider:Endpoint' must be an absolute HTTPS URI.");

        var options = new CosmosMemoryOptions(
            endpointUri,
            Required(section["DatabaseId"], "Knowledge:CosmosProvider:DatabaseId"),
            Required(section["ContainerId"], "Knowledge:CosmosProvider:ContainerId"),
            Required(section["ResourceId"], "Knowledge:CosmosProvider:ResourceId"),
            section.GetValue<long>("ResourceGeneration"),
            Required(section["OptionsRevision"], "Knowledge:CosmosProvider:OptionsRevision"),
            section.GetValue("OptionsSchemaVersion", CurrentOptionsSchemaVersion));
        options.Validate();
        return options;
    }

    /// <summary>
    /// Requires a root HTTPS endpoint, database and container names of at most 255 characters,
    /// a resource token of at most 256 characters, a positive generation, a revision of at most
    /// 128 characters, and the supported options schema version.
    /// </summary>
    /// <exception cref="ArgumentException">Any configured value violates the provider contract.</exception>
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            Endpoint.Scheme != Uri.UriSchemeHttps ||
            Endpoint.UserInfo.Length > 0 ||
            Endpoint.Query.Length > 0 ||
            Endpoint.Fragment.Length > 0 ||
            Endpoint.AbsolutePath != "/" ||
            !IsResourceName(DatabaseId) ||
            !IsResourceName(ContainerId) ||
            !IsToken(ResourceId, 256) ||
            ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Cosmos Memory options are invalid.");
    }

    private static bool IsResourceName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 255 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
