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

    /// <summary>
    /// Wire value consumed by the web app's <c>AuthMode</c> union type
    /// (<c>'entra' | 'github-legacy'</c>). Distinct from <see cref="Normalize"/>, which is the
    /// storage/one-time-code encoding.
    /// </summary>
    public static string ToWireValue(AuthMode authMode) =>
        authMode == AuthMode.GitHubLegacy ? "github-legacy" : "entra";

    /// <summary>Human-readable label matching the web app's AUTH_MODE_LABELS map.</summary>
    public static string ToLabel(AuthMode authMode) =>
        authMode == AuthMode.GitHubLegacy ? "GitHub" : "Entra ID";

    /// <summary>Entra is the recommended (non-legacy) sign-in mode.</summary>
    public static bool IsRecommended(AuthMode authMode) =>
        authMode == AuthMode.Entra;
}
