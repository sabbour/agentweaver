using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Vault;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class NativeCliAuthIntegrationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _postgres;
    private FakeIdentityProvider _fakeIdp = null!;
    private IdentityBrokerWebApplicationFactory _factory = null!;
    private HttpClient _broker = null!;
    private HttpClient _fakeIdpClient = null!;
    private (string PfxPath, string Password) _signingCertificate;

    public NativeCliAuthIntegrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        await BrokerCliScopePermissionTestSupport.ProvisionAndMigrateLegacyCliClientAsync(connectionString);
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _signingCertificate = TestSigningCertificate.Create();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString, _fakeIdp, signingCertificate: _signingCertificate, registerCliClient: true);
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        _fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority),
        };
    }

    public async Task DisposeAsync()
    {
        _broker.Dispose();
        _fakeIdpClient.Dispose();
        await _factory.DisposeAsync();
        await _fakeIdp.DisposeAsync();
        File.Delete(_signingCertificate.PfxPath);
        if (Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
    }

    [Fact]
    public async Task NativeCliLoginCompletesPkceConsentAndValidatesBrokerIdentity()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-cli-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vault = new MemoryVault();
        using var browser = new ConsentApprovingBrowser(_broker, _fakeIdpClient);
        using var brokerHttp = new HttpClient(_factory.Server.CreateHandler())
        {
            BaseAddress = new Uri(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri),
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var authentication = new CliAuthService(
            brokerHttp,
            vault,
            browser,
            new OAuthCallbackListenerFactory(),
            new CliCredentialLock(Path.Combine(directory, "locks"), TimeSpan.FromSeconds(20)));

        try
        {
            var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
            var identity = new CliIdentity(
                new CliTarget(
                    new Uri("https://gateway.test/"),
                    new Uri(issuer),
                    IdentityBrokerWebApplicationFactory.CliClientId,
                    "https://api.test",
                    "test-tenant"),
                ["openid", "api.read", "offline_access", "projects.admin"]);

            var status = await authentication.LoginAsync(identity, CancellationToken.None);
            var bearer = await authentication.GetBearerAsync(
                identity, InteractionPolicy.Never, CancellationToken.None);
            var beforeRefresh = Assert.Single(vault.Values.Values);

            using var refreshHttp = new HttpClient(_factory.Server.CreateHandler())
            {
                BaseAddress = new Uri(issuer),
                Timeout = TimeSpan.FromSeconds(30),
            };
            using var refreshAuthentication = new CliAuthService(
                refreshHttp,
                vault,
                browser,
                new OAuthCallbackListenerFactory(),
                new CliCredentialLock(Path.Combine(directory, "locks"), TimeSpan.FromSeconds(20)));
            var refreshedBearer = await refreshAuthentication.GetBearerAsync(
                identity, InteractionPolicy.Never, CancellationToken.None);
            var afterRefresh = Assert.Single(vault.Values.Values);

            Assert.True(status.IsSignedIn);
            Assert.False(string.IsNullOrWhiteSpace(status.ActorId));
            Assert.StartsWith("https://broker.test/connect/authorize", browser.AuthorizationUri!.AbsoluteUri);
            Assert.Contains("code_challenge_method=S256", browser.AuthorizationUri.Query);
            Assert.Equal(1, browser.ConsentCount);
            Assert.Equal("text/html", browser.CallbackContentType);
            Assert.False(string.IsNullOrWhiteSpace(bearer));
            var accessToken = new JwtSecurityTokenHandler().ReadJwtToken(bearer);
            Assert.Contains("projects.admin", accessToken.Claims
                .Where(claim => claim.Type == "scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            Assert.DoesNotContain(accessToken.Claims, claim =>
                claim.Type is "role" or "roles" or "project_id" or "run_id");
            Assert.False(string.IsNullOrWhiteSpace(refreshedBearer));
            Assert.NotEqual(beforeRefresh, afterRefresh);
            Assert.All(vault.Values.Values, value => Assert.DoesNotContain("access-token", value, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemoryVault : ICliCredentialVault
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Values => _values;
        public string? Get(string account) => _values.GetValueOrDefault(account);
        public void Set(string account, string refreshToken) => _values[account] = refreshToken;
        public void Delete(string account) => _values.TryRemove(account, out _);
    }

    private sealed class ConsentApprovingBrowser(HttpClient broker, HttpClient fakeIdp) : ICliSystemBrowser, IDisposable
    {
        private readonly HttpClient _callbackClient = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        });

        public Uri? AuthorizationUri { get; private set; }
        public int ConsentCount { get; private set; }
        public string? CallbackContentType { get; private set; }

        public async Task OpenAsync(Uri authorizationUri, CancellationToken ct)
        {
            AuthorizationUri = authorizationUri;
            using var authorize = await broker.GetAsync(authorizationUri.PathAndQuery, ct);
            Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

            using var upstream = await fakeIdp.GetAsync(authorize.Headers.Location, ct);
            Assert.Equal(HttpStatusCode.Redirect, upstream.StatusCode);
            using var signedIn = await broker.GetAsync(Resolve(upstream.Headers.Location!), ct);
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            using var resumed = await broker.GetAsync(Resolve(signedIn.Headers.Location!), ct);
            Assert.Equal(HttpStatusCode.Redirect, resumed.StatusCode);
            using var page = await broker.GetAsync(Resolve(resumed.Headers.Location!), ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.StartsWith("text/html", page.Content.Headers.ContentType?.ToString());
            var html = await page.Content.ReadAsStringAsync(ct);
            Assert.Contains("Authorize Agentweaver CLI", html);
            Assert.Contains("api.read", html);
            Assert.Contains("projects.admin", html);

            var handle = HiddenInput(html, "consent_handle");
            var antiforgery = HiddenInput(html, "__RequestVerificationToken");
            using var approved = await broker.PostAsync("/connect/consent/native", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["consent_handle"] = handle,
                    ["approve"] = "true",
                    ["__RequestVerificationToken"] = antiforgery,
                }), ct);
            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
            ConsentCount++;

            using var issued = await broker.GetAsync(Resolve(approved.Headers.Location!), ct);
            Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
            var callbackUri = issued.Headers.Location
                ?? throw new InvalidOperationException("The Broker omitted the CLI callback URI.");
            Assert.Equal("127.0.0.1", callbackUri.Host);
            Assert.Equal(49173, callbackUri.Port);
            Assert.Equal("/oauth/callback", callbackUri.AbsolutePath);

            using var callback = await _callbackClient.GetAsync(callbackUri, ct);
            Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
            CallbackContentType = callback.Content.Headers.ContentType?.MediaType;
        }

        public void Dispose() => _callbackClient.Dispose();

        private Uri Resolve(Uri location) =>
            location.IsAbsoluteUri ? location : new Uri(broker.BaseAddress!, location);

        private static string HiddenInput(string html, string name)
        {
            var match = Regex.Match(html,
                $"""<input type="hidden" name="{Regex.Escape(name)}" value="([^"]*)">""");
            Assert.True(match.Success, $"Expected hidden input '{name}' in native CLI consent page.");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
    }
}
