namespace Agentweaver.Mcp;

public sealed record McpOAuthConfiguration(
    Uri Issuer,
    Uri Resource,
    Uri ResourceMetadata)
{
    public const string RequiredScope = "mcp:invoke";

    public static McpOAuthConfiguration Resolve(
        IConfiguration configuration,
        IHostEnvironment environment,
        string apiUrl)
    {
        var configured = configuration["Auth:OAuth:PublicOrigin"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Auth:OAuth:PublicOrigin is required outside Development.");
            configured = apiUrl;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var candidate)
            || !candidate.IsWellFormedOriginalString()
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || candidate.AbsolutePath != "/"
            || (!environment.IsDevelopment() && candidate.Scheme != Uri.UriSchemeHttps)
            || (environment.IsDevelopment()
                && candidate.Scheme != Uri.UriSchemeHttps
                && !(candidate.Scheme == Uri.UriSchemeHttp && candidate.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Auth:OAuth:PublicOrigin must be an HTTPS origin with no path, query, fragment, " +
                "or userinfo (HTTP loopback is allowed only in Development).");
        }

        var issuer = new Uri(candidate.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return new(
            issuer,
            new Uri(issuer, "/mcp"),
            new Uri(issuer, "/.well-known/oauth-protected-resource/mcp"));
    }
}
