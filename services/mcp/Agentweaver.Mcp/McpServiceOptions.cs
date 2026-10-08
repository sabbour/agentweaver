namespace Agentweaver.Mcp;

public sealed record McpServiceOptions(
    Uri IdentityIssuer,
    string IdentityAudience,
    Uri GatewayBaseAddress)
{
    internal static readonly TimeSpan GatewayRequestTimeout = TimeSpan.FromSeconds(10);

    public Uri ProtectedResourceMetadataUri
    {
        get
        {
            var resource = new Uri(IdentityAudience, UriKind.Absolute);
            var resourcePath = resource.AbsolutePath.TrimEnd('/');
            var metadataPath = "/.well-known/oauth-protected-resource" +
                (resourcePath.Length == 0 ? string.Empty : resourcePath);
            return new UriBuilder(resource)
            {
                Path = metadataPath,
                Query = string.Empty,
                Fragment = string.Empty,
            }.Uri;
        }
    }

    public string ProtectedResourceMetadataPath => ProtectedResourceMetadataUri.AbsolutePath;

    public static McpServiceOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var identity = configuration.GetSection("Identity");
        var gateway = configuration.GetSection("Gateway");

        return new McpServiceOptions(
            RequiredHttpsUri(identity["Issuer"], "Identity:Issuer", allowPath: true),
            RequiredHttpsUri(identity["Audience"], "Identity:Audience", allowPath: true).AbsoluteUri,
            RequiredHttpsUri(gateway["BaseAddress"], "Gateway:BaseAddress", allowPath: false));
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
                $"MCP configuration '{key}' must be an absolute HTTPS URI without credentials, query, or fragment" +
                (allowPath ? "." : " and must identify the service root."));
        return uri;
    }
}
