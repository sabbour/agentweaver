using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Playwright;
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
    private bool _listDuplicateRepository;

    public NativeCliAuthIntegrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        await BrokerCliScopePermissionTestSupport.ProvisionAndMigrateLegacyCliClientAsync(connectionString);
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _signingCertificate = TestSigningCertificate.Create();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString, _fakeIdp, signingCertificate: _signingCertificate, registerCliClient: true,
            configure: AddRepoAppTestSettings, configureServices: ConfigureRepoAppTestServices);
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

    [Fact]
    public async Task NativeWorkspaceSelectionIsDeliveredOnceAfterPkceAndCurrentOwnerChecks()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vault = new MemoryVault();
        using var consentBrowser = new ConsentApprovingBrowser(_broker, _fakeIdpClient);
        using var brokerHttp = new HttpClient(_factory.Server.CreateHandler())
        {
            BaseAddress = new Uri(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri),
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var authentication = new CliAuthService(
            brokerHttp,
            vault,
            consentBrowser,
            new OAuthCallbackListenerFactory(),
            new CliCredentialLock(Path.Combine(directory, "locks"), TimeSpan.FromSeconds(20)));

        try
        {
            string tenantId = Guid.NewGuid().ToString("D");
            string projectId = Guid.NewGuid().ToString("D");
            var identity = new CliIdentity(
                new CliTarget(
                    new Uri("https://gateway.test/"),
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                    IdentityBrokerWebApplicationFactory.CliClientId,
                    "https://api.test",
                    tenantId),
                ["openid", "api.read", "offline_access", "projects.admin"]);
            var login = await authentication.LoginAsync(identity, CancellationToken.None);
            Guid ownerId = Guid.Parse(login.ActorId!);
            const string connectionId = "native_workspace_connection";
            await SeedRepoAppConnectionAsync(ownerId, connectionId);

            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(32));
            string nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
            var intent = new NativeRepoAppSelectionIntent(
                1, ownerId.ToString("D"), tenantId, projectId, connectionId, 7, 456, 789,
                "octo/widget", "readWorkspaceRepositoryTree");
            string encodedIntent = EncodeIntent(intent);
            var query = new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                ["scope"] = "openid api.read offline_access projects.admin",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["nonce"] = nonce,
                ["workspace_selection_request"] = encodedIntent,
            };
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);

            Assert.Equal(1, selectionBrowser.ReviewCount);
            Assert.Contains("octo/widget", selectionBrowser.ReviewPage, StringComparison.Ordinal);
            Assert.Contains(projectId, selectionBrowser.ReviewPage, StringComparison.Ordinal);
            Assert.Contains(
                $"<dt>Tenant ID</dt><dd><code>{WebUtility.HtmlEncode(tenantId)}</code></dd>",
                selectionBrowser.ReviewPage,
                StringComparison.Ordinal);
            Assert.Contains(
                $"<dt>Connection ID</dt><dd><code>{WebUtility.HtmlEncode(connectionId)}</code></dd>",
                selectionBrowser.ReviewPage,
                StringComparison.Ordinal);
            Assert.Contains(
                $"<dt>Connection revision</dt><dd><code>{intent.ConnectionRevision}</code></dd>",
                selectionBrowser.ReviewPage,
                StringComparison.Ordinal);
            Assert.DoesNotContain(encodedIntent, selectionBrowser.ReviewPage, StringComparison.Ordinal);

            Uri callback = selectionBrowser.CallbackUri!;
            var callbackQuery = QueryHelpers.ParseQuery(callback.Query);
            string authorizationCode = callbackQuery["code"].Single()
                ?? throw new Xunit.Sdk.XunitException("The Broker omitted the authorization code.");
            Assert.Equal(state, callbackQuery["state"].Single());
            Assert.DoesNotContain("workspace_selection", callback.Query, StringComparison.Ordinal);

            var selectionRows = await ReadRepositorySelectionsAsync(ownerId);
            string selectionCodeHash = Assert.Single(selectionRows).CodeHash;
            Assert.Matches("^[0-9a-f]{64}$", selectionCodeHash);

            using (var wrongVerifier = await _broker.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                    ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                    ["code"] = authorizationCode,
                    ["code_verifier"] = Base64Url(RandomNumberGenerator.GetBytes(32)),
                })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);
                Assert.DoesNotContain("workspace_selection",
                    await wrongVerifier.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            var exchangeTasks = Enumerable.Range(0, 2).Select(_ => _broker.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                    ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                    ["code"] = authorizationCode,
                    ["code_verifier"] = verifier,
                }))).ToArray();
            using var first = await exchangeTasks[0];
            using var second = await exchangeTasks[1];
            var responses = new[] { first, second };
            using var winner = Assert.Single(responses, response => response.IsSuccessStatusCode);
            using var loser = Assert.Single(responses, response => !response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, loser.StatusCode);
            using var issued = JsonDocument.Parse(await winner.Content.ReadAsStringAsync());
            JsonElement selection = issued.RootElement.GetProperty("workspace_selection");
            Assert.Equal(ownerId.ToString("D"), selection.GetProperty("actorId").GetString());
            Assert.Equal(tenantId, selection.GetProperty("tenantId").GetString());
            Assert.Equal(projectId, selection.GetProperty("projectId").GetString());
            Assert.Equal("readWorkspaceRepositoryTree", selection.GetProperty("operationId").GetString());
            string deliveredCode = selection.GetProperty("code").GetString()!;
            Assert.Matches("^[0-9a-f]{64}$", deliveredCode);
            Assert.Equal(connectionId, selection.GetProperty("connectionId").GetString());

            var tokenHandler = new JwtSecurityTokenHandler();
            var accessToken = tokenHandler.ReadJwtToken(issued.RootElement.GetProperty("access_token").GetString());
            var idToken = tokenHandler.ReadJwtToken(issued.RootElement.GetProperty("id_token").GetString());
            foreach (var token in new[] { accessToken, idToken })
            {
                Assert.DoesNotContain(token.Claims, claim =>
                    claim.Type is "aw:native_repo_app_selection_payload" or "aw:native_repo_app_selection_intent_hash");
                Assert.DoesNotContain(token.Claims, claim => claim.Value == deliveredCode);
            }
            Assert.DoesNotContain(deliveredCode, string.Join('|', _factory.LogMessages), StringComparison.Ordinal);
            Assert.DoesNotContain(deliveredCode, string.Join('|', vault.Values.Values), StringComparison.Ordinal);
            Assert.DoesNotContain(deliveredCode, selectionBrowser.ReviewPage, StringComparison.Ordinal);
            Assert.DoesNotContain(deliveredCode, callback.AbsoluteUri, StringComparison.Ordinal);

            var refreshToken = issued.RootElement.GetProperty("refresh_token").GetString()
                ?? throw new Xunit.Sdk.XunitException("The Broker omitted the refresh token.");
            using var refresh = await _broker.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                    ["refresh_token"] = refreshToken,
                }));
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            var refreshBody = await refresh.Content.ReadAsStringAsync();
            Assert.DoesNotContain(deliveredCode, refreshBody, StringComparison.Ordinal);
            using var refreshed = JsonDocument.Parse(refreshBody);
            Assert.False(refreshed.RootElement.TryGetProperty("workspace_selection", out _));
            var refreshedTokenNames = new List<string> { "access_token" };
            if (refreshed.RootElement.TryGetProperty("id_token", out _))
                refreshedTokenNames.Add("id_token");
            foreach (var tokenName in refreshedTokenNames)
            {
                var token = tokenHandler.ReadJwtToken(refreshed.RootElement.GetProperty(tokenName).GetString());
                Assert.DoesNotContain(token.Claims, claim =>
                    claim.Type is "aw:native_repo_app_selection_payload" or "aw:native_repo_app_selection_intent_hash");
                Assert.DoesNotContain(token.Claims, claim => claim.Value == deliveredCode);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeWorkspaceSelectionUsesNativePkceAndTheExactLoopbackCallback()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vault = new MemoryVault();
        using var browser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient);
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
            string tenantId = Guid.NewGuid().ToString("D");
            string projectId = Guid.NewGuid().ToString("D");
            var identity = new CliIdentity(
                new CliTarget(
                    new Uri("https://gateway.test/"),
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                    IdentityBrokerWebApplicationFactory.CliClientId,
                    "https://api.test",
                    tenantId),
                ["openid", "api.read", "offline_access", "projects.admin"]);
            var login = await authentication.LoginAsync(identity, CancellationToken.None);
            Guid ownerId = Guid.Parse(login.ActorId!);
            const string connectionId = "native_workspace_cli_connection";
            await SeedRepoAppConnectionAsync(ownerId, connectionId);

            var intent = new Agentweaver.Cli.Auth.NativeRepoAppSelectionIntent(
                1, ownerId.ToString("D"), tenantId, projectId, connectionId, 7, 456, 789,
                "octo/widget", "listWorkspaceRepositoryRefs");
            using var selection = await authentication.AcquireRepositorySelectionAsync(
                identity, intent, InteractionPolicy.Allow, CancellationToken.None);

            Assert.True(intent.Matches(selection));
            Assert.Equal(1, browser.ReviewCount);
            Assert.Contains("octo/widget", browser.ReviewPage, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Agentweaver.Cli.Auth.NativeRepoAppSelectionIntent.Encode(intent),
                browser.ReviewPage,
                StringComparison.Ordinal);
            Assert.Equal("127.0.0.1", browser.CallbackUri!.Host);
            Assert.Equal(49173, browser.CallbackUri.Port);
            Assert.Equal("/oauth/callback", browser.CallbackUri.AbsolutePath);
            Assert.DoesNotContain("workspace_selection", browser.CallbackUri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain(selection.Code, browser.CallbackUri.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain(selection.Code, string.Join('|', vault.Values.Values), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeWorkspaceSelectionReviewRequiresOwnerCookieAndAntiforgery()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string tenantId = Guid.NewGuid().ToString("D");
            string projectId = Guid.NewGuid().ToString("D");
            var signedIn = await SignInWorkspaceOwnerAsync(directory, tenantId);
            Guid ownerId = Guid.Parse(signedIn.ActorId);
            const string connectionId = "native_workspace_review_connection";
            await SeedRepoAppConnectionAsync(ownerId, connectionId);

            var intent = new NativeRepoAppSelectionIntent(
                1, ownerId.ToString("D"), tenantId, projectId, connectionId, 7, 456, 789,
                "octo/widget", "readWorkspaceRepositoryFile");
            var (query, _, _) = CreateWorkspaceAuthorization(intent);
            var reviewBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient, approveReview: false);
            await reviewBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);
            Assert.NotNull(reviewBrowser.ReviewHandle);
            Assert.NotNull(reviewBrowser.AntiforgeryToken);

            using (var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://broker.test.local"),
            }))
            using (var noCookie = await PostWorkspaceReviewAsync(
                anonymous, reviewBrowser.ReviewHandle!, reviewBrowser.AntiforgeryToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
            }

            using (var noAntiforgery = await PostWorkspaceReviewAsync(
                _broker, reviewBrowser.ReviewHandle!, antiforgeryToken: null))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);
            }

            var mismatchedIntent = intent with { ActorId = Guid.NewGuid().ToString("D") };
            var (mismatchedQuery, _, _) = CreateWorkspaceAuthorization(mismatchedIntent);
            using (var mismatchedOwner = await _broker.GetAsync(
                QueryHelpers.AddQueryString("/connect/authorize", mismatchedQuery)))
            {
                Assert.NotEqual(HttpStatusCode.OK, mismatchedOwner.StatusCode);
            }

            Assert.Empty(await ReadRepositorySelectionsAsync(ownerId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeWorkspaceReviewCompletesInControlledChromiumWithoutChangingCallback()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string tenantId = Guid.NewGuid().ToString("D");
            string projectId = Guid.NewGuid().ToString("D");
            var signedIn = await SignInWorkspaceOwnerAsync(directory, tenantId);
            Guid ownerId = Guid.Parse(signedIn.ActorId);
            const string connectionId = "native_workspace_browser_connection";
            await SeedRepoAppConnectionAsync(ownerId, connectionId);

            var intent = new NativeRepoAppSelectionIntent(
                1, ownerId.ToString("D"), tenantId, projectId, connectionId, 7, 456, 789,
                "octo/widget", "readWorkspaceRepositoryFile");
            var (query, verifier, expectedState) = CreateWorkspaceAuthorization(intent);
            using var review = await _broker.GetAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query));
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
            string reviewHtml = await review.Content.ReadAsStringAsync();
            string contentSecurityPolicy = review.Headers.GetValues("Content-Security-Policy").Single();
            string reviewHandle = HiddenInput(reviewHtml, "review_handle");
            Assert.Contains("octo/widget", reviewHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(EncodeIntent(intent), reviewHtml, StringComparison.Ordinal);
            Assert.Contains(
                $"form-action 'self' {IdentityBrokerWebApplicationFactory.CliClientRedirectUri}",
                contentSecurityPolicy);

            using var callbackListener = new LoopbackCallbackListener(
                new Uri(IdentityBrokerWebApplicationFactory.CliClientRedirectUri));
            var playwright = await _postgres.GetPlaywrightAsync();
            await using var chromium = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-proxy-server"],
            });
            var proxy = await StartWorkspaceSelectionBrowserProxyAsync(
                reviewHtml, (int)review.StatusCode, contentSecurityPolicy);
            await using var proxyApp = proxy.App;
            await using var context = await chromium.NewContextAsync();
            var page = await context.NewPageAsync();
            var cspViolations = new List<string>();
            var browserTrace = new List<string>();
            page.Console += (_, message) =>
            {
                if (message.Text.Contains("form-action", StringComparison.OrdinalIgnoreCase))
                    cspViolations.Add(message.Text);
            };
            page.Request += (_, request) =>
                browserTrace.Add($"{request.Method} {new Uri(request.Url).AbsolutePath}");
            page.Response += (_, response) =>
                browserTrace.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            page.RequestFailed += (_, request) =>
                browserTrace.Add($"failed {request.Method} {new Uri(request.Url).AbsolutePath}: {request.Failure}");

            var callbackTask = callbackListener.WaitForCallbackAsync(TimeSpan.FromSeconds(15));
            await page.GotoAsync(new Uri(proxy.BaseAddress, "/native-review").AbsoluteUri);
            Assert.Equal(reviewHandle,
                await page.Locator("input[name='review_handle']").GetAttributeAsync("value"));
            await page.Locator("button[name='approve'][value='true']").ClickAsync();
            Uri callback;
            try
            {
                callback = await callbackTask;
            }
            catch (OperationCanceledException exception)
            {
                throw new InvalidOperationException(
                    $"The native Workspace review did not reach its callback. Browser URL: " +
                    $"{new Uri(page.Url).AbsolutePath}. Trace: {string.Join("; ", browserTrace)}",
                    exception);
            }
            var callbackQuery = QueryHelpers.ParseQuery(callback.Query);
            string authorizationCode = callbackQuery["code"].Single()!;
            Assert.False(string.IsNullOrWhiteSpace(authorizationCode));
            Assert.Equal(expectedState, callbackQuery["state"].Single());
            Assert.DoesNotContain("workspace_selection", callback.Query, StringComparison.Ordinal);
            Assert.Empty(cspViolations);

            using var exchange = await _broker.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                    ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                    ["code"] = authorizationCode,
                    ["code_verifier"] = verifier,
                }));
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
            using var tokenResponse = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
            Assert.Equal("octo/widget", tokenResponse.RootElement
                .GetProperty("workspace_selection").GetProperty("repositoryFullName").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<WorkspaceSelectionBrowserProxy> StartWorkspaceSelectionBrowserProxyAsync(
        string reviewHtml,
        int reviewStatusCode,
        string contentSecurityPolicy)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/native-review", async context =>
        {
            context.Response.StatusCode = reviewStatusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await context.Response.WriteAsync(reviewHtml);
        });
        app.Map("/{**path}", async context =>
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(context.Request.Method),
                new Uri(_broker.BaseAddress!, context.Request.Path + context.Request.QueryString));
            if (context.Request.ContentLength > 0)
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType is { } contentType)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            using var response = await _broker.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Headers.Location is { } location)
            {
                context.Response.Headers.Location = location.IsAbsoluteUri &&
                    location.Host.Equals(_broker.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase)
                    ? location.PathAndQuery
                    : location.ToString();
            }
            if (response.Content.Headers.ContentType is { } responseContentType)
                context.Response.ContentType = responseContentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return new WorkspaceSelectionBrowserProxy(app, new Uri(address));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("connection-revision")]
    public async Task NativeWorkspaceSelectionIsNotReleasedAfterExpiryOrConnectionChange(string change)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string tenantId = Guid.NewGuid().ToString("D");
            string projectId = Guid.NewGuid().ToString("D");
            var signedIn = await SignInWorkspaceOwnerAsync(directory, tenantId);
            Guid ownerId = Guid.Parse(signedIn.ActorId);
            const string connectionId = "native_workspace_invalid_connection";
            await SeedRepoAppConnectionAsync(ownerId, connectionId);

            var intent = new NativeRepoAppSelectionIntent(
                1, ownerId.ToString("D"), tenantId, projectId, connectionId, 7, 456, 789,
                "octo/widget", "readWorkspaceRepositoryTree");
            var (query, verifier, _) = CreateWorkspaceAuthorization(intent);
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);
            var callbackQuery = QueryHelpers.ParseQuery(selectionBrowser.CallbackUri!.Query);
            string authorizationCode = callbackQuery["code"].Single()!;
            var selection = Assert.Single(await ReadRepositorySelectionsAsync(ownerId));

            await using (var scope = _factory.Services.CreateAsyncScope())
            await using (var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>())
            {
                if (change == "expired")
                {
                    await db.RepoAppRepositorySelections
                        .Where(row => row.CodeHash == selection.CodeHash)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(row => row.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-11))
                            .SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                }
                else
                {
                    await db.RepoAppConnections
                        .Where(row => row.ConnectionId == connectionId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(
                            row => row.ConnectionRevision, 8));
                }
            }

            using var response = await _broker.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                    ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                    ["code"] = authorizationCode,
                    ["code_verifier"] = verifier,
                }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(error.RootElement.TryGetProperty("workspace_selection", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolvedWorkspaceSelectionReturnsBrokerResolvedRepositoryValues()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (ownerId, tenantId, projectId, connectionId) = await PrepareResolvedSelectionAsync(directory);
            var intent = ResolvedIntent(ownerId, tenantId, projectId, connectionId, "octo/widget");
            var (query, verifier, _) = CreateWorkspaceAuthorization(intent);
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);

            Assert.Equal(1, selectionBrowser.ReviewCount);
            Assert.Contains("octo/widget", selectionBrowser.ReviewPage, StringComparison.Ordinal);
            Assert.Contains(
                $"<dt>Connection ID</dt><dd><code>{WebUtility.HtmlEncode(connectionId)}</code></dd>",
                selectionBrowser.ReviewPage,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Connection revision", selectionBrowser.ReviewPage, StringComparison.Ordinal);
            var row = Assert.Single(await ReadRepositorySelectionsAsync(ownerId));
            Assert.Equal(7, row.ConnectionRevision);
            Assert.Equal(456, row.InstallationId);
            Assert.Equal(789, row.RepositoryId);

            string authorizationCode = QueryHelpers.ParseQuery(selectionBrowser.CallbackUri!.Query)["code"].Single()!;
            using var issuedResponse = await ExchangeWorkspaceCodeAsync(authorizationCode, verifier);
            Assert.Equal(HttpStatusCode.OK, issuedResponse.StatusCode);
            using var issued = JsonDocument.Parse(await issuedResponse.Content.ReadAsStringAsync());
            JsonElement selection = issued.RootElement.GetProperty("workspace_selection");
            Assert.Equal(2, selection.GetProperty("version").GetInt32());
            Assert.Equal(ownerId.ToString("D"), selection.GetProperty("actorId").GetString());
            Assert.Equal(projectId, selection.GetProperty("projectId").GetString());
            Assert.Equal(connectionId, selection.GetProperty("connectionId").GetString());
            Assert.Equal(7, selection.GetProperty("connectionRevision").GetInt64());
            Assert.Equal(456, selection.GetProperty("installationId").GetInt64());
            Assert.Equal(789, selection.GetProperty("repositoryId").GetInt64());
            Assert.Equal("octo/widget", selection.GetProperty("repositoryFullName").GetString());
            Assert.Equal("readWorkspaceRepositoryTree", selection.GetProperty("operationId").GetString());
            Assert.Equal(12, selection.EnumerateObject().Count());

            var tokenHandler = new JwtSecurityTokenHandler();
            foreach (var tokenName in new[] { "access_token", "id_token" })
            {
                var token = tokenHandler.ReadJwtToken(issued.RootElement.GetProperty(tokenName).GetString());
                Assert.DoesNotContain(token.Claims, claim =>
                    claim.Type is "aw:native_repo_app_selection_payload" or "aw:native_repo_app_selection_intent_hash");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-listed")]
    [InlineData("listed-twice")]
    [InlineData("other-connection")]
    public async Task ResolvedWorkspaceSelectionFailsWithoutOneExactRepositoryMatch(string change)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-v2-match-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (ownerId, tenantId, projectId, connectionId) = await PrepareResolvedSelectionAsync(directory);
            _listDuplicateRepository = change == "listed-twice";
            var intent = ResolvedIntent(
                ownerId, tenantId, projectId,
                change == "other-connection" ? "other_connection" : connectionId,
                change == "not-listed" ? "octo/missing" : "octo/widget");
            var (query, _, state) = CreateWorkspaceAuthorization(intent);
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient, followCompletion: false);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);

            Uri callback = selectionBrowser.CompletionUri!;
            Assert.Equal("127.0.0.1", callback.Host);
            var callbackQuery = QueryHelpers.ParseQuery(callback.Query);
            Assert.Equal("temporarily_unavailable", callbackQuery["error"].Single());
            Assert.Equal(state, callbackQuery["state"].Single());
            Assert.False(callbackQuery.ContainsKey("code"));
            Assert.Empty(await ReadRepositorySelectionsAsync(ownerId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolvedWorkspaceSelectionIsNotReleasedAfterRevisionChange()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-v2-exchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (ownerId, tenantId, projectId, connectionId) = await PrepareResolvedSelectionAsync(directory);
            var intent = ResolvedIntent(ownerId, tenantId, projectId, connectionId, "octo/widget");
            var (query, verifier, _) = CreateWorkspaceAuthorization(intent);
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);
            string authorizationCode = QueryHelpers.ParseQuery(selectionBrowser.CallbackUri!.Query)["code"].Single()!;
            await ChangeResolvedSelectionAsync("revision", connectionId);

            using var response = await ExchangeWorkspaceCodeAsync(authorizationCode, verifier);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_grant", error.RootElement.GetProperty("error").GetString());
            Assert.False(error.RootElement.TryGetProperty("workspace_selection", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("installation")]
    [InlineData("repository")]
    public async Task ResolvedWorkspaceSelectionCompletionRejectsChangedRepositoryValues(string change)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "native-workspace-v2-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (ownerId, tenantId, projectId, connectionId) = await PrepareResolvedSelectionAsync(directory);
            var intent = ResolvedIntent(ownerId, tenantId, projectId, connectionId, "octo/widget");
            var (query, _, _) = CreateWorkspaceAuthorization(intent);
            var selectionBrowser = new WorkspaceSelectionBrowser(_broker, _fakeIdpClient, followCompletion: false);
            await selectionBrowser.OpenAsync(
                QueryHelpers.AddQueryString("/connect/authorize", query), CancellationToken.None);
            Uri completion = selectionBrowser.CompletionUri!;
            Assert.Contains("workspace_selection_completion", completion.Query, StringComparison.Ordinal);
            Assert.Single(await ReadRepositorySelectionsAsync(ownerId));
            await ChangeResolvedSelectionAsync(change, connectionId);

            using var response = await _broker.GetAsync(completion);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_grant", error.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<(Guid OwnerId, string TenantId, string ProjectId, string ConnectionId)>
        PrepareResolvedSelectionAsync(string directory)
    {
        string tenantId = Guid.NewGuid().ToString("D");
        string projectId = Guid.NewGuid().ToString("D");
        var signedIn = await SignInWorkspaceOwnerAsync(directory, tenantId);
        Guid ownerId = Guid.Parse(signedIn.ActorId);
        const string connectionId = "native_workspace_v2_connection";
        await SeedRepoAppConnectionAsync(ownerId, connectionId);
        return (ownerId, tenantId, projectId, connectionId);
    }

    private static NativeRepoAppSelectionIntent ResolvedIntent(
        Guid ownerId, string tenantId, string projectId, string connectionId, string repositoryFullName) =>
        new(2, ownerId.ToString("D"), tenantId, projectId, connectionId, null, null, null,
            repositoryFullName, "readWorkspaceRepositoryTree");

    private async Task ChangeResolvedSelectionAsync(string change, string connectionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var updated = change switch
        {
            "revision" => await db.RepoAppConnections
                .Where(row => row.ConnectionId == connectionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ConnectionRevision, 8)),
            "installation" => await db.RepoAppInstallations
                .Where(row => row.ConnectionId == connectionId && row.InstallationId == 456)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    row => row.RevokedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)),
            "repository" => await db.RepoAppRepositorySelections
                .Where(row => row.ConnectionId == connectionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RepositoryId, 790)),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(1, updated);
    }

    private Task<HttpResponseMessage> ExchangeWorkspaceCodeAsync(string authorizationCode, string verifier) =>
        _broker.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
                ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
                ["code"] = authorizationCode,
                ["code_verifier"] = verifier,
            }));

    private async Task<(CliIdentity Identity, string ActorId)> SignInWorkspaceOwnerAsync(
        string directory,
        string tenantId)
    {
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
        var identity = new CliIdentity(
            new CliTarget(
                new Uri("https://gateway.test/"),
                new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                IdentityBrokerWebApplicationFactory.CliClientId,
                "https://api.test",
                tenantId),
            ["openid", "api.read", "offline_access", "projects.admin"]);
        var login = await authentication.LoginAsync(identity, CancellationToken.None);
        return (identity, login.ActorId!);
    }

    private static (Dictionary<string, string?> Query, string Verifier, string State) CreateWorkspaceAuthorization(
        NativeRepoAppSelectionIntent intent)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = IdentityBrokerWebApplicationFactory.CliClientId,
            ["redirect_uri"] = IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
            ["scope"] = "openid api.read offline_access projects.admin",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(32)),
            ["workspace_selection_request"] = EncodeIntent(intent),
        };
        return (query, verifier, state);
    }

    private static Task<HttpResponseMessage> PostWorkspaceReviewAsync(
        HttpClient client,
        string reviewHandle,
        string? antiforgeryToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["review_handle"] = reviewHandle,
            ["approve"] = "true",
        };
        if (antiforgeryToken is not null)
            fields["__RequestVerificationToken"] = antiforgeryToken;
        return client.PostAsync(
            "/connect/consent/native-repo-app",
            new FormUrlEncodedContent(fields));
    }

    private static void AddRepoAppTestSettings(Dictionary<string, string?> settings)
    {
        settings["IdentityBroker__GitHubRepoApp__OAuthClientId"] = "repo-app-test-client";
        settings["IdentityBroker__GitHubRepoApp__OAuthClientSecret"] = "repo-app-test-secret";
        settings["IdentityBroker__GitHubRepoApp__CallbackUri"] =
            "https://broker.test/auth/github/repo-app/callback";
        settings["IdentityBroker__GitHubRepoApp__AppId"] = "123";
        settings["IdentityBroker__GitHubRepoApp__AppSlug"] = "agentweaver";
        settings["IdentityBroker__GitHubRepoApp__PrivateKeySecretId"] = "repo-app-private-key";
        settings["IdentityBroker__GitHubRepoApp__PrivateKeySecretVersion"] = "test-version";
    }

    private void ConfigureRepoAppTestServices(IServiceCollection services)
    {
        services.RemoveAll<ISecretRedemption>();
        services.AddSingleton<ISecretRedemption>(new RepoAppSecretRedemption());
        services.RemoveAll<ISecretVersionWriter>();
        services.AddSingleton<ISecretVersionWriter>(new UnusedSecretVersionWriter());
        services.Configure<HttpClientFactoryOptions>("github-repo-app-api", options =>
        {
            options.HttpMessageHandlerBuilderActions.Clear();
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.PrimaryHandler = new RepoAppApiHandler(() => _listDuplicateRepository));
        });
    }

    private async Task SeedRepoAppConnectionAsync(Guid ownerId, string connectionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.RepoAppConnections.Add(new RepoAppConnectionRecord
        {
            ConnectionId = connectionId,
            OwnerId = ownerId,
            GitHubLogin = "connected-user",
            AccessTokenSecretId = "repo-app-user-access",
            AccessTokenSecretVersion = "access-v1",
            AccessTokenExpiresAt = now.AddHours(8),
            RefreshTokenSecretId = "repo-app-user-refresh",
            RefreshTokenSecretVersion = "refresh-v1",
            RefreshTokenExpiresAt = now.AddDays(180),
            ConnectionRevision = 7,
            CredentialRevision = 1,
            State = RepoAppConnectionState.Connected,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<RepoAppRepositorySelectionRecord>> ReadRepositorySelectionsAsync(Guid ownerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        return await db.RepoAppRepositorySelections.AsNoTracking()
            .Where(item => item.OwnerId == ownerId)
            .ToListAsync();
    }

    private static string EncodeIntent(NativeRepoAppSelectionIntent intent)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        try { return WebEncoders.Base64UrlEncode(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string HiddenInput(string html, string name)
    {
        var match = Regex.Match(html,
            $"""<input type="hidden" name="{Regex.Escape(name)}" value="([^"]*)">""");
        Assert.True(match.Success, $"Expected hidden input '{name}' on the repository review page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Base64Url(byte[] value) => WebEncoders.Base64UrlEncode(value);

    private sealed record WorkspaceSelectionBrowserProxy(WebApplication App, Uri BaseAddress);

    private sealed class LoopbackCallbackListener : IDisposable
    {
        private readonly TcpListener _listener;

        public LoopbackCallbackListener(Uri redirectUri)
        {
            RedirectUri = redirectUri;
            _listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
            _listener.Start();
        }

        public Uri RedirectUri { get; }

        public async Task<Uri> WaitForCallbackAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            using var client = await _listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellation.Token)
                ?? throw new InvalidOperationException("The callback request was empty.");
            while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 })
            {
            }

            var requestTarget = requestLine.Split(' ', 3)[1];
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, cancellation.Token);
            return new Uri(RedirectUri, requestTarget);
        }

        public void Dispose() => _listener.Stop();
    }

    private sealed class RepoAppSecretRedemption : ISecretRedemption
    {
        public Task<SecretCredential> RedeemAsync(
            SecretRedemptionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretCredential(
                "repo-app-test-access", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class UnusedSecretVersionWriter : ISecretVersionWriter
    {
        public Task<SecretRef> WriteVersionAsync(
            string secretId, SecretCredential credential, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The repository-selection test does not write secrets.");
    }

    private sealed class RepoAppApiHandler(Func<bool> listDuplicateRepository) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("repo-app-test-access", request.Headers.Authorization?.Parameter);
            string response = request.RequestUri!.PathAndQuery switch
            {
                "/user/installations?per_page=100&page=1" =>
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\"," +
                    "\"type\":\"Organization\"},\"repository_selection\":\"selected\"}]}",
                "/user/installations/456/repositories?per_page=100&page=1" =>
                    "{\"repositories\":[{\"id\":789,\"full_name\":\"octo/widget\"," +
                    "\"owner\":{\"login\":\"octo\"},\"private\":true,\"default_branch\":\"main\"}" +
                    (listDuplicateRepository()
                        ? ",{\"id\":790,\"full_name\":\"octo/widget\",\"owner\":{\"login\":\"octo\"}," +
                            "\"private\":true,\"default_branch\":\"main\"}"
                        : string.Empty) +
                    "]}",
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected controlled GitHub API path: {request.RequestUri.PathAndQuery}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class WorkspaceSelectionBrowser(
        HttpClient broker,
        HttpClient fakeIdp,
        bool approveReview = true,
        bool followCompletion = true) : ICliSystemBrowser, IDisposable
    {
        private readonly HttpClient _callbackClient = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        });

        public int ReviewCount { get; private set; }
        public string ReviewPage { get; private set; } = string.Empty;
        public string? ReviewHandle { get; private set; }
        public string? AntiforgeryToken { get; private set; }
        public Uri? CompletionUri { get; private set; }
        public Uri? CallbackUri { get; private set; }

        public async Task OpenAsync(Uri authorizationUri, CancellationToken ct)
        {
            await OpenAsync(authorizationUri.PathAndQuery, ct);
            if (CallbackUri is not null)
            {
                using var callback = await _callbackClient.GetAsync(CallbackUri, ct);
                Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
            }
        }

        public async Task OpenAsync(string authorizationPath, CancellationToken ct)
        {
            var authorize = await broker.GetAsync(authorizationPath, ct);
            if (authorize.StatusCode == HttpStatusCode.Redirect)
                authorize = await CompleteExternalLoginAsync(authorize, ct);

            await FinishAuthorizationAsync(authorize, ct);
        }

        private async Task<HttpResponseMessage> CompleteExternalLoginAsync(
            HttpResponseMessage initial, CancellationToken ct)
        {
            using (initial)
            using (var upstream = await fakeIdp.GetAsync(initial.Headers.Location!, ct))
            {
                Assert.Equal(HttpStatusCode.Redirect, upstream.StatusCode);
                using var signedIn = await broker.GetAsync(Resolve(upstream.Headers.Location!), ct);
                Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
                using var resumed = await broker.GetAsync(Resolve(signedIn.Headers.Location!), ct);
                Assert.Equal(HttpStatusCode.Redirect, resumed.StatusCode);
                return await broker.GetAsync(Resolve(resumed.Headers.Location!), ct);
            }
        }

        private async Task FinishAuthorizationAsync(HttpResponseMessage response, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    HttpResponseMessage next;
                    using (response)
                    {
                        var html = await response.Content.ReadAsStringAsync(ct);
                        if (html.Contains("name=\"review_handle\"", StringComparison.Ordinal))
                        {
                            ReviewPage = html;
                            ReviewCount++;
                            ReviewHandle = HiddenInput(html, "review_handle");
                            AntiforgeryToken = HiddenInput(html, "__RequestVerificationToken");
                            if (!approveReview) return;
                            using var approved = await broker.PostAsync(
                                "/connect/consent/native-repo-app",
                                new FormUrlEncodedContent(new Dictionary<string, string>
                                {
                                    ["review_handle"] = ReviewHandle,
                                    ["approve"] = "true",
                                    ["__RequestVerificationToken"] = AntiforgeryToken,
                                }), ct);
                            Assert.True(approved.StatusCode == HttpStatusCode.Redirect,
                                $"Repository review returned {(int)approved.StatusCode}: " +
                                await approved.Content.ReadAsStringAsync(ct));
                            CompletionUri = Resolve(approved.Headers.Location!);
                            if (!followCompletion) return;
                            next = await broker.GetAsync(CompletionUri, ct);
                        }
                        else if (html.Contains("name=\"consent_handle\"", StringComparison.Ordinal))
                        {
                            using var approved = await broker.PostAsync(
                                "/connect/consent/native",
                                new FormUrlEncodedContent(new Dictionary<string, string>
                                {
                                    ["consent_handle"] = HiddenInput(html, "consent_handle"),
                                    ["approve"] = "true",
                                    ["__RequestVerificationToken"] =
                                        HiddenInput(html, "__RequestVerificationToken"),
                                }), ct);
                            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
                            next = await broker.GetAsync(Resolve(approved.Headers.Location!), ct);
                        }
                        else
                        {
                            throw new Xunit.Sdk.XunitException("The Broker returned an unexpected authorization page.");
                        }
                    }
                    response = next;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
                {
                    var location = response.Headers.Location
                        ?? throw new Xunit.Sdk.XunitException("The Broker omitted the authorization redirect.");
                    response.Dispose();
                    CallbackUri = Resolve(location);
                    Assert.Equal("127.0.0.1", CallbackUri.Host);
                    Assert.Equal("/oauth/callback", CallbackUri.AbsolutePath);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
                throw new Xunit.Sdk.XunitException(
                    $"Expected a review page or callback redirect, got {response.StatusCode}: {body}");
            }

            response.Dispose();
            throw new Xunit.Sdk.XunitException("The Broker did not finish repository review.");
        }

        public void Dispose() => _callbackClient.Dispose();

        private Uri Resolve(Uri location) =>
            location.IsAbsoluteUri ? location : new Uri(broker.BaseAddress!, location);

        private static string HiddenInput(string html, string name)
        {
            var match = Regex.Match(html,
                $"""<input type="hidden" name="{Regex.Escape(name)}" value="([^"]*)">""");
            Assert.True(match.Success, $"Expected hidden input '{name}' on the authorization page.");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
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
