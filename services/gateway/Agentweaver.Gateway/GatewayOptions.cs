namespace Agentweaver.Gateway;

public sealed record GatewayOptions(
    Uri IdentityIssuer,
    string IdentityAudience,
    Uri ProjectsOwnerBaseAddress,
    Uri OrchestratorOwnerBaseAddress,
    Uri KnowledgeOwnerBaseAddress,
    Uri EventsOwnerBaseAddress,
    Uri IdentityBrokerAddress,
    TimeSpan OwnerRequestTimeout)
{
    public const int EventPollIntervalMilliseconds = 1000;

    public Uri GetOwnerBaseAddress(GatewayOwner owner) => owner switch
    {
        GatewayOwner.Projects => ProjectsOwnerBaseAddress,
        GatewayOwner.Orchestrator => OrchestratorOwnerBaseAddress,
        GatewayOwner.Knowledge => KnowledgeOwnerBaseAddress,
        GatewayOwner.Events => EventsOwnerBaseAddress,
        GatewayOwner.IdentityBroker => IdentityBrokerAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(owner)),
    };

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
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string RequiredHttpsAudience(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Gateway configuration 'Identity:Audience' must be an absolute HTTPS audience.");
        _ = RequiredHttpsUri(value, "Identity:Audience", allowPath: true);
        return value;
    }

    private static Uri RequiredHttpsRoot(string? value, string key)
    {
        var uri = RequiredHttpsUri(value, key, allowPath: false);
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

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
