namespace Agentweaver.Cli;

internal static class CliConfiguration
{
    private static readonly string[] BaseScopes = ["openid", "api.read", "offline_access"];
    internal static readonly string[] TargetOptionNames =
    [
        "gateway-url", "broker-issuer", "client-id", "api-audience",
        "tenant-id", "project-id", "run-id",
    ];
    private static readonly HashSet<string> OptionalScopes =
        new(StringComparer.Ordinal) { "projects.admin", "projects.orchestrator" };

    public static CliIdentity ReadIdentity(
        IReadOnlyList<string>? requestedScopes = null,
        bool discoveryOnly = false,
        CliOptions? options = null)
    {
        var gatewayOverride = options?.Single("gateway-url");
        var issuerOverride = options?.Single("broker-issuer");
        var clientOverride = options?.Single("client-id");
        var audienceOverride = options?.Single("api-audience");
        var tenantOverride = options?.Single("tenant-id");
        var projectOverride = options?.Single("project-id");
        var runOverride = options?.Single("run-id");
        var gateway = ReadUri("AGENTWEAVER_GATEWAY_URL", gatewayOverride, originRoot: true);
        var issuer = discoveryOnly
            ? gateway
            : ReadUri("AGENTWEAVER_BROKER_ISSUER", issuerOverride, originRoot: false);
        var audience = discoveryOnly
            ? gateway.GetLeftPart(UriPartial.Authority)
            : Required("AGENTWEAVER_API_AUDIENCE", audienceOverride);
        var tenant = discoveryOnly ? "discovery-only" : Required("AGENTWEAVER_TENANT_ID", tenantOverride);
        var clientId = discoveryOnly ? null : Optional("AGENTWEAVER_CLIENT_ID", clientOverride);
        if (string.IsNullOrWhiteSpace(clientId))
            clientId = "agentweaver-cli";

        var project = discoveryOnly ? null : Optional("AGENTWEAVER_PROJECT_ID", projectOverride);
        var run = discoveryOnly ? null : Optional("AGENTWEAVER_RUN_ID", runOverride);
        if ((project is null) != (run is null))
            throw new InvalidOperationException("AGENTWEAVER_PROJECT_ID and AGENTWEAVER_RUN_ID must be supplied together.");

        var scopes = BaseScopes.Concat(discoveryOnly ? [] : requestedScopes ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var scope in scopes)
        {
            if (!BaseScopes.Contains(scope, StringComparer.Ordinal) && !OptionalScopes.Contains(scope))
                throw new InvalidOperationException($"The requested OAuth scope '{scope}' is not supported by this CLI.");
        }

        var identity = new CliIdentity(
            new CliTarget(gateway, issuer, clientId, audience, tenant),
            scopes,
            project,
            run);
        return ValidateIdentity(identity);
    }

    internal static CliIdentity ValidateIdentity(CliIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.Target);
        if (identity.Target.Gateway is null ||
            identity.Target.Issuer is null ||
            string.IsNullOrWhiteSpace(identity.Target.ClientId) ||
            string.IsNullOrWhiteSpace(identity.Target.Audience) ||
            string.IsNullOrWhiteSpace(identity.Target.TenantId) ||
            identity.Scopes is null ||
            identity.Scopes.Count < BaseScopes.Length ||
            BaseScopes.Any(scope => !identity.Scopes.Contains(scope, StringComparer.Ordinal)) ||
            identity.Scopes.Any(scope =>
                string.IsNullOrWhiteSpace(scope) ||
                !BaseScopes.Contains(scope, StringComparer.Ordinal) &&
                !OptionalScopes.Contains(scope)) ||
            identity.Scopes.Distinct(StringComparer.Ordinal).Count() != identity.Scopes.Count ||
            (identity.ProjectId is null) != (identity.RunId is null) ||
            identity.ProjectId is not null && string.IsNullOrWhiteSpace(identity.ProjectId) ||
            identity.RunId is not null && string.IsNullOrWhiteSpace(identity.RunId) ||
            identity.ProjectId is not null &&
            !identity.Scopes.Contains("projects.orchestrator", StringComparer.Ordinal) ||
            identity.ProjectId is not null &&
            identity.Scopes.Contains("projects.admin", StringComparer.Ordinal))
        {
            throw new ArgumentException("The selected CLI target, tenant, scopes, or project/run binding is invalid.");
        }
        _ = ValidateAuthority(identity.Target.Issuer, "Broker issuer");
        return identity;
    }

    private static Uri ReadUri(string name, string? overrideValue, bool originRoot) =>
        ParseUri(name, Required(name, overrideValue), originRoot);

    private static Uri ParseUri(string name, string raw, bool originRoot)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var value))
            throw new InvalidOperationException($"{name} must be a valid absolute URL.");
        try
        {
            ValidateAuthority(value, name, originRoot);
        }
        catch (ArgumentException)
        {
            var detail = originRoot
                ? "an HTTPS origin root (HTTP is allowed only for loopback fixtures)"
                : "an HTTPS issuer (HTTP is allowed only for loopback fixtures)";
            throw new InvalidOperationException($"{name} must be {detail}.");
        }
        return value;
    }

    private static Uri ValidateAuthority(Uri value, string name, bool originRoot = false)
    {
        if (!value.IsAbsoluteUri ||
            value.UserInfo.Length > 0 ||
            value.Query.Length > 0 ||
            value.Fragment.Length > 0 ||
            !(value.Scheme == Uri.UriSchemeHttps ||
              value.Scheme == Uri.UriSchemeHttp && value.IsLoopback) ||
            originRoot && value.AbsolutePath != "/")
        {
            throw new ArgumentException($"{name} is invalid.");
        }
        return value;
    }

    private static string Required(string name, string? overrideValue = null)
    {
        var value = overrideValue ?? Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Set {name} to select the Agentweaver target.")
            : value.Trim();
    }

    private static string? Optional(string name, string? overrideValue = null)
    {
        var value = overrideValue ?? Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
