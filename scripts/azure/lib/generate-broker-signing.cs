#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using var key = RSA.Create(3072);
var request = new CertificateRequest(
    "CN=Agentweaver Identity Broker", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
request.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
var now = DateTimeOffset.UtcNow;
using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var pfx = certificate.Export(X509ContentType.Pkcs12, password);
try
{
    Console.Write(JsonSerializer.Serialize(new
    {
        apiVersion = "v1",
        kind = "Secret",
        metadata = new
        {
            name = "identity-broker-signing",
            @namespace = "agentweaver-v1-p0",
            labels = new Dictionary<string, string>
            {
                ["agentweaver.io/service"] = "identity-broker",
                ["agentweaver.io/managed-by"] = "installer",
            },
        },
        type = "Opaque",
        immutable = true,
        data = new Dictionary<string, string>
        {
            ["signing.pfx"] = Convert.ToBase64String(pfx),
            ["password"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
        },
    }));
}
finally
{
    CryptographicOperations.ZeroMemory(pfx);
}
