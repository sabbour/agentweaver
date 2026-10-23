namespace Agentweaver.Gateway;

/// <summary>Contains the fixed HTTPS owner addresses, token validation identity, and proxy timeout.</summary>
/// <remarks>The Environment and AgentHost owners are optional. Their routes return 503 <c>owner_not_configured</c> when absent.</remarks>
public sealed record GatewayOptions(
    Uri IdentityIssuer,
    string IdentityAudience,
    Uri ProjectsOwnerBaseAddress,
    Uri OrchestratorOwnerBaseAddress,
    Uri KnowledgeOwnerBaseAddress,
    Uri EventsOwnerBaseAddress,
    Uri IdentityBrokerAddress,
    Uri WebOrigin,
    TimeSpan OwnerRequestTimeout,
    Uri? EnvironmentOwnerBaseAddress = null,
    Uri? AgentHostOwnerBaseAddress = null)
{
    public const int EventPollIntervalMilliseconds = 1000;

    /// <summary>Returns the configured root URI for one known service owner.</summary>
    public Uri GetOwnerBaseAddress(GatewayOwner owner) => owner switch
    {
        GatewayOwner.Projects => ProjectsOwnerBaseAddress,
        GatewayOwner.Orchestrator => OrchestratorOwnerBaseAddress,
        GatewayOwner.Knowledge => KnowledgeOwnerBaseAddress,
        GatewayOwner.Events => EventsOwnerBaseAddress,
        GatewayOwner.IdentityBroker => IdentityBrokerAddress,
        GatewayOwner.Environment => EnvironmentOwnerBaseAddress
            ?? throw new InvalidOperationException("Gateway:Owners:Environment is not configured."),
        GatewayOwner.AgentHost => AgentHostOwnerBaseAddress
            ?? throw new InvalidOperationException("Gateway:Owners:AgentHost is not configured."),
        _ => throw new ArgumentOutOfRangeException(nameof(owner)),
    };

    /// <summary>Reads required HTTPS identities and owner roots, rejecting unsafe URI forms and timeout values.</summary>
    public static GatewayOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var identity = configuration.GetSection("Identity");
        var owners = configuration.GetSection("Gateway:Owners");
        var timeoutSeconds = configuration.GetValue("Gateway:OwnerRequestTimeoutSeconds", 15);
        if (timeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException(
                "Gateway:OwnerRequestTimeoutSeconds must be between 1 and 120.");

        return new GatewayOptions(
            RequiredHttpsUri(identity["Issuer"], "Identity:Issuer", allowPath: true),
            RequiredHttpsAudience(identity["Audience"]),
            RequiredHttpsRoot(owners["Projects"], "Gateway:Owners:Projects"),
            RequiredHttpsRoot(owners["Orchestrator"], "Gateway:Owners:Orchestrator"),
            RequiredHttpsRoot(owners["Knowledge"], "Gateway:Owners:Knowledge"),
            RequiredHttpsRoot(owners["Events"], "Gateway:Owners:Events"),
            RequiredHttpsRoot(owners["IdentityBrokerAddress"], "Gateway:Owners:IdentityBrokerAddress"),
            RequiredHttpsRoot(configuration["Gateway:WebOrigin"], "Gateway:WebOrigin"),
            TimeSpan.FromSeconds(timeoutSeconds),
            // Environment is optional so existing deployments start without it. When it is set, it gets
            // the same HTTPS root validation as the required owners.
            owners["Environment"] is null
                ? null
                : RequiredHttpsRoot(owners["Environment"], "Gateway:Owners:Environment"),
            owners["AgentHost"] is null
                ? null
                : RequiredHttpsRoot(owners["AgentHost"], "Gateway:Owners:AgentHost"));
    }

    /// <summary>Requires an HTTPS audience without credentials, query, or fragment.</summary>
    private static string RequiredHttpsAudience(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Gateway configuration 'Identity:Audience' must be an absolute HTTPS audience.");
        _ = RequiredHttpsUri(value, "Identity:Audience", allowPath: true);
        return value;
    }

    /// <summary>Requires a root-only HTTPS owner address and normalizes it to one trailing slash.</summary>
    private static Uri RequiredHttpsRoot(string? value, string key)
    {
        var uri = RequiredHttpsUri(value, key, allowPath: false);
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <summary>Parses an HTTPS URI and rejects credentials and query/fragment components.</summary>
    private static Uri RequiredHttpsUri(string? value, string key, bool allowPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!allowPath && uri.AbsolutePath != "/"))
            throw new InvalidOperationException(
                $"Gateway configuration '{key}' must be an absolute HTTPS URI without credentials, query, or fragment" +
                (allowPath ? "." : " and must identify the service root."));
        return uri;
    }
}
