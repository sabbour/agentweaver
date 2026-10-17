using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Builds the pinned Projects-owner TLS handler for run-binding requests. It verifies the
/// mounted CA bundle digest, leaf certificate pin, server-auth usage and chain, with no
/// proxy, redirects, cookies or certificate downloads.
/// </summary>
internal sealed class BrokerRunBindingTlsIdentity : IDisposable
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string TrustBundlePath = "/var/run/service-backend-ca/ca.crt";
    private static readonly SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    private readonly X509Certificate2Collection _trustedAuthorities;
    private readonly byte[] _leafPin;
    private readonly TimeProvider _timeProvider;

    private BrokerRunBindingTlsIdentity(
        X509Certificate2Collection trustedAuthorities,
        byte[] leafPin,
        TimeProvider timeProvider)
    {
        _trustedAuthorities = trustedAuthorities;
        _leafPin = leafPin;
        _timeProvider = timeProvider;
    }

    /// <summary>Loads the configured trust bundle from the fixed mount and verifies its digest and CA contents.</summary>
    internal static BrokerRunBindingTlsIdentity Load(
        BrokerRunBindingOptions options, TimeProvider? timeProvider = null)
    {
        if (!IsSha256(options.ProjectsTrustBundleSha256) ||
            !IsSha256(options.ProjectsServerCertificateSha256))
            throw new InvalidOperationException("The Broker run-binding TLS profile is incomplete or invalid.");

        return Load(options, File.ReadAllBytes(TrustBundlePath), timeProvider);
    }

    /// <summary>Loads the supplied trust bundle bytes; the overload supports deterministic certificate tests.</summary>
    internal static BrokerRunBindingTlsIdentity Load(
        BrokerRunBindingOptions options,
        ReadOnlySpan<byte> trustBundleBytes,
        TimeProvider? timeProvider = null)
    {
        if (!IsSha256(options.ProjectsTrustBundleSha256) ||
            !IsSha256(options.ProjectsServerCertificateSha256))
            throw new InvalidOperationException("The Broker run-binding TLS profile is incomplete or invalid.");

        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(trustBundleBytes), Convert.FromHexString(options.ProjectsTrustBundleSha256!)))
            throw new InvalidOperationException("The Broker run-binding trust bundle does not match its configured digest.");

        var pem = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(trustBundleBytes);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException("The Broker run-binding trust bundle must not contain private keys.");
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPem(pem);
        if (authorities.Count == 0 || authorities.Cast<X509Certificate2>().Any(certificate =>
                certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                    .SingleOrDefault()?.CertificateAuthority != true ||
                certificate.Extensions.OfType<X509KeyUsageExtension>()
                    .SingleOrDefault()?.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) != true))
        {
            foreach (var certificate in authorities)
                certificate.Dispose();
            throw new InvalidOperationException("The Broker run-binding trust bundle must contain only CA certificates.");
        }

        return new BrokerRunBindingTlsIdentity(
            authorities,
            Convert.FromHexString(options.ProjectsServerCertificateSha256!),
            timeProvider ?? TimeProvider.System);
    }

    /// <summary>Creates an HTTP handler that accepts only the pinned Projects-owner certificate chain.</summary>
    internal HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        SslProtocols = AllowedProtocols,
        ServerCertificateCustomValidationCallback = ValidateServerCertificate,
    };

    private bool ValidateServerCertificate(
        HttpRequestMessage _,
        X509Certificate? certificate,
        X509Chain? peerChain,
        SslPolicyErrors errors)
    {
        if (certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var now = _timeProvider.GetUtcNow();
        var serverPurpose = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (now < leaf.NotBefore.ToUniversalTime() ||
            now >= leaf.NotAfter.ToUniversalTime() ||
            leaf.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(extension => extension.CertificateAuthority) ||
            serverPurpose is null ||
            !serverPurpose.EnhancedKeyUsages.Cast<Oid>()
                .Any(usage => usage.Value == ServerAuthenticationOid) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.RawData), _leafPin))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        foreach (var authority in _trustedAuthorities)
            chain.ChainPolicy.CustomTrustStore.Add(authority);
        if (peerChain is not null)
        {
            foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return chain.Build(leaf);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public void Dispose()
    {
        foreach (var certificate in _trustedAuthorities)
            certificate.Dispose();
    }
}
