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

    /// <summary>
    /// Selects the Kubernetes API server address: the explicit setting, else the kubelet-injected service
    /// host and port (the in-cluster client rule, which needs no DNS lookup), else the cluster service name.
    /// </summary>
    /// <param name="configuration">Configuration that includes process environment variables.</param>
    /// <returns>The validated HTTPS base URI of the Kubernetes API server.</returns>
    /// <exception cref="InvalidOperationException">The selected address is not an acceptable HTTPS base URI.</exception>
    public static Uri ResolveKubernetesApiServer(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["Kubernetes:ApiServer"];
        if (!string.IsNullOrWhiteSpace(configured))
            return RequireTrustedHttpsBaseUri(configured, "Kubernetes:ApiServer");

        var host = configuration["KUBERNETES_SERVICE_HOST"];
        var port = configuration["KUBERNETES_SERVICE_PORT"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
            return RequireTrustedHttpsBaseUri("https://kubernetes.default.svc/", "Kubernetes:ApiServer");
        if (!int.TryParse(port, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var portNumber) ||
            portNumber is < 1 or > 65535)
            throw new InvalidOperationException("'KUBERNETES_SERVICE_PORT' must be a TCP port number.");
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return RequireTrustedHttpsBaseUri($"https://{authority}:{portNumber}/", "KUBERNETES_SERVICE_HOST");
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
