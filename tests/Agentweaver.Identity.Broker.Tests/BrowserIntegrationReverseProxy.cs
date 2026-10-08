using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class BrowserIntegrationReverseProxy : IAsyncDisposable
{
    private const string BrokerHost = "broker.test";
    private const string IdentityProviderHost = "fake-idp.test";

    private readonly WebApplication _application;
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient _broker;
    private readonly HttpClient _gateway;
    private readonly HttpClient _webClient;
    private readonly int _port;
    private readonly ConcurrentQueue<string> _requestTrace = new();
    private HttpClient? _identityProvider;

    private BrowserIntegrationReverseProxy(
        WebApplication application,
        X509Certificate2 certificate,
        HttpClient broker,
        HttpClient gateway,
        HttpClient webClient,
        int port)
    {
        _application = application;
        _certificate = certificate;
        _broker = broker;
        _gateway = gateway;
        _webClient = webClient;
        _port = port;
        BrokerAuthority = $"https://{BrokerHost}:{port}";
        IdentityProviderAuthority = $"https://{IdentityProviderHost}:{port}";
    }

    public string BrokerAuthority { get; }

    public string IdentityProviderAuthority { get; }

    public string HostResolverRules =>
        $"MAP {BrokerHost} 127.0.0.1 , MAP {IdentityProviderHost} 127.0.0.1";

    public string? BrokerAccessToken { get; private set; }

    public string RequestTrace => string.Join(Environment.NewLine, _requestTrace);

    public static async Task<BrowserIntegrationReverseProxy> StartAsync(
        int port,
        HttpClient broker,
        HttpClient gateway,
        HttpClient webClient)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=agentweaver-browser-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(BrokerHost);
        subjectAlternativeNames.AddDnsName(IdentityProviderHost);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));
        using var generatedCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        var password = Guid.NewGuid().ToString("N");
        var certificate = X509CertificateLoader.LoadPkcs12(
            generatedCertificate.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.UserKeySet);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, port, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
                listen.UseHttps(certificate, options =>
                    options.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
            }));
        var application = builder.Build();
        var proxy = new BrowserIntegrationReverseProxy(
            application,
            certificate,
            broker,
            gateway,
            webClient,
            port);
        application.Run(proxy.ForwardAsync);
        try
        {
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            proxy._requestTrace.Enqueue(
                $"Listening: {(addresses is { Count: > 0 } ? string.Join(", ", addresses) : "<none>")}");
            return proxy;
        }
        catch
        {
            await application.DisposeAsync();
            certificate.Dispose();
            throw;
        }
    }

    public void UseIdentityProvider(HttpClient identityProvider) =>
        _identityProvider = identityProvider;

    private async Task ForwardAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var path = context.Request.Path.Value ?? string.Empty;
        _requestTrace.Enqueue($"{context.Request.Method} https://{context.Request.Host}{path}{context.Request.QueryString}");
        HttpClient? destination;
        var brokerRequest = false;

        if (string.Equals(host, IdentityProviderHost, StringComparison.OrdinalIgnoreCase))
        {
            destination = _identityProvider;
        }
        else if (string.Equals(host, BrokerHost, StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("/api/v1", StringComparison.Ordinal))
                destination = _gateway;
            else if (path.StartsWith("/connect/", StringComparison.Ordinal) ||
                     path.StartsWith("/signin-", StringComparison.Ordinal))
            {
                destination = _broker;
                brokerRequest = true;
            }
            else
            {
                destination = _webClient;
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (destination is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (path.EndsWith("/events/live", StringComparison.Ordinal) &&
            HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}");
        if (context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            HttpMethods.IsPost(context.Request.Method) ||
            HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method))
            request.Content = new ByteArrayContent(
                await ReadRequestBodyAsync(context.Request, context.RequestAborted));

        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = string.Join(", ", header.Value.ToArray());
            if (request.Content is not null && header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.TryAddWithoutValidation(header.Key, value);
            else
                request.Headers.TryAddWithoutValidation(header.Key, value);
        }

        using var response = await destination.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);
        _requestTrace.Enqueue($"{(int)response.StatusCode} {host}{path}");
        var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        if (brokerRequest && response.StatusCode == HttpStatusCode.BadRequest)
            _requestTrace.Enqueue($"Broker error: {System.Text.Encoding.UTF8.GetString(body)}");
        if (brokerRequest &&
            path.Equals("/connect/token", StringComparison.Ordinal) &&
            response.IsSuccessStatusCode)
        {
            using var tokenResponse = JsonDocument.Parse(body);
            if (tokenResponse.RootElement.TryGetProperty("access_token", out var token) &&
                token.ValueKind == JsonValueKind.String)
                BrokerAccessToken = token.GetString();
        }

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
        {
            var values = header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)
                ? header.Value.Select(RewriteLocation).ToArray()
                : header.Value.ToArray();
            context.Response.Headers.Append(header.Key, values);
        }
        foreach (var header in response.Content.Headers)
        {
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers.Append(header.Key, header.Value.ToArray());
        }
        context.Response.Headers.Remove("transfer-encoding");
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private string RewriteLocation(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var location) ||
            !location.IsDefaultPort ||
            (!string.Equals(location.Host, BrokerHost, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(location.Host, IdentityProviderHost, StringComparison.OrdinalIgnoreCase)))
            return value;

        // The test issuer stays host-only; this port routes browser redirects to the local HTTPS bridge.
        return new UriBuilder(location) { Port = _port }.Uri.AbsoluteUri;
    }

    private static async Task<byte[]> ReadRequestBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        return body.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        _certificate.Dispose();
    }

    public static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
