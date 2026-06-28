using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for the MCP OAuth 2.1 Authorization Server (Option C) token service and broker (T2-T3):
///  - JWT signing produces audience-bound tokens that self-validate and reject tampering.
///  - Redirect-URI policy allows loopback/HTTPS only.
///  - PKCE S256 is enforced and authorization codes are single-use.
///  - microsoft org membership is enforced before any code is issued.
/// </summary>
public sealed class McpOAuthServerTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public McpOAuthServerTests()
    {
        _connectionString = $"DataSource=file:mcpoauthserver-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var db = NewDbContext();
        db.Database.EnsureCreated();
    }

    private MemoryDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MemoryDbContext(options);
    }

    public void Dispose() => _keepAlive.Dispose();
    // =========================================================================
    // T2: token signing + validation
    // =========================================================================
    [Fact]
    public async Task CreateAccessToken_ProducesAudienceBoundToken_ThatValidates()
    {
        using var svc = BuildTokenService();
        const string issuer = "https://host.example";
        const string audience = "https://host.example/mcp";

        var token = svc.CreateAccessToken(issuer, audience, "octocat", "octocat", "microsoft");

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, svc.CreateValidationParameters(issuer, audience));

        result.IsValid.Should().BeTrue();
        result.Claims["aud"].Should().Be(audience);
        result.Claims["iss"].Should().Be(issuer);
        result.Claims["sub"].Should().Be("octocat");
        result.Claims["scope"].Should().Be("mcp:invoke");
        result.Claims["gh_login"].Should().Be("octocat");
        result.Claims["org"].Should().Be("microsoft");
    }

    [Fact]
    public async Task ValidateToken_RejectsTamperedSignature()
    {
        using var svc = BuildTokenService();
        const string issuer = "https://host.example";
        const string audience = "https://host.example/mcp";

        var token = svc.CreateAccessToken(issuer, audience, "octocat", "octocat", "microsoft");
        var tampered = token[..^4] + (token.EndsWith("aaaa") ? "bbbb" : "aaaa");

        var result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(tampered, svc.CreateValidationParameters(issuer, audience));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateToken_RejectsWrongAudience()
    {
        using var svc = BuildTokenService();
        var token = svc.CreateAccessToken("https://host.example", "https://host.example/mcp", "octocat", "octocat", "microsoft");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token, svc.CreateValidationParameters("https://host.example", "https://evil.example/mcp"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GetPublicJsonWebKey_ExposesCurrentSigningKey()
    {
        using var svc = BuildTokenService();
        var jwk = svc.GetPublicJsonWebKey();

        jwk.Kty.Should().Be("RSA");
        jwk.Use.Should().Be("sig");
        jwk.Alg.Should().Be("RS256");
        jwk.Kid.Should().Be(svc.KeyId);
        jwk.N.Should().NotBeNullOrEmpty();
        jwk.E.Should().NotBeNullOrEmpty();
    }

    // =========================================================================
    // T3: redirect-URI policy
    // =========================================================================
    [Theory]
    [InlineData("http://127.0.0.1:51234/callback", true)]
    [InlineData("http://localhost:8765/cb", true)]
    [InlineData("https://app.example.com/callback", true)]   // matches configured allowlist prefix
    [InlineData("https://evil.example.com/callback", false)] // F2: HTTPS not in allowlist is rejected
    [InlineData("http://example.com/callback", false)] // non-loopback http
    [InlineData("ftp://127.0.0.1/cb", false)]          // wrong scheme
    [InlineData("https://app.example.com/cb#frag", false)] // fragment not allowed
    [InlineData("https://user@app.example.com/cb", false)] // F2: userinfo (user@host) rejected
    [InlineData("not-a-uri", false)]
    [InlineData("", false)]
    public void IsAllowedRedirectUri_EnforcesLoopbackOrHttps(string uri, bool expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:OAuth:AllowedRedirectUriPrefixes:0"] = "https://app.example.com/",
            })
            .Build();

        OAuthServerConfig.IsAllowedRedirectUri(uri, config).Should().Be(expected);
    }

    // =========================================================================
    // T3: PKCE happy path + single-use code + org enforcement
    // =========================================================================
    [Fact]
    public async Task Broker_HappyPath_IssuesSingleUseCode_RedeemableWithPkceVerifier()
    {
        var broker = BuildBroker(OrgAuthResult.Allowed);
        var verifier = "verifier-abc123_DEF-456~test.value";
        var challenge = ComputeChallenge(verifier);
        const string redirectUri = "http://127.0.0.1:50000/cb";

        var gitHubUrl = await broker.BeginAuthorization("client-1", redirectUri, challenge, "client-state", "mcp:invoke", null);
        var state = ExtractState(gitHubUrl);

        var callback = await broker.HandleGitHubCallbackAsync("gh-code", state, CancellationToken.None);
        callback.Outcome.Should().Be(BrokerOutcome.Success);
        callback.Code.Should().NotBeNullOrEmpty();
        callback.RedirectUri.Should().Be(redirectUri);
        callback.ClientState.Should().Be("client-state");

        var (grant, error) = await broker.RedeemAuthorizationCode(callback.Code, verifier, redirectUri, "client-1");
        error.Should().BeNull();
        grant.Should().NotBeNull();
        grant!.GithubLogin.Should().Be("octocat");

        // Single-use: a second redemption of the same code fails.
        var (grant2, error2) = await broker.RedeemAuthorizationCode(callback.Code, verifier, redirectUri, "client-1");
        grant2.Should().BeNull();
        error2!.Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Broker_WrongPkceVerifier_IsRejected()
    {
        var broker = BuildBroker(OrgAuthResult.Allowed);
        var challenge = ComputeChallenge("the-real-verifier-value-1234567890");
        const string redirectUri = "http://127.0.0.1:50001/cb";

        var state = ExtractState(await broker.BeginAuthorization("c", redirectUri, challenge, null, "mcp:invoke", null));
        var callback = await broker.HandleGitHubCallbackAsync("gh-code", state, CancellationToken.None);

        var (grant, error) = await broker.RedeemAuthorizationCode(callback.Code, "a-different-verifier-value-000000", redirectUri, "c");
        grant.Should().BeNull();
        error!.Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Broker_NonOrgMember_IsDenied_NoCodeIssued()
    {
        var broker = BuildBroker(OrgAuthResult.Denied);
        var challenge = ComputeChallenge("verifier-value-for-denied-case-1234");
        const string redirectUri = "http://127.0.0.1:50002/cb";

        var state = ExtractState(await broker.BeginAuthorization("c", redirectUri, challenge, "st", "mcp:invoke", null));
        var callback = await broker.HandleGitHubCallbackAsync("gh-code", state, CancellationToken.None);

        callback.Outcome.Should().Be(BrokerOutcome.Denied);
        callback.Code.Should().BeNull();
        callback.Error.Should().Be("access_denied");
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private static McpTokenService BuildTokenService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new McpTokenService(config, NullLogger<McpTokenService>.Instance);
    }

    private McpOAuthBrokerService BuildBroker(OrgAuthResult orgResult)
    {
        var gitHub = new GitHubOAuthRedirectService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:GitHub:BaseUrl"] = "https://github.com",
                ["Auth:GitHub:ClientId"] = "test-client-id",
                ["Auth:GitHub:ClientSecret"] = "test-client-secret",
                ["Auth:GitHub:CallbackUrl"] = "https://host.example/auth/github/callback",
                ["Auth:GitHub:Scopes"] = "repo read:user read:org",
            }).Build(),
            new InMemoryGitHubTokenStore(),
            new SingleClientHttpClientFactory(new RoutingHandler()),
            MemoryDbScopeFactory.ForSqlite(_connectionString),
            NullLogger<GitHubOAuthRedirectService>.Instance);

        return new McpOAuthBrokerService(gitHub, new StubOrgAuth(orgResult), new InMemoryGitHubTokenStore(), NewDbContext(), NullLogger<McpOAuthBrokerService>.Instance);
    }

    private static string ComputeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncoder.Encode(hash);
    }

    private static string ExtractState(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?');
        var pair = query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    private sealed class StubOrgAuth(OrgAuthResult result) : IGitHubOrgAuthorizationService
    {
        public bool IsConfigured => true;
        public Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsoluteUri.Contains("/login/oauth/access_token")
                ? """{"access_token":"ghu_access","refresh_token":"ghr_refresh","expires_in":28800}"""
                : """{"login":"octocat","avatar_url":"https://avatars/x"}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
