using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class AgentHostMtlsClientHandlerTests
{
    [Fact]
    public void Create_returns_plain_handler_with_no_client_cert_when_mtls_disabled()
    {
        var options = new SandboxAgentOptions { RequireMtls = false };

        using var handler = AgentHostMtlsClientHandler.Create(options);

        handler.ClientCertificates.Count.Should().Be(0);
        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
    }

    [Fact]
    public void ValidateServerCertificate_accepts_a_leaf_issued_by_the_pinned_ca_despite_name_and_chain_errors()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agenthost-test-ca");
        using var serverCertificate = CreateServerCertificate("CN=agentweaver-agent-host", trustedCa);

        // Ephemeral pod IPs mean the OS validator legitimately reports both a hostname mismatch
        // and (because it doesn't trust our private CA) chain errors — both are expected/ignored.
        var reported = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;

        AgentHostMtlsClientHandler
            .ValidateServerCertificate(serverCertificate, reported, trustedCa)
            .Should().BeTrue();
    }

    [Fact]
    public void ValidateServerCertificate_rejects_a_leaf_issued_by_a_different_ca()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agenthost-test-ca");
        using var untrustedCa = CreateCertificateAuthority("CN=other-ca");
        using var foreignServerCertificate = CreateServerCertificate("CN=agentweaver-agent-host", untrustedCa);

        var reported = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;

        AgentHostMtlsClientHandler
            .ValidateServerCertificate(foreignServerCertificate, reported, trustedCa)
            .Should().BeFalse();
    }

    [Fact]
    public void ValidateServerCertificate_rejects_unexpected_policy_errors_even_with_a_trusted_ca()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agenthost-test-ca");
        using var serverCertificate = CreateServerCertificate("CN=agentweaver-agent-host", trustedCa);

        AgentHostMtlsClientHandler
            .ValidateServerCertificate(serverCertificate, SslPolicyErrors.RemoteCertificateNotAvailable, trustedCa)
            .Should().BeFalse();
    }

    [Fact]
    public void ValidateServerCertificate_rejects_a_missing_certificate()
    {
        using var trustedCa = CreateCertificateAuthority("CN=agenthost-test-ca");

        AgentHostMtlsClientHandler
            .ValidateServerCertificate(null, SslPolicyErrors.None, trustedCa)
            .Should().BeFalse();
    }

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

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 CreateServerCertificate(string subjectName, X509Certificate2 certificateAuthority)
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
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
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
