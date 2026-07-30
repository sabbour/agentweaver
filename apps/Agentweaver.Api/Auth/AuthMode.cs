namespace Agentweaver.Api.Auth;

public enum AuthMode
{
    Entra,
    GitHubLegacy,
}

public static class AuthModeResolver
{
    public static AuthMode Resolve(IConfiguration configuration) =>
        Parse(configuration["Auth:Mode"]);

    public static AuthMode Parse(string? raw)
    {
        if (string.Equals(raw, "GitHubLegacy", StringComparison.OrdinalIgnoreCase))
            return AuthMode.GitHubLegacy;

        return AuthMode.Entra;
    }

    public static string Normalize(AuthMode authMode) =>
        authMode.ToString().ToLowerInvariant();
}
