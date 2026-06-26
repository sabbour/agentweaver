namespace Agentweaver.Api.Security;

/// <summary>
/// Retained for backward compatibility. API key authentication has been replaced by GitHub OAuth
/// token authentication. This registry always reports no keys configured.
/// </summary>
public sealed class ApiKeyRegistry
{
    public ApiKeyRegistry(IConfiguration configuration) { }

    public bool TryResolveUser(string token, out string user)
    {
        user = string.Empty;
        return false;
    }

    public bool HasAnyKey => false;
}
