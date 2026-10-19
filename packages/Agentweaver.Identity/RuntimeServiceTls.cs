using System.Collections.Immutable;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Identity;

/// <summary>Direct mTLS identity, endpoint, listener, and certificate-pin configuration for a runtime service pair.</summary>
public sealed record RuntimeServiceTlsOptions
{
    public string? LocalServiceName { get; init; }
    public string? LocalServicePrincipalId { get; init; }
    public string? LocalCertificatePemPath { get; init; }
    public string? LocalPrivateKeyPemPath { get; init; }
    public string? LocalCertificateSha256 { get; init; }
    public int ListenerPort { get; init; }
    public string? PeerServiceName { get; init; }
    public string? PeerServicePrincipalId { get; init; }
    public string? PeerOwnerAddress { get; init; }
    public string[]? PeerCertificateSha256Pins { get; init; }

    /// <summary>Checks service identities, principal IDs, certificate paths, ports, pins, and fixed HTTPS owner address.</summary>
    public RuntimeServiceTlsOptions Validate(string localServiceName, string peerServiceName)
    {
        if (LocalServiceName != localServiceName || PeerServiceName != peerServiceName ||
            !Guid.TryParse(LocalServicePrincipalId, out var localPrincipal) || localPrincipal == Guid.Empty ||
            !Guid.TryParse(PeerServicePrincipalId, out var peerPrincipal) || peerPrincipal == Guid.Empty ||
            localPrincipal == peerPrincipal ||
            LocalCertificatePemPath is not { Length: > 0 } certificatePath ||
            LocalPrivateKeyPemPath is not { Length: > 0 } keyPath ||
            !Path.IsPathFullyQualified(certificatePath) || !Path.IsPathFullyQualified(keyPath) ||
            certificatePath == keyPath || ListenerPort is < 1 or > 65535 ||
            !IsPin(LocalCertificateSha256) ||
            PeerCertificateSha256Pins is not { Length: > 0 and <= 4 } pins ||
            pins.Any(pin => !IsPin(pin)) ||
            pins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Length ||
            !Uri.TryCreate(PeerOwnerAddress, UriKind.Absolute, out var peerAddress) ||
            !RuntimeContractValidation.IsHttpsEndpoint(peerAddress) || peerAddress.AbsolutePath != "/")
            throw new InvalidOperationException("The direct runtime service TLS profile is invalid.");
        return this with { PeerCertificateSha256Pins = pins.ToArray() };
    }

    /// <summary>Checks for a 64-character hexadecimal certificate digest.</summary>
    internal static bool IsPin(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public override string ToString() => nameof(RuntimeServiceTlsOptions) + " [REDACTED]";
}

/// <summary>
/// Loads the local certificate and applies mutual TLS, custom-root server validation, EKU,
/// validity, and peer certificate pin checks to direct service connections.
/// </summary>
public sealed class RuntimeServiceTlsIdentity : IDisposable
{
    public const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private readonly ImmutableArray<byte[]> _peerPins;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeServiceTlsOptions _options;

    /// <summary>Loads and validates a local client/server certificate against its declared fingerprint.</summary>
    /// <exception cref="InvalidOperationException">The profile or local certificate is invalid.</exception>
    public RuntimeServiceTlsIdentity(
        RuntimeServiceTlsOptions options, string localServiceName, string peerServiceName,
        TimeProvider? timeProvider = null)
    {
        _options = options.Validate(localServiceName, peerServiceName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _peerPins = _options.PeerCertificateSha256Pins!
            .Select(Convert.FromHexString).ToImmutableArray();
        Certificate = X509Certificate2.CreateFromPemFile(
            _options.LocalCertificatePemPath!, _options.LocalPrivateKeyPemPath!);
        if (!Certificate.HasPrivateKey ||
            !HasCertificatePurpose(Certificate, ClientAuthentication, _timeProvider.GetUtcNow()) ||
            !HasCertificatePurpose(Certificate, ServerAuthentication, _timeProvider.GetUtcNow()) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Certificate.RawData), Convert.FromHexString(_options.LocalCertificateSha256!)))
        {
            Certificate.Dispose();
            throw new InvalidOperationException("The runtime service certificate does not match its TLS profile.");
        }
    }

    public X509Certificate2 Certificate { get; }
    public int ListenerPort => _options.ListenerPort;
    public string LocalServiceName => _options.LocalServiceName!;
    public string PeerServiceName => _options.PeerServiceName!;
    public Uri PeerOwnerAddress => new(_options.PeerOwnerAddress!, UriKind.Absolute);

    /// <summary>Accepts a peer certificate only when chain validation and client EKU/pinning both pass.</summary>
    public bool AcceptClientCertificate(
        X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        chain is not null && errors == SslPolicyErrors.None &&
        AcceptPeerCertificate(certificate, ClientAuthentication);

    /// <summary>Rejects peer connections that do not use the configured direct TLS listener and certificate.</summary>
    public void RequirePeerConnection(
        bool isHttps, SslProtocols? protocol, int localPort, X509Certificate2? certificate)
    {
        RequireDirectTls(isHttps, protocol, localPort, ListenerPort);
        if (!AcceptPeerCertificate(certificate, ClientAuthentication))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_peer_denied");
    }

    /// <summary>Requires HTTPS with TLS 1.2 or 1.3 on the expected listener port.</summary>
    public static void RequireDirectTls(
        bool isHttps, SslProtocols? protocol, int localPort, int listenerPort)
    {
        if (!isHttps || protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_https_required");
        if (localPort != listenerPort)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_listener_denied");
    }

    /// <summary>Builds a non-redirecting TLS client with backend-CA trust and online revocation, pinned to the peer.</summary>
    public HttpClientHandler CreateHandler() =>
        CreateHandler(RuntimeOwnerHttpTransport.ServiceBackendCaFile);

    /// <summary>Creates the pinned mTLS owner handler using the specified service backend CA file.</summary>
    internal HttpClientHandler CreateHandler(string caFile)
    {
        if (!HasCertificatePurpose(Certificate, ClientAuthentication, _timeProvider.GetUtcNow()))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_certificate_expired");
        if (!File.Exists(caFile))
            throw new InvalidOperationException("The service backend CA ConfigMap is required.");

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true,
            SslProtocols = AllowedProtocols,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                AcceptServerCertificate(certificate, chain, errors, caFile)
        };
        handler.ClientCertificates.Add(Certificate);
        return handler;
    }

    /// <summary>Checks backend-CA trust and hostname before enforcing server purpose and the peer pin.</summary>
    internal bool AcceptServerCertificate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        string caFile,
        X509RevocationMode revocationMode = X509RevocationMode.Online) =>
        chain is not null &&
        RuntimeOwnerHttpTransport.ValidateServerCertificate(
            certificate, chain, errors, caFile, revocationMode, disableCertificateDownloads: false) &&
        AcceptPeerCertificate(certificate, ServerAuthentication);

    /// <summary>Checks certificate validity, required EKU, and fixed-time SHA-256 pin equality.</summary>
    private bool AcceptPeerCertificate(X509Certificate? certificate, string purpose)
    {
        if (certificate is null)
            return false;

        using var converted = certificate is X509Certificate2
            ? null
            : X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var leaf = certificate as X509Certificate2 ?? converted!;
        return HasCertificatePurpose(leaf, purpose, _timeProvider.GetUtcNow()) &&
               _peerPins.Any(pin => CryptographicOperations.FixedTimeEquals(
                   pin, SHA256.HashData(leaf.RawData)));
    }

    /// <summary>Checks the certificate time window, non-CA status, and required extended key usage.</summary>
    private static bool HasCertificatePurpose(
        X509Certificate2 certificate, string purpose, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() &&
        now < certificate.NotAfter.ToUniversalTime() &&
        !certificate.Extensions.OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority) &&
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == purpose));

    /// <summary>Disposes the loaded local certificate.</summary>
    public void Dispose() => Certificate.Dispose();
}
