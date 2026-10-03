using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>
/// Generates an ephemeral, self-signed RSA certificate and writes it to a temp PFX file for
/// use as the broker's own signing/encryption credential in tests. This lives entirely in the
/// test project — the broker itself (see <c>Program.cs</c>) has no "development certificate"
/// fallback; it always requires an explicit <c>PfxPath</c>/<c>PfxPassword</c>, and tests
/// satisfy that requirement with a real, freshly generated cert rather than a baked-in one.
/// </summary>
public static class TestSigningCertificate
{
    public static (string PfxPath, string Password) Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=agentweaver-identity-broker-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var password = Guid.NewGuid().ToString("N");
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);

        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"identity-broker-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);
        return (path, password);
    }
}
