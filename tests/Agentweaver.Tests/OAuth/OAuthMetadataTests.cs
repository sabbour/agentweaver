using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// S1 — Metadata / 401 contract tests.
///
/// Validates:
///   • <c>GET /.well-known/oauth-authorization-server</c> returns valid RFC 8414 AS metadata.
///   • <c>GET /.well-known/oauth-protected-resource</c> (served by MCP RS) returns valid RFC 9728 document.
///   • <c>GET /oauth/jwks</c> is a well-formed JWKS with at least one key.
///   • Both <c>.well-known</c> documents are reachable unauthenticated.
///   • A missing/invalid token produces a 401 with a <c>WWW-Authenticate</c> header that carries
///     <c>resource_metadata</c> pointing to the protected-resource document (RFC 9728 §5.1).
///
/// Seraph design ref: §3a (protected resource), §3b (AS metadata), §2 (401 challenge), §4 (JWKS).
///
/// CURRENT STATUS: All tests are Skip-marked pending Tank T1 (AS metadata + JWKS) and T6 (MCP RS).
/// Remove [Skip] and update assertions as each endpoint lands.
/// </summary>
public sealed class OAuthMetadataTests : IClassFixture<OAuthWebApplicationFactory>
{
    private readonly OAuthWebApplicationFactory _factory;

    public OAuthMetadataTests(OAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // =========================================================================
    // S1-AS-01 — GET /.well-known/oauth-authorization-server → 200, unauthenticated
    // Tank T1: LIVE
    // =========================================================================
    [Fact]
    public async Task AsMetadata_ReturnsOk_Unauthenticated()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "AS metadata must be reachable without authentication (RFC 8414 §3)");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // =========================================================================
    // S1-AS-02 — AS metadata document contains all RFC 8414 required + MCP-mandated fields.
    // Tank T1: LIVE
    // =========================================================================
    [Fact]
    public async Task AsMetadata_ContainsRequiredRfc8414Fields()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonDocument>("/.well-known/oauth-authorization-server");

        doc.Should().NotBeNull();
        var root = doc!.RootElement;

        // RFC 8414 §2 required fields
        root.GetProperty("issuer").GetString()
            .Should().NotBeNullOrEmpty("issuer is required by RFC 8414");
        root.GetProperty("authorization_endpoint").GetString()
            .Should().EndWith("/oauth/authorize");
        root.GetProperty("token_endpoint").GetString()
            .Should().EndWith("/oauth/token");
        root.GetProperty("jwks_uri").GetString()
            .Should().EndWith("/oauth/jwks");

        // MCP OAuth mandatory fields
        root.GetProperty("registration_endpoint").GetString()
            .Should().EndWith("/oauth/register",
                "DCR (RFC 7591) must be advertised (MCP Authorization spec)");
        root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString())
            .Should().ContainSingle()
            .Which.Should().Be("S256",
                "Only S256 PKCE is allowed; plain must NOT appear (Seraph §3b)");
        root.GetProperty("grant_types_supported").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain("authorization_code")
            .And.Contain("refresh_token");
        root.GetProperty("response_types_supported").EnumerateArray()
            .Select(e => e.GetString())
            .Should().ContainSingle("code");
        root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(e => e.GetString())
            .Should().ContainSingle("none",
                "Public clients authenticate via PKCE, not a secret");
    }

    // =========================================================================
    // S1-AS-03 — AS metadata must NOT contain plain in code_challenge_methods_supported.
    // Tank T1: LIVE
    // =========================================================================
    [Fact]
    public async Task AsMetadata_DoesNotAdvertise_PlainPkce()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonDocument>("/.well-known/oauth-authorization-server");

        doc.Should().NotBeNull();
        var methods = doc!.RootElement
            .GetProperty("code_challenge_methods_supported")
            .EnumerateArray()
            .Select(e => e.GetString());

        methods.Should().NotContain("plain",
            "plain PKCE must be rejected — Seraph §3b explicitly excludes it");
    }

    // =========================================================================
    // S1-AS-04 — GET /.well-known/oauth-authorization-server/mcp → 200, application/json
    // RFC 8414 path-aware clients probe the suffixed form first; it must not fall through to the SPA.
    // =========================================================================
    [Fact]
    public async Task AsMetadata_SuffixedMcpPath_ReturnsOk_WithJson()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/.well-known/oauth-authorization-server/mcp");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the /mcp-suffixed AS metadata path must return 200 (RFC 8414 path-aware discovery)");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // =========================================================================
    // S1-AS-05 — /.well-known/oauth-authorization-server/mcp contains required fields
    // =========================================================================
    [Fact]
    public async Task AsMetadata_SuffixedMcpPath_ContainsRequiredFields()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonDocument>("/.well-known/oauth-authorization-server/mcp");

        doc.Should().NotBeNull();
        var root = doc!.RootElement;
        root.GetProperty("issuer").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("authorization_endpoint").GetString().Should().EndWith("/oauth/authorize");
        root.GetProperty("token_endpoint").GetString().Should().EndWith("/oauth/token");
        root.GetProperty("jwks_uri").GetString().Should().EndWith("/oauth/jwks");
    }

    // =========================================================================
    // S1-AS-06 — GET /.well-known/openid-configuration → 200, application/json
    // Many clients also probe the OIDC discovery URL; must return AS metadata (not SPA HTML).
    // =========================================================================
    [Fact]
    public async Task OidcConfiguration_ReturnsOk_WithJson()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "OIDC discovery path must return 200 with AS metadata");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // =========================================================================
    // S1-PR-01 (STUB — requires Tank T6)
    // GET /.well-known/oauth-protected-resource (root form) on MCP RS → 200, unauthenticated
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — protected-resource metadata route on MCP RS not yet implemented")]
    public async Task ProtectedResourceMetadata_RootPath_ReturnsOk_Unauthenticated()
    {
        // NOTE: this endpoint is served by the MCP server, not the API.
        // In tests, we'd need a McpWebApplicationFactory; until T6 lands, stub.
        await Task.CompletedTask;
    }

    // =========================================================================
    // S1-PR-02 (STUB — requires Tank T6)
    // GET /.well-known/oauth-protected-resource/mcp (suffixed path) also returns 200
    // to maximize Copilot CLI / VS Code client compatibility.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — /.well-known/oauth-protected-resource/mcp suffixed path not yet implemented")]
    public async Task ProtectedResourceMetadata_SuffixedPath_ReturnsOk()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S1-PR-03 (STUB — requires Tank T6)
    // Protected-resource document contains required RFC 9728 fields.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — validate RFC 9728 document shape")]
    public async Task ProtectedResourceMetadata_ContainsRequiredRfc9728Fields()
    {
        // Expected shape (Seraph §3a):
        // {
        //   "resource": "https://{HOST}/mcp",
        //   "authorization_servers": ["https://{HOST}"],
        //   "bearer_methods_supported": ["header"],
        //   "scopes_supported": ["mcp:invoke"],
        //   "resource_documentation": "https://{HOST}/docs"
        // }
        await Task.CompletedTask;
    }

    // =========================================================================
    // S1-JWKS-01 — GET /oauth/jwks → 200, well-formed JWKS, at least one key with a kid.
    // Tank T1 + T2: LIVE
    // =========================================================================
    [Fact]
    public async Task JwksEndpoint_ReturnsWellFormedJwks()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonDocument>("/oauth/jwks");

        doc.Should().NotBeNull();
        var keys = doc!.RootElement.GetProperty("keys").EnumerateArray().ToList();
        keys.Should().NotBeEmpty("JWKS must expose at least one public key");
        keys[0].GetProperty("kid").GetString().Should().NotBeNullOrEmpty(
            "each JWKS key must have a kid for rotation support");
        keys[0].GetProperty("kty").GetString()
            .Should().BeOneOf("RSA", "EC", "okp",
                "kty must identify the algorithm family");
    }

    // =========================================================================
    // S1-JWKS-02 — JWKS endpoint is unauthenticated (RS must be able to fetch it without a token).
    // Tank T1: LIVE
    // =========================================================================
    [Fact]
    public async Task JwksEndpoint_IsUnauthenticated()
    {
        var client = _factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/oauth/jwks");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "JWKS must be accessible without a bearer token so the RS can validate offline");
    }

    // =========================================================================
    // S1-WWW-01 (STUB — requires Tank T6)
    // POST /mcp with no token → 401 with WWW-Authenticate header containing
    //   resource_metadata="https://{HOST}/.well-known/oauth-protected-resource"
    // This is the discovery trigger for Copilot CLI (RFC 9728 §5.1).
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — WWW-Authenticate: Bearer resource_metadata= not yet added to McpBearerTokenMiddleware")]
    public async Task McpEndpoint_NoToken_Returns401_WithWwwAuthenticate_ResourceMetadata()
    {
        // NOTE: tests the MCP server, not the API. Needs McpWebApplicationFactory.
        // Seraph §2: "WWW-Authenticate: Bearer realm=\"agentweaver-mcp\", resource_metadata=\"https://${HOST}/.well-known/oauth-protected-resource\""
        await Task.CompletedTask;
    }

    // =========================================================================
    // S1-WWW-02 (STUB — requires Tank T6)
    // WWW-Authenticate must be present on EVERY unauthenticated/failed-auth
    // response (not just the first), including when an invalid token is supplied.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — invalid token must also trigger WWW-Authenticate with error=invalid_token")]
    public async Task McpEndpoint_InvalidToken_Returns401_WithInvalidTokenError()
    {
        await Task.CompletedTask;
    }
}
