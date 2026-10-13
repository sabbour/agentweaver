using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Projects.Config;

internal sealed class BrokerRunBindingTlsIdentity : IDisposable
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string TlsSection = "ProjectsConfig:Tls";
    private static readonly SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    private BrokerRunBindingTlsIdentity(X509Certificate2 certificate) => Certificate = certificate;

    internal X509Certificate2 Certificate { get; }

    internal static void Configure(WebApplicationBuilder builder, bool enabled)
    {
        var section = builder.Configuration.GetSection(TlsSection);
        var hasTlsSettings = section.GetChildren().Any();
        if (!enabled)
        {
            if (hasTlsSettings)
                throw new InvalidOperationException(
                    "Projects TLS settings require the Broker run-binding audience.");
            return;
        }

        var certificatePath = section["CertificatePath"];
        var keyPath = section["KeyPath"];
        var serverCertificateSha256 = section["ServerCertificateSha256"];
        if (string.IsNullOrWhiteSpace(certificatePath) ||
            string.IsNullOrWhiteSpace(keyPath) ||
            string.IsNullOrWhiteSpace(serverCertificateSha256) ||
            !Path.IsPathFullyQualified(certificatePath) ||
            !Path.IsPathFullyQualified(keyPath) ||
            string.Equals(certificatePath, keyPath, StringComparison.Ordinal) ||
            !IsSha256(serverCertificateSha256))
            throw new InvalidOperationException(
                "Projects TLS requires absolute certificate and key paths and a leaf certificate SHA-256.");

        var identity = Load(certificatePath, keyPath, serverCertificateSha256);
        builder.Services.AddSingleton<BrokerRunBindingTlsIdentity>(_ => identity);
        builder.WebHost.ConfigureKestrel(server =>
            server.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = identity.Certificate;
                https.SslProtocols = AllowedProtocols;
            }));
    }

    internal static bool IsNativeHttps(HttpContext context) =>
        context.Request.IsHttps &&
        context.Features.Get<ITlsHandshakeFeature>()?.Protocol is
            SslProtocols.Tls12 or SslProtocols.Tls13;

    private static BrokerRunBindingTlsIdentity Load(
        string certificatePath, string keyPath, string expectedSha256)
    {
        using var pemCertificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var pkcs12 = pemCertificate.Export(X509ContentType.Pkcs12);
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(
                pkcs12, null, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
        var now = DateTimeOffset.UtcNow;
        var serverPurpose = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                .Any(usage => usage.Value == ServerAuthenticationOid));
        if (!certificate.HasPrivateKey ||
            now < certificate.NotBefore.ToUniversalTime() ||
            now >= certificate.NotAfter.ToUniversalTime() ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(extension => extension.CertificateAuthority) ||
            !serverPurpose ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.RawData), Convert.FromHexString(expectedSha256)))
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The Projects TLS certificate does not match its configured server profile.");
        }

        return new BrokerRunBindingTlsIdentity(certificate);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    public void Dispose() => Certificate.Dispose();
}
