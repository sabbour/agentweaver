extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayRetailRateCardTests
{
    [Fact]
    public async Task OpenApiExposesRetailRateCardWriteRouteWithBoundsAndStatuses()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call owners."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var pathItem = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/platform/model-pricing/azure-retail/rate-cards/{id}/{version}");
        var operation = pathItem.GetProperty("put");
        Assert.Equal("savePlatformAzureRetailRuntimeRateCard", operation.GetProperty("operationId").GetString());
        Assert.Equal("Events", operation.GetProperty("x-agentweaver-owner").GetString());
        Assert.Equal("/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}",
            operation.GetProperty("x-agentweaver-owner-path").GetString());
        Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
        Assert.Equal(64 * 1024, operation.GetProperty("x-agentweaver-max-request-body-bytes").GetInt64());
        Assert.Equal(256 * 1024, operation.GetProperty("x-agentweaver-max-response-bytes").GetInt64());

        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "201", "400", "401", "403", "409", "413", "415", "503" })
            Assert.True(responses.TryGetProperty(status, out _), $"Missing status {status}");
        Assert.True(responses.GetProperty("200").GetProperty("headers").TryGetProperty("Cache-Control", out _));
        Assert.True(responses.GetProperty("201").GetProperty("headers").TryGetProperty("Cache-Control", out _));
    }

    [Fact]
    public async Task ProxiesPutToEventsAfterProjectsPrecheckAndExposesEtagAndNoStore()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string canonical = """{"runtimeRateCardSchemaVersion":1}""";
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}\"";

        var callOrder = new List<string>();
        string? observedPath = null;
        string? observedTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(request =>
            {
                callOrder.Add("projects:" + request.RequestUri!.PathAndQuery);
                Assert.Null(request.Content);
                return NoContentNoStore();
            }),
            (owner, request, _) =>
            {
                callOrder.Add("events:" + owner);
                observedPath = request.RequestUri!.PathAndQuery;
                observedTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(canonical, Encoding.UTF8, "application/json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
                return Task.FromResult(response);
            });

        using var request = CreatePutRequest(
            "/api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1",
            bearer,
            ValidBody());
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.Equal(canonical, await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1",
            observedPath);
        Assert.Equal("tenant-1", observedTenant);
        Assert.Equal(
            ["projects:/api/authorization/platform/model-pricing-rate-cards", "events:Events"],
            callOrder);
    }

    [Theory]
    [InlineData("/api/v1/platform/model-pricing/azure-retail/rate-cards/-bad/v1")]
    [InlineData("/api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1?extra=1")]
    [InlineData("/api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v%2F1")]
    public async Task RejectsInvalidKeysQueriesAndEncodedAliasesBeforeProjectsOrEvents(string path)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var projectsCalls = 0;
        var eventsCalls = 0;

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ => { projectsCalls++; return NoContentNoStore(); }),
            (_, _, _) => { eventsCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });

        using var request = CreatePutRequest(path, bearer, ValidBody());
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, projectsCalls);
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task DeniesUnauthorizedUploadBeforeReadingStreamingBodyOverHttp11()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var eventsCalls = 0;

        await using var gateway = await StartHttp11GatewayAsync(
            key,
            () => new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            }),
            (_, _, _) => { eventsCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });

        var address = gateway.Client.BaseAddress!;
        using var tcp = new TcpClient(address.Host, address.Port);
        await using var stream = tcp.GetStream();
        var headers =
            "PUT /api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1 HTTP/1.1\r\n" +
            $"Host: {address.Authority}\r\n" +
            $"Authorization: Bearer {bearer}\r\n" +
            "X-Agentweaver-Tenant: tenant-1\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var statusLine = await reader.ReadLineAsync(readTimeout.Token);
        Assert.Equal("HTTP/1.1 403 Forbidden", statusLine);
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task PropagatesProjects401BeforeReadingStreamingBodyOverHttp11()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var eventsCalls = 0;

        await using var gateway = await StartHttp11GatewayAsync(
            key,
            () => new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            }),
            (_, _, _) => { eventsCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });

        var address = gateway.Client.BaseAddress!;
        using var tcp = new TcpClient(address.Host, address.Port);
        await using var stream = tcp.GetStream();
        var headers =
            "PUT /api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1 HTTP/1.1\r\n" +
            $"Host: {address.Authority}\r\n" +
            $"Authorization: Bearer {bearer}\r\n" +
            "X-Agentweaver-Tenant: tenant-1\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var statusLine = await reader.ReadLineAsync(readTimeout.Token);
        var responseText = await ReadHeadersAndBodyPrefixAsync(reader, readTimeout.Token);
        Assert.Equal("HTTP/1.1 401 Unauthorized", statusLine);
        Assert.Contains("projects_authorization_unauthorized", responseText, StringComparison.Ordinal);
        Assert.Equal(0, eventsCalls);
    }

    private static async Task<string> ReadHeadersAndBodyPrefixAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[1024];
        while (sb.Length < 4096)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;
            sb.Append(buffer, 0, read);
            if (sb.ToString().Contains("projects_authorization_unauthorized", StringComparison.Ordinal))
                break;
        }
        return sb.ToString();
    }

    [Fact]
    public async Task RejectsStreamedBodyOver64KiBWith413()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ => NoContentNoStore()),
            async (_, request, ct) =>
            {
                await request.Content!.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Created);
            });

        var oversized = new string(' ', 64 * 1024 + 1);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/v1/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1")
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(oversized))),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static HttpRequestMessage CreatePutRequest(string path, string bearer, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        return request;
    }

    private static string ValidBody() => """
        {
          "region": "eastus",
          "modelId": "gpt-4o",
          "input": { "meterId": "meter-in", "expectedUsdPer1M": 2.5 },
          "output": { "meterId": "meter-out", "expectedUsdPer1M": 10 }
        }
        """;

    private static HttpResponseMessage NoContentNoStore() =>
        new(HttpStatusCode.NoContent)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    private static string CreateToken(
        RsaSecurityKey signingKey,
        string scopes = "api.read projects.admin")
    {
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience: "https://api.test",
            claims:
            [
                new("sub", "33333333-3333-3333-3333-333333333333"),
                new("scope", scopes),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<Http11GatewayHost> StartHttp11GatewayAsync(
        RsaSecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandlerFactory,
        Func<GatewayOwner, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> eventsHandler)
    {
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer);
        const string audience = "https://api.test";
        var options = new GatewayOptions(
            issuer,
            audience,
            new Uri("https://projects.test/"),
            new Uri("https://orchestrator.test/"),
            new Uri("https://knowledge.test/"),
            new Uri("https://events.test/"),
            new Uri("https://identity-broker.test/"),
            new Uri("https://web.test/"),
            TimeSpan.FromSeconds(10));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        builder.Logging.ClearProviders();

        var requestContext = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(requestContext);
        GatewayEndpoints.SuppressTargetContextRequestLogs(builder.Services, requestContext);
        builder.Services.AddSingleton(options);
        GatewayWebCors.AddGatewayWebCors(builder.Services, options);
        builder.Services.AddHttpClient(GatewayOwner.Projects.ToString(), client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(projectsHandlerFactory);
        foreach (var owner in Enum.GetValues<GatewayOwner>().Where(owner => owner != GatewayOwner.Projects))
        {
            builder.Services.AddHttpClient(owner.ToString(), client =>
                    client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((request, ct) =>
                    eventsHandler(owner, request, ct)));
        }
        builder.Services.AddSingleton<GatewayOwnerClient>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication =>
            {
                authentication.MapInboundClaims = false;
                authentication.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer.AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, GatewayAuthorizationResultHandler>();

        var app = builder.Build();
        app.UseRouting();
        GatewayEndpoints.UseTargetContextGuard(app);
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        GatewayEndpoints.MapGatewayEndpoints(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new Http11GatewayHost(app, client);
    }

    private sealed class Http11GatewayHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> syncHandler)
            : this((request, _) => Task.FromResult(syncHandler(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
