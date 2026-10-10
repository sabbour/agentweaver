extern alias WebHost;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private bool _allowRepoAppBrowserProviderEffects;
    private int _repoAppBrowserSecretWrites;

    [Fact]
    public async Task RepoAppConnectionCompletesInBrowserThroughBrokerAndGateway()
    {
        const string upstreamSubject = "repo-app-browser-owner";
        const string projectName = "Repo App browser connection";
        _allowRepoAppBrowserProviderEffects = false;
        _repoAppBrowserSecretWrites = 0;

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));

        var tenantAdminToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "repo-app-browser-tenant-admin",
            null,
            null,
            []);
        var tenantAdminSubject = SingleClaim(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(tenantAdminToken).Claims,
            "sub");
        var tenantAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, tenantAdminToken, TenantId, projectName);

        var ownerToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            upstreamSubject,
            null,
            null,
            []);
        var ownerSubject = SingleClaim(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(ownerToken).Claims,
            "sub");
        var ownerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, ownerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);

        var brokerAuthority = $"https://broker.test:{_browserPort}";
        var webAuthority = $"https://web.broker.test:{_browserPort}";
        var brokerIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var browserRedirectUri = $"{webAuthority}/auth/callback";
        var repoAppCallbackUri = $"{brokerAuthority}/auth/github/repo-app/callback";
        var scopes = string.Join(
            " ",
            IdentityBrokerWebApplicationFactory.TestClientScopes.Concat(ProjectScopes));
        var webClient = await StartWebClientAsync(brokerAuthority, browserRedirectUri, scopes);
        try
        {
            _fakeIdp.Subject = upstreamSubject;
            _fakeIdp.TenantIds = [TenantId];
            _fakeIdp.Roles = [];
            await AddBrowserRedirectUriAsync(browserRedirectUri);

            var brokerOAuthHandler = new BrowserRepoAppOAuthResponseHandler(
                _brokerFactory.Server.CreateHandler(),
                repoAppCallbackUri);
            using var brokerProxy = new HttpClient(brokerOAuthHandler)
            {
                BaseAddress = new Uri(brokerAuthority),
            };
            using var fakeIdpProxy = new HttpClient(_fakeIdp.Server.CreateHandler())
            {
                BaseAddress = new Uri(FakeIdentityProvider.Authority),
            };
            using var webClientProxy = new HttpClient
            {
                BaseAddress = webClient.BaseAddress,
            };
            using var webCallbackFactory = new WebApplicationFactory<WebHost::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseWebRoot(Path.Combine(FindRepositoryRootForWebHost(), "apps", "web", "dist"));
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["VITE_IDENTITY_BROKER_URL"] = brokerAuthority,
                            ["VITE_IDENTITY_BROKER_ISSUER"] = brokerIssuer,
                            ["VITE_OAUTH_REDIRECT_URI"] = browserRedirectUri,
                        }));
                });
            using var webCallback = webCallbackFactory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress = new Uri(webAuthority),
                });
            await using var gateway = GatewayProductionResourceServer.Start(
                new X509SecurityKey(certificate),
                (request, cancellationToken) =>
                    ForwardOwnerRequestAsync(request, projects.Client, cancellationToken),
                identityAudience: "https://api.test");
            await using var reverseProxy = await BrowserIntegrationReverseProxy.StartAsync(
                _browserPort,
                brokerProxy,
                gateway.Client,
                webClientProxy,
                webCallback);
            reverseProxy.UseIdentityProvider(fakeIdpProxy);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args =
                    [
                        "--no-proxy-server",
                        $"--host-resolver-rules={reverseProxy.HostResolverRules}",
                    ],
                });
            await using var browserContext = await browser.NewContextAsync(
                new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var page = await browserContext.NewPageAsync();
            var authorizationRequests = brokerOAuthHandler.AuthorizationRequests;
            var authorizationTrace = brokerOAuthHandler.Trace;
            var browserTrace = new ConcurrentQueue<string>();
            page.Console += (_, message) => browserTrace.Enqueue(
                $"console {message.Type}: {message.Text}");
            page.PageError += (_, error) => browserTrace.Enqueue($"page error: {error}");
            page.RequestFailed += (_, request) => browserTrace.Enqueue(
                $"request failed: {request.Method} {request.Url} ({request.Failure})");
            browserContext.Page += (_, popupPage) =>
            {
                popupPage.Console += (_, message) => browserTrace.Enqueue(
                    $"popup console {message.Type}: {message.Text}");
                popupPage.PageError += (_, error) => browserTrace.Enqueue($"popup page error: {error}");
                popupPage.RequestFailed += (_, request) => browserTrace.Enqueue(
                    $"popup request failed: {request.Method} {request.Url} ({request.Failure})");
            };

            await page.AddInitScriptAsync($$"""
                window.__AGENTWEAVER_CONFIG_BASE64__ = {
                  GATEWAY_URL: btoa('/api/v1'),
                  IDENTITY_BROKER_URL: btoa('{{brokerAuthority}}'),
                  IDENTITY_BROKER_ISSUER: btoa('{{brokerIssuer}}'),
                  OAUTH_CLIENT_ID: btoa('{{IdentityBrokerWebApplicationFactory.TestClientId}}'),
                  OAUTH_REDIRECT_URI: btoa('{{browserRedirectUri}}'),
                  OAUTH_SCOPES: btoa('{{scopes}}')
                };
                """);

            try
            {
                await page.GotoAsync(
                    $"{webAuthority}/projects",
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Commit,
                        Timeout = 15_000,
                    });
                await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Continue with Identity Broker" }).ClickAsync();
                await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = "Review access" }).WaitForAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
                await page.GetByText(projectName, new() { Exact = true }).WaitForAsync();

                var configurationPath =
                    $"/api/v1/projects/{Uri.EscapeDataString(project.ProjectId)}/configuration";
                await page.GetByRole(
                    AriaRole.Link,
                    new() { Name = projectName, Exact = true }).ClickAsync();
                await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = projectName, Exact = true }).WaitForAsync();
                var configurationResponseTask = page.WaitForResponseAsync(response =>
                    response.Request.Method == "GET" &&
                    new Uri(response.Url).AbsolutePath.Equals(
                        configurationPath, StringComparison.Ordinal));
                await page.GetByRole(
                    AriaRole.Link,
                    new() { Name = "Configuration" }).First.ClickAsync();
                var configurationResponse = await configurationResponseTask;
                Assert.Equal((int)HttpStatusCode.OK, configurationResponse.Status);
                await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = "Project configuration" }).WaitForAsync();
                await page.GetByLabel("Typed ProjectConfiguration JSON").WaitForAsync();
                await page.GetByText(
                    "No GitHub Repo App connection is available for this identity.")
                    .WaitForAsync();

                _allowRepoAppBrowserProviderEffects = true;
                var popup = await page.RunAndWaitForPopupAsync(
                    () => page.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Connect GitHub Repo App" }).ClickAsync());
                await page.GetByText(
                    "GitHub returned to Agentweaver. Checking the connection status with the Identity Broker.")
                    .WaitForAsync();
                await page.GetByText("Connected as connected-user").WaitForAsync();

                var authorizationRequest = Assert.Single(authorizationRequests);
                var authorizationQuery = QueryHelpers.ParseQuery(authorizationRequest.Query);
                Assert.Equal(repoAppCallbackUri, authorizationQuery["redirect_uri"].ToString());
                Assert.False(string.IsNullOrWhiteSpace(authorizationQuery["state"].ToString()));
                Assert.True(popup.IsClosed);

                using var scope = _brokerFactory.Services.CreateScope();
                var connection = await scope.ServiceProvider
                    .GetRequiredService<IdentityBrokerDbContext>()
                    .RepoAppConnections
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(RepoAppConnectionState.Connected, connection.State);
                Assert.Equal("connected-user", connection.GitHubLogin);
                Assert.Equal(2, _repoAppBrowserSecretWrites);
            }
            catch (Exception exception)
            {
                var openPageDiagnostics = new List<string>();
                foreach (var openPage in browserContext.Pages)
                {
                    var hasOpener = await openPage.EvaluateAsync<bool>("() => Boolean(window.opener)");
                    var issuer = await openPage.EvaluateAsync<string>("() => { const value = window.__AGENTWEAVER_CONFIG_BASE64__?.IDENTITY_BROKER_ISSUER; return value ? atob(value) : '<missing>'; }");
                    var body = await openPage.Locator("body").InnerTextAsync();
                    openPageDiagnostics.Add(
                        $"{openPage.Url}; opener={hasOpener}; issuer={issuer}{Environment.NewLine}{body}");
                }
                throw new InvalidOperationException(
                    $"Repo App browser flow did not complete.{Environment.NewLine}" +
                    $"Body:{Environment.NewLine}{await page.Locator("body").InnerTextAsync()}{Environment.NewLine}" +
                    $"Vite: {webClient.BaseAddress}; running={(!webClient.Process.HasExited).ToString()}{Environment.NewLine}" +
                    $"Bridge:{Environment.NewLine}{reverseProxy.RequestTrace}{Environment.NewLine}" +
                    $"Browser:{Environment.NewLine}{string.Join(Environment.NewLine, browserTrace)}{Environment.NewLine}" +
                    $"OAuth route:{Environment.NewLine}{string.Join(Environment.NewLine, authorizationTrace)}{Environment.NewLine}" +
                    $"Open pages:{Environment.NewLine}{string.Join(Environment.NewLine, openPageDiagnostics)}",
                    exception);
            }
        }
        finally
        {
            _allowRepoAppBrowserProviderEffects = false;
            await StopWebClientAsync(webClient);
        }
    }

    private sealed class BrowserRepoAppOAuthResponseHandler(
        HttpMessageHandler innerHandler,
        string callbackUri) : DelegatingHandler(innerHandler)
    {
        public ConcurrentQueue<Uri> AuthorizationRequests { get; } = new();

        public ConcurrentQueue<string> Trace { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method != HttpMethod.Post ||
                request.RequestUri?.AbsolutePath != "/auth/github/repo-app/connect")
                return response;

            if (response.StatusCode != HttpStatusCode.Found ||
                response.Headers.Location is not { } location)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    "The Broker connect endpoint did not return its expected OAuth redirect.");
            }

            var authorizationUri = location.IsAbsoluteUri
                ? location
                : new Uri(request.RequestUri, location);
            var query = QueryHelpers.ParseQuery(authorizationUri.Query);
            var state = query["state"].ToString();
            var redirectUri = query["redirect_uri"].ToString();
            if (!string.Equals(
                    authorizationUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !authorizationUri.AbsolutePath.Equals(
                    "/login/oauth/authorize", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(state) ||
                !string.Equals(callbackUri, redirectUri, StringComparison.Ordinal))
            {
                response.Dispose();
                throw new InvalidOperationException(
                    "The Broker redirect omitted OAuth state or used an unexpected provider or callback.");
            }

            AuthorizationRequests.Enqueue(authorizationUri);
            var callback = QueryHelpers.AddQueryString(
                redirectUri,
                new Dictionary<string, string?>
                {
                    ["code"] = "browser-repo-app-authorization-code",
                    ["state"] = state,
                });
            response.Headers.Location = new Uri(callback);
            Trace.Enqueue(
                "Rewrote the Broker connect response Location to its callback; all other response headers were preserved.");
            return response;
        }
    }

    private sealed class BrowserRepoAppSecretVersionWriter(
        Func<bool> allowWrite,
        Action recordWrite) : ISecretVersionWriter
    {
        public Task<SecretRef> WriteVersionAsync(
            string secretId,
            SecretCredential credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowWrite())
                throw new Xunit.Sdk.XunitException("Repo App provider writes are disabled for this test.");
            recordWrite();
            return Task.FromResult(new SecretRef(secretId, "version-1"));
        }
    }

    private sealed class BrowserRepoAppOAuthHandler(Func<bool> allowProviderEffects) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!allowProviderEffects())
                throw new Xunit.Sdk.XunitException("Unexpected Repo App OAuth provider request.");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/login/oauth/access_token", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"browser-user-access-secret\",\"expires_in\":28800," +
                    "\"refresh_token\":\"browser-user-refresh-secret\",\"refresh_token_expires_in\":15897600}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class BrowserRepoAppApiHandler(Func<bool> allowProviderEffects) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!allowProviderEffects())
                throw new Xunit.Sdk.XunitException("Unexpected Repo App API provider request.");
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/user", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"login\":\"connected-user\"}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BrowserRepoAppUnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException(
                $"Unexpected Repo App provider request: {request.Method} {request.RequestUri}.");
    }
}
