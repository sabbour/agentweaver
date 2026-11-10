using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Identity.Broker;

public static class IdentityBrokerInternalTlsConfiguration
{
    private const string HttpsEndpoint = "Kestrel:Endpoints:Https";
    private const string InternalServiceHost = "identity-broker.agentweaver.svc.cluster.local";
    private const string InternalCertificatePath = "/var/run/identity-broker-internal-tls/tls.crt";
    private const string InternalCertificateKeyPath = "/var/run/identity-broker-internal-tls/tls.key";

    public static bool Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var endpointUrl = builder.Configuration[$"{HttpsEndpoint}:Url"];
        if (!builder.Environment.IsProduction() ||
            endpointUrl is null ||
            !endpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        var defaultCertificatePath = builder.Configuration["Kestrel:Certificates:Default:Path"];
        var defaultCertificateKeyPath = builder.Configuration["Kestrel:Certificates:Default:KeyPath"];
        if (string.IsNullOrWhiteSpace(defaultCertificatePath) ||
            string.IsNullOrWhiteSpace(defaultCertificateKeyPath))
            throw new InvalidOperationException("The Broker HTTPS endpoint requires default certificate paths.");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{HttpsEndpoint}:Sni:{InternalServiceHost}:Certificate:Path"] = InternalCertificatePath,
            [$"{HttpsEndpoint}:Sni:{InternalServiceHost}:Certificate:KeyPath"] = InternalCertificateKeyPath,
            [$"{HttpsEndpoint}:Sni:*:Certificate:Path"] = defaultCertificatePath,
            [$"{HttpsEndpoint}:Sni:*:Certificate:KeyPath"] = defaultCertificateKeyPath,
        });
        return true;
    }
}
