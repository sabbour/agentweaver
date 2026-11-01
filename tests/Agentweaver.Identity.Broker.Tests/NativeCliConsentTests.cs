using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class NativeCliConsentTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _postgres;
    private FakeIdentityProvider _fakeIdp = null!;
    private IdentityBrokerWebApplicationFactory _factory = null!;
    private HttpClient _broker = null!;
    private HttpClient _fakeIdpClient = null!;
    private (string PfxPath, string Password) _signingCertificate;

    public NativeCliConsentTests(PostgresContainerFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        await BrokerCliScopePermissionTestSupport.ProvisionAndMigrateLegacyCliClientAsync(connectionString);
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _signingCertificate = TestSigningCertificate.Create();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString, _fakeIdp, signingCertificate: _signingCertificate, registerCliClient: true,
            configure: settings => settings["IdentityBroker__Clients__1__Scopes__5"] = "<img>");
        _broker = CreateBrokerClient(_factory);
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
    public async Task NativeCliFirstConsentApprovesAndContinuesExactPkceState()
    {
        var page = await StartCliConsentAsync();
        Assert.Equal(HttpStatusCode.OK, page.Response.StatusCode);
        Assert.StartsWith("text/html", page.Response.Content.Headers.ContentType?.ToString());
        Assert.Contains("no-store", page.Response.Headers.CacheControl?.ToString());
        Assert.Contains("default-src 'none'", page.Response.Headers.GetValues("Content-Security-Policy").Single());
        var html = await page.Response.Content.ReadAsStringAsync();
        Assert.Contains("Authorize Agentweaver CLI", html);
        Assert.Contains("openid", html);
        Assert.Contains("api.read", html);
        Assert.Contains("offline_access", html);

        var consent = ReadConsent(html);
        var approved = await SubmitNativeConsentAsync(_broker, consent, approve: true);
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var issued = await _broker.GetAsync(Resolve(approved.Headers.Location!));
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        var callback = issued.Headers.Location!;
        Assert.Equal(page.State, QueryValue(callback, "state"));
        var code = QueryValue(callback, "code");
        Assert.False(string.IsNullOrWhiteSpace(code));

        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId,
            IdentityBrokerWebApplicationFactory.CliClientRedirectUri, code!, page.CodeVerifier);
        Assert.False(string.IsNullOrWhiteSpace(tokens.GetProperty("access_token").GetString()));
        Assert.True(tokens.TryGetProperty("refresh_token", out _));

        var replay = await SubmitNativeConsentAsync(_broker, consent, approve: true);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var replayCode = await BrokerFlowDriver.RefreshAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId,
            tokens.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, replayCode.StatusCode);
    }

    [Fact]
    public async Task CliWildcardAllowsRegisteredScopesButNotUnknownScopesOrOtherClients()
    {
        var (_, cliChallenge) = Pkce.Create();
        using var cliRequest = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId,
            IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
            "openid projects.admin", cliChallenge, "S256");
        Assert.Equal(HttpStatusCode.Redirect, cliRequest.StatusCode);

        var promptsBeforeUnknownScope = _fakeIdp.AuthorizationPrompts.Count;
        var (_, unknownChallenge) = Pkce.Create();
        using var unknownScope = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId,
            IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
            "openid future.unregistered", unknownChallenge, "S256");
        Assert.Equal(HttpStatusCode.BadRequest, unknownScope.StatusCode);
        Assert.Contains("error:invalid_scope", await unknownScope.Content.ReadAsStringAsync());
        Assert.Equal(promptsBeforeUnknownScope, _fakeIdp.AuthorizationPrompts.Count);

        var (_, otherClientChallenge) = Pkce.Create();
        using var otherClient = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid projects.admin", otherClientChallenge, "S256");
        Assert.Equal(HttpStatusCode.BadRequest, otherClient.StatusCode);
        Assert.Contains("error:invalid_request", await otherClient.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NativeCliConsentMatchesAppAppearanceAndEscapesRequestedScopes()
    {
        var page = await StartCliConsentAsync("openid api.read <img>");
        Assert.Equal(HttpStatusCode.OK, page.Response.StatusCode);

        var csp = page.Response.Headers.GetValues("Content-Security-Policy").Single();
        var styleNonce = Regex.Match(csp, "style-src 'nonce-([^']+)'").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(styleNonce));
        Assert.Contains("img-src 'self'", csp);
        Assert.Contains(
            $"form-action 'self' {IdentityBrokerWebApplicationFactory.CliClientRedirectUri}",
            csp);
        var alternatePort = new UriBuilder(IdentityBrokerWebApplicationFactory.CliClientRedirectUri)
        {
            Port = 49174,
        }.Uri.GetLeftPart(UriPartial.Path);
        Assert.DoesNotContain(alternatePort, csp);
        Assert.DoesNotContain("http://127.0.0.1:*", csp);
        Assert.DoesNotContain("form-action *", csp);

        var html = await page.Response.Content.ReadAsStringAsync();
        Assert.Contains($"<style nonce=\"{styleNonce}\">", html);
        Assert.Contains("""<img src="/agentweaver.png" alt="">""", html);
        Assert.Contains("background: #efeae7", html);
        Assert.Contains("background: #fdfbf8", html);
        Assert.Contains("background: #272320", html);
        Assert.Contains("width: min(100%, 520px)", html);
        Assert.Contains("border-radius: 16px", html);
        Assert.Contains("<li><code>&lt;img&gt;</code></li>", html);
        Assert.DoesNotContain("<li><code><img></code></li>", html);
        Assert.Contains("""<form method="post" action="/connect/consent/native">""", html);
        Assert.Contains("""name="approve" value="true">Allow</button>""", html);
        Assert.Contains("""name="approve" value="false">Deny</button>""", html);
        Assert.Contains("""name="consent_handle" value=""", html);
        Assert.Contains("""name="__RequestVerificationToken" value=""", html);

        using var logo = await _broker.GetAsync("/agentweaver.png");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        var image = await logo.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image.Take(8).ToArray());
    }

    [Fact]
    public async Task NativeCliConsentEnforcesAntiforgeryCookieClientAndOneUseDenial()
    {
        var page = await StartCliConsentAsync();
        var consent = ReadConsent(await page.Response.Content.ReadAsStringAsync());

        var missingAntiforgery = await SubmitNativeConsentAsync(
            _broker, consent with { AntiforgeryToken = null }, approve: true);
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        var invalidAntiforgery = await SubmitNativeConsentAsync(
            _broker, consent with { AntiforgeryToken = "forged-token" }, approve: true);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAntiforgery.StatusCode);
        var duplicateField = await PostRawNativeConsentAsync(
            $"consent_handle={Uri.EscapeDataString(consent.Handle)}&approve=true&approve=false&__RequestVerificationToken={Uri.EscapeDataString(consent.AntiforgeryToken!)}");
        Assert.Equal(HttpStatusCode.BadRequest, duplicateField.StatusCode);
        var unknownField = await PostRawNativeConsentAsync(
            $"consent_handle={Uri.EscapeDataString(consent.Handle)}&approve=true&__RequestVerificationToken={Uri.EscapeDataString(consent.AntiforgeryToken!)}&extra=x");
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        var oversized = await PostRawNativeConsentAsync(new string('x', 17 * 1024));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using (var anonymous = CreateBrokerClient(_factory))
        {
            var wrongCookie = await SubmitNativeConsentAsync(anonymous, consent, approve: true);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongCookie.StatusCode);
        }

        var webPrompt = await BrokerFlowDriver.BeginConsentAsync(
            _broker, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid profile email",
            Pkce.Create().Challenge);
        var wrongClient = await SubmitNativeConsentAsync(_broker, new NativeConsent(
            webPrompt.GetProperty("consent_handle").GetString()!,
            webPrompt.GetProperty("csrf_token").GetString()), approve: true);
        Assert.Equal(HttpStatusCode.BadRequest, wrongClient.StatusCode);
        var webCompatibility = await BrokerFlowDriver.SubmitConsentAsync(_broker, webPrompt);
        Assert.Equal(HttpStatusCode.Redirect, webCompatibility.StatusCode);

        var denied = await SubmitNativeConsentAsync(_broker, consent, approve: false);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var denial = denied.Headers.Location!;
        Assert.Equal("access_denied", QueryValue(denial, "error"));
        Assert.Equal(page.State, QueryValue(denial, "state"));
        var replay = await SubmitNativeConsentAsync(_broker, consent, approve: false);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task NativeCliConsentAllowsOnlyRegisteredCallbackAcrossBrowserRedirects()
    {
        using var callbackListener = new LoopbackCallbackListener(
            new Uri(IdentityBrokerWebApplicationFactory.CliClientRedirectUri));
        var playwright = await _postgres.GetPlaywrightAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-proxy-server"],
        });

        var deniedPage = await StartCliConsentAsync();
        var deniedCallbackTask = callbackListener.WaitForCallbackAsync(TimeSpan.FromSeconds(15));
        var denied = await SubmitNativeConsentInChromiumAsync(browser, deniedPage, approve: false);
        var deniedCallback = await AwaitCallbackOrExplainAsync(deniedCallbackTask, denied);
        var deniedCsp = deniedPage.Response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains(
            $"form-action 'self' {IdentityBrokerWebApplicationFactory.CliClientRedirectUri}",
            deniedCsp);
        Assert.Equal(deniedPage.State, QueryValue(deniedCallback, "state"));
        Assert.Equal("access_denied", QueryValue(deniedCallback, "error"));
        Assert.Null(QueryValue(deniedCallback, "code"));
        Assert.Empty(denied.CspViolations);

        var approvedPage = await StartCliConsentAsync();
        var approvedCallbackTask = callbackListener.WaitForCallbackAsync(TimeSpan.FromSeconds(15));
        var approved = await SubmitNativeConsentInChromiumAsync(browser, approvedPage, approve: true);
        var approvedCallback = await AwaitCallbackOrExplainAsync(approvedCallbackTask, approved);
        var code = QueryValue(approvedCallback, "code");
        Assert.Equal(approvedPage.State, QueryValue(approvedCallback, "state"));
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Empty(approved.CspViolations);

        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId,
            IdentityBrokerWebApplicationFactory.CliClientRedirectUri, code!,
            approvedPage.CodeVerifier);
        Assert.False(string.IsNullOrWhiteSpace(tokens.GetProperty("access_token").GetString()));

        var unapprovedRedirectUri =
            new Uri(IdentityBrokerWebApplicationFactory.CliClientRedirectUri)
                .GetLeftPart(UriPartial.Authority) + "/oauth/unapproved";
        var (_, challenge) = Pkce.Create();
        using var rejected = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.CliClientId, unapprovedRedirectUri,
            "openid api.read offline_access", challenge, "S256", Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(rejected.Headers.Location);
    }

    private async Task<NativeConsentPage> StartCliConsentAsync(string scope = "openid api.read offline_access")
    {
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var (verifier, challenge) = Pkce.Create();
        var authorize = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker,
            IdentityBrokerWebApplicationFactory.CliClientId,
            IdentityBrokerWebApplicationFactory.CliClientRedirectUri,
            scope,
            challenge,
            "S256",
            state,
            nonce);
        if (authorize.StatusCode == HttpStatusCode.OK)
            return new NativeConsentPage(authorize, state, verifier);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

        var upstream = await _fakeIdpClient.GetAsync(authorize.Headers.Location!);
        Assert.Equal(HttpStatusCode.Redirect, upstream.StatusCode);
        var callback = await _broker.GetAsync(Resolve(upstream.Headers.Location!));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var resume = await _broker.GetAsync(Resolve(callback.Headers.Location!));
        Assert.Equal(HttpStatusCode.Redirect, resume.StatusCode);
        var consent = await _broker.GetAsync(Resolve(resume.Headers.Location!));

        return new NativeConsentPage(consent, state, verifier);
    }

    private async Task<BrowserConsentResult> SubmitNativeConsentInChromiumAsync(
        IBrowser browser, NativeConsentPage consentPage, bool approve)
    {
        var proxy = await StartBrowserBrokerProxyAsync(consentPage);
        await using var proxyApp = proxy.App;
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var violations = new List<string>();
        var trace = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("form-action", StringComparison.OrdinalIgnoreCase))
                violations.Add(message.Text);
        };
        page.RequestFailed += (_, request) => trace.Add($"failed {request.Method} {request.Url}: {request.Failure}");

        var response = await page.GotoAsync(new Uri(proxy.BaseAddress, "/native-consent").AbsoluteUri);
        Assert.Equal((int)HttpStatusCode.OK, response?.Status);
        await page.Locator($"button[name='approve'][value='{(approve ? "true" : "false")}']").ClickAsync();
        trace.Add($"final browser URL: {page.Url}");
        return new BrowserConsentResult(violations, trace);
    }

    private async Task<BrowserProxy> StartBrowserBrokerProxyAsync(NativeConsentPage consentPage)
    {
        var consentHtml = await consentPage.Response.Content.ReadAsStringAsync();
        var csp = consentPage.Response.Headers.GetValues("Content-Security-Policy").Single();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/native-consent", async context =>
        {
            context.Response.StatusCode = (int)consentPage.Response.StatusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = csp;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await context.Response.WriteAsync(consentHtml);
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
        return new BrowserProxy(app, new Uri(address));
    }

    private static async Task<Uri> AwaitCallbackOrExplainAsync(
        Task<Uri> callbackTask, BrowserConsentResult result)
    {
        try
        {
            return await callbackTask;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Browser callback not reached. CSP violations: {string.Join(" | ", result.CspViolations)}. " +
                string.Join("; ", result.Trace),
                exception);
        }
    }

    private static async Task<HttpResponseMessage> SubmitNativeConsentAsync(
        HttpClient client, NativeConsent consent, bool approve)
    {
        var fields = new Dictionary<string, string>
        {
            ["consent_handle"] = consent.Handle,
            ["approve"] = approve ? "true" : "false",
        };
        if (consent.AntiforgeryToken is not null)
            fields["__RequestVerificationToken"] = consent.AntiforgeryToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/consent/native")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostRawNativeConsentAsync(string body) =>
        PostRawNativeConsentAsync(_broker, body);

    private static Task<HttpResponseMessage> PostRawNativeConsentAsync(HttpClient client, string body) =>
        client.PostAsync("/connect/consent/native",
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));

    private static NativeConsent ReadConsent(string html)
    {
        var handle = HiddenInput(html, "consent_handle");
        var antiforgery = HiddenInput(html, "__RequestVerificationToken");
        return new NativeConsent(handle, antiforgery);
    }

    private static string HiddenInput(string html, string name)
    {
        var match = Regex.Match(html, $"""<input type="hidden" name="{Regex.Escape(name)}" value="([^"]*)">""");
        Assert.True(match.Success, $"Expected hidden input '{name}' in native CLI consent page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static HttpClient CreateBrokerClient(IdentityBrokerWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });

    private Uri Resolve(Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(_broker.BaseAddress!, location);

    private static string? QueryValue(Uri uri, string name)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    private sealed record BrowserProxy(WebApplication App, Uri BaseAddress);
    private sealed record BrowserConsentResult(
        IReadOnlyCollection<string> CspViolations, IReadOnlyCollection<string> Trace);
    private sealed record NativeConsent(string Handle, string? AntiforgeryToken);
    private sealed record NativeConsentPage(HttpResponseMessage Response, string State, string CodeVerifier);

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
}
