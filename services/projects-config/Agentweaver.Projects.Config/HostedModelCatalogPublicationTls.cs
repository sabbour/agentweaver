using System.Net.Security;
using System.Security.Authentication;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Projects.Config;

/// <summary>Authenticates AgentHost's catalog publication channel with its pinned workload certificate.</summary>
internal sealed class HostedModelCatalogPublicationTls
{
    public const string SectionName = "ProjectsConfig:HostedModelCatalogPublication";
    private const string CertificatePinKey = "AgentHostCertificateSha256";
    private const string BackendCaFile = "/var/run/service-backend-ca/ca.crt";
    private readonly string _agentHostCertificatePin;
    private readonly TimeProvider _timeProvider;

    internal HostedModelCatalogPublicationTls(string pin, TimeProvider timeProvider)
    {
        _agentHostCertificatePin = pin;
        _timeProvider = timeProvider;
    }

    /// <summary>Enables optional TLS client certificates and rejects every peer except pinned AgentHost.</summary>
    internal static void Configure(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SectionName);
        if (!section.Exists())
            return;
        var pin = section[CertificatePinKey];
        if (pin is not { Length: 64 } || pin.Any(character => !char.IsAsciiHexDigit(character)) ||
            !File.Exists(BackendCaFile))
            throw new InvalidOperationException(
                "Hosted model catalog publication requires the AgentHost certificate pin and backend CA.");

        var identity = new HostedModelCatalogPublicationTls(pin, TimeProvider.System);
        builder.Services.AddSingleton(identity);
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.CheckCertificateRevocation = true;
            https.ClientCertificateValidation = identity.AcceptClientCertificate;
        }));
    }

    /// <summary>Requires direct TLS and the exact AgentHost client certificate for publication.</summary>
    internal bool IsAgentHost(HttpContext context)
    {
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        if (!context.Request.IsHttps || protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            return false;
        // Kestrel validates the custom-root chain; the route rechecks the exact leaf identity.
        return RuntimeServiceTlsIdentity.IsPinnedClientCertificateLeaf(
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate,
            [_agentHostCertificatePin],
            _timeProvider.GetUtcNow());
    }

    private bool AcceptClientCertificate(
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        SslPolicyErrors errors) =>
        RuntimeServiceTlsIdentity.ValidatePinnedClientCertificate(
            certificate, chain, errors, [_agentHostCertificatePin], BackendCaFile, _timeProvider.GetUtcNow());
}
