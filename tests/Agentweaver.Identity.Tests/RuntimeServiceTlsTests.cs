using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeServiceTlsTests
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    [Fact]
    public void ValidProfileCopiesExplicitPeerPinsWithoutReadingOrCreatingCertificateFiles()
    {
        var options = Profile();

        var validated = options.Validate("Orchestrator", "EventsAndSessions");

        Assert.NotSame(options.PeerCertificateSha256Pins, validated.PeerCertificateSha256Pins);
        Assert.Equal(options.PeerCertificateSha256Pins, validated.PeerCertificateSha256Pins);
        Assert.DoesNotContain(options.LocalPrivateKeyPemPath!, validated.ToString());
    }

    [Theory]
    [InlineData("local-service")]
    [InlineData("peer-service")]
    [InlineData("local-principal")]
    [InlineData("peer-principal")]
    [InlineData("same-principal")]
    [InlineData("certificate-path")]
    [InlineData("key-path")]
    [InlineData("local-pin")]
    [InlineData("peer-pins")]
    [InlineData("duplicate-pins")]
    [InlineData("port")]
    [InlineData("peer-http")]
    [InlineData("peer-path")]
    public void PartialOrInvalidExplicitProfilesAreNotAccepted(string fault)
    {
        var options = Profile();
        options = fault switch
        {
            "local-service" => options with { LocalServiceName = "AgentHost" },
            "peer-service" => options with { PeerServiceName = "Projects" },
            "local-principal" => options with { LocalServicePrincipalId = null },
            "peer-principal" => options with { PeerServicePrincipalId = Guid.Empty.ToString() },
            "same-principal" => options with { PeerServicePrincipalId = options.LocalServicePrincipalId },
            "certificate-path" => options with { LocalCertificatePemPath = "relative.pem" },
            "key-path" => options with { LocalPrivateKeyPemPath = options.LocalCertificatePemPath },
            "local-pin" => options with { LocalCertificateSha256 = "not-a-pin" },
            "peer-pins" => options with { PeerCertificateSha256Pins = [] },
            "duplicate-pins" => options with { PeerCertificateSha256Pins = [new string('b', 64), new string('B', 64)] },
            "port" => options with { ListenerPort = 0 },
            "peer-http" => options with { PeerOwnerAddress = "http://events.example/" },
            "peer-path" => options with { PeerOwnerAddress = "https://events.example/path" },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate("Orchestrator", "EventsAndSessions"));
    }

    [Theory]
    [InlineData(false, null, 9443, "runtime_remote_tool_result_https_required")]
    [InlineData(true, null, 9443, "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.None, 9443, "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.Tls12, 8080, "runtime_remote_tool_result_service_listener_denied")]
    public void HttpsFlagsWithoutNativeTlsOrTheDedicatedListenerDoNotAuthorizeAPeer(
        bool isHttps, SslProtocols? protocol, int port, string expectedCode)
    {
        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeServiceTlsIdentity.RequireDirectTls(isHttps, protocol, port, 9443));

        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public void PinnedMtlsOwnerClientUsesBackendRootAndKeepsHostnamePinAndClientCertificateChecks()
    {
        using var rootKey = RSA.Create(2048);
        using var otherRootKey = RSA.Create(2048);
        using var localKey = RSA.Create(2048);
        using var peerKey = RSA.Create(2048);
        using var differentPeerKey = RSA.Create(2048);
        using var foreignPeerKey = RSA.Create(2048);
        using var root = CreateRoot(rootKey, "Agentweaver mTLS test CA");
        using var otherRoot = CreateRoot(otherRootKey, "Untrusted mTLS test CA");
        using var localCertificate = CreateLeaf(
            root, localKey, "orchestrator.internal", ClientAuthenticationOid, ServerAuthenticationOid);
        using var peerCertificate = CreateLeaf(root, peerKey, "events.internal", ServerAuthenticationOid);
        using var differentPeerCertificate = CreateLeaf(root, differentPeerKey, "events.internal", ServerAuthenticationOid);
        using var untrustedPeerCertificate = CreateLeaf(otherRoot, foreignPeerKey, "events.internal", ServerAuthenticationOid);
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".test-certificates", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var caFile = Path.Combine(directory, "ca.crt");
        var certificateFile = Path.Combine(directory, "client.crt");
        var privateKeyFile = Path.Combine(directory, "client.key");

        try
        {
            File.WriteAllText(caFile, root.ExportCertificatePem());
            File.WriteAllText(certificateFile, localCertificate.ExportCertificatePem());
            File.WriteAllText(privateKeyFile, localKey.ExportPkcs8PrivateKeyPem());
            using var identity = new RuntimeServiceTlsIdentity(
                Profile() with
                {
                    LocalCertificatePemPath = certificateFile,
                    LocalPrivateKeyPemPath = privateKeyFile,
                    LocalCertificateSha256 = Convert.ToHexString(SHA256.HashData(localCertificate.RawData)),
                    PeerCertificateSha256Pins = [Convert.ToHexString(SHA256.HashData(peerCertificate.RawData))]
                },
                "Orchestrator",
                "EventsAndSessions");
            using var peerChain = new X509Chain();

            Assert.True(identity.AcceptServerCertificate(
                peerCertificate, peerChain, SslPolicyErrors.RemoteCertificateChainErrors,
                caFile, X509RevocationMode.NoCheck));
            Assert.False(identity.AcceptServerCertificate(
                peerCertificate, peerChain,
                SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
                caFile, X509RevocationMode.NoCheck));
            Assert.False(identity.AcceptServerCertificate(
                differentPeerCertificate, peerChain, SslPolicyErrors.RemoteCertificateChainErrors,
                caFile, X509RevocationMode.NoCheck));
            Assert.False(identity.AcceptServerCertificate(
                untrustedPeerCertificate, peerChain, SslPolicyErrors.RemoteCertificateChainErrors,
                caFile, X509RevocationMode.NoCheck));

            using var handler = identity.CreateHandler(caFile);
            Assert.False(handler.AllowAutoRedirect);
            Assert.True(handler.CheckCertificateRevocationList);
            Assert.Equal(RuntimeServiceTlsIdentity.AllowedProtocols, handler.SslProtocols);
            Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
            Assert.Single(handler.ClientCertificates);
            Assert.Equal(identity.Certificate, handler.ClientCertificates[0]);
            Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
            using var request = new HttpRequestMessage();
            Assert.False(handler.ServerCertificateCustomValidationCallback(
                request, peerCertificate, peerChain, SslPolicyErrors.RemoteCertificateNameMismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PinnedClientCertificateUsesMountedRootInsteadOfSystemTrust()
    {
        using var rootKey = RSA.Create(2048);
        using var otherRootKey = RSA.Create(2048);
        using var clientKey = RSA.Create(2048);
        using var foreignClientKey = RSA.Create(2048);
        using var root = CreateRoot(rootKey, "Agentweaver client test CA");
        using var otherRoot = CreateRoot(otherRootKey, "Untrusted client test CA");
        using var client = CreateLeaf(root, clientKey, "agenthost.internal", ClientAuthenticationOid);
        using var foreignClient = CreateLeaf(
            otherRoot, foreignClientKey, "agenthost.internal", ClientAuthenticationOid);
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".test-certificates", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var caFile = Path.Combine(directory, "ca.crt");

        try
        {
            File.WriteAllText(caFile, root.ExportCertificatePem());
            using var peerChain = new X509Chain();
            var pin = Convert.ToHexString(SHA256.HashData(client.RawData));
            var errors = SslPolicyErrors.RemoteCertificateChainErrors;

            Assert.True(RuntimeServiceTlsIdentity.ValidatePinnedClientCertificate(
                client, peerChain, errors, [pin], caFile, DateTimeOffset.UtcNow,
                X509RevocationMode.NoCheck));
            Assert.True(RuntimeServiceTlsIdentity.IsPinnedClientCertificateLeaf(
                client, [pin], DateTimeOffset.UtcNow));
            Assert.False(RuntimeServiceTlsIdentity.IsPinnedClientCertificateLeaf(
                client, [new string('0', 64)], DateTimeOffset.UtcNow));
            Assert.False(RuntimeServiceTlsIdentity.ValidatePinnedClientCertificate(
                client, peerChain, errors, [new string('0', 64)], caFile, DateTimeOffset.UtcNow,
                X509RevocationMode.NoCheck));
            Assert.False(RuntimeServiceTlsIdentity.ValidatePinnedClientCertificate(
                foreignClient, peerChain, errors,
                [Convert.ToHexString(SHA256.HashData(foreignClient.RawData))], caFile,
                DateTimeOffset.UtcNow, X509RevocationMode.NoCheck));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BoundedOwnerResponseRejectsAnExtraByteEvenWithoutAContentLength()
    {
        using var client = new HttpClient(new ResponseHandler(
            new UnknownLengthContent(new byte[1025])));
        var bearer = Bearer();

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeOwnerHttpTransport.SendBoundedAsync<OwnerResponse>(
                client, new Uri("https://events.example/"), "/internal/result", new(bearer, "tenant"),
                new { eventId = "event" }, 1024, false, CancellationToken.None));

        Assert.Equal("runtime_owner_response_size_invalid", failure.Code);
    }

    [Fact]
    public async Task BoundedOwnerResponseDeadlineIncludesTheBodyAfterHeaders()
    {
        using var client = new HttpClient(new ResponseHandler(new UnknownLengthContent(new StalledStream())))
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        var bearer = Bearer();
        using var safetyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RuntimeOwnerHttpTransport.SendBoundedAsync<OwnerResponse>(
                client, new Uri("https://events.example/"), "/internal/result", new(bearer, "tenant"),
                new { eventId = "event" }, 1024, false, safetyDeadline.Token));

        Assert.False(safetyDeadline.IsCancellationRequested);
    }

    [Theory]
    [InlineData("redirect", "runtime_owner_redirect_rejected")]
    [InlineData("cache", "runtime_owner_cache_contract_invalid")]
    [InlineData("denied", "runtime_owner_denied")]
    [InlineData("media", "runtime_owner_response_size_invalid")]
    public async Task BoundedOwnerResponsesRejectInvalidTransportContracts(string fault, string expectedCode)
    {
        using var client = new HttpClient(new ResponseHandler(
            new StringContent("""{"value":"saved"}""", Encoding.UTF8, "application/json"), fault));
        var bearer = Bearer();

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeOwnerHttpTransport.SendBoundedAsync<OwnerResponse>(
                client, new Uri("https://events.example/"), "/internal/result", new(bearer, "tenant"),
                new { eventId = "event" }, 1024, false, CancellationToken.None));

        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task BoundedOwnerReadAcceptsOnlyTheExplicitNoStoreNotFoundCaseAsMissing()
    {
        using var client = new HttpClient(new ResponseHandler(new ByteArrayContent([]), "missing"));
        var bearer = Bearer();

        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<OwnerResponse>(
            client, new Uri("https://events.example/"), "/internal/result", new(bearer, "tenant"),
            new { eventId = "event" }, 1024, true, CancellationToken.None);

        Assert.Null(result);
    }

    private static RuntimeServiceTlsOptions Profile() => new()
    {
        LocalServiceName = "Orchestrator",
        LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
        LocalCertificatePemPath = Path.Combine(Path.GetTempPath(), "unused-service-certificate.pem"),
        LocalPrivateKeyPemPath = Path.Combine(Path.GetTempPath(), "unused-service-private-key.pem"),
        LocalCertificateSha256 = new string('a', 64),
        ListenerPort = 9443,
        PeerServiceName = "EventsAndSessions",
        PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
        PeerOwnerAddress = "https://events.example:9443/",
        PeerCertificateSha256Pins = [new string('b', 64)]
    };

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
        X509Certificate2 issuer,
        RSA key,
        string hostName,
        params string[] enhancedKeyUsages)
    {
        var request = new CertificateRequest(
            $"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName(hostName);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        var usages = new OidCollection();
        foreach (var usage in enhancedKeyUsages)
            usages.Add(new Oid(usage));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            usages, false));
        using var certificate = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    private static SecretCredential Bearer() =>
        new("managed-test-bearer", DateTimeOffset.UtcNow.AddMinutes(5), TimeProvider.System);

    private sealed record OwnerResponse(string Value);

    private sealed class ResponseHandler(HttpContent content, string? fault = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(fault switch
            {
                "redirect" => HttpStatusCode.TemporaryRedirect,
                "denied" => HttpStatusCode.Forbidden,
                "missing" => HttpStatusCode.NotFound,
                _ => HttpStatusCode.OK
            })
            {
                RequestMessage = request,
                Content = content,
                Headers = { CacheControl = new() { NoStore = fault != "cache" } }
            };
            if (fault == "media")
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly Stream _stream;

        public UnknownLengthContent(byte[] bytes) : this(new MemoryStream(bytes)) { }

        public UnknownLengthContent(Stream stream)
        {
            _stream = stream;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
