extern alias McpHost;

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using McpGatewayCertificateValidation = McpHost::Agentweaver.Mcp.McpGatewayCertificateValidation;
using Xunit;

namespace Agentweaver.Mcp.Tests;

public sealed class BackendCertificateValidationTests
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    [Fact]
    public void GatewayCertificateRequiresServerAuthenticationTrustedRootAndMatchingHostname()
    {
        using var rootKey = RSA.Create(2048);
        using var otherRootKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        using var root = CreateRoot(rootKey, "Agentweaver test backend CA");
        using var otherRoot = CreateRoot(otherRootKey, "Untrusted test backend CA");
        using var leaf = CreateLeaf(root, leafKey);
        using var clientAuthLeaf = CreateLeaf(root, leafKey, ClientAuthenticationOid);
        using var expiredLeaf = CreateLeaf(
            root, leafKey, ServerAuthenticationOid,
            DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-2));
        using var unrestrictedLeaf = CreateLeaf(root, leafKey, null);
        var rootFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem");
        var otherRootFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem");
        try
        {
            File.WriteAllText(rootFile, root.ExportCertificatePem());
            File.WriteAllText(otherRootFile, otherRoot.ExportCertificatePem());

            Assert.True(McpGatewayCertificateValidation.Validate(leaf, null, SslPolicyErrors.None, rootFile));
            Assert.False(McpGatewayCertificateValidation.Validate(
                leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, rootFile));
            Assert.False(McpGatewayCertificateValidation.Validate(leaf, null, SslPolicyErrors.None, otherRootFile));
            Assert.False(McpGatewayCertificateValidation.Validate(
                clientAuthLeaf, null, SslPolicyErrors.None, rootFile));
            Assert.False(McpGatewayCertificateValidation.Validate(
                expiredLeaf, null, SslPolicyErrors.None, rootFile));
            Assert.False(McpGatewayCertificateValidation.Validate(
                unrestrictedLeaf, null, SslPolicyErrors.None, rootFile));
        }
        finally
        {
            File.Delete(rootFile);
            File.Delete(otherRootFile);
        }
    }

    private static X509Certificate2 CreateRoot(RSA key, string subject)
    {
        var request = new CertificateRequest(
            $"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 CreateLeaf(
        X509Certificate2 root,
        RSA key,
        string? enhancedKeyUsageOid = ServerAuthenticationOid,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        var request = new CertificateRequest(
            "CN=gateway.internal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("gateway.internal");
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        if (enhancedKeyUsageOid is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(enhancedKeyUsageOid) }, false));
        }
        return request.Create(
            root,
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter ?? DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
    }
}
