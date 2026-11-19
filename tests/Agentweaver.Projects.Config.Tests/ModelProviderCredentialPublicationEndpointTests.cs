using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderCredentialPublicationEndpointTests
{
    [Theory]
    [InlineData(true, "POST", "/internal/byok/credential-publications", "valid")]
    [InlineData(false, "POST", "/internal/byok/credential-publications", null)]
    [InlineData(true, "GET", "/INTERNAL/BYOK/CREDENTIAL-PUBLICATIONS///?value=sentinel", "invalid")]
    [InlineData(false, "HEAD", "/internal/byok/credential-publications/", null)]
    [InlineData(true, "PUT", "/internal/byok/credential-publications//", "invalid")]
    [InlineData(false, "DELETE", "/internal/byok/credential-publications", null)]
    [InlineData(true, "PATCH", "/internal/byok/credential-publications", "valid")]
    [InlineData(false, "OPTIONS", "/internal/byok/credential-publications", null)]
    [InlineData(true, "TRACE", "/internal/byok/credential-publications", "invalid")]
    public async Task LegacyRouteFailsBeforeBodyAndNextMiddleware(
        bool enabled, string method, string path, string? authorization)
    {
        var nextCalls = 0;
        var logs = new ConcurrentQueue<string>();
        var accessor = new HttpContextAccessor();
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpContextAccessor>(accessor);
                services.AddLogging(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Information).AddProvider(new CapturingLoggerProvider(logs));
                    builder.AddFilter<CapturingLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
                });
                ModelProviderCredentialPublicationEndpoints.SuppressLegacyRequestLogs(services, accessor);
                services.AddRouting();
                services.AddAuthentication();
                services.AddAuthorization(options => options.AddPolicy(
                    ProjectAuthorizationOwner.BrokerCredentialPublicationAuthorizationPolicy,
                    policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(new ModelProviderDraftService(null!, TimeProvider.System));
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Body = new UnreadableBody();
                    await next(context);
                });
                app.UseRouting();
                app.UseModelProviderCredentialPublicationNoStore();
                app.Use(async (context, next) =>
                {
                    Interlocked.Increment(ref nextCalls);
                    await next(context);
                });
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                    endpoints.MapModelProviderCredentialPublicationEndpoints(enabled));
            });
        }).StartAsync();
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorization}");
        request.Content = new StringContent(new string('x', 16384) + "sentinel", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.Ordinal);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(method == "HEAD" ? "" : """{"code":"credential_publication_unavailable"}""",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, nextCalls);
        Assert.DoesNotContain(logs, message => message.Contains("sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectLegacyPublicationFailsBeforeValidationOrDatabase()
    {
        var service = new ModelProviderDraftService(null!, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<ModelProviderDraftException>(
            () => service.PublishCredentialAsync(null!, null!, CancellationToken.None));
        Assert.Equal("credential_publication_unavailable", failure.Code);
    }

    [Fact]
    public async Task OtherPathsKeepHostingRequestLogs()
    {
        var logs = new ConcurrentQueue<string>();
        var accessor = new HttpContextAccessor();
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpContextAccessor>(accessor);
                services.AddLogging(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Information).AddProvider(new CapturingLoggerProvider(logs));
                });
                ModelProviderCredentialPublicationEndpoints.SuppressLegacyRequestLogs(services, accessor);
            });
            web.Configure(app => app.Run(context => context.Response.WriteAsync("ok")));
        }).StartAsync();
        using var response = await host.GetTestClient().GetAsync("/safe-path");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(logs, message =>
            message.Contains("Request starting", StringComparison.Ordinal) &&
            message.Contains("/safe-path", StringComparison.Ordinal));
    }

    private sealed class UnreadableBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Body read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Body read.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => logs.Enqueue(formatter(state, exception));
    }
}
