namespace Agentweaver.Api.Security;

/// <summary>
/// Production fail-fast guard for the OAuth issuer/audience pinning (Seraph T4–T7 review, Fix 1).
///
/// The MCP Resource Server forwards an AS-minted JWT whose <c>aud</c> is the PUBLIC host
/// (<c>https://&lt;HOST&gt;/mcp</c>). That JWT is then validated by this API on an INTERNAL call that
/// arrives at <c>http://agentweaver-api:8080</c>. If issuer/audience were derived from the request
/// host they would resolve to the internal address and JWT validation would always fail (fail-closed
/// 401), silently breaking per-user identity (T7). Therefore <c>Auth:OAuth:Issuer</c> and
/// <c>Auth:OAuth:Audience</c> MUST be pinned to explicit public values in Production, and validation
/// must use those pinned values (see <c>OAuthServerConfig.ResolveIssuer/ResolveAudience</c>, which
/// prefer config over the host-derived fallback).
///
/// This guard refuses to start the process when either value is missing in a Production environment,
/// surfacing the misconfiguration loudly at boot rather than as broken token validation at runtime.
/// Host-derived fallback remains allowed in Development for local convenience.
/// </summary>
public static class OAuthConfigGuard
{
    private static readonly string[] RequiredProductionKeys =
    [
        "Auth:OAuth:Issuer",
        "Auth:OAuth:Audience",
    ];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the environment is Production and any
    /// required pinned OAuth config value is unset/empty. No-op in every other environment.
    /// </summary>
    public static void EnsureProductionIssuerAudiencePinned(
        IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsProduction())
            return;

        var missing = RequiredProductionKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();

        if (missing.Length == 0)
            return;

        throw new InvalidOperationException(
            "Refusing to start: the following OAuth configuration value(s) must be pinned to the " +
            $"public host in Production but are unset/empty: {string.Join(", ", missing)}. " +
            "Set Auth:OAuth:Issuer = https://<HOST> and Auth:OAuth:Audience = https://<HOST>/mcp " +
            "(via env var / ConfigMap / Secret). Host-derived issuer/audience is permitted only in " +
            "Development; in Production it would break MCP->API JWT validation (audience mismatch).");
    }
}
