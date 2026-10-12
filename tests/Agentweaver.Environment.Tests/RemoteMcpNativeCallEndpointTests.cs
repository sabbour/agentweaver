using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpNativeCallEndpointTests
{
    [Fact]
    public async Task DispatchRouteIsOffWithoutAnExplicitOrchestratorPeerProfile()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var verifierResolutions = 0;
        builder.Services.AddSingleton<RemoteMcpCurrentCallVerificationService>(_ =>
        {
            verifierResolutions++;
            throw new InvalidOperationException("A denied request must not create the verifier.");
        });
        await using var app = builder.Build();
        app.MapRemoteMcpNativeCallDispatch();
        var endpoint = SingleEndpoint(app);
        var context = Context(app.Services, 9448, "{");
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("remote_mcp_reviewed_tool_recipient_unavailable", await ReadCodeAsync(response));
        Assert.Equal(0, verifierResolutions);
    }

    [Fact]
    public async Task OutboundOrchestratorClientProfileDoesNotAuthorizeRecipientRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddKeyedSingleton(RemoteMcpTrustedServiceTlsComposition.OrchestratorTlsKey,
            new RuntimeServiceTlsOptions
            {
                LocalServiceName = "Environment",
                PeerServiceName = "Orchestrator",
                ListenerPort = 9448
            });
        var certificateResolutions = 0;
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
            RemoteMcpTrustedServiceTlsComposition.OrchestratorTlsKey,
            (_, _) =>
            {
                certificateResolutions++;
                throw new InvalidOperationException("Outbound TLS must not authorize recipient requests.");
            });
        var verifierResolutions = 0;
        builder.Services.AddSingleton<RemoteMcpCurrentCallVerificationService>(_ =>
        {
            verifierResolutions++;
            throw new InvalidOperationException("A denied request must not create the verifier.");
        });
        await using var app = builder.Build();
        app.MapRemoteMcpNativeCallDispatch();
        var endpoint = SingleEndpoint(app);
        var context = Context(app.Services, 9448, "{");
        var body = new CountingReadStream(System.Text.Encoding.UTF8.GetBytes("{"));
        context.Request.Body = body;
        context.Request.ContentLength = null;
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("remote_mcp_reviewed_tool_recipient_unavailable", await ReadCodeAsync(response));
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(0, certificateResolutions);
        Assert.Equal(0, verifierResolutions);
    }

    [Fact]
    public async Task WrongOrchestratorListenerDeniesBeforeCertificateOrVerifierResolution()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddKeyedSingleton(RemoteMcpTrustedServiceTlsComposition.OrchestratorRecipientTlsKey,
            new RuntimeServiceTlsOptions
            {
                LocalServiceName = "Environment",
                PeerServiceName = "Orchestrator",
                ListenerPort = 9448
            });
        var certificateResolutions = 0;
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
            RemoteMcpTrustedServiceTlsComposition.OrchestratorRecipientTlsKey,
            (_, _) =>
            {
                certificateResolutions++;
                throw new InvalidOperationException("A wrong listener must not load the TLS identity.");
            });
        var verifierResolutions = 0;
        builder.Services.AddSingleton<RemoteMcpCurrentCallVerificationService>(_ =>
        {
            verifierResolutions++;
            throw new InvalidOperationException("A denied request must not create the verifier.");
        });
        await using var app = builder.Build();
        app.MapRemoteMcpNativeCallDispatch();
        var endpoint = SingleEndpoint(app);
        var context = Context(app.Services, 9449, "{");
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("runtime_remote_tool_result_service_listener_denied", await ReadCodeAsync(response));
        Assert.Equal(0, certificateResolutions);
        Assert.Equal(0, verifierResolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthBodyReadsAtMostOneBytePastMaximumEnvelope(bool readOnlyFeature)
    {
        var context = new DefaultHttpContext();
        var maximumBytes = Agentweaver.Abstractions.RemoteMcpNativeCallContract.MaximumEnvelopeBytes;
        var input = new byte[maximumBytes + 1];
        var body = new CountingReadStream(input);
        context.Request.Body = body;
        Assert.Null(context.Request.ContentLength);
        if (readOnlyFeature)
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new ReadOnlyRequestBodySizeFeature());

        var result = await RemoteMcpNativeCallEndpoints.ReadBoundedBodyAsync(
            context, maximumBytes, CancellationToken.None);

        Assert.Equal(maximumBytes + 1, result.Length);
        Assert.Equal(maximumBytes + 1, body.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthBodyAtMaximumEnvelopeIsFullyReturned(bool readOnlyFeature)
    {
        var context = new DefaultHttpContext();
        var input = new byte[Agentweaver.Abstractions.RemoteMcpNativeCallContract.MaximumEnvelopeBytes];
        var body = new CountingReadStream(input);
        context.Request.Body = body;
        Assert.Null(context.Request.ContentLength);
        if (readOnlyFeature)
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new ReadOnlyRequestBodySizeFeature());

        var result = await RemoteMcpNativeCallEndpoints.ReadBoundedBodyAsync(
            context, input.Length, CancellationToken.None);

        Assert.Equal(input, result);
        Assert.Equal(input.Length, body.BytesRead);
    }

    private static RouteEndpoint SingleEndpoint(IEndpointRouteBuilder app) =>
        Assert.Single(app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());

    private static DefaultHttpContext Context(IServiceProvider services, int localPort, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.IsHttps = true;
        context.Connection.LocalPort = localPort;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = System.Text.Encoding.UTF8.GetByteCount(body);
        context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshake(SslProtocols.Tls13));
        return context;
    }

    private static async Task<string?> ReadCodeAsync(MemoryStream response)
    {
        response.Position = 0;
        using var result = await JsonDocument.ParseAsync(response);
        return result.RootElement.GetProperty("code").GetString();
    }

    private sealed class TestTlsHandshake(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private sealed class ReadOnlyRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public long? MaxRequestBodySize { get; set; } = 1;
        public bool IsReadOnly => true;
    }

    private sealed class CountingReadStream(byte[] content) : MemoryStream(content)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }
    }
}
