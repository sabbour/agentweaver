using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Mcp;

internal static class McpGatewayCertificateValidation
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    internal static bool Validate(
        X509Certificate? certificate,
        X509Chain? peerChain,
        SslPolicyErrors errors,
        string caFile)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            return false;

        using var root = X509CertificateLoader.LoadCertificateFromFile(caFile);
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var extendedKeyUsage = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (extendedKeyUsage is null ||
            !extendedKeyUsage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ServerAuthenticationOid))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (peerChain is not null)
        {
            foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return chain.Build(leaf);
    }
}
