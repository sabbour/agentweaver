extern alias OrchestratorHost;

using System.Security.Claims;
using System.Security.Authentication;
using System.Text.Encodings.Web;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class ReviewedRemoteToolResultUnavailableTests
{
    [Theory]
    [InlineData(false, System.Net.HttpStatusCode.ServiceUnavailable, "runtime_remote_tool_result_route_unavailable")]
    [InlineData(true, System.Net.HttpStatusCode.Forbidden, "runtime_remote_tool_result_https_required")]
    public async Task HttpsFlagWithoutNativeTlsCannotReadProofOrLoadTheServiceCertificate(
        bool configured, System.Net.HttpStatusCode expectedStatus, string expectedCode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var certificateResolved = false;
        if (configured)
        {
            builder.Services.AddSingleton(new RuntimeServiceTlsOptions
            {
                LocalServiceName = "Orchestrator",
                LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
                LocalCertificatePemPath = Path.Combine(Path.GetTempPath(), "unused-service.pem"),
                LocalPrivateKeyPemPath = Path.Combine(Path.GetTempPath(), "unused-service-key.pem"),
                LocalCertificateSha256 = new string('a', 64),
                ListenerPort = 9443,
                PeerServiceName = "EventsAndSessions",
                PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
                PeerOwnerAddress = "https://events.example:9443/",
                PeerCertificateSha256Pins = [new string('b', 64)]
            });
            builder.Services.AddSingleton<RuntimeServiceTlsIdentity>(_ =>
            {
                certificateResolved = true;
                throw new InvalidOperationException("The native-TLS denial must precede certificate loading.");
            });
        }
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = "https";
            context.Connection.LocalPort = 9443;
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        OrchestratorHost::Agentweaver.Orchestrator.ReviewedRemoteToolResultEndpoints
            .MapReviewedRemoteToolResultEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/runtime/remote-tool-results/verify")
        {
            Content = new ByteArrayContent("private proof"u8.ToArray())
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetString());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
        Assert.False(certificateResolved);
    }

    [Fact]
    public async Task OrchestratorVerifierRejectsHttpBeforeReadingProofBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        OrchestratorHost::Agentweaver.Orchestrator.ReviewedRemoteToolResultEndpoints
            .MapReviewedRemoteToolResultEndpoints(app);
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/internal/runtime/remote-tool-results/verify")
        {
            Content = new ByteArrayContent("private proof"u8.ToArray())
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_remote_tool_result_https_required",
            document.RootElement.GetProperty("error").GetString());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
    }

    [Theory]
    [InlineData(false, null, 9446, "Environment", System.Net.HttpStatusCode.ServiceUnavailable,
        "runtime_remote_tool_result_https_required")]
    [InlineData(true, null, 9446, "Environment", System.Net.HttpStatusCode.Forbidden,
        "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.Tls13, 9446, "IdentityBroker", System.Net.HttpStatusCode.Forbidden,
        "runtime_remote_mcp_call_service_profile_invalid")]
    [InlineData(true, SslProtocols.Tls13, 9447, "Environment", System.Net.HttpStatusCode.Forbidden,
        "runtime_remote_tool_result_service_listener_denied")]
    [InlineData(true, null, 9443, "Environment", System.Net.HttpStatusCode.Forbidden,
        "runtime_remote_tool_result_https_required")]
    public async Task EnvironmentProfileCannotUsePlainHttpsAnotherRoleOrAnotherListener(
        bool https, SslProtocols? protocol, int port, string peer,
        System.Net.HttpStatusCode expectedStatus, string expectedCode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Orchestrator",
            PeerServiceName = "EventsAndSessions",
            ListenerPort = 9443
        });
        var key = OrchestratorHost::Agentweaver.Orchestrator.RemoteMcpCallVerificationComposition.TlsKey;
        builder.Services.AddKeyedSingleton(key, new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Orchestrator",
            PeerServiceName = peer,
            ListenerPort = 9446
        });
        var certificateResolved = false;
        builder.Services.AddSingleton<RuntimeServiceTlsIdentity>(_ =>
        {
            certificateResolved = true;
            throw new InvalidOperationException("Denied requests must not resolve the Events certificate.");
        });
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(key, (_, _) =>
        {
            certificateResolved = true;
            throw new InvalidOperationException("Denied requests must not resolve the Environment certificate.");
        });
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = https ? "https" : "http";
            context.Connection.LocalPort = port;
            if (protocol is { } value)
                context.Features.Set<ITlsHandshakeFeature>(new Handshake(value));
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        OrchestratorHost::Agentweaver.Orchestrator.ReviewedRemoteToolResultEndpoints
            .MapReviewedRemoteToolResultEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/internal/runtime/remote-tool-results/verify", new ByteArrayContent("private proof"u8.ToArray()));

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetString());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
        Assert.False(certificateResolved);
    }

    private sealed class Handshake(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058 // ITlsHandshakeFeature still requires the obsolete algorithm properties.
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public System.Net.Security.TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "test-actor")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class UnexpectedBodyReadStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Fail<int>();
        public override int Read(Span<byte> buffer) => Fail<int>();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) => Fail<ValueTask<int>>();
        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Fail<Task<int>>();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private T Fail<T>()
        {
            WasRead = true;
            throw new InvalidOperationException("The disabled verifier route read the request body.");
        }
    }
}
