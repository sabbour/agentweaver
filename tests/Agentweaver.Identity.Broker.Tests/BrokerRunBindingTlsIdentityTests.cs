using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agentweaver.Identity.Broker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class BrokerRunBindingTlsIdentityTests
{
    [Fact]
    public void HandlerDisablesProxyCookiesAndRedirects()
    {
        using var authority = TestCertificateAuthority.Create();
        using var identity = BrokerRunBindingTlsIdentity.Load(
            authority.CreateOptions(), authority.TrustBundleBytes);
        using var handler = identity.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task PinnedCustomCaTransportCompletesNativeTls12AndTls13(SslProtocols protocol)
    {
        using var authority = TestCertificateAuthority.Create();
        using var identity = BrokerRunBindingTlsIdentity.Load(
            authority.CreateOptions(), authority.TrustBundleBytes);
        await using var server = await StartServerAsync(authority.ServerCertificate);
        var handler = identity.CreateHandler();
        handler.SslProtocols = protocol;
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(server.Address + "/proof");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(protocol.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RedirectsAreReturnedWithoutFollowingThem()
    {
        using var authority = TestCertificateAuthority.Create();
        using var identity = BrokerRunBindingTlsIdentity.Load(
            authority.CreateOptions(), authority.TrustBundleBytes);
        await using var server = await StartServerAsync(authority.ServerCertificate, redirect: true);
        using var client = new HttpClient(identity.CreateHandler());

        using var response = await client.GetAsync(server.Address + "/redirect");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://outside.test/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task RejectsWrongLeafPinEvenWhenCustomChainIsValid()
    {
        using var authority = TestCertificateAuthority.Create();
        var options = authority.CreateOptions();
        options.ProjectsServerCertificateSha256 = new string('0', 64);
        using var identity = BrokerRunBindingTlsIdentity.Load(options, authority.TrustBundleBytes);
        await using var server = await StartServerAsync(authority.ServerCertificate);
        using var client = new HttpClient(identity.CreateHandler());

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync(server.Address + "/proof"));
    }

    [Fact]
    public async Task RejectsWrongRootEvenWhenLeafPinMatches()
    {
        using var trustedAuthority = TestCertificateAuthority.Create();
        using var untrustedAuthority = TestCertificateAuthority.Create();
        var options = trustedAuthority.CreateOptions();
        options.ProjectsServerCertificateSha256 =
            Convert.ToHexString(SHA256.HashData(untrustedAuthority.ServerCertificate.RawData));
        using var identity = BrokerRunBindingTlsIdentity.Load(options, trustedAuthority.TrustBundleBytes);
        await using var server = await StartServerAsync(untrustedAuthority.ServerCertificate);
        using var client = new HttpClient(identity.CreateHandler());

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync(server.Address + "/proof"));
    }

    [Fact]
    public async Task RejectsHostnameMismatch()
    {
        using var authority = TestCertificateAuthority.Create();
        using var identity = BrokerRunBindingTlsIdentity.Load(
            authority.CreateOptions(), authority.TrustBundleBytes);
        await using var server = await StartServerAsync(authority.ServerCertificate);
        using var client = new HttpClient(identity.CreateHandler());

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync(server.Address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/proof"));
    }

    [Theory]
    [InlineData("no-eku")]
    [InlineData("expired")]
    public async Task RejectsWrongPurposeOrExpiredServerCertificate(string profile)
    {
        using var authority = TestCertificateAuthority.Create(serverProfile: profile);
        using var identity = BrokerRunBindingTlsIdentity.Load(
            authority.CreateOptions(), authority.TrustBundleBytes);
        await using var server = await StartServerAsync(authority.ServerCertificate);
        using var client = new HttpClient(identity.CreateHandler());

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync(server.Address + "/proof"));
    }

    [Fact]
    public void TrustBundleDigestBindsExactRawBytes()
    {
        using var authority = TestCertificateAuthority.Create();
        var options = authority.CreateOptions();
        var modifiedBytes = authority.TrustBundleBytes.Concat(Encoding.UTF8.GetBytes("\n")).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            BrokerRunBindingTlsIdentity.Load(options, modifiedBytes));
    }

    [Fact]
    public void RejectsMalformedOrNonCaPemBundles()
    {
        using var malformed = TestCertificateAuthority.Create();
        var malformedOptions = malformed.CreateOptions();
        var malformedBytes = Encoding.UTF8.GetBytes("not a PEM certificate");
        malformedOptions.ProjectsTrustBundleSha256 =
            Convert.ToHexString(SHA256.HashData(malformedBytes));
        Assert.Throws<InvalidOperationException>(
            () => BrokerRunBindingTlsIdentity.Load(malformedOptions, malformedBytes));

        using var nonCa = TestCertificateAuthority.Create(serverProfile: "server");
        var nonCaOptions = nonCa.CreateOptions();
        var nonCaBytes = Encoding.UTF8.GetBytes(nonCa.ServerCertificate.ExportCertificatePem());
        nonCaOptions.ProjectsTrustBundleSha256 =
            Convert.ToHexString(SHA256.HashData(nonCaBytes));
        Assert.Throws<InvalidOperationException>(() =>
            BrokerRunBindingTlsIdentity.Load(nonCaOptions, nonCaBytes));

        using var privateKeyBundle = TestCertificateAuthority.Create();
        var privateKeyOptions = privateKeyBundle.CreateOptions();
        var privateKeyBytes = Encoding.UTF8.GetBytes(privateKeyBundle.CertificateAndPrivateKeyPem());
        privateKeyOptions.ProjectsTrustBundleSha256 =
            Convert.ToHexString(SHA256.HashData(privateKeyBytes));
        Assert.Throws<InvalidOperationException>(
            () => BrokerRunBindingTlsIdentity.Load(privateKeyOptions, privateKeyBytes));
    }

    private static async Task<RunningServer> StartServerAsync(
        X509Certificate2 certificate, bool redirect = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen =>
                listen.UseHttps(certificate, options =>
                    options.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13)));
        var app = builder.Build();
        app.MapGet("/proof", (HttpContext context) =>
            Results.Text(context.Features.Get<ITlsHandshakeFeature>()!.Protocol.ToString()));
        app.MapGet("/redirect", () => redirect
            ? Results.Redirect("https://outside.test/")
            : Results.Ok());
        await app.StartAsync();
        var address = Assert.Single(
            app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RunningServer(app, address);
    }

    private sealed class RunningServer(WebApplication app, string address) : IAsyncDisposable
    {
        public string Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    internal sealed class TestCertificateAuthority : IDisposable
    {
        private readonly X509Certificate2 _root;

        private TestCertificateAuthority(
            X509Certificate2 root, X509Certificate2 serverCertificate)
        {
            _root = root;
            ServerCertificate = serverCertificate;
            TrustBundleBytes = Encoding.UTF8.GetBytes(root.ExportCertificatePem());
        }

        internal byte[] TrustBundleBytes { get; }
        internal X509Certificate2 ServerCertificate { get; }

        internal static TestCertificateAuthority Create(string serverProfile = "server")
        {
            var now = DateTimeOffset.UtcNow;
            var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest(
                "CN=Projects test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));

            var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest(
                "CN=Projects test server", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var purposes = new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1")
            };
            if (serverProfile != "no-eku")
                serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(names.Build());

            using var rootPrivateKey = root.GetRSAPrivateKey()!;
            var notBefore = serverProfile == "expired" ? now.AddDays(-2) : now.AddMinutes(-1);
            var notAfter = serverProfile == "expired" ? now.AddDays(-1) : now.AddHours(1);
            using var signed = serverRequest.Create(
                root.SubjectName,
                X509SignatureGenerator.CreateForRSA(rootPrivateKey, RSASignaturePadding.Pkcs1),
                notBefore,
                notAfter,
                RandomNumberGenerator.GetBytes(16));
            using var signedWithPrivateKey = signed.CopyWithPrivateKey(serverKey);
            var serverCertificate = X509CertificateLoader.LoadPkcs12(
                signedWithPrivateKey.Export(X509ContentType.Pkcs12),
                null,
                X509KeyStorageFlags.Exportable);
            rootKey.Dispose();
            serverKey.Dispose();

            return new TestCertificateAuthority(root, serverCertificate);
        }

        internal BrokerRunBindingOptions CreateOptions() => new()
        {
            ProjectsServerCertificateSha256 =
                Convert.ToHexString(SHA256.HashData(ServerCertificate.RawData)),
            ProjectsTrustBundleSha256 =
                Convert.ToHexString(SHA256.HashData(TrustBundleBytes)),
        };

        internal string CertificateAndPrivateKeyPem()
        {
            using var key = _root.GetRSAPrivateKey()!;
            return _root.ExportCertificatePem() + key.ExportPkcs8PrivateKeyPem();
        }

        public void Dispose()
        {
            ServerCertificate.Dispose();
            _root.Dispose();
        }
    }
}
