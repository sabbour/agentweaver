using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class HostedModelCatalogCredentialTlsTestSupport : IDisposable
{
    private const int ListenerPort = 9445;
    private readonly X509Certificate2 _identityCertificate;
    private readonly DirectoryInfo _directory;

    public HostedModelCatalogCredentialTlsTestSupport()
    {
        _identityCertificate = CreateCertificate(
            "identity-broker", "1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2");
        PeerCertificate = CreateCertificate("agenthost", "1.3.6.1.5.5.7.3.2");
        _directory = Directory.CreateTempSubdirectory("agentweaver-hosted-catalog-tls-");
        CertificatePath = Path.Combine(_directory.FullName, "identity.pem");
        KeyPath = Path.Combine(_directory.FullName, "identity-key.pem");
        File.WriteAllText(CertificatePath, _identityCertificate.ExportCertificatePem());
        using var privateKey = _identityCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The test service certificate has no RSA private key.");
        File.WriteAllText(KeyPath, privateKey.ExportPkcs8PrivateKeyPem());
        Options = new()
        {
            LocalServiceName = "IdentityBroker",
            LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
            LocalCertificatePemPath = CertificatePath,
            LocalPrivateKeyPemPath = KeyPath,
            LocalCertificateSha256 = Digest(_identityCertificate),
            ListenerPort = ListenerPort,
            PeerServiceName = "AgentHost",
            PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
            PeerOwnerAddress = "https://agenthost.example:9446/",
            PeerCertificateSha256Pins = [Digest(PeerCertificate)]
        };
    }

    public RuntimeServiceTlsOptions Options { get; }
    public X509Certificate2 PeerCertificate { get; }
    private string CertificatePath { get; }
    private string KeyPath { get; }

    public void Configure(Dictionary<string, string?> settings)
    {
        Configure(settings,
            HostedModelCatalogCredentialUseComposition.SectionName + ":Tls:",
            HostedModelCatalogCredentialUseComposition.ListenerName,
            ListenerPort,
            Options.PeerServiceName!);
    }

    public void Configure(
        Dictionary<string, string?> settings,
        string prefix,
        string listenerName,
        int listenerPort,
        string peerServiceName)
    {
        settings[prefix + nameof(Options.LocalServiceName)] = Options.LocalServiceName;
        settings[prefix + nameof(Options.LocalServicePrincipalId)] = Options.LocalServicePrincipalId;
        settings[prefix + nameof(Options.LocalCertificatePemPath)] = CertificatePath;
        settings[prefix + nameof(Options.LocalPrivateKeyPemPath)] = KeyPath;
        settings[prefix + nameof(Options.LocalCertificateSha256)] = Options.LocalCertificateSha256;
        settings[prefix + nameof(Options.ListenerPort)] = listenerPort.ToString();
        settings[prefix + nameof(Options.PeerServiceName)] = peerServiceName;
        settings[prefix + nameof(Options.PeerServicePrincipalId)] = "33333333-3333-3333-3333-333333333333";
        settings[prefix + nameof(Options.PeerOwnerAddress)] =
            $"https://{peerServiceName.ToLowerInvariant()}.example:{listenerPort}/";
        settings[prefix + nameof(Options.PeerCertificateSha256Pins) + ":0"] =
            Options.PeerCertificateSha256Pins![0];
        settings[$"Kestrel:Endpoints:{listenerName}:Url"] = $"https://127.0.0.1:{listenerPort}/";
    }

    public IStartupFilter CreateStartupFilter() => new TlsStartupFilter(this);

    public void ConfigureContext(HttpContext context)
    {
        context.Request.Scheme = "https";
        context.Connection.LocalPort = ListenerPort;
        context.Features.Set<ITlsHandshakeFeature>(new Handshake());
        context.Features.Set<ITlsConnectionFeature>(new TlsConnection(PeerCertificate));
    }

    public void Dispose()
    {
        _identityCertificate.Dispose();
        PeerCertificate.Dispose();
        _directory.Delete(recursive: true);
    }

    private static string Digest(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static X509Certificate2 CreateCertificate(string subject, params string[] purposes)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usages = new OidCollection();
        foreach (var purpose in purposes)
            usages.Add(new Oid(purpose));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TlsStartupFilter(HostedModelCatalogCredentialTlsTestSupport profile) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                profile.ConfigureContext(context);
                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class Handshake : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => SslProtocols.Tls13;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public System.Net.Security.TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private sealed class TlsConnection(X509Certificate2 certificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate;
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ClientCertificate);
    }
}
