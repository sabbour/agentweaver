using Microsoft.Extensions.Configuration;

namespace Agentweaver.Knowledge;

public sealed record CosmosMemoryOptions(
    Uri Endpoint,
    string DatabaseId,
    string ContainerId,
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    public const int CurrentOptionsSchemaVersion = 1;
    public const string PartitionKeyPath = "/projectId";

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
