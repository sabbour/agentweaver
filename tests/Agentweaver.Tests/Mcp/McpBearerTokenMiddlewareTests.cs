using System.IO;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Mcp;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="McpBearerTokenMiddleware"/>.
///
/// Coverage map:
///   S1 (partial) — Verifies the current 401 shape; after T6 new assertions check
///                  WWW-Authenticate header with resource_metadata.
///   S4            — Static API key path removed; only OAuth JWT + GitHub passthrough remain.
///
/// Tests labelled [Skip] need Tank's T6 (MCP RS changes) before they can be enabled.
///
/// Seraph design ref: §2 (WWW-Authenticate), §6 (backward compat).
/// </summary>
public sealed class McpBearerTokenMiddlewareTests
{
    private const string HealthPath  = "/healthz";
    private const string McpPath     = "/mcp";

    // =========================================================================
    // S4-03 / S1-01 — Missing Authorization header → 401
    //                 Current behavior: no WWW-Authenticate header (pre-T6).
    //                 After T6 see S1-02 below.
    // =========================================================================
    [Fact]
    public async Task NoAuthorizationHeader_Returns401_WithJsonBody()
    {
        var middleware = BuildMiddleware(nextStatusCode: 200);
        var context = MakeContext(McpPath, bearerToken: null);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        var body = ReadBody(context);
        body.Should().Contain("error", "401 body must be a JSON object with an error field");
    }

    // =========================================================================
    // S4-04 / S1-03 — Unknown bearer token (not an API key, GitHub call fails) → 401
    //                 GitHub HTTP call in this test always returns 401.
    // =========================================================================
    [Fact]
    public async Task UnknownBearerToken_GitHubValidationFails_Returns401()
    {
        // GitHub /user returns 401 for the unknown token.
        var githubHandler = new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized);
        var middleware = BuildMiddleware(nextStatusCode: 200, githubHandler: githubHandler);
        var context = MakeContext(McpPath, bearerToken: "unknown-token-xyz");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // =========================================================================
    // S1-02 — /healthz is exempt from authentication
    // =========================================================================
    [Fact]
    public async Task HealthzPath_BypassesAuth_Returns200()
    {
        var middleware = BuildMiddleware(nextStatusCode: 200);
        // No bearer token — if auth were applied this would return 401.
        var context = MakeContext(HealthPath, bearerToken: null);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200,
            "/healthz must bypass bearer-token authentication");
    }

    // =========================================================================
    // S4-05 — An unknown bearer token (GitHub validation fails) → 401
    // =========================================================================
    [Fact]
    public async Task UnknownBearer_WithLowercaseScheme_Rejected()
    {
        var githubHandler = new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized);
        var middleware = BuildMiddleware(nextStatusCode: 200, githubHandler: githubHandler);
        var context = MakeContext(McpPath);
        context.Request.Headers.Authorization = "bearer some-unknown-token";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized,
            "no static key path remains; an unknown token must fail GitHub validation");
    }

    [Fact]
    public async Task EntraBearer_WithValidIssuerAudienceTenant_PassesThroughAndIsForwardable()
    {
        using var rsa = RSA.Create(2048);
        var fixture = CreateEntraFixture(rsa);
        string? forwardedToken = null;
        RequestDelegate next = context =>
        {
            forwardedToken = context.Items["mcp.bearer_token"] as string;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };
        var middleware = BuildMiddleware(
            next,
            configValues: fixture.Configuration);
        var context = MakeContext(McpPath, fixture.CreateToken());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        forwardedToken.Should().Be(context.Request.Headers.Authorization.ToString()["Bearer ".Length..],
            "MCP must forward the same validated Entra bearer so the API can independently validate and authorize it");
        context.Items["mcp.user"].Should().Be(fixture.ObjectId);
    }

    [Theory]
    [InlineData("wrong-issuer", false, true)]
    [InlineData("wrong-audience", true, false)]
    [InlineData("wrong-tenant", true, true)]
    public async Task EntraBearer_WithMismatchedTrustBoundary_Returns401(
        string mismatch,
        bool validIssuer,
        bool validAudience)
    {
        using var rsa = RSA.Create(2048);
        var fixture = CreateEntraFixture(rsa);
        var middleware = BuildMiddleware(
            nextStatusCode: 200,
            githubHandler: new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized),
            configValues: fixture.Configuration);
        var token = fixture.CreateToken(
            issuer: validIssuer ? fixture.Issuer : "https://invalid.example/tenant/v2.0",
            audience: validAudience ? fixture.ClientId : "different-client-id",
            tenantId: mismatch == "wrong-tenant" ? "different-tenant-id" : fixture.TenantId);
        var context = MakeContext(McpPath, token);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized,
            "issuer, audience, and tenant binding must remain fail-closed for forwarded Entra credentials");
        context.Items.Should().NotContainKey("mcp.bearer_token");
    }

    [Fact]
    public async Task EntraBearer_WithUntrustedSignature_Returns401()
    {
        using var trustedRsa = RSA.Create(2048);
        using var untrustedRsa = RSA.Create(2048);
        var trustedFixture = CreateEntraFixture(trustedRsa);
        var untrustedFixture = CreateEntraFixture(untrustedRsa);
        var middleware = BuildMiddleware(
            nextStatusCode: 200,
            githubHandler: new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized),
            configValues: trustedFixture.Configuration);
        var context = MakeContext(McpPath, untrustedFixture.CreateToken());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Items.Should().NotContainKey("mcp.bearer_token");
    }

    [Fact]
    public async Task EntraBearer_WhenExpired_Returns401()
    {
        using var rsa = RSA.Create(2048);
        var fixture = CreateEntraFixture(rsa);
        var middleware = BuildMiddleware(
            nextStatusCode: 200,
            githubHandler: new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized),
            configValues: fixture.Configuration);
        var context = MakeContext(
            McpPath,
            fixture.CreateToken(
                issuedAt: DateTime.UtcNow.AddMinutes(-10),
                expires: DateTime.UtcNow.AddMinutes(-5)));

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Items.Should().NotContainKey("mcp.bearer_token");
    }

    // =========================================================================
    // S1-04 (STUB — requires Tank T6)
    // After T6: missing-token 401 must include WWW-Authenticate header with:
    //   Bearer realm="agentweaver-mcp", resource_metadata="https://{HOST}/.well-known/oauth-protected-resource"
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 (McpBearerTokenMiddleware: add WWW-Authenticate header) not yet implemented")]
    public async Task NoToken_Returns401_WithWwwAuthenticateHeader_ContainingResourceMetadata()
    {
        var middleware = BuildMiddleware(nextStatusCode: 200);
        var context = MakeContext(McpPath, bearerToken: null);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        var wwwAuth = context.Response.Headers["WWW-Authenticate"].ToString();
        wwwAuth.Should().Contain("Bearer",           "scheme must be Bearer");
        wwwAuth.Should().Contain("resource_metadata=", "resource_metadata param required by RFC 9728 §5.1");
        wwwAuth.Should().Contain("/.well-known/oauth-protected-resource",
            "must point at the protected-resource metadata document");
    }

    // =========================================================================
    // S1-05 (STUB — requires Tank T6)
    // Invalid/expired token 401 must include error="invalid_token" in WWW-Authenticate.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — error=\"invalid_token\" in WWW-Authenticate for malformed JWT")]
    public async Task InvalidToken_Returns401_WithInvalidTokenError_InWwwAuthenticate()
    {
        var githubHandler = new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized);
        var middleware = BuildMiddleware(nextStatusCode: 200, githubHandler: githubHandler);
        var context = MakeContext(McpPath, bearerToken: "definitely.not.a.valid.jwt");

        await middleware.InvokeAsync(context);

        var wwwAuth = context.Response.Headers["WWW-Authenticate"].ToString();
        wwwAuth.Should().Contain("error=\"invalid_token\"");
    }

    // =========================================================================
    // S2-01 (STUB — requires Tank T2 + T6)
    // A valid Agentweaver-minted JWT (correct iss/aud/exp/sig) must be accepted.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T2 (McpTokenService), T6 (JWT validation in McpBearerTokenMiddleware)")]
    public async Task ValidAgentWeaverJwt_PassesThrough()
    {
        // When T6 lands: mint a test JWT via McpTokenService (test-only RSA key),
        // send it as Bearer, assert 200 and mcp.user is the sub claim.
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-02 (STUB — requires Tank T2 + T6)
    // A JWT with a tampered signature must be rejected 401.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T2 + T6 — tampered JWT signature must be rejected")]
    public async Task TamperedJwtSignature_Returns401()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-03 (STUB — requires Tank T2 + T6)
    // A JWT with wrong audience (aud ≠ TestAudience) must be rejected 401.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — wrong aud must be rejected (audience binding, RFC 8707)")]
    public async Task WrongAudience_Returns401()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private McpBearerTokenMiddleware BuildMiddleware(
        int nextStatusCode = 200,
        HttpMessageHandler? githubHandler = null,
        IReadOnlyDictionary<string, string?>? configValues = null)
    {
        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = nextStatusCode;
            return Task.CompletedTask;
        };
        return BuildMiddleware(next, githubHandler, configValues);
    }

    private McpBearerTokenMiddleware BuildMiddleware(
        RequestDelegate next,
        HttpMessageHandler? githubHandler = null,
        IReadOnlyDictionary<string, string?>? configValues = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        var cache    = new MemoryCache(new MemoryCacheOptions());
        var handler  = githubHandler ?? new FixedStatusHttpMessageHandler(HttpStatusCode.Unauthorized);
        var factory  = new SingleClientHttpClientFactory(handler);
        var validator = new McpAccessTokenValidator(
            config,
            cache,
            factory,
            NullLogger<McpAccessTokenValidator>.Instance);
        var entraValidator = new McpEntraAccessTokenValidator(
            config,
            factory,
            NullLogger<McpEntraAccessTokenValidator>.Instance);

        return new McpBearerTokenMiddleware(
            next,
            validator,
            entraValidator,
            config,
            cache,
            factory,
            NullLogger<McpBearerTokenMiddleware>.Instance);
    }

    private static EntraFixture CreateEntraFixture(RSA rsa)
    {
        const string tenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
        const string clientId = "11111111-2222-3333-4444-555555555555";
        const string issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        const string objectId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var key = new RsaSecurityKey(rsa) { KeyId = "mcp-entra-test-key" };
        var parameters = rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = key.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });

        return new EntraFixture(
            tenantId,
            clientId,
            issuer,
            objectId,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            new Dictionary<string, string?>
            {
                ["Auth:Mode"] = "Entra",
                ["Auth:Entra:TenantId"] = tenantId,
                ["Auth:Entra:ClientId"] = clientId,
                ["Auth:Entra:Issuer"] = issuer,
                ["Auth:Entra:JwksJson"] = jwks,
                ["Auth:Mcp:AllowGitHubPassthrough"] = "true",
            });
    }

    private static DefaultHttpContext MakeContext(string path, string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path  = path;
        context.Response.Body = new MemoryStream();
        if (bearerToken is not null)
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    // -------------------------------------------------------------------------
    // Test infrastructure helpers
    // -------------------------------------------------------------------------

    /// <summary>Always returns the same status code for any request.</summary>
    private sealed class FixedStatusHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public FixedStatusHttpMessageHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    /// <summary>Returns the same handler for every named client.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed record EntraFixture(
        string TenantId,
        string ClientId,
        string Issuer,
        string ObjectId,
        SigningCredentials SigningCredentials,
        IReadOnlyDictionary<string, string?> Configuration)
    {
        public string CreateToken(
            string? issuer = null,
            string? audience = null,
            string? tenantId = null,
            DateTime? issuedAt = null,
            DateTime? expires = null) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer ?? Issuer,
                Audience = audience ?? ClientId,
                Subject = new ClaimsIdentity(
                [
                    new Claim("oid", ObjectId),
                    new Claim("tid", tenantId ?? TenantId),
                ]),
                IssuedAt = issuedAt ?? DateTime.UtcNow.AddMinutes(-1),
                Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = SigningCredentials,
            });
    }
}
