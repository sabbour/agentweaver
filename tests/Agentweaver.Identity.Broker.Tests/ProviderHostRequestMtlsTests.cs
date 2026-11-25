extern alias EnvironmentService;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using EnvironmentDbContext = EnvironmentService::Agentweaver.Environment.EnvironmentDbContext;
using EnvironmentEndpoints = EnvironmentService::Agentweaver.Environment.ProviderHostRequestEndpoints;
using HttpProjectsHostRequestProofClient = EnvironmentService::Agentweaver.Environment.HttpProjectsHostRequestProofClient;
using IProjectsHostRequestProofClient = EnvironmentService::Agentweaver.Environment.IProjectsHostRequestProofClient;
using ProviderHostRequestStore = EnvironmentService::Agentweaver.Environment.ProviderHostRequestStore;
using UnavailableProjectsHostRequestProofClient = EnvironmentService::Agentweaver.Environment.UnavailableProjectsHostRequestProofClient;
using ProjectsConfigDbContext = ProjectsConfig::Agentweaver.Projects.Config.ProjectsConfigDbContext;
using HttpEnvironmentHostRequestClient = ProjectsConfig::Agentweaver.Projects.Config.HttpEnvironmentHostRequestClient;
using IEnvironmentHostRequestClient = ProjectsConfig::Agentweaver.Projects.Config.IEnvironmentHostRequestClient;
using EnvironmentHostRequestOutcome = ProjectsConfig::Agentweaver.Projects.Config.EnvironmentHostRequestOutcome;
using ProviderHostRequestProofEndpoints = ProjectsConfig::Agentweaver.Projects.Config.ProviderHostRequestProofEndpoints;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class ProviderHostRequestMtlsTests
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    [Fact]
    public async Task BidirectionalPinnedMtlsChannelSucceedsWithCrlAndRejectsWrongOrMissingPeers()
    {
        using var crlServer = new LocalCrlServer();
        var tempDir = Directory.CreateTempSubdirectory("aw-mtls-bidir-");
        try
        {
            using var caKey = RSA.Create(2048);
            using var caCert = CreateCa(caKey, $"Agentweaver Test CA {Guid.NewGuid():N}");
            var caPath = Path.Combine(tempDir.FullName, "ca.crt");
            File.WriteAllText(caPath, caCert.ExportCertificatePem());

            var crlPath = "crl-" + Guid.NewGuid().ToString("N");
            var crlUri = new Uri(crlServer.BaseAddress, crlPath);
            crlServer.SetCrl(crlPath, BuildCrl(caCert, caKey, [], 1));

            using var projCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            using var envCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            using var wrongCert = CreateLeaf(caCert, "127.0.0.1", crlUri);

            var projCertPath = Path.Combine(tempDir.FullName, "proj.crt");
            var projKeyPath = Path.Combine(tempDir.FullName, "proj.key");
            var envCertPath = Path.Combine(tempDir.FullName, "env.crt");
            var envKeyPath = Path.Combine(tempDir.FullName, "env.key");
            var wrongCertPath = Path.Combine(tempDir.FullName, "wrong.crt");
            var wrongKeyPath = Path.Combine(tempDir.FullName, "wrong.key");

            WritePem(projCert, projCertPath, projKeyPath);
            WritePem(envCert, envCertPath, envKeyPath);
            WritePem(wrongCert, wrongCertPath, wrongKeyPath);

            var projPin = Sha256Hex(projCert);
            var envPin = Sha256Hex(envCert);
            var wrongPin = Sha256Hex(wrongCert);
            var projPort = GetFreeTcpPort();
            var envPort = GetFreeTcpPort();
            var projPrincipal = Guid.NewGuid().ToString("D");
            var envPrincipal = Guid.NewGuid().ToString("D");

            await using var envApp = await StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, projPin, envPrincipal, projPrincipal, caPath);
            await using var projApp = await StartProjectsAsync(
                projPort, projCertPath, projKeyPath, projPin, envPort, envPin, projPrincipal, envPrincipal, caPath);

            // 1. Projects -> Environment succeeds and triggers CRL lookup.
            using (var scope = projApp.Services.CreateScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<IEnvironmentHostRequestClient>();
                var sendResult = await client.SendHostRequestAsync(
                    new ProviderHostRequestDispatch(
                        Guid.NewGuid(), "tenant-1", ModelProviderBindingScope.Platform, null,
                        "byok-test", 1, new string('a', 64), Guid.NewGuid(), "actor-1", "127.0.0.1", 443),
                    CancellationToken.None);
                Assert.True(
                    sendResult.Outcome == EnvironmentHostRequestOutcome.PermanentFailure,
                    $"Unexpected outcome {sendResult.Outcome}: {sendResult.FailureCode}");
                Assert.Equal("destination_host_invalid", sendResult.FailureCode);
            }
            Assert.True(crlServer.RequestCount > 0, "Expected at least one CRL lookup.");

            // 2. Environment -> Projects succeeds.
            using (var scope = envApp.Services.CreateScope())
            {
                var proofClient = scope.ServiceProvider.GetRequiredService<IProjectsHostRequestProofClient>();
                var proof = await proofClient.VerifyCurrentAsync([], CancellationToken.None);
                Assert.NotNull(proof);
                Assert.Empty(proof!);
            }

            // 3. Missing peer certificate is rejected at TLS handshake in both directions.
            using (var unauthHandler = new HttpClientHandler
                   {
                       ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                   })
            using (var unauthClient = new HttpClient(unauthHandler))
            {
                await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                    unauthClient.PostAsync($"https://127.0.0.1:{envPort}/internal/egress/host-requests",
                        new StringContent("{}")));
                await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                    unauthClient.PostAsync($"https://127.0.0.1:{projPort}/internal/egress/host-requests/proof",
                        new StringContent("{}")));
            }

            // 4. Wrong same-issuer peer certificate (valid dual-EKU, different pin) is rejected in both directions.
            await AssertClientRejectedAsync(wrongCertPath, wrongKeyPath, wrongPin, envPort, envPin, caPath);
            await AssertClientRejectedAsync(wrongCertPath, wrongKeyPath, wrongPin, projPort, projPin, caPath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PinnedMtlsChannelRejectsRevokedPeerCertificate()
    {
        using var crlServer = new LocalCrlServer();
        var tempDir = Directory.CreateTempSubdirectory("aw-mtls-revoked-");
        try
        {
            using var caKey = RSA.Create(2048);
            using var caCert = CreateCa(caKey, $"Agentweaver Revoked CA {Guid.NewGuid():N}");
            var caPath = Path.Combine(tempDir.FullName, "ca.crt");
            File.WriteAllText(caPath, caCert.ExportCertificatePem());

            var crlPath = "crl-" + Guid.NewGuid().ToString("N");
            var crlUri = new Uri(crlServer.BaseAddress, crlPath);

            using var envCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            using var revokedProjCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            crlServer.SetCrl(crlPath, BuildCrl(caCert, caKey, [revokedProjCert], 1));

            var envCertPath = Path.Combine(tempDir.FullName, "env.crt");
            var envKeyPath = Path.Combine(tempDir.FullName, "env.key");
            var revokedCertPath = Path.Combine(tempDir.FullName, "revoked.crt");
            var revokedKeyPath = Path.Combine(tempDir.FullName, "revoked.key");
            WritePem(envCert, envCertPath, envKeyPath);
            WritePem(revokedProjCert, revokedCertPath, revokedKeyPath);

            var envPin = Sha256Hex(envCert);
            var revokedPin = Sha256Hex(revokedProjCert);
            var envPort = GetFreeTcpPort();
            var projPort = GetFreeTcpPort();

            await using var envApp = await StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, revokedPin,
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), caPath);

            await AssertClientRejectedAsync(revokedCertPath, revokedKeyPath, revokedPin, envPort, envPin, caPath);
            Assert.True(crlServer.RequestCount > 0, "Expected CRL lookup for revoked certificate.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PinnedMtlsChannelRejectsMissingAndUnreachableRevocationData()
    {
        using var crlServer = new LocalCrlServer();
        var tempDir = Directory.CreateTempSubdirectory("aw-mtls-nocrl-");
        try
        {
            using var caKey = RSA.Create(2048);
            using var caCert = CreateCa(caKey, $"Agentweaver Unreachable CA {Guid.NewGuid():N}");
            var caPath = Path.Combine(tempDir.FullName, "ca.crt");
            File.WriteAllText(caPath, caCert.ExportCertificatePem());

            var validCrlPath = "crl-" + Guid.NewGuid().ToString("N");
            crlServer.SetCrl(validCrlPath, BuildCrl(caCert, caKey, [], 1));

            using var envCert = CreateLeaf(caCert, "127.0.0.1", new Uri(crlServer.BaseAddress, validCrlPath));
            using var noCrlCert = CreateLeaf(caCert, "127.0.0.1", crlUri: null);
            using var unreachableCert = CreateLeaf(
                caCert, "127.0.0.1", new Uri(crlServer.BaseAddress, "missing-" + Guid.NewGuid().ToString("N")));

            var envCertPath = Path.Combine(tempDir.FullName, "env.crt");
            var envKeyPath = Path.Combine(tempDir.FullName, "env.key");
            var noCrlCertPath = Path.Combine(tempDir.FullName, "nocrl.crt");
            var noCrlKeyPath = Path.Combine(tempDir.FullName, "nocrl.key");
            var unreachableCertPath = Path.Combine(tempDir.FullName, "unreach.crt");
            var unreachableKeyPath = Path.Combine(tempDir.FullName, "unreach.key");

            WritePem(envCert, envCertPath, envKeyPath);
            WritePem(noCrlCert, noCrlCertPath, noCrlKeyPath);
            WritePem(unreachableCert, unreachableCertPath, unreachableKeyPath);

            var envPin = Sha256Hex(envCert);
            var noCrlPin = Sha256Hex(noCrlCert);
            var unreachablePin = Sha256Hex(unreachableCert);
            var envPort = GetFreeTcpPort();
            var projPort = GetFreeTcpPort();

            await using var envApp = await StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, noCrlPin,
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), caPath,
                extraPins: [unreachablePin]);

            var envIdentity = envApp.Services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                HttpProjectsHostRequestProofClient.ChannelKey);
            Assert.False(envIdentity.AcceptClientCertificate(
                unreachableCert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
            await AssertClientRejectedAsync(noCrlCertPath, noCrlKeyPath, noCrlPin, envPort, envPin, caPath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoopbackCrlEndpointServesConcurrentMtlsSwapsFileAndIsolatesFromHttpsPort()
    {
        var tempDir = Directory.CreateTempSubdirectory("aw-loopback-crl-");
        try
        {
            using var caKey = RSA.Create(2048);
            using var caCert = CreateCa(caKey, $"Agentweaver Loopback CRL CA {Guid.NewGuid():N}");
            var caPath = Path.Combine(tempDir.FullName, "ca.crt");
            File.WriteAllText(caPath, caCert.ExportCertificatePem());

            var crlTarget1 = Path.Combine(tempDir.FullName, "ca-v1.crl");
            var crlTarget2 = Path.Combine(tempDir.FullName, "ca-v2.crl");
            var crlLinkPath = Path.Combine(tempDir.FullName, "ca.crl");
            var crlBytes1 = BuildCrl(caCert, caKey, [], 1);
            await File.WriteAllBytesAsync(crlTarget1, crlBytes1);
            File.Copy(crlTarget1, crlLinkPath);

            var envCrlPort = GetFreeTcpPort();
            var projCrlPort = GetFreeTcpPort();
            var envPort = GetFreeTcpPort();
            var projPort = GetFreeTcpPort();

            // Leaf certificates point to the loopback CRL listener on envCrlPort.
            var crlUri = new Uri($"http://127.0.0.1:{envCrlPort}{LoopbackCrlEndpoint.RoutePath}");
            using var projCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            using var envCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            var projCertPath = Path.Combine(tempDir.FullName, "proj.crt");
            var projKeyPath = Path.Combine(tempDir.FullName, "proj.key");
            var envCertPath = Path.Combine(tempDir.FullName, "env.crt");
            var envKeyPath = Path.Combine(tempDir.FullName, "env.key");
            WritePem(projCert, projCertPath, projKeyPath);
            WritePem(envCert, envCertPath, envKeyPath);

            var projPin = Sha256Hex(projCert);
            var envPin = Sha256Hex(envCert);
            var projPrincipal = Guid.NewGuid().ToString("D");
            var envPrincipal = Guid.NewGuid().ToString("D");

            // Port collision and missing CRL file fail fast at startup.
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, projPin, envPrincipal, envPrincipal == projPrincipal ? Guid.NewGuid().ToString("D") : projPrincipal,
                caPath, crlPort: envPort, crlPath: crlLinkPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, projPin, envPrincipal, projPrincipal,
                caPath, crlPort: envCrlPort, crlPath: Path.Combine(tempDir.FullName, "missing.crl")));

            await using var envApp = await StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, projPin, envPrincipal, projPrincipal,
                caPath, crlPort: envCrlPort, crlPath: crlLinkPath);
            await using var projApp = await StartProjectsAsync(
                projPort, projCertPath, projKeyPath, projPin, envPort, envPin, projPrincipal, envPrincipal,
                caPath, crlPort: projCrlPort, crlPath: crlLinkPath);

            // Concurrent mTLS requests in both directions succeed using the programmatic loopback CRL listener.
            var sends = Enumerable.Range(0, 6).Select(async _ =>
            {
                using var scope = projApp.Services.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<IEnvironmentHostRequestClient>();
                var sendResult = await client.SendHostRequestAsync(
                    new ProviderHostRequestDispatch(
                        Guid.NewGuid(), "tenant-1", ModelProviderBindingScope.Platform, null,
                        "byok-test", 1, new string('a', 64), Guid.NewGuid(), "actor-1", "127.0.0.1", 443),
                    CancellationToken.None);
                Assert.Equal(EnvironmentHostRequestOutcome.PermanentFailure, sendResult.Outcome);
                Assert.Equal("destination_host_invalid", sendResult.FailureCode);
            });
            var proofs = Enumerable.Range(0, 6).Select(async _ =>
            {
                using var scope = envApp.Services.CreateScope();
                var proofClient = scope.ServiceProvider.GetRequiredService<IProjectsHostRequestProofClient>();
                var proof = await proofClient.VerifyCurrentAsync([], CancellationToken.None);
                Assert.NotNull(proof);
                Assert.Empty(proof!);
            });
            await Task.WhenAll(sends.Concat(proofs));

            // Loopback CRL endpoint serves application/pkix-crl with no-store and reflects atomic file replacement.
            using var http = new HttpClient();
            var firstCrl = await http.GetAsync(crlUri);
            Assert.Equal(HttpStatusCode.OK, firstCrl.StatusCode);
            Assert.Equal("application/pkix-crl", firstCrl.Content.Headers.ContentType?.MediaType);
            Assert.True(firstCrl.Headers.CacheControl?.NoStore);
            Assert.Equal(crlBytes1, await firstCrl.Content.ReadAsByteArrayAsync());

            var crlBytes2 = BuildCrl(caCert, caKey, [projCert], 2);
            await File.WriteAllBytesAsync(crlTarget2, crlBytes2);
            File.Move(crlTarget2, crlLinkPath, overwrite: true);
            var secondCrl = await http.GetByteArrayAsync(crlUri);
            Assert.Equal(crlBytes2, secondCrl);

            // Requesting /ca.crl on the HTTPS mTLS port (8444) is rejected with 404.
            var projIdentity = projApp.Services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                HttpEnvironmentHostRequestClient.ChannelKey);
            using var mtlsClient = new HttpClient(projIdentity.CreateHandler());
            var httpsCrlResponse = await mtlsClient.GetAsync($"https://127.0.0.1:{envPort}{LoopbackCrlEndpoint.RoutePath}");
            Assert.Equal(HttpStatusCode.NotFound, httpsCrlResponse.StatusCode);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoopbackCrlEndpointRefreshesAfterNextUpdateAndRejectsNewlyRevokedPeer()
    {
        var tempDir = Directory.CreateTempSubdirectory("aw-loopback-crl-refresh-");
        try
        {
            using var caKey = RSA.Create(2048);
            using var caCert = CreateCa(caKey, $"Agentweaver Refresh CRL CA {Guid.NewGuid():N}");
            var caPath = Path.Combine(tempDir.FullName, "ca.crt");
            File.WriteAllText(caPath, caCert.ExportCertificatePem());

            var crlLinkPath = Path.Combine(tempDir.FullName, "ca.crl");
            var crlNextPath = Path.Combine(tempDir.FullName, "ca-next.crl");
            // Seed a valid CRL for host startup so host boot time cannot race the short nextUpdate window.
            await File.WriteAllBytesAsync(crlLinkPath, BuildCrl(caCert, caKey, [], 1));

            var envCrlPort = GetFreeTcpPort();
            var projCrlPort = GetFreeTcpPort();
            var envPort = GetFreeTcpPort();
            var projPort = GetFreeTcpPort();

            var crlUri = new Uri($"http://127.0.0.1:{envCrlPort}{LoopbackCrlEndpoint.RoutePath}");
            using var projCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            using var envCert = CreateLeaf(caCert, "127.0.0.1", crlUri);
            var projCertPath = Path.Combine(tempDir.FullName, "proj.crt");
            var projKeyPath = Path.Combine(tempDir.FullName, "proj.key");
            var envCertPath = Path.Combine(tempDir.FullName, "env.crt");
            var envKeyPath = Path.Combine(tempDir.FullName, "env.key");
            WritePem(projCert, projCertPath, projKeyPath);
            WritePem(envCert, envCertPath, envKeyPath);

            var projPin = Sha256Hex(projCert);
            var envPin = Sha256Hex(envCert);
            var projPrincipal = Guid.NewGuid().ToString("D");
            var envPrincipal = Guid.NewGuid().ToString("D");

            await using var envApp = await StartEnvironmentAsync(
                envPort, envCertPath, envKeyPath, envPin, projPort, projPin, envPrincipal, projPrincipal,
                caPath, crlPort: envCrlPort, crlPath: crlLinkPath);
            await using var projApp = await StartProjectsAsync(
                projPort, projCertPath, projKeyPath, projPin, envPort, envPin, projPrincipal, envPrincipal,
                caPath, crlPort: projCrlPort, crlPath: crlLinkPath);

            var shortNextUpdate = DateTimeOffset.UtcNow.AddSeconds(4);
            await File.WriteAllBytesAsync(crlNextPath, BuildCrl(caCert, caKey, [], 2, shortNextUpdate));
            File.Move(crlNextPath, crlLinkPath, overwrite: true);

            using (var scope = projApp.Services.CreateScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<IEnvironmentHostRequestClient>();
                var initial = await client.SendHostRequestAsync(
                    new ProviderHostRequestDispatch(
                        Guid.NewGuid(), "tenant-1", ModelProviderBindingScope.Platform, null,
                        "byok-test", 1, new string('a', 64), Guid.NewGuid(), "actor-1", "127.0.0.1", 443),
                    CancellationToken.None);
                Assert.True(DateTimeOffset.UtcNow < shortNextUpdate,
                    "Initial mTLS handshake exceeded the short CRL nextUpdate window.");
                Assert.Equal(EnvironmentHostRequestOutcome.PermanentFailure, initial.Outcome);
                Assert.Equal("destination_host_invalid", initial.FailureCode);
            }

            // Wait past the short CRL nextUpdate, then atomically replace ca.crl with one revoking projCert.
            var remaining = shortNextUpdate.AddMilliseconds(500) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);
            await File.WriteAllBytesAsync(crlNextPath, BuildCrl(caCert, caKey, [projCert], 3));
            File.Move(crlNextPath, crlLinkPath, overwrite: true);

            // Force a fresh TLS handshake (not a pooled HTTP connection) to verify CRL refresh after nextUpdate.
            var projIdentity = projApp.Services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                HttpEnvironmentHostRequestClient.ChannelKey);
            using var freshClient = new HttpClient(projIdentity.CreateHandler());
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                freshClient.GetAsync($"https://127.0.0.1:{envPort}/internal/egress/host-requests"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task<WebApplication> StartEnvironmentAsync(
        int envPort,
        string envCertPath,
        string envKeyPath,
        string envPin,
        int projPort,
        string projPin,
        string envPrincipal,
        string projPrincipal,
        string caPath,
        string[]? extraPins = null,
        int? crlPort = null,
        string? crlPath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:ProviderHostRequest:Url"] = $"https://127.0.0.1:{envPort}",
            [$"{HttpProjectsHostRequestProofClient.SectionName}:LocalServiceName"] = "Environment",
            [$"{HttpProjectsHostRequestProofClient.SectionName}:LocalServicePrincipalId"] = envPrincipal,
            [$"{HttpProjectsHostRequestProofClient.SectionName}:LocalCertificatePemPath"] = envCertPath,
            [$"{HttpProjectsHostRequestProofClient.SectionName}:LocalPrivateKeyPemPath"] = envKeyPath,
            [$"{HttpProjectsHostRequestProofClient.SectionName}:LocalCertificateSha256"] = envPin,
            [$"{HttpProjectsHostRequestProofClient.SectionName}:ListenerPort"] = envPort.ToString(),
            [$"{HttpProjectsHostRequestProofClient.SectionName}:PeerServiceName"] = "Projects",
            [$"{HttpProjectsHostRequestProofClient.SectionName}:PeerServicePrincipalId"] = projPrincipal,
            [$"{HttpProjectsHostRequestProofClient.SectionName}:PeerOwnerAddress"] = $"https://127.0.0.1:{projPort}/",
            [$"{HttpProjectsHostRequestProofClient.SectionName}:PeerCertificateSha256Pins:0"] = projPin,
        };
        if (crlPort is { } port && crlPath is not null)
        {
            values[$"{LoopbackCrlEndpoint.SectionName}:LoopbackPort"] = port.ToString();
            values[$"{LoopbackCrlEndpoint.SectionName}:FilePath"] = crlPath;
        }
        if (extraPins is not null)
        {
            for (var i = 0; i < extraPins.Length; i++)
                values[$"{HttpProjectsHostRequestProofClient.SectionName}:PeerCertificateSha256Pins:{i + 1}"] = extraPins[i];
        }

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(
            new DbContextOptionsBuilder<EnvironmentDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=unused")
                .Options);
        builder.Services.AddScoped<EnvironmentDbContext>();
        builder.Services.AddScoped<IProjectsHostRequestProofClient, UnavailableProjectsHostRequestProofClient>();
        builder.Services.AddScoped<ProviderHostRequestStore>();
        Assert.True(IssueSyncServiceTransport.AddChannel(
            builder,
            HttpProjectsHostRequestProofClient.ChannelKey,
            HttpProjectsHostRequestProofClient.SectionName,
            "Environment",
            "Projects",
            listener: true,
            backendCaFile: caPath));
        var loopbackCrl = LoopbackCrlEndpoint.AddLoopbackCrl(builder);
        builder.Services.AddScoped<IProjectsHostRequestProofClient>(services =>
            new HttpProjectsHostRequestProofClient(
                services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                    HttpProjectsHostRequestProofClient.ChannelKey)));

        var app = builder.Build();
        EnvironmentEndpoints.MapProviderHostRequestInternalEndpoints(app, enabled: true);
        app.MapLoopbackCrl(loopbackCrl);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartProjectsAsync(
        int projPort,
        string projCertPath,
        string projKeyPath,
        string projPin,
        int envPort,
        string envPin,
        string projPrincipal,
        string envPrincipal,
        string caPath,
        int? crlPort = null,
        string? crlPath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:ProviderHostRequest:Url"] = $"https://127.0.0.1:{projPort}",
            [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalServiceName"] = "Projects",
            [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalServicePrincipalId"] = projPrincipal,
            [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalCertificatePemPath"] = projCertPath,
            [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalPrivateKeyPemPath"] = projKeyPath,
            [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalCertificateSha256"] = projPin,
            [$"{HttpEnvironmentHostRequestClient.SectionName}:ListenerPort"] = projPort.ToString(),
            [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerServiceName"] = "Environment",
            [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerServicePrincipalId"] = envPrincipal,
            [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerOwnerAddress"] = $"https://127.0.0.1:{envPort}/",
            [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerCertificateSha256Pins:0"] = envPin,
        };
        if (crlPort is { } port && crlPath is not null)
        {
            values[$"{LoopbackCrlEndpoint.SectionName}:LoopbackPort"] = port.ToString();
            values[$"{LoopbackCrlEndpoint.SectionName}:FilePath"] = crlPath;
        }
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(
            new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=unused")
                .Options);
        builder.Services.AddScoped<ProjectsConfigDbContext>();
        Assert.True(IssueSyncServiceTransport.AddChannel(
            builder,
            HttpEnvironmentHostRequestClient.ChannelKey,
            HttpEnvironmentHostRequestClient.SectionName,
            "Projects",
            "Environment",
            listener: true,
            backendCaFile: caPath));
        var loopbackCrl = LoopbackCrlEndpoint.AddLoopbackCrl(builder);
        builder.Services.AddScoped<IEnvironmentHostRequestClient>(services =>
            new HttpEnvironmentHostRequestClient(
                services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                    HttpEnvironmentHostRequestClient.ChannelKey)));

        var app = builder.Build();
        ProviderHostRequestProofEndpoints.MapProviderHostRequestProof(app, enabled: true);
        app.MapLoopbackCrl(loopbackCrl);
        await app.StartAsync();
        return app;
    }

    private static async Task AssertClientRejectedAsync(
        string clientCertPath,
        string clientKeyPath,
        string clientPin,
        int targetPort,
        string targetPin,
        string caPath)
    {
        var options = new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Projects",
            LocalServicePrincipalId = Guid.NewGuid().ToString("D"),
            LocalCertificatePemPath = clientCertPath,
            LocalPrivateKeyPemPath = clientKeyPath,
            LocalCertificateSha256 = clientPin,
            ListenerPort = GetFreeTcpPort(),
            PeerServiceName = "Environment",
            PeerServicePrincipalId = Guid.NewGuid().ToString("D"),
            PeerOwnerAddress = $"https://127.0.0.1:{targetPort}/",
            PeerCertificateSha256Pins = [targetPin],
        };
        using var identity = new RuntimeServiceTlsIdentity(
            options, "Projects", "Environment", TimeProvider.System, caPath);
        using var handler = identity.CreateHandler();
        using var client = new HttpClient(handler);
        try
        {
            using var resp = await client.PostAsync($"https://127.0.0.1:{targetPort}/internal/egress/host-requests",
                new StringContent("{}"));
            throw new Xunit.Sdk.XunitException($"Expected TLS rejection, got HTTP {(int)resp.StatusCode}");
        }
        catch (HttpRequestException)
        {
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Sha256Hex(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();

    private static void WritePem(X509Certificate2 cert, string certPath, string keyPath)
    {
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
    }

    private static X509Certificate2 CreateCa(RSA key, string cn)
    {
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(5));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca, string sanIpOrDns, Uri? crlUri)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={sanIpOrDns}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthOid), new Oid(ClientAuthOid)], true));
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(sanIpOrDns, out var ip))
            san.AddIpAddress(ip);
        else
            san.AddDnsName(sanIpOrDns);
        req.CertificateExtensions.Add(san.Build());
        if (crlUri is not null)
            req.CertificateExtensions.Add(new X509Extension(
                "2.5.29.31", CreateCrlDistributionPoints(crlUri), critical: false));
        req.CertificateExtensions.Add(new X509Extension(
            "2.5.29.35", CreateAuthorityKeyIdentifier(ca), critical: false));
        var now = DateTimeOffset.UtcNow;
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = req.Create(ca, now.AddMinutes(-5), now.AddDays(1), serial);
        return X509Certificate2.CreateFromPem(issued.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static byte[] BuildCrl(
        X509Certificate2 issuer,
        RSA issuerKey,
        IReadOnlyCollection<X509Certificate2> revokedCerts,
        int crlNumber,
        DateTimeOffset? nextUpdate = null)
    {
        var now = DateTimeOffset.UtcNow;
        var builder = new CertificateRevocationListBuilder();
        foreach (var cert in revokedCerts)
            builder.AddEntry(cert, now.AddMinutes(-1), X509RevocationReason.KeyCompromise);
        using var issuerWithKey = issuer.HasPrivateKey ? null : issuer.CopyWithPrivateKey(issuerKey);
        return builder.Build(
            issuerWithKey ?? issuer,
            crlNumber,
            nextUpdate ?? now.AddDays(1),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1,
            now.AddMinutes(-5));
    }

    private static byte[] CreateCrlDistributionPoints(Uri crlUri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            writer.WriteCharacterString(
                UniversalTagNumber.IA5String, crlUri.AbsoluteUri,
                new Asn1Tag(TagClass.ContextSpecific, 6));
        return writer.Encode();
    }

    private static byte[] CreateAuthorityKeyIdentifier(X509Certificate2 issuer)
    {
        var ski = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().RawData;
        var reader = new AsnReader(ski, AsnEncodingRules.DER);
        var keyId = reader.ReadOctetString();
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
            writer.WriteOctetString(keyId, new Asn1Tag(TagClass.ContextSpecific, 0));
        return writer.Encode();
    }

    private sealed class LocalCrlServer : IDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly ConcurrentDictionary<string, byte[]> crls = new(StringComparer.Ordinal);
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);
        public Uri BaseAddress { get; }

        public LocalCrlServer()
        {
            var port = GetFreeTcpPort();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            listener = new HttpListener();
            listener.Prefixes.Add(BaseAddress.AbsoluteUri);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync().ConfigureAwait(false);
                        Interlocked.Increment(ref requestCount);
                        var path = ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
                        if (crls.TryGetValue(path, out var bytes))
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/pkix-crl";
                            ctx.Response.ContentLength64 = bytes.Length;
                            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                        ctx.Response.Close();
                    }
                    catch when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                    }
                }
            });
        }

        public void SetCrl(string path, byte[] bytes) => crls[path] = bytes;

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
            cts.Dispose();
        }
    }
}
