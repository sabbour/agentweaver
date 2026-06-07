namespace Scaffolder.Cli;

/// <summary>Reads client configuration from environment variables.</summary>
public sealed class CliConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }

    public static CliConfig Load()
    {
        var url = Environment.GetEnvironmentVariable("SCAFFOLDER_API_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "http://localhost:5000";
        }

        var key = Environment.GetEnvironmentVariable("SCAFFOLDER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ConfigException(
                "SCAFFOLDER_API_KEY is not set. Set the environment variable to your API key.");
        }

        return new CliConfig
        {
            ApiUrl = url.TrimEnd('/'),
            ApiKey = key
        };
    }
}

/// <summary>Raised when required configuration is missing.</summary>
public sealed class ConfigException(string message) : Exception(message);
