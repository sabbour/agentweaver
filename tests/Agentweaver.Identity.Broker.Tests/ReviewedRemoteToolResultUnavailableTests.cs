extern alias OrchestratorHost;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
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
