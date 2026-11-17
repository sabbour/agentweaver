using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Environment.Tests;

internal sealed class PlatformPolicySourceClientCertificateFixture : IDisposable
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private readonly X509Certificate2 _authority;
    private readonly List<X509Certificate2> _certificates = [];
    private readonly string _directory;

    internal PlatformPolicySourceClientCertificateFixture()
    {
        _directory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".policy-source-client-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Environment policy source test CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        _authority = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        BackendCaPath = Path.Combine(_directory, "backend-ca.pem");
        File.WriteAllText(BackendCaPath, _authority.ExportCertificatePem());

        Client = CreateLeaf(
            "environment.agentweaver.svc.cluster.local",
            [ClientAuthenticationOid, ServerAuthenticationOid]);
        ProjectsServer = CreateLeaf("localhost", [ServerAuthenticationOid]);
        RemoteMcpIdentity = CreateLeaf(
            "environment.agentweaver.svc.cluster.local",
            [ClientAuthenticationOid, ServerAuthenticationOid]);
        ClientFiles = WriteFiles("source-client", Client);
        RemoteMcpFiles = WriteFiles("remote-mcp", RemoteMcpIdentity);
    }

    internal string BackendCaPath { get; }
    internal X509Certificate2 Client { get; }
    internal X509Certificate2 ProjectsServer { get; }
    internal X509Certificate2 RemoteMcpIdentity { get; }
    internal (string CertificatePath, string PrivateKeyPath) ClientFiles { get; }
    internal (string CertificatePath, string PrivateKeyPath) RemoteMcpFiles { get; }
    internal string BackendCaSha256 => Fingerprint(_authority);

    internal Dictionary<string, string?> Configuration(string? projectsPin = null)
    {
        projectsPin ??= Fingerprint(ProjectsServer);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Environment:PlatformEgress:Source:Enabled"] = "true",
            ["Environment:PlatformEgress:Source:Tls:ClientCertificatePemPath"] =
                ClientFiles.CertificatePath,
            ["Environment:PlatformEgress:Source:Tls:ClientPrivateKeyPemPath"] =
                ClientFiles.PrivateKeyPath,
            ["Environment:PlatformEgress:Source:Tls:ClientCertificateSha256"] =
                Fingerprint(Client),
            ["Environment:PlatformEgress:Source:Tls:BackendCaPemPath"] = BackendCaPath,
            ["Environment:PlatformEgress:Source:Tls:BackendCaSha256"] = BackendCaSha256,
            ["Environment:PlatformEgress:Source:Tls:ProjectsPins:0:Sha256"] = projectsPin,
            ["Environment:PlatformEgress:Source:Tls:ProjectsPins:0:RetireAtUtc"] =
                DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["ProjectsConfig:BaseAddress"] = "https://localhost/",
        };
    }

    internal void AddRemoteMcpProfile(
        IDictionary<string, string?> values,
        string sectionName,
        string peerName,
        (string CertificatePath, string PrivateKeyPath)? files = null,
        string? localPin = null)
    {
        files ??= RemoteMcpFiles;
        var prefix = sectionName + ":";
        values[prefix + "LocalServiceName"] = "Environment";
        values[prefix + "LocalServicePrincipalId"] = "dbe6ef3c-913c-40ee-b214-4ac7ed93eb25";
        values[prefix + "LocalCertificatePemPath"] = files.Value.CertificatePath;
        values[prefix + "LocalPrivateKeyPemPath"] = files.Value.PrivateKeyPath;
        values[prefix + "LocalCertificateSha256"] =
            localPin ?? Fingerprint(RemoteMcpIdentity);
        values[prefix + "ListenerPort"] = "8444";
        values[prefix + "PeerServiceName"] = peerName;
        values[prefix + "PeerServicePrincipalId"] = "a18f4658-59bc-4860-8177-3dfdc0a60e39";
        values[prefix + "PeerOwnerAddress"] = "https://" + peerName.ToLowerInvariant() +
            ".agentweaver.svc.cluster.local/";
        values[prefix + "PeerCertificateSha256Pins:0"] = Fingerprint(ProjectsServer);
    }

    internal static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    private X509Certificate2 CreateLeaf(string dnsName, string[] purposes)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + dnsName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection();
        foreach (var purpose in purposes)
            usages.Add(new Oid(purpose));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        using var issued = request.Create(
            _authority,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(2),
            RandomNumberGenerator.GetBytes(16));
        var certificate = issued.CopyWithPrivateKey(key);
        _certificates.Add(certificate);
        return certificate;
    }

    private (string CertificatePath, string PrivateKeyPath) WriteFiles(
        string name,
        X509Certificate2 certificate)
    {
        var certificatePath = Path.Combine(_directory, name + ".pem");
        var privateKeyPath = Path.Combine(_directory, name + "-key.pem");
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The test certificate has no RSA private key.");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
        return (certificatePath, privateKeyPath);
    }

    public void Dispose()
    {
        foreach (var certificate in _certificates)
            certificate.Dispose();
        _authority.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
