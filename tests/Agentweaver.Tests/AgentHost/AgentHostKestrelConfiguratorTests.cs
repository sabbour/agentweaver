extern alias agenthost;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using agenthost::Agentweaver.AgentHost;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.AgentHost;

public sealed class AgentHostKestrelConfiguratorTests
{
    [Fact]
    public void Resolve_uses_plain_http_fallback_when_mtls_is_disabled_and_no_endpoints_exist()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AgentHost:RequireMtls"] = "false",
            ["AgentHost:Port"] = "8088",
        });

        var plan = AgentHostKestrelConfigurator.Resolve(configuration);

        plan.RequireMtls.Should().BeFalse();
        plan.KestrelEndpointsConfigured.Should().BeFalse();
        plan.BindPlainHttpFallback.Should().BeTrue();
        plan.BindMtlsFallback.Should().BeFalse();
        plan.A2APort.Should().Be(8088);
    }

    [Fact]
    public void Resolve_uses_mtls_defaults_when_required_without_kestrel_endpoint_config()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AgentHost:RequireMtls"] = "true",
            ["AgentHost:Port"] = "8443",
        });

        var plan = AgentHostKestrelConfigurator.Resolve(configuration);

        plan.RequireMtls.Should().BeTrue();
        plan.KestrelEndpointsConfigured.Should().BeFalse();
        plan.BindPlainHttpFallback.Should().BeFalse();
        plan.BindMtlsFallback.Should().BeTrue();
        plan.A2APort.Should().Be(8443);
        plan.ServerCertificatePath.Should().Be("/mnt/a2a-tls/tls.crt");
        plan.ServerCertificateKeyPath.Should().Be("/mnt/a2a-tls/tls.key");
        plan.ClientCACertificatePath.Should().Be("/mnt/a2a-tls/ca.crt");
    }

    [Fact]
    public void ValidateClientCertificate_accepts_only_certificates_issued_by_the_configured_ca()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agentweaver-test-ca");
        using var clientCertificate = CreateClientCertificate("CN=agentweaver-worker", trustedCa);
        using var untrustedCa = CreateCertificateAuthority("CN=other-ca");
        using var foreignClientCertificate = CreateClientCertificate("CN=agentweaver-worker", untrustedCa);

        AgentHostKestrelConfigurator.ValidateClientCertificate(clientCertificate, trustedCa)
            .Should().BeTrue();
        AgentHostKestrelConfigurator.ValidateClientCertificate(foreignClientCertificate, trustedCa)
            .Should().BeFalse();
        AgentHostKestrelConfigurator.ValidateClientCertificate(null, trustedCa)
            .Should().BeFalse();
    }

    [Fact]
    public void LoadPublicCertificate_loads_certificate_only_pem_for_chain_validation()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agentweaver-test-ca");
        using var clientCertificate = CreateClientCertificate("CN=agentweaver-worker", trustedCa);
        var certificatePath = Path.Combine(
            AppContext.BaseDirectory,
            $"{nameof(AgentHostKestrelConfiguratorTests)}-{Guid.NewGuid():N}-ca.crt");
        File.WriteAllText(certificatePath, trustedCa.ExportCertificatePem());

        try
        {
            using var loadedCertificate = AgentHostKestrelConfigurator.LoadPublicCertificate(certificatePath);

            loadedCertificate.HasPrivateKey.Should().BeFalse();
            loadedCertificate.Thumbprint.Should().Be(trustedCa.Thumbprint);
            loadedCertificate.PublicKey.Should().NotBeNull();
            AgentHostKestrelConfigurator.ValidateClientCertificate(clientCertificate, loadedCertificate)
                .Should().BeTrue();
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static X509Certificate2 CreateCertificateAuthority(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        return certificate;
    }

    private static X509Certificate2 CreateClientCertificate(string subjectName, X509Certificate2 certificateAuthority)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2") },
                true));

        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);

        using var issuedCertificate = request.Create(
            certificateAuthority,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            serialNumber);
        return issuedCertificate.CopyWithPrivateKey(rsa);
    }
}
