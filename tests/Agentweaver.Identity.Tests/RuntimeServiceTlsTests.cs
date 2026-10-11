using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeServiceTlsTests
{
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
