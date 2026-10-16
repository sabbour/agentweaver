using System.Collections.Immutable;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Identity;

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

    internal static bool IsPin(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public override string ToString() => nameof(RuntimeServiceTlsOptions) + " [REDACTED]";
}

public sealed class RuntimeServiceTlsIdentity : IDisposable
{
    public const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private readonly ImmutableArray<byte[]> _peerPins;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeServiceTlsOptions _options;

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

    public bool AcceptClientCertificate(
        X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        chain is not null && errors == SslPolicyErrors.None &&
        AcceptPeerCertificate(certificate, ClientAuthentication);

    public void RequirePeerConnection(
        bool isHttps, SslProtocols? protocol, int localPort, X509Certificate2? certificate)
    {
        RequireDirectTls(isHttps, protocol, localPort, ListenerPort);
        if (!AcceptPeerCertificate(certificate, ClientAuthentication))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_peer_denied");
    }

    public static void RequireDirectTls(
        bool isHttps, SslProtocols? protocol, int localPort, int listenerPort)
    {
        if (!isHttps || protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_https_required");
        if (localPort != listenerPort)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_listener_denied");
    }

    public HttpClientHandler CreateHandler()
    {
        if (!HasCertificatePurpose(Certificate, ClientAuthentication, _timeProvider.GetUtcNow()))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_service_certificate_expired");
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true,
            SslProtocols = AllowedProtocols,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                chain is not null && errors == SslPolicyErrors.None &&
                AcceptPeerCertificate(certificate, ServerAuthentication)
        };
        handler.ClientCertificates.Add(Certificate);
        return handler;
    }

    private bool AcceptPeerCertificate(X509Certificate2? certificate, string purpose) =>
        certificate is not null &&
        HasCertificatePurpose(certificate, purpose, _timeProvider.GetUtcNow()) &&
        _peerPins.Any(pin => CryptographicOperations.FixedTimeEquals(
            pin, SHA256.HashData(certificate.RawData)));

    private static bool HasCertificatePurpose(
        X509Certificate2 certificate, string purpose, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() &&
        now < certificate.NotAfter.ToUniversalTime() &&
        !certificate.Extensions.OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority) &&
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == purpose));

    public void Dispose() => Certificate.Dispose();
}
