namespace Agentweaver.Environment;

internal static class EnvironmentHttpTransport
{
    public static Uri RequireTrustedHttpsBaseUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"'{name}' must be a trusted absolute HTTPS URI without user info, query, or fragment.");
        return uri;
    }

    public static HttpClientHandler CreateRedirectDisabledHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    public static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400;
}
