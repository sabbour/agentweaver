extern alias GatewayHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayTargetContextTests
{
    private const string Actor = "target-context-actor";
    private const string Tenant = "target-context-tenant";
    private const string Canary = "12345678-1234-1234-1234-123456789abc";
    private const string PublicPath = "/api/v1/authorization/target-context";
    private const string Valid = """
        {"contractVersion":1,"issuer":"https://broker.test/","actorId":"target-context-actor","tenantId":"target-context-tenant","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[{"resourceType":"project","resourceId":"p","permissions":[{"permission":"cancelRun","roleRevision":1}]}],"installationId":"12345678-1234-1234-1234-123456789abc"}
        """;

    [Fact]
    public void TargetContextDatabaseLogsRemoveExceptionDetails()
    {
        using var logs = new TargetLogs();
        using var underlying = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var safe = new PlatformPolicyReadLoggerFactory(underlying);
        var logger = safe.CreateLogger("fixture");
        using (PlatformPolicyReadLoggerFactory.BeginTargetContextRead())
            logger.LogError(new InvalidOperationException(Canary), "Sensitive {Id}", Canary);
        logger.LogInformation("Normal log");
        Assert.DoesNotContain(Canary, logs.Text);
        Assert.Contains("Target-context read database event.", logs.Text);
        Assert.Contains("Normal log", logs.Text);
    }

    [Fact]
    public async Task GatewayTargetContextValidatesAndSanitizesOwnerResponses()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var captured = new List<(string? Bearer, string? Tenant)>();
        var queue = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>();
        using var logs = new TargetLogs();
        await using var gateway = await GatewayResourceServer.StartAsync(key,
            () => new OwnerHandler((request, token) =>
            {
                captured.Add((request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var values) ? values.Single() : null));
                return queue.Dequeue()(token);
            }), (_, _, _) => throw new InvalidOperationException(), ownerRequestTimeout: TimeSpan.FromMilliseconds(100),
            loggerProvider: logs);
        var bearer = Token(key);
        async Task AssertReply(Func<CancellationToken, Task<HttpResponseMessage>> owner, int status, string? code)
        {
            queue.Enqueue(owner);
            using var request = Request(bearer);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            var body = await response.Content.ReadAsStringAsync();
            if (code is null)
                Assert.Equal(Canary, JsonDocument.Parse(body).RootElement.GetProperty("installationId").GetString());
            else
            {
                Assert.Equal($"{{\"type\":\"about:blank\",\"title\":\"{code}\",\"status\":{status},\"code\":\"{code}\"}}", body);
                Assert.DoesNotContain(Canary, body);
                Assert.DoesNotContain(Canary, response.Headers.ToString());
            }
        }

        await AssertReply(_ => Task.FromResult(Response(200, Valid)), 200, null);
        await AssertReply(_ => Task.FromResult(Response(403, $"secret {Canary}")), 403, "target_context_forbidden");
        await AssertReply(_ => Task.FromResult(Response(401, Canary)), 401, "authentication_required");
        await AssertReply(_ => Task.FromResult(Response(503, Canary)), 503, "target_context_unavailable");
        await AssertReply(_ => Task.FromResult(Response(500, Canary)), 502, "owner_unavailable");
        await AssertReply(_ => Task.FromResult(Response(302, Canary)), 502, "owner_contract_invalid");
        await AssertReply(_ => Task.FromResult(Response(200, Valid, noStore: false)), 502, "owner_contract_invalid");
        await AssertReply(_ => Task.FromResult(Response(200, Valid, mediaType: "text/plain")), 502, "owner_contract_invalid");
        await AssertReply(_ => Task.FromResult(Response(200, new string('x', 65537))), 502, "owner_contract_invalid");
        await AssertReply(_ => Task.FromResult(Response(200, "{")), 502, "owner_contract_invalid");
        await AssertReply(_ => throw new HttpRequestException(), 502, "owner_unavailable");
        await AssertReply(_ => throw new InvalidOperationException(Canary), 502, "owner_unavailable");
        await AssertReply(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return Response(200, Valid);
        }, 504, "owner_timeout");

        foreach (var invalid in new[]
        {
            Valid.Replace("\"contractVersion\":1", "\"contractVersion\":2"),
            Valid.Replace("\"contractVersion\":1", "\"contractVersion\":1,\"contractVersion\":1"),
            Valid.Replace("\"issuer\":\"https://broker.test/\"", "\"issuer\":\"https://other.test/\""),
            Valid.Replace(Actor, "other-actor"),
            Valid.Replace(Tenant, "other-tenant"),
            Valid.Replace("\"boundProjectId\":null", "\"boundProjectId\":\"p\""),
            Valid.Replace(Canary, Canary.ToUpperInvariant()),
            Valid.Replace(Canary, "00000000-0000-0000-0000-000000000000"),
            Valid.Replace("\"roleRevision\":1", "\"roleRevision\":0"),
            Valid.Replace("\"roleRevision\":1", "\"roleRevision\":1,\"extra\":true"),
            Valid.Replace("\"permission\":\"cancelRun\",", ""),
            Valid.Replace("\"permission\":\"cancelRun\"", "\"permission\":\"cancelRun\",\"permission\":\"cancelRun\""),
            Valid.Replace("\"permission\":\"cancelRun\"", "\"permission\":\"CancelRun\""),
            Valid.Replace("\"permission\":\"cancelRun\"", "\"permission\":\"\\uD800\""),
            Valid.Replace("\"permission\":\"cancelRun\"", "\"\\uD800\":\"cancelRun\""),
            Valid.Replace("\"resourceId\":\"p\"", "\"resourceId\":\"\\uD800\""),
            Valid.Replace("\"actorId\":\"" + Actor + "\"", "\"actorId\":\"\\uD800\""),
            Valid.Replace("\"resourceType\":\"project\"", "\"resourceType\":\"other\""),
            Valid.Replace("\"resourceId\":\"p\"", "\"resourceId\":\"invalid!\""),
            Valid.Replace("\"resourceId\":\"p\"", "\"resourceId\":\"p\",\"unexpected\":true"),
            Valid.Replace("\"permissions\":[", "\"permissions\":[null,"),
            Valid.Replace("\"membershipRevision\":1", "\"membershipRevision\":\"1\""),
            Valid.Replace("\"installationId\":\"" + Canary + "\"", "\"installationId\":42"),
        })
            await AssertReply(_ => Task.FromResult(Response(200, invalid)), 502, "owner_contract_invalid");

        Assert.All(captured, pair =>
        {
            Assert.Equal(bearer, pair.Bearer);
            Assert.Equal(Tenant, pair.Tenant);
        });
        using var missing = await gateway.Client.GetAsync(PublicPath);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal("Bearer", missing.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("authentication_required", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        foreach (var token in new[] { Token(key, scopes: "projects.admin"), Token(key, binding: "project_id"),
                     Token(key, binding: "run_id"), Token(key, binding: "purpose") })
        {
            using var request = Request(token);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("target_context_forbidden", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("code").GetString());
        }
        using var invalidSelector = Request(bearer);
        invalidSelector.Headers.Remove("X-Agentweaver-Tenant");
        invalidSelector.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "invalid@tenant");
        using var invalidSelectorResponse = await gateway.Client.SendAsync(invalidSelector);
        Assert.Equal(HttpStatusCode.Forbidden, invalidSelectorResponse.StatusCode);
        Assert.Equal("target_context_forbidden",
            JsonDocument.Parse(await invalidSelectorResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Empty(queue);
        using var query = Request(bearer);
        query.RequestUri = new Uri(PublicPath + "/?leak=" + Canary, UriKind.Relative);
        using var queryResponse = await gateway.Client.SendAsync(query);
        Assert.Equal(HttpStatusCode.BadRequest, queryResponse.StatusCode);
        Assert.True(queryResponse.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(Canary, logs.Text);
        Assert.DoesNotContain(bearer, logs.Text);
    }

    [Fact]
    public async Task ProductionGatewayTargetContextEnforcesCorsPreflightsAndRequestShape()
    {
        using var rsa = RSA.Create(2048);
        var ownerCalls = 0;
        await using var gateway = GatewayProductionResourceServer.Start(
            new RsaSecurityKey(rsa), (_, _) =>
            {
                ownerCalls++;
                return Task.FromResult(Response(200, Valid));
            }, identityAudience: "https://api.test");

        async Task<HttpResponseMessage> Preflight(string origin, string method, string headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, PublicPath);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", headers);
            return await gateway.Client.SendAsync(request);
        }

        using (var response = await Preflight("https://web.test", "GET", "authorization,x-agentweaver-tenant"))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("https://web.test", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
            var allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
            Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x-agentweaver-tenant", allowedHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        using (var response = await Preflight("https://denied.test", "GET", "authorization"))
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        using (var response = await Preflight("https://web.test", "POST", "authorization"))
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        using (var response = await Preflight("https://web.test", "GET", "x-unapproved-header"))
        {
            Assert.DoesNotContain("x-unapproved-header",
                response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }

        foreach (var (method, header) in new[]
        {
            (HttpMethod.Options, (string?)null),
            (HttpMethod.Options, "Origin"),
            (HttpMethod.Options, "Access-Control-Request-Method"),
            (HttpMethod.Post, (string?)null),
            (HttpMethod.Head, (string?)null),
        })
        {
            using var request = new HttpRequestMessage(method, PublicPath);
            if (header == "Origin") request.Headers.Add(header, "https://web.test");
            if (header == "Access-Control-Request-Method") request.Headers.Add(header, "GET");
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            if (method != HttpMethod.Head)
                Assert.Equal("""{"type":"about:blank","title":"request_shape_invalid","status":400,"code":"request_shape_invalid"}""",
                    await response.Content.ReadAsStringAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Options, PublicPath + "?id=" + Canary))
        {
            request.Headers.Add("Origin", "https://web.test");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.DoesNotContain(Canary, await response.Content.ReadAsStringAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, PublicPath))
        {
            request.Headers.Add("Origin", "https://web.test");
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.DoesNotContain(Canary, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, ownerCalls);
    }

    [Fact]
    public async Task ProductionGatewayRejectsUnpairedSurrogateInOwnerResponse()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var gateway = GatewayProductionResourceServer.Start(
            key, (_, _) => Task.FromResult(Response(200,
                Valid.Replace("\"permission\":\"cancelRun\"", "\"permission\":\"\\uD800\""))),
            identityAudience: "https://api.test");
        using var request = Request(Token(key));
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("""{"type":"about:blank","title":"owner_contract_invalid","status":502,"code":"owner_contract_invalid"}""",
            await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PublicPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Agentweaver-Tenant", Tenant);
        return request;
    }

    private static string Token(SecurityKey key, string scopes = "api.read", string? binding = null)
    {
        var claims = new List<Claim> { new("sub", Actor), new("scope", scopes) };
        if (binding is not null) claims.Add(new Claim(binding, "bound"));
        var token = new JwtSecurityToken(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage Response(int status, string body, bool noStore = true,
        string mediaType = "application/json")
    {
        var result = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
        if (noStore) result.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return result;
    }

    private sealed class OwnerHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class TargetLogs : ILoggerProvider
    {
        private readonly StringBuilder _text = new();
        public string Text { get { lock (_text) return _text.ToString(); } }
        public ILogger CreateLogger(string categoryName) => new TargetLogger(_text);
        public void Dispose() { }

        private sealed class TargetLogger(StringBuilder text) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (text) text.AppendLine(formatter(state, exception)).AppendLine(exception?.ToString());
            }
        }
    }
}
