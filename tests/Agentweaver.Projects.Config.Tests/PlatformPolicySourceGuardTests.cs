using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class PlatformPolicySourceGuardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReusedConnectionDeniesTheSameLeafAfterItsCachedCrlExpires(int httpVersion)
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        certificates.SetUnrevokedCrl(TimeSpan.FromSeconds(3));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var values = certificates.Configuration();
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();
        var connectionIds = new List<string>();
        app.Use((context, next) =>
        {
            connectionIds.Add(context.Connection.Id);
            return next(context);
        });
        app.UsePlatformPolicySourceGuard(port);
        app.MapGet(PlatformPolicySourceTls.RoutePath, () => Results.Text("source"));
        await app.StartAsync();
        try
        {
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                    peer is not null && peer.RawData.AsSpan().SequenceEqual(certificates.ServerCertificate.RawData),
            };
            handler.ClientCertificates.Add(certificates.EnvironmentClient);
            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = new Version(httpVersion, httpVersion == 1 ? 1 : 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            var address = values["urls"] + PlatformPolicySourceTls.RoutePath;
            using var first = await client.GetAsync(address);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var requestsBeforeRevocation = certificates.CrlRequestCount;

            // CRL timestamps have one-second precision; publish a strictly newer issuer timestamp.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            certificates.Revoke(certificates.EnvironmentClient);
            await Task.Delay(TimeSpan.FromSeconds(3));
            using var revoked = await client.GetAsync(address);

            Assert.True(revoked.StatusCode == HttpStatusCode.Forbidden,
                $"Status {revoked.StatusCode}; CRL requests {requestsBeforeRevocation} -> {certificates.CrlRequestCount}.");
            Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await revoked.Content.ReadAsStringAsync());
            Assert.Equal(2, connectionIds.Count);
            Assert.Equal(connectionIds[0], connectionIds[1]);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task CaReplacementBeforeHeadersReplacesEveryProtectedResult(int statusCode)
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var values = certificates.Configuration();
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();
        app.UsePlatformPolicySourceGuard(port);
        app.MapGet(PlatformPolicySourceTls.RoutePath, () =>
        {
            File.WriteAllText(certificates.CaPath, certificates.OtherCa.ExportCertificatePem());
            return Results.Json(new { code = "protected_result", artifact = "must not escape" },
                statusCode: statusCode);
        });
        await app.StartAsync();
        try
        {
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                    peer is not null && peer.RawData.AsSpan().SequenceEqual(certificates.ServerCertificate.RawData),
            };
            handler.ClientCertificates.Add(certificates.EnvironmentClient);
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(values["urls"] + PlatformPolicySourceTls.RoutePath);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("RequireCertificate", null)]
    [InlineData("DelayCertificate", null)]
    [InlineData(null, "Http1AndHttp2AndHttp3")]
    public async Task UnsupportedEffectiveListenerProfileFailsStartup(string? certificateMode, string? protocols)
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var values = certificates.Configuration();
        values["Kestrel:Endpoints:Projects:Url"] = "https://127.0.0.1:8443";
        values["Kestrel:Endpoints:Projects:ClientCertificateMode"] = certificateMode;
        values["Kestrel:Endpoints:Projects:Protocols"] = protocols;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
    }

    [Fact]
    public async Task PreparationRouteSeparatesPeersAndRequiresWorkerPins()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var preparationId = Guid.NewGuid();
        var prepPath = $"{PlatformPolicySourceTls.PreparationRoutePrefix}{preparationId:D}";

        // Disabled preparation route keeps head route active and returns 404 for preparation paths.
        using (var disabledListener = new TcpListener(IPAddress.Loopback, 0))
        {
            disabledListener.Start();
            var port = ((IPEndPoint)disabledListener.LocalEndpoint).Port;
            disabledListener.Stop();
            var values = certificates.Configuration(includePreparationWorkerPins: false);
            values["urls"] = $"https://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values);
            BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
            PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
            await using var app = builder.Build();
            app.UsePlatformPolicySourceGuard(port);
            app.MapGet(PlatformPolicySourceTls.RoutePath, () => Results.Text("head"));
            await app.StartAsync();
            try
            {
                using var envClient = CreateClient(certificates.EnvironmentClient, certificates.ServerCertificate);
                using var headResponse = await envClient.GetAsync(values["urls"] + PlatformPolicySourceTls.RoutePath);
                Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);

                using var prepResponse = await envClient.GetAsync(values["urls"] + prepPath);
                Assert.Equal(HttpStatusCode.NotFound, prepResponse.StatusCode);
                Assert.Equal("{\"code\":\"not_found\"}", await prepResponse.Content.ReadAsStringAsync());
            }
            finally
            {
                await app.StopAsync();
            }
        }

        // Enabled preparation route enforces strict bidirectional peer separation.
        using (var enabledListener = new TcpListener(IPAddress.Loopback, 0))
        {
            enabledListener.Start();
            var port = ((IPEndPoint)enabledListener.LocalEndpoint).Port;
            enabledListener.Stop();
            var values = certificates.Configuration(includePreparationWorkerPins: true);
            values["urls"] = $"https://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values);
            BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
            PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
            await using var app = builder.Build();
            app.UsePlatformPolicySourceGuard(port);
            app.MapGet(PlatformPolicySourceTls.RoutePath, () => Results.Text("head"));
            app.MapGet(PlatformPolicySourceTls.PreparationRoutePattern, (string preparationId) => Results.Text($"prep:{preparationId}"));
            await app.StartAsync();
            try
            {
                using var envClient = CreateClient(certificates.EnvironmentClient, certificates.ServerCertificate);
                using var workerClient = CreateClient(certificates.PreparationWorkerClient, certificates.ServerCertificate);

                using var envOnHead = await envClient.GetAsync(values["urls"] + PlatformPolicySourceTls.RoutePath);
                Assert.Equal(HttpStatusCode.OK, envOnHead.StatusCode);

                using var envOnPrep = await envClient.GetAsync(values["urls"] + prepPath);
                Assert.Equal(HttpStatusCode.Forbidden, envOnPrep.StatusCode);
                Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await envOnPrep.Content.ReadAsStringAsync());

                using var workerOnHead = await workerClient.GetAsync(values["urls"] + PlatformPolicySourceTls.RoutePath);
                Assert.Equal(HttpStatusCode.Forbidden, workerOnHead.StatusCode);
                Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await workerOnHead.Content.ReadAsStringAsync());

                using var workerOnPrep = await workerClient.GetAsync(values["urls"] + prepPath);
                Assert.Equal(HttpStatusCode.OK, workerOnPrep.StatusCode);
            }
            finally
            {
                await app.StopAsync();
            }
        }
    }

    [Fact]
    public async Task ReusedConnectionDeniesTheSameWorkerLeafAfterItsCachedCrlExpires()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        certificates.SetUnrevokedCrl(TimeSpan.FromSeconds(3));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var values = certificates.Configuration(includePreparationWorkerPins: true);
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();
        var connectionIds = new List<string>();
        app.Use((context, next) =>
        {
            connectionIds.Add(context.Connection.Id);
            return next(context);
        });
        app.UsePlatformPolicySourceGuard(port);
        app.MapGet(PlatformPolicySourceTls.PreparationRoutePattern, () => Results.Text("prep"));
        await app.StartAsync();
        try
        {
            using var client = CreateClient(certificates.PreparationWorkerClient, certificates.ServerCertificate);
            var address = $"{values["urls"]}{PlatformPolicySourceTls.PreparationRoutePrefix}{Guid.NewGuid():D}";
            using var first = await client.GetAsync(address);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            certificates.Revoke(certificates.PreparationWorkerClient);
            await Task.Delay(TimeSpan.FromSeconds(3));
            using var revoked = await client.GetAsync(address);

            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
            Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await revoked.Content.ReadAsStringAsync());
            Assert.Equal(2, connectionIds.Count);
            Assert.Equal(connectionIds[0], connectionIds[1]);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CaReplacementBeforeHeadersReplacesProtectedPreparationResult()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var values = certificates.Configuration(includePreparationWorkerPins: true);
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();
        app.UsePlatformPolicySourceGuard(port);
        app.MapGet(PlatformPolicySourceTls.PreparationRoutePattern, () =>
        {
            File.WriteAllText(certificates.CaPath, certificates.OtherCa.ExportCertificatePem());
            return Results.Json(new { code = "protected_result", artifact = "must not escape" }, statusCode: 200);
        });
        await app.StartAsync();
        try
        {
            using var client = CreateClient(certificates.PreparationWorkerClient, certificates.ServerCertificate);
            var address = $"{values["urls"]}{PlatformPolicySourceTls.PreparationRoutePrefix}{Guid.NewGuid():D}";
            using var response = await client.GetAsync(address);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("{\"code\":\"policy_source_peer_denied\"}", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task PreparationRouteRejectsAbsentIdAuthorizationHeaderAndUnsupportedVerb()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var values = certificates.Configuration(includePreparationWorkerPins: true);
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        await using var app = builder.Build();
        app.UsePlatformPolicySourceGuard(port);
        app.MapGet(PlatformPolicySourceTls.PreparationRoutePattern, () => Results.Text("prep"));
        await app.StartAsync();
        try
        {
            using var client = CreateClient(certificates.PreparationWorkerClient, certificates.ServerCertificate);
            var baseUrl = values["urls"]!;

            using var noSegment = await client.GetAsync(baseUrl + "/internal/policy-source/platform/preparations");
            Assert.Equal(HttpStatusCode.NotFound, noSegment.StatusCode);
            Assert.Equal("{\"code\":\"not_found\"}", await noSegment.Content.ReadAsStringAsync());

            using var trailingSlash = await client.GetAsync(baseUrl + "/internal/policy-source/platform/preparations/");
            Assert.Equal(HttpStatusCode.NotFound, trailingSlash.StatusCode);
            Assert.Equal("{\"code\":\"not_found\"}", await trailingSlash.Content.ReadAsStringAsync());

            var validPath = $"{baseUrl}{PlatformPolicySourceTls.PreparationRoutePrefix}{Guid.NewGuid():D}";
            using var authRequest = new HttpRequestMessage(HttpMethod.Get, validPath);
            authRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
            using var authResponse = await client.SendAsync(authRequest);
            Assert.Equal(HttpStatusCode.BadRequest, authResponse.StatusCode);
            Assert.Equal("{\"code\":\"policy_source_request_invalid\"}", await authResponse.Content.ReadAsStringAsync());

            using var postResponse = await client.PostAsync(validPath, content: null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
            Assert.Contains("GET", postResponse.Content.Headers.Allow);
            Assert.Equal("{\"code\":\"policy_source_request_invalid\"}", await postResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static HttpClient CreateClient(X509Certificate2 clientCertificate, X509Certificate2 serverCertificate)
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                peer is not null && peer.RawData.AsSpan().SequenceEqual(serverCertificate.RawData),
        };
        handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler);
    }
}
