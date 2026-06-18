namespace Agentweaver.Api.Security;

/// <summary>
/// Maps a bearer API key to the user accountable for runs submitted with it
/// (FR-024). Keys come from configuration so the same build runs locally and in
/// the cloud (Principle VI). This is the enforcement point for authentication;
/// every request that reaches a run endpoint carries a resolved identity.
/// </summary>
public sealed class ApiKeyRegistry
{
    private readonly Dictionary<string, string> _keyToUser;

    public ApiKeyRegistry(IConfiguration configuration)
    {
        _keyToUser = new Dictionary<string, string>(StringComparer.Ordinal);

        // Supports either a single key (Auth:ApiKey + Auth:User) or a list under
        // Auth:Keys with Token and User entries.
        var singleKey = configuration["Auth:ApiKey"];
        var singleUser = configuration["Auth:User"];
        if (!string.IsNullOrWhiteSpace(singleKey) && !string.IsNullOrWhiteSpace(singleUser))
        {
            _keyToUser[singleKey] = singleUser;
        }

        foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
        {
            var token = entry["Token"];
            var user = entry["User"];
            if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user))
            {
                _keyToUser[token] = user;
            }
        }
    }

    public bool TryResolveUser(string token, out string user) => _keyToUser.TryGetValue(token, out user!);

    public bool HasAnyKey => _keyToUser.Count > 0;
}
