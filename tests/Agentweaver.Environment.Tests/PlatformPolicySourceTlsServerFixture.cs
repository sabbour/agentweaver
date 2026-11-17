using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Agentweaver.Environment.Tests;

internal sealed class PlatformPolicySourceTlsServerFixture : IDisposable
{
    private readonly X509Certificate2 _authority;
    private readonly RSA _authorityKey;
    private readonly X509Certificate2 _otherAuthority;
    private readonly List<X509Certificate2> _certificates = [];
    private readonly List<RSA> _privateKeys = [];
    private readonly LocalCertificateServer _certificateServer = new();
    private readonly string _directory;
    private int _crlNumber;

    internal PlatformPolicySourceTlsServerFixture()
    {
        _directory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".policy-source-client-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _authorityKey = RSA.Create(2048);
        _authority = CreateAuthority(_authorityKey, "CN=Environment source test CA");
        using var otherKey = RSA.Create(2048);
        _otherAuthority = CreateAuthority(otherKey, "CN=Other Environment source test CA");
        BackendCaPath = Path.Combine(_directory, "projects-ca.pem");
        OtherCaPath = Path.Combine(_directory, "other-ca.pem");
        File.WriteAllText(BackendCaPath, _authority.ExportCertificatePem());
        File.WriteAllText(OtherCaPath, _otherAuthority.ExportCertificatePem());

        ProjectsServer = CreateServerCertificate(includeCrl: true);
    }

    internal string BackendCaPath { get; }
    internal string OtherCaPath { get; }
    internal X509Certificate2 ProjectsServer { get; }
    internal string ProjectsServerCrlPath => GetCrlPath(ProjectsServer);
    internal string BackendCaSha256 => Fingerprint(_authority);
    internal string OtherCaSha256 => Fingerprint(_otherAuthority);
    internal Uri CertificateServerAddress => _certificateServer.BaseAddress;
    internal int CrlRequestCount => _certificateServer.CrlRequestCount;
    internal int AiaRequestCount => _certificateServer.AiaRequestCount;

    internal X509Certificate2 CreateServerWithoutCrl() =>
        CreateServerCertificate(includeCrl: false);

    internal X509Certificate2 CreateServerWithUntrustedIssuerFetch()
    {
        var key = RSA.Create(2048);
        _privateKeys.Add(key);
        var request = new CertificateRequest(
            "CN=Environment source test intermediate",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var notAfter = DateTimeOffset.UtcNow.AddDays(2);
        using var issuedIntermediate = request.Create(
            _authority,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter,
            RandomNumberGenerator.GetBytes(16));
        using var intermediateWithKey = issuedIntermediate.CopyWithPrivateKey(key);
        var intermediate = CopyWithPersistedKey(intermediateWithKey);
        _certificates.Add(intermediate);

        var aiaPath = "aia/" + Guid.NewGuid().ToString("N");
        _certificateServer.SetAia(aiaPath, intermediate.Export(X509ContentType.Cert));
        return CreateServerCertificate(
            includeCrl: false,
            issuer: intermediate,
            aiaUri: new Uri(_certificateServer.BaseAddress, aiaPath),
            notAfter: notAfter);
    }

    internal (Task RequestStarted, Action Release) BlockCrlRequests(string path) =>
        _certificateServer.BlockCrlRequests(path);

    internal void Revoke(X509Certificate2 certificate)
    {
        var path = GetCrlPath(certificate);
        _certificateServer.SetCrl(
            path,
            CreateCrl([Convert.FromHexString(certificate.SerialNumber)]));
    }

    internal string GetCrlPath(X509Certificate2 certificate) =>
        _crlPaths.TryGetValue(Fingerprint(certificate), out var path)
            ? path
            : throw new InvalidOperationException("The certificate has no test CRL.");

    private readonly Dictionary<string, string> _crlPaths = new(StringComparer.Ordinal);

    private X509Certificate2 CreateServerCertificate(
        bool includeCrl,
        X509Certificate2? issuer = null,
        Uri? aiaUri = null,
        DateTimeOffset? notAfter = null)
    {
        issuer ??= _authority;
        var key = RSA.Create(2048);
        _privateKeys.Add(key);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        string? crlPath = null;
        if (includeCrl)
        {
            crlPath = "crl/" + Guid.NewGuid().ToString("N");
            request.CertificateExtensions.Add(new X509Extension(
                "2.5.29.31",
                CreateCrlDistributionPoints(new Uri(_certificateServer.BaseAddress, crlPath)),
                critical: false));
        }
        if (aiaUri is not null)
            request.CertificateExtensions.Add(new X509Extension(
                "1.3.6.1.5.5.7.1.1",
                CreateAuthorityInfoAccess(aiaUri),
                critical: false));

        using var issued = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(2),
            RandomNumberGenerator.GetBytes(16));
        using var certificateWithKey = issued.CopyWithPrivateKey(key);
        var certificate = CopyWithPersistedKey(certificateWithKey);
        _certificates.Add(certificate);
        if (crlPath is not null)
        {
            _crlPaths.Add(Fingerprint(certificate), crlPath);
            _certificateServer.SetCrl(crlPath, CreateCrl([]));
        }
        return certificate;
    }

    private byte[] CreateCrl(IReadOnlyCollection<byte[]> revokedSerials)
    {
        var now = DateTimeOffset.UtcNow;
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            tbs.WriteInteger(1);
            WriteSignatureAlgorithm(tbs);
            tbs.WriteEncodedValue(_authority.SubjectName.RawData);
            tbs.WriteUtcTime(now.AddMinutes(-1));
            tbs.WriteUtcTime(now.AddDays(1));
            if (revokedSerials.Count > 0)
            {
                using (tbs.PushSequence())
                    foreach (var serial in revokedSerials)
                    {
                        using (tbs.PushSequence())
                        {
                            tbs.WriteIntegerUnsigned(serial);
                            tbs.WriteUtcTime(now.AddMinutes(-1));
                        }
                    }
            }

            using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (tbs.PushSequence())
            {
                WriteExtension(tbs, "2.5.29.35", CreateAuthorityKeyIdentifier(_authority));
                var number = new AsnWriter(AsnEncodingRules.DER);
                number.WriteInteger(++_crlNumber);
                WriteExtension(tbs, "2.5.29.20", number.Encode());
            }
        }

        var encodedTbs = tbs.Encode();
        var signature = _authorityKey.SignData(
            encodedTbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var crl = new AsnWriter(AsnEncodingRules.DER);
        using (crl.PushSequence())
        {
            crl.WriteEncodedValue(encodedTbs);
            WriteSignatureAlgorithm(crl);
            crl.WriteBitString(signature);
        }
        return crl.Encode();
    }

    private static X509Certificate2 CreateAuthority(RSA key, string subject)
    {
        var request = new CertificateRequest(
            subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CopyWithPersistedKey(X509Certificate2 certificate)
    {
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private static byte[] CreateAuthorityKeyIdentifier(X509Certificate2 authority)
    {
        var keyIdentifier = authority.Extensions
            .OfType<X509SubjectKeyIdentifierExtension>()
            .Single()
            .SubjectKeyIdentifier!;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
            writer.WriteOctetString(
                Convert.FromHexString(keyIdentifier),
                new Asn1Tag(TagClass.ContextSpecific, 0));
        return writer.Encode();
    }

    private static byte[] CreateCrlDistributionPoints(Uri uri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            writer.WriteCharacterString(
                UniversalTagNumber.IA5String,
                uri.AbsoluteUri,
                new Asn1Tag(TagClass.ContextSpecific, 6));
        return writer.Encode();
    }

    private static byte[] CreateAuthorityInfoAccess(Uri uri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
            writer.WriteCharacterString(
                UniversalTagNumber.IA5String,
                uri.AbsoluteUri,
                new Asn1Tag(TagClass.ContextSpecific, 6));
        }
        return writer.Encode();
    }

    private static void WriteExtension(AsnWriter writer, string oid, byte[] value)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            writer.WriteOctetString(value);
        }
    }

    private static void WriteSignatureAlgorithm(AsnWriter writer)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.1.11");
            writer.WriteNull();
        }
    }

    internal static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public void Dispose()
    {
        _certificateServer.Dispose();
        foreach (var certificate in _certificates)
            certificate.Dispose();
        foreach (var key in _privateKeys)
            key.Dispose();
        _otherAuthority.Dispose();
        _authority.Dispose();
        _authorityKey.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class LocalCertificateServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly ConcurrentDictionary<string, byte[]> _crls = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _aia = new(StringComparer.Ordinal);
        private string? _blockedCrlPath;
        private TaskCompletionSource? _requestStarted;
        private TaskCompletionSource? _release;
        private int _crlRequestCount;
        private int _aiaRequestCount;

        internal LocalCertificateServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _loop = Task.Run(ServeAsync);
        }

        internal Uri BaseAddress { get; }
        internal int CrlRequestCount => Volatile.Read(ref _crlRequestCount);
        internal int AiaRequestCount => Volatile.Read(ref _aiaRequestCount);

        internal void SetCrl(string path, byte[] value) => _crls[path] = value.ToArray();
        internal void SetAia(string path, byte[] value) => _aia[path] = value.ToArray();

        internal (Task RequestStarted, Action Release) BlockCrlRequests(string path)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockedCrlPath = path;
            _requestStarted = started;
            _release = release;
            return (started.Task, release.SetResult);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token)
                        .ConfigureAwait(false);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(
                        stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                        bufferSize: 1024, leaveOpen: true);
                    try
                    {
                        var requestLine = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                        while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { Length: > 0 })
                        {
                        }

                        var path = requestLine?.Split(' ').ElementAtOrDefault(1)?.TrimStart('/') ?? "";
                        byte[]? content;
                        string contentType;
                        if (path.StartsWith("crl/", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _crlRequestCount);
                            if (path == Volatile.Read(ref _blockedCrlPath))
                            {
                                Volatile.Read(ref _requestStarted)?.TrySetResult();
                                await (Volatile.Read(ref _release)?.Task ?? Task.CompletedTask)
                                    .WaitAsync(_stop.Token)
                                    .ConfigureAwait(false);
                            }
                            _crls.TryGetValue(path, out content);
                            contentType = "application/pkix-crl";
                        }
                        else
                        {
                            Interlocked.Increment(ref _aiaRequestCount);
                            _aia.TryGetValue(path, out content);
                            contentType = "application/pkix-cert";
                        }

                        content ??= [];
                        var headers = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n" +
                            $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, _stop.Token).ConfigureAwait(false);
                        await stream.WriteAsync(content, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or OperationCanceledException or SocketException)
                    {
                    }
                }
            }
            catch (Exception exception) when (
                exception is ObjectDisposedException or OperationCanceledException or SocketException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (SocketException)
            {
            }
            _stop.Dispose();
        }
    }
}
