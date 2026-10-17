namespace Agentweaver.Environment;

/// <summary>Shared safeguards for HTTP clients used by the Environment service.</summary>
internal static class EnvironmentHttpTransport
{
    /// <summary>Accepts only absolute HTTPS base URIs without embedded credentials or request components.</summary>
    /// <param name="value">Configured URI text to validate.</param>
    /// <param name="name">Configuration name included in a rejection message.</param>
    /// <returns>The parsed trusted base URI.</returns>
    /// <exception cref="InvalidOperationException">The value is not an acceptable HTTPS base URI.</exception>
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

    /// <summary>Creates a handler that leaves redirect decisions to the calling client.</summary>
    /// <returns>An HTTP handler with automatic redirect following disabled.</returns>
    public static HttpClientHandler CreateRedirectDisabledHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    /// <summary>Reports whether an HTTP response has a redirect status code.</summary>
    public static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400;
}
