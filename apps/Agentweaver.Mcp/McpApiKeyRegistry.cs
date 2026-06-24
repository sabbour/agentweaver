namespace Agentweaver.Mcp;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Maps a bearer API key to the user accountable for MCP requests submitted with it.
/// Keys come from configuration (same structure as the API's ApiKeyRegistry) so the
/// same credentials work against both endpoints.
///
/// Configuration shape (either form is accepted):
///   Auth:ApiKey  / Auth:User           — single key
///   Auth:Keys:N:Token / Auth:Keys:N:User — list of keys
/// </summary>
public sealed class McpApiKeyRegistry
{
    private readonly Dictionary<string, string> _keyToUser;

    public McpApiKeyRegistry(IConfiguration configuration)
    {
        _keyToUser = new Dictionary<string, string>(StringComparer.Ordinal);

        var singleKey = configuration["Auth:ApiKey"];
        var singleUser = configuration["Auth:User"];
        if (!string.IsNullOrWhiteSpace(singleKey) && !string.IsNullOrWhiteSpace(singleUser))
            _keyToUser[singleKey] = singleUser;

        foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
        {
            var token = entry["Token"];
            var user = entry["User"];
            if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user))
                _keyToUser[token] = user;
        }
    }

    public bool TryResolveUser(string token, out string user) =>
        _keyToUser.TryGetValue(token, out user!);
}
