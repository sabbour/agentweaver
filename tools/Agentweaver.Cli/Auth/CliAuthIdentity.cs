using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli.Auth;

internal sealed record CliAuthIdentity(
    CliIdentity Identity,
    string Issuer,
    string GatewayOrigin,
    string Account)
{
    private static readonly string[] RequiredScopes = ["openid", "api.read", "offline_access"];
    private static readonly HashSet<string> SupportedScopes = new(
        ["openid", "api.read", "offline_access", "projects.admin", "projects.orchestrator"],
        StringComparer.Ordinal);
    private static readonly Regex BindingSelector = new(
        "^[A-Za-z0-9._:-]{1,256}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private const int MaximumScopeLength = 128;
    private const int MaximumScopes = 32;

    public static CliAuthIdentity Create(CliIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.Target);

        var target = identity.Target;
        ValidateHttpsRoot(target.Issuer, "Identity Broker issuer");
        ValidateHttpsRoot(target.Gateway, "Gateway URL");
        ValidateText(target.ClientId, "OAuth client ID", 256);
        ValidateText(target.Audience, "API audience", 512);
        ValidateText(target.TenantId, "Tenant selector", 256);

        if (identity.Scopes is null || identity.Scopes.Count is < 3 or > MaximumScopes)
            throw new ArgumentException("Requested CLI scopes are invalid.", nameof(identity));
        var scopes = identity.Scopes.ToArray();
        if (scopes.Any(scope => !IsScopeToken(scope)) ||
            scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length ||
            RequiredScopes.Any(scope => !scopes.Contains(scope, StringComparer.Ordinal)) ||
            scopes.Any(scope => !SupportedScopes.Contains(scope)))
            throw new ArgumentException("Requested CLI scopes are invalid or unsupported.", nameof(identity));

        var project = ValidateBindingSelector(identity.ProjectId, "Project selector");
        var run = ValidateBindingSelector(identity.RunId, "Run selector");
        if ((project is null) != (run is null))
            throw new ArgumentException("Project and run selectors must be supplied together.", nameof(identity));

        var issuer = target.Issuer.AbsoluteUri;
        var hasAdmin = scopes.Contains("projects.admin", StringComparer.Ordinal);
        var hasOrchestrator = scopes.Contains("projects.orchestrator", StringComparer.Ordinal);
        if (hasAdmin && hasOrchestrator ||
            project is not null && (!hasOrchestrator || hasAdmin) ||
            project is null && hasOrchestrator)
            throw new ArgumentException("Project scopes do not match the selected project and run.", nameof(identity));

        var normalized = new CliIdentity(target, scopes, project, run);
        var gatewayOrigin = target.Gateway.GetLeftPart(UriPartial.Authority);
        var canonicalScopes = scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToArray();
        var keyMaterial = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            issuer,
            target.ClientId,
            target.Audience,
            gatewayOrigin,
            target.TenantId,
            string.Join(" ", canonicalScopes),
            project,
            run,
        });
        var account = "agentweaver-v1-" +
            Convert.ToHexString(SHA256.HashData(keyMaterial)).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(keyMaterial);

        return new CliAuthIdentity(
            normalized,
            issuer,
            gatewayOrigin,
            account);
    }

    private static void ValidateHttpsRoot(Uri? value, string name)
    {
        if (value is null || !value.IsAbsoluteUri ||
            !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(value.Host) ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) ||
            !string.Equals(value.AbsolutePath, "/", StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be an absolute HTTPS origin without credentials, path, query, or fragment.");
    }

    private static void ValidateText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} is invalid.");
    }

    private static bool IsScopeToken(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Length > MaximumScopeLength) return false;
        return scope.All(character =>
            character is >= (char)0x21 and <= (char)0x7e &&
            character is not ('"' or '\\'));
    }

    private static string? ValidateBindingSelector(string? value, string name)
    {
        if (value is null) return null;
        if (!BindingSelector.IsMatch(value))
            throw new ArgumentException($"{name} is invalid.");
        return value;
    }
}
