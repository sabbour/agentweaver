extern alias WebHost;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class BrowserIntegrationReverseProxyTests
{
    [Fact]
    public async Task BrowserCallbackUsesScrubbedAppShellWithoutCors()
    {
        var port = BrowserIntegrationReverseProxy.GetAvailablePort();
        using var broker = CreateClient("broker");
        using var gateway = CreateClient("gateway");
        using var webClient = CreateClient("vite");
        using var webCallbackFactory = new WebApplicationFactory<WebHost::Program>()
            .WithWebHostBuilder(builder =>
                builder.UseWebRoot(Path.Combine(FindRepositoryRoot(), "apps", "web")));
        using var webCallback = webCallbackFactory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"https://web.broker.test:{port}")
            });
        await using var proxy = await BrowserIntegrationReverseProxy.StartAsync(
            port,
            broker,
            gateway,
            webClient,
            webCallback);
        var origin = proxy.WebAuthority;
        using var bridgeClient = CreateBridgeClient(origin);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/callback?code=must-not-be-embedded&state=state-value");

        using var response = await bridgeClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains(
            "window.__AGENTWEAVER_IDENTITY_CALLBACK__ = window.location.search;",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.history.replaceState(null, '', identityCallbackPath);",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl.search", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-embedded", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserFederationRedirectsUseTheEphemeralBridgePort()
    {
        var port = BrowserIntegrationReverseProxy.GetAvailablePort();
        using var broker = new HttpClient(new BrowserResponseHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/connect/authorize")
                return Redirect("https://fake-idp.test/connect/authorize");
            if (request.RequestUri?.AbsolutePath == "/signin-oidc")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("broker-callback"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri($"https://broker.test:{port}")
        };
        using var gateway = CreateClient("gateway");
        using var identityProvider = new HttpClient(new BrowserResponseHandler(_ =>
            Redirect("https://broker.test/signin-oidc")))
        {
            BaseAddress = new Uri($"https://fake-idp.test:{port}")
        };
        using var webClient = CreateClient("web");
        await using var proxy = await BrowserIntegrationReverseProxy.StartAsync(
            port,
            broker,
            gateway,
            webClient);
        proxy.UseIdentityProvider(identityProvider);

        using var bridgeClient = CreateBridgeClient(proxy.BrokerAuthority);
        using var response = await bridgeClient.GetAsync("/connect/authorize");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("broker-callback", await response.Content.ReadAsStringAsync());
        Assert.Contains($"GET https://fake-idp.test:{port}/connect/authorize", proxy.RequestTrace);
        Assert.Contains($"GET https://broker.test:{port}/signin-oidc", proxy.RequestTrace);
    }

    [Fact]
    public async Task BrowserCanReachHttpsBridgeAndLoadWebClient()
    {
        using var broker = CreateClient("broker");
        using var gateway = CreateClient("gateway");
        using var identityProvider = CreateClient("identity-provider");
        using var webClient = CreateClient("<!doctype html><html><body>bridge-ready</body></html>");
        await using var proxy = await BrowserIntegrationReverseProxy.StartAsync(
            BrowserIntegrationReverseProxy.GetAvailablePort(),
            broker,
            gateway,
            webClient);
        proxy.UseIdentityProvider(identityProvider);

        var bridgeHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using (var bridgeClient = new HttpClient(bridgeHandler)
               {
                   BaseAddress = new Uri(proxy.BrokerAuthority)
               })
        {
            try
            {
                using var bridgeResponse = await bridgeClient.GetAsync("/");
                Assert.Equal(HttpStatusCode.OK, bridgeResponse.StatusCode);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"HTTPS probe failed.{Environment.NewLine}{proxy.RequestTrace}",
                    exception);
            }
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-proxy-server", $"--host-resolver-rules={proxy.HostResolverRules}"]
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        try
        {
            var page = await context.NewPageAsync();
            IResponse? response;
            try
            {
                response = await page.GotoAsync(
                    $"{proxy.BrokerAuthority}/",
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Commit,
                        Timeout = 15_000
                    });
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Browser could not reach the bridge.{Environment.NewLine}{proxy.RequestTrace}",
                    exception);
            }

            Assert.Equal((int)HttpStatusCode.OK, response?.Status);
            Assert.Equal("bridge-ready", await page.Locator("body").InnerTextAsync());
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static HttpClient CreateClient(string responseBody)
    {
        var client = new HttpClient(new BrowserResponseHandler(responseBody))
        {
            BaseAddress = new Uri("http://test-server")
        };
        return client;
    }

    private static HttpClient CreateBridgeClient(string authority)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri(authority) };
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "apps", "web", "package.json")))
                    return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root for the Web callback test.");
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } };

    private sealed class BrowserResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory =
            responseFactory ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            });

        public BrowserResponseHandler(string responseBody)
            : this(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/html")
            })
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responseFactory(request));
    }
}
