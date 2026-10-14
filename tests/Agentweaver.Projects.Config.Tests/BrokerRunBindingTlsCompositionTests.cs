using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class BrokerRunBindingTlsCompositionTests
{
    [Fact]
    public void DisabledCompositionDoesNotAddTlsOrLoadCertificates()
    {
        var builder = Builder([]);

        BrokerRunBindingTlsIdentity.Configure(builder, enabled: false);

        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(BrokerRunBindingTlsIdentity));
    }

    [Fact]
    public void TlsSettingsAreRejectedWhenPrivateAudienceIsDisabled()
    {
        var builder = Builder(new()
        {
            ["ProjectsConfig:Tls:CertificatePath"] = Path.Combine(Path.GetTempPath(), "missing.pem")
        });

        Assert.Throws<InvalidOperationException>(() =>
            BrokerRunBindingTlsIdentity.Configure(builder, enabled: false));
    }

    [Theory]
    [InlineData("ProjectsConfig:Tls:KeyPath")]
    [InlineData("ProjectsConfig:Tls:ServerCertificateSha256")]
    public void EnabledCompositionRequiresEveryTlsSetting(string missingKey)
    {
        using var certificate = TestServerCertificate.Create();
        var settings = certificate.Configuration();
        settings.Remove(missingKey);
        var builder = Builder(settings);

        Assert.Throws<InvalidOperationException>(() =>
            BrokerRunBindingTlsIdentity.Configure(builder, enabled: true));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("client")]
    [InlineData("expired")]
    public void RejectsCertificatesWithoutCurrentServerPurposeOrMatchingHash(string profile)
    {
        using var certificate = TestServerCertificate.Create(profile);
        var settings = certificate.Configuration();
        if (profile == "server")
            settings["ProjectsConfig:Tls:ServerCertificateSha256"] = new string('0', 64);
        var builder = Builder(settings);

        Assert.Throws<InvalidOperationException>(() =>
            BrokerRunBindingTlsIdentity.Configure(builder, enabled: true));
    }

    [Fact]
    public void RejectsCertificateAndPrivateKeyMismatch()
    {
        using var certificate = TestServerCertificate.Create();
        var settings = certificate.Configuration();
        using var other = TestServerCertificate.Create();
        settings["ProjectsConfig:Tls:KeyPath"] = other.KeyPath;
        var builder = Builder(settings);

        Assert.ThrowsAny<CryptographicException>(() =>
            BrokerRunBindingTlsIdentity.Configure(builder, enabled: true));
    }

    [Fact]
    public void ForwardedHttpsHeaderWithoutNativeHandshakeIsNotPrivateTls()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        Assert.False(BrokerRunBindingTlsIdentity.IsNativeHttps(context));
    }

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task KestrelUsesPinnedCertificateAndNativeTlsOnPrivateProofRoute(SslProtocols protocol)
    {
        using var certificate = TestServerCertificate.Create();
        var builder = Builder(certificate.Configuration());
        BrokerRunBindingTlsIdentity.Configure(builder, enabled: true);
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
        var app = builder.Build();
        const string proofRoute = "/internal/projects/project-1/runs/run-1/broker-run-binding";
        app.MapPost(proofRoute, (HttpContext context) =>
            BrokerRunBindingTlsIdentity.IsNativeHttps(context)
                ? Results.Text(context.Features.Get<ITlsHandshakeFeature>()!.Protocol.ToString())
                : Results.StatusCode(StatusCodes.Status403Forbidden));
        await app.StartAsync();
        try
        {
            var address = Assert.Single(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses);
            Assert.StartsWith("https://", address, StringComparison.Ordinal);
            byte[]? peerCertificateDigest = null;
            var peerErrors = SslPolicyErrors.None;
            using var handler = new HttpClientHandler
            {
                SslProtocols = protocol,
                ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                {
                    peerErrors = errors;
                    if (peer is null)
                        return false;
                    peerCertificateDigest = SHA256.HashData(peer.Export(X509ContentType.Cert));
                    return true;
                }
            };
            using var client = new HttpClient(handler);
            using var response = await client.PostAsync(address + proofRoute, content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(protocol.ToString(), await response.Content.ReadAsStringAsync());
            Assert.False(peerErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.NotNull(peerCertificateDigest);
            Assert.True(CryptographicOperations.FixedTimeEquals(
                peerCertificateDigest,
                Convert.FromHexString(certificate.Configuration()["ProjectsConfig:Tls:ServerCertificateSha256"]!)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private sealed class TestServerCertificate : IDisposable
    {
        private TestServerCertificate(string directory, X509Certificate2 certificate)
        {
            DirectoryPath = directory;
            Certificate = certificate;
            CertificatePath = Path.Combine(directory, "projects-server.pem");
            KeyPath = Path.Combine(directory, "projects-server-key.pem");
            File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
            using var key = certificate.GetRSAPrivateKey()!;
            File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
        }

        private string DirectoryPath { get; }
        internal X509Certificate2 Certificate { get; }
        internal string CertificatePath { get; }
        internal string KeyPath { get; }

        internal static TestServerCertificate Create(string profile = "server")
        {
            var now = DateTimeOffset.UtcNow;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Projects test server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var purposes = new OidCollection
            {
                new Oid(profile == "client" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var notBefore = profile == "expired" ? now.AddDays(-2) : now.AddMinutes(-1);
            var notAfter = profile == "expired" ? now.AddDays(-1) : now.AddHours(1);
            var certificate = request.CreateSelfSigned(notBefore, notAfter);
            var directory = Path.Combine(Path.GetTempPath(), "agentweaver-projects-tls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TestServerCertificate(directory, certificate);
        }

        internal Dictionary<string, string?> Configuration() => new()
        {
            ["ProjectsConfig:Tls:CertificatePath"] = CertificatePath,
            ["ProjectsConfig:Tls:KeyPath"] = KeyPath,
            ["ProjectsConfig:Tls:ServerCertificateSha256"] =
                Convert.ToHexString(SHA256.HashData(Certificate.RawData))
        };

        public void Dispose()
        {
            Certificate.Dispose();
            File.Delete(CertificatePath);
            File.Delete(KeyPath);
            Directory.Delete(DirectoryPath);
        }
    }
}
