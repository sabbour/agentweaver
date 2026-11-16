using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Identity;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class PlatformPolicySourceTlsTests
{
    [Fact]
    public void DisabledProfileAddsNoPrivateEndpointAndDoesNotLoadTheCa()
    {
        var builder = Builder([]);

        Assert.False(PlatformPolicySourceTls.Configure(builder, @"Z:\missing\backend-ca.pem"));

        Assert.Null(builder.Configuration[PlatformPolicySourceTls.EndpointUrlKey]);
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(PlatformPolicySourceTlsIdentity));
    }

    [Fact]
    public void EnabledProfileAddsPrivateAndExplicitPublicEndpoints()
    {
        using var certificates = new PolicySourceCertificates();
        var builder = Builder(certificates.Configuration());

        Assert.True(PlatformPolicySourceTls.Configure(builder, certificates.CaPath));

        Assert.Equal("https://+:8445", builder.Configuration[PlatformPolicySourceTls.EndpointUrlKey]);
        Assert.Equal("https://+:8443", builder.Configuration["Kestrel:Endpoints:ProjectsHttps:Url"]);
        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(PlatformPolicySourceTlsIdentity));
    }

    [Fact]
    public void InvalidPinProfilesAndConflictingListenerConfigurationFailStartup()
    {
        using var certificates = new PolicySourceCertificates();
        var invalidProfiles = new[]
        {
            new Dictionary<string, string?>(certificates.Configuration())
            {
                ["ProjectsConfig:PolicySource:EnvironmentPins:0:RetireAtUtc"] =
                    DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            },
            new Dictionary<string, string?>(certificates.Configuration())
            {
                ["ProjectsConfig:PolicySource:EnvironmentPins:1:Sha256"] =
                    certificates.EnvironmentPin,
                ["ProjectsConfig:PolicySource:EnvironmentPins:1:RetireAtUtc"] =
                    DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            },
            new Dictionary<string, string?>(certificates.Configuration())
            {
                ["ProjectsConfig:PolicySource:EnvironmentPins:1:Sha256"] =
                    certificates.AgentHostPin,
                ["ProjectsConfig:PolicySource:EnvironmentPins:1:RetireAtUtc"] =
                    DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            },
            new Dictionary<string, string?>(certificates.Configuration())
            {
                [PlatformPolicySourceTls.EndpointUrlKey] = "https://+:9445",
            },
            new Dictionary<string, string?>(certificates.Configuration())
            {
                ["Kestrel:Endpoints:Other:Url"] = "https://+:8445",
            },
        };

        foreach (var values in invalidProfiles)
        {
            var builder = Builder(values);
            Assert.Throws<InvalidOperationException>(() =>
                PlatformPolicySourceTls.Configure(builder, certificates.CaPath));
        }
    }

    [Fact]
    public void MissingOrChangedMountedCaDeniesTheEnvironmentPeer()
    {
        using var certificates = new PolicySourceCertificates();
        var identity = certificates.CreateIdentity();

        Assert.True(identity.AcceptClientCertificate(
            certificates.EnvironmentClient, null, SslPolicyErrors.None));

        File.WriteAllText(certificates.CaPath, certificates.OtherCa.ExportCertificatePem());

        Assert.False(identity.AcceptClientCertificate(
            certificates.EnvironmentClient, null, SslPolicyErrors.None));
    }

    [Fact]
    public void LeafMustMatchEnvironmentSanPinEkuValidityAndRetirement()
    {
        using var certificates = new PolicySourceCertificates();
        using var wrongSan = certificates.CreateClient(
            "other.agentweaver.svc.cluster.local", clientAuthentication: true);
        using var wrongPurpose = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName, clientAuthentication: false);
        using var expired = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName,
            clientAuthentication: true,
            notAfter: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var wrongPin = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName, clientAuthentication: true);
        using var wrongCa = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName,
            clientAuthentication: true,
            issuedByOtherCa: true);
        var host = certificates.CreateIdentity(
            [wrongSan, wrongPurpose, expired, certificates.AgentHostClient, wrongCa]);

        Assert.False(host.AcceptClientCertificate(wrongSan, null, SslPolicyErrors.None));
        Assert.False(host.AcceptClientCertificate(wrongPurpose, null, SslPolicyErrors.None));
        Assert.False(host.AcceptClientCertificate(expired, null, SslPolicyErrors.None));
        Assert.False(host.AcceptClientCertificate(
            certificates.AgentHostClient, null, SslPolicyErrors.None));
        Assert.False(host.AcceptClientCertificate(wrongPin, null, SslPolicyErrors.None));
        Assert.False(host.AcceptClientCertificate(wrongCa, null, SslPolicyErrors.None));
    }

    [Fact]
    public void RotatingPinsHonorTheExactRetirementInstant()
    {
        using var certificates = new PolicySourceCertificates();
        using var nextEnvironment = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName,
            clientAuthentication: true);
        var retirement = DateTimeOffset.UtcNow.AddSeconds(5);
        var beforeRetirement = certificates.CreateIdentity(
            [nextEnvironment],
            retireAtUtc: retirement,
            timeProvider: new FixedTimeProvider(retirement.AddTicks(-1)),
            additionalPinsRetireAtUtc: retirement.AddMinutes(1));
        var atRetirement = certificates.CreateIdentity(
            [nextEnvironment],
            retireAtUtc: retirement,
            timeProvider: new FixedTimeProvider(retirement),
            additionalPinsRetireAtUtc: retirement.AddMinutes(1));

        Assert.True(beforeRetirement.AcceptClientCertificate(
            certificates.EnvironmentClient, null, SslPolicyErrors.None));
        Assert.True(beforeRetirement.AcceptClientCertificate(
            nextEnvironment, null, SslPolicyErrors.None));
        Assert.False(atRetirement.AcceptClientCertificate(
            certificates.EnvironmentClient, null, SslPolicyErrors.None));
        Assert.True(atRetirement.AcceptClientCertificate(
            nextEnvironment, null, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PeerValidityIsCheckedAgainAfterChainBuild(bool leafExpires)
    {
        using var certificates = new PolicySourceCertificates();
        var now = DateTimeOffset.UtcNow;
        using var client = leafExpires
            ? certificates.CreateClient(
                PlatformPolicySourceTls.EnvironmentDnsName,
                clientAuthentication: true,
                notAfter: now.AddSeconds(20))
            : certificates.CreateClient(
                PlatformPolicySourceTls.EnvironmentDnsName,
                clientAuthentication: true,
                notAfter: now.AddDays(1));
        var retirement = leafExpires ? now.AddMinutes(5) : now.AddSeconds(20);
        var afterChainBuild = leafExpires ? now.AddMinutes(1) : retirement;
        var identity = certificates.CreateIdentity(
            [client],
            retireAtUtc: retirement,
            timeProvider: new TransitionTimeProvider(now, afterChainBuild));

        Assert.False(identity.AcceptClientCertificate(client, null, SslPolicyErrors.None));
    }

    [Fact]
    public void RevokedAndUnknownCertificatesAreDenied()
    {
        using var certificates = new PolicySourceCertificates();
        using var revoked = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName,
            clientAuthentication: true);
        using var unknown = certificates.CreateClient(
            PlatformPolicySourceTls.EnvironmentDnsName,
            clientAuthentication: true,
            includeCrlDistributionPoint: false);
        var identity = certificates.CreateIdentity([revoked, unknown]);

        certificates.Revoke(revoked);

        Assert.False(identity.AcceptClientCertificate(revoked, null, SslPolicyErrors.None));
        Assert.False(identity.AcceptClientCertificate(unknown, null, SslPolicyErrors.None));
    }

    [Fact]
    public async Task ValidEnvironmentPeerWithIntermediateChainBuildsBeforeDisposal()
    {
        using var certificates = new PolicySourceCertificates();
        using var intermediate = certificates.CreateIntermediateAuthority();
        using var client = certificates.CreateClientThroughIntermediate(intermediate);
        using var peerChain = new X509Chain();
        peerChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        peerChain.ChainPolicy.CustomTrustStore.Add(certificates.BackendCa);
        peerChain.ChainPolicy.ExtraStore.Add(intermediate);
        peerChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(peerChain.Build(client));
        Assert.Equal(3, peerChain.ChainElements.Count);

        var identity = certificates.CreateIdentity([client]);

        Assert.True(identity.AcceptClientCertificate(client, peerChain, SslPolicyErrors.None));
        Assert.True(await identity.IsEnvironmentPeerAsync(client, CancellationToken.None));
    }

    [Fact]
    public async Task PrivateListenerRequiresEnvironmentTlsAndKeepsThePublicAndIssueSyncListeners()
    {
        using var certificates = new PolicySourceCertificates();
        var values = certificates.Configuration();
        values["ProjectsConfig:HostedModelCatalogPublication:AgentHostCertificateSha256"] =
            certificates.AgentHostPin;
        values["Kestrel:Endpoints:IssueSyncIdentity:Url"] = "https://localhost:8444";
        AddIssueSyncProfile(values, certificates);
        var builder = Builder(values);
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        BrokerRunBindingTlsIdentity.Configure(builder, enabled: true);
        Assert.True(IssueSyncServiceTransport.AddChannel(
            builder, "IssueSyncIdentity", "ProjectsConfig:IssueSync:IdentityTls",
            "Projects", "IdentityBroker", listener: true));
        HostedModelCatalogPublicationTls.Configure(builder, certificates.CaPath);
        Assert.True(PlatformPolicySourceTls.Configure(builder, certificates.CaPath, clock));
        builder.Services.AddSingleton<TimeProvider>(clock);

        await using var app = builder.Build();
        app.UseRouting();
        var privateConnectionIds = new ConcurrentQueue<string>();
        app.Use((context, next) =>
        {
            if (context.Connection.LocalPort == 8445)
                privateConnectionIds.Enqueue(context.Connection.Id);
            return next(context);
        });
        app.UsePrivateListenerGuard();
        app.MapGet(PlatformPolicySourceTls.RoutePath, () => Results.Text("environment"));
        app.MapGet("/public", () => Results.Text("public"));
        await app.StartAsync();
        try
        {
            certificates.SetUnrevokedCrl(TimeSpan.FromSeconds(5));
            using var publicClient = CreateClient();
            using var publicResponse = await publicClient.GetAsync("https://127.0.0.1:8443/public");
            Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);

            using var environmentClient = CreateClient(
                certificates.EnvironmentClient);
            using (var privateResponse = await environmentClient.GetAsync(
                "https://127.0.0.1:8445" + PlatformPolicySourceTls.RoutePath))
            {
                Assert.Equal(HttpStatusCode.OK, privateResponse.StatusCode);
                Assert.Equal("environment", await privateResponse.Content.ReadAsStringAsync());
            }

            var retirement = DateTimeOffset.Parse(
                values["ProjectsConfig:PolicySource:EnvironmentPins:0:RetireAtUtc"]!,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal);
            var beforeRetirement = clock.GetUtcNow();
            clock.SetUtcNow(retirement);
            using var retiredPinResponse = await environmentClient.GetAsync(
                "https://127.0.0.1:8445" + PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.Forbidden, retiredPinResponse.StatusCode);
            using var retiredPinBody = JsonDocument.Parse(
                await retiredPinResponse.Content.ReadAsStringAsync());
            Assert.Equal("policy_source_peer_denied",
                retiredPinBody.RootElement.GetProperty("code").GetString());
            Assert.Equal(privateConnectionIds.ElementAt(0), privateConnectionIds.ElementAt(1));

            clock.SetUtcNow(beforeRetirement);
            var crlRequestsBeforeRevocation = certificates.CrlRequestCount;
            await Task.Delay(TimeSpan.FromSeconds(6));
            certificates.Revoke(certificates.EnvironmentClient);
            using var revokedPeerResponse = await environmentClient.GetAsync(
                "https://127.0.0.1:8445" + PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.Forbidden, revokedPeerResponse.StatusCode);
            using var revokedPeerBody = JsonDocument.Parse(
                await revokedPeerResponse.Content.ReadAsStringAsync());
            Assert.Equal("policy_source_peer_denied",
                revokedPeerBody.RootElement.GetProperty("code").GetString());
            Assert.True(certificates.CrlRequestCount > crlRequestsBeforeRevocation);
            Assert.Equal(privateConnectionIds.ElementAt(0), privateConnectionIds.ElementAt(2));

            using var otherPrivatePath = await environmentClient.GetAsync("https://127.0.0.1:8445/public");
            Assert.Equal(HttpStatusCode.NotFound, otherPrivatePath.StatusCode);
            Assert.True(otherPrivatePath.Headers.CacheControl?.NoStore);

            using var wrongListener = await publicClient.GetAsync(
                "https://127.0.0.1:8443" + PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.NotFound, wrongListener.StatusCode);
            Assert.True(wrongListener.Headers.CacheControl?.NoStore);

            using var noCertificateClient = CreateClient();
            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                noCertificateClient.GetAsync("https://127.0.0.1:8445" + PlatformPolicySourceTls.RoutePath));
            using var agentHostClient = CreateClient(
                certificates.AgentHostClient);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                agentHostClient.GetAsync("https://127.0.0.1:8445" + PlatformPolicySourceTls.RoutePath));

            using var issueSyncSocket = new TcpClient();
            await issueSyncSocket.ConnectAsync(IPAddress.Loopback, 8444);
            Assert.True(issueSyncSocket.Connected);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static HttpClient CreateClient(X509Certificate2? clientCertificate = null)
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            SslProtocols = PlatformPolicySourceTlsIdentity.AllowedProtocols,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null,
        };
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler);
    }

    private static void AddIssueSyncProfile(
        Dictionary<string, string?> values,
        PolicySourceCertificates certificates)
    {
        const string prefix = "ProjectsConfig:IssueSync:IdentityTls:";
        values[prefix + "LocalServiceName"] = "Projects";
        values[prefix + "LocalServicePrincipalId"] = "d1f6f08f-20b4-4a80-81c0-f08f7e11e251";
        values[prefix + "LocalCertificatePemPath"] = certificates.ServerCertificatePath;
        values[prefix + "LocalPrivateKeyPemPath"] = certificates.ServerKeyPath;
        values[prefix + "LocalCertificateSha256"] = certificates.ServerPin;
        values[prefix + "ListenerPort"] = "8444";
        values[prefix + "PeerServiceName"] = "IdentityBroker";
        values[prefix + "PeerServicePrincipalId"] = "a18f4658-59bc-4860-8177-3dfdc0a60e39";
        values[prefix + "PeerOwnerAddress"] = "https://identity-broker.agentweaver.svc.cluster.local/";
        values[prefix + "PeerCertificateSha256Pins:0"] = certificates.EnvironmentPin;
    }

    internal sealed class PolicySourceCertificates : IDisposable
    {
        private readonly RSA _caKey = RSA.Create(2048);
        private readonly RSA _otherCaKey = RSA.Create(2048);
        private readonly X509Certificate2 _ca;
        private readonly X509Certificate2 _otherCa;
        private readonly RSA _serverKey = RSA.Create(2048);
        private readonly byte[] _crl;
        private int _crlNumber;
        private readonly LocalCrlServer _crlServer;
        private readonly string _fixtureDirectory;

        internal PolicySourceCertificates()
        {
            _ca = CreateCa(_caKey, "CN=Agentweaver test backend CA");
            _otherCa = CreateCa(_otherCaKey, "CN=Other test CA");
            _fixtureDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".policy-source-fixtures",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fixtureDirectory);
            CaPath = Path.Combine(_fixtureDirectory, "backend-ca.pem");
            File.WriteAllText(CaPath, _ca.ExportCertificatePem());
            _crlServer = new LocalCrlServer();
            _crl = CreateCrl([]);
            _crlServer.SetCrl(_crl);
            var crlUri = new Uri(_crlServer.BaseAddress, Guid.NewGuid().ToString("N"));
            EnvironmentClient = CreateClient(
                PlatformPolicySourceTls.EnvironmentDnsName,
                crlUri,
                clientAuthentication: true,
                notAfter: null,
                issuer: _ca);
            AgentHostClient = CreateClient(
                "agenthost.agentweaver.svc.cluster.local",
                crlUri,
                clientAuthentication: true,
                notAfter: null,
                issuer: _ca);
            ServerCertificate = CreateServer();

            var keyPath = Path.Combine(_fixtureDirectory, "server");
            ServerCertificatePath = keyPath + ".pem";
            ServerKeyPath = keyPath + "-key.pem";
            File.WriteAllText(ServerCertificatePath, ServerCertificate.ExportCertificatePem());
            File.WriteAllText(ServerKeyPath, _serverKey.ExportPkcs8PrivateKeyPem());
        }

        internal string CaPath { get; }
        internal string ServerCertificatePath { get; }
        internal string ServerKeyPath { get; }
        internal X509Certificate2 OtherCa => _otherCa;
        internal X509Certificate2 BackendCa => _ca;
        internal X509Certificate2 EnvironmentClient { get; }
        internal X509Certificate2 AgentHostClient { get; }
        internal X509Certificate2 ServerCertificate { get; }
        internal int CrlRequestCount => _crlServer.RequestCount;
        internal string EnvironmentPin => Fingerprint(EnvironmentClient);
        internal string AgentHostPin => Fingerprint(AgentHostClient);
        internal string ServerPin => Fingerprint(ServerCertificate);

        internal Dictionary<string, string?> Configuration() => new()
        {
            ["ProjectsConfig:PolicySource:Enabled"] = "true",
            ["ProjectsConfig:PolicySource:BackendCaSha256"] = Fingerprint(_ca),
            ["ProjectsConfig:PolicySource:EnvironmentPins:0:Sha256"] = EnvironmentPin,
            ["ProjectsConfig:PolicySource:EnvironmentPins:0:RetireAtUtc"] =
                DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["ProjectsConfig:Tls:CertificatePath"] = ServerCertificatePath,
            ["ProjectsConfig:Tls:KeyPath"] = ServerKeyPath,
            ["ProjectsConfig:Tls:ServerCertificateSha256"] = ServerPin,
        };

        internal PlatformPolicySourceTlsIdentity CreateIdentity(
            X509Certificate2[]? additionalPins = null,
            DateTimeOffset? retireAtUtc = null,
            TimeProvider? timeProvider = null,
            DateTimeOffset? additionalPinsRetireAtUtc = null)
        {
            var defaultRetirement = retireAtUtc ?? DateTimeOffset.UtcNow.AddDays(2);
            var pins = new[] { (Certificate: EnvironmentClient, RetireAtUtc: defaultRetirement) }
                .Concat((additionalPins ?? []).Select(certificate => (
                    Certificate: certificate,
                    RetireAtUtc: additionalPinsRetireAtUtc ?? defaultRetirement)))
                .DistinctBy(pin => Fingerprint(pin.Certificate))
                .Select(pin => new PlatformPolicySourceLeafPin(
                    Fingerprint(pin.Certificate),
                    pin.RetireAtUtc))
                .ToImmutableArray();
            return new PlatformPolicySourceTlsIdentity(
                new PlatformPolicySourceTlsOptions(Fingerprint(_ca), pins),
                CaPath,
                timeProvider ?? TimeProvider.System);
        }

        internal X509Certificate2 CreateClient(
            string dnsName,
            bool clientAuthentication,
            DateTimeOffset? notAfter = null,
            bool includeCrlDistributionPoint = true,
            bool issuedByOtherCa = false)
        {
            var crlUri = includeCrlDistributionPoint
                ? new Uri(_crlServer.BaseAddress, Guid.NewGuid().ToString("N"))
                : null;
            return CreateClient(
                dnsName, crlUri, clientAuthentication, notAfter, issuedByOtherCa ? _otherCa : _ca);
        }

        internal X509Certificate2 CreateIntermediateAuthority()
        {
            using var key = RSA.Create(2048);
            var crlPath = "intermediate-ca-" + Guid.NewGuid().ToString("N");
            var request = new CertificateRequest(
                "CN=Agentweaver test intermediate CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(new X509Extension(
                "2.5.29.31",
                CreateCrlDistributionPoints(new Uri(_crlServer.BaseAddress, crlPath)),
                critical: false));
            using var issued = request.Create(
                _ca,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(2),
                RandomNumberGenerator.GetBytes(16));
            _crlServer.SetCrl(crlPath, CreateCrl([]));
            return issued.CopyWithPrivateKey(key);
        }

        internal X509Certificate2 CreateClientThroughIntermediate(X509Certificate2 intermediate)
        {
            var crlPath = "intermediate-leaf-" + Guid.NewGuid().ToString("N");
            var client = CreateClient(
                PlatformPolicySourceTls.EnvironmentDnsName,
                new Uri(_crlServer.BaseAddress, crlPath),
                true,
                null,
                intermediate);
            _crlServer.SetCrl(crlPath, CreateCrl([], issuer: intermediate));
            return client;
        }

        private X509Certificate2 CreateClient(
            string dnsName,
            Uri? crlUri,
            bool clientAuthentication,
            DateTimeOffset? notAfter,
            X509Certificate2 issuer) =>
            CreateLeaf(
                "CN=" + dnsName,
                dnsName,
                crlUri,
                clientAuthentication ? ["1.3.6.1.5.5.7.3.2"] : ["1.3.6.1.5.5.7.3.1"],
                DateTimeOffset.UtcNow.AddDays(-1),
                notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
                issuer);

        private X509Certificate2 CreateServer()
        {
            var request = new CertificateRequest(
                "CN=projects-config test server", _serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1"),
                    new("1.3.6.1.5.5.7.3.2"),
                },
                true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var issued = request.Create(
                _ca,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                RandomNumberGenerator.GetBytes(16));
            return issued.CopyWithPrivateKey(_serverKey);
        }

        private X509Certificate2 CreateLeaf(
            string subject,
            string dnsName,
            Uri? crlUri,
            string[] purposes,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            X509Certificate2 issuer)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var enhancedKeyUsages = new OidCollection();
            foreach (var purpose in purposes)
                enhancedKeyUsages.Add(new Oid(purpose));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsages, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(dnsName);
            request.CertificateExtensions.Add(names.Build());
            if (crlUri is not null)
                request.CertificateExtensions.Add(new X509Extension(
                    "2.5.29.31",
                    CreateCrlDistributionPoints(crlUri),
                    critical: false));
            request.CertificateExtensions.Add(new X509Extension(
                "2.5.29.35", CreateAuthorityKeyIdentifier(issuer), critical: false));

            using var issued = request.Create(
                issuer,
                notBefore,
                notAfter,
                RandomNumberGenerator.GetBytes(16));
            using var certificateWithKey = issued.CopyWithPrivateKey(key);
            var pkcs12 = certificateWithKey.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pkcs12, null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }

        private byte[] CreateCrl(
            IReadOnlyCollection<byte[]> revokedSerials,
            DateTimeOffset? nextUpdate = null,
            X509Certificate2? issuer = null)
        {
            var issuerCertificate = issuer ?? _ca;
            var now = DateTimeOffset.UtcNow;
            var tbs = new AsnWriter(AsnEncodingRules.DER);
            using (tbs.PushSequence())
            {
                tbs.WriteInteger(1);
                WriteSignatureAlgorithm(tbs);
                tbs.WriteEncodedValue(issuerCertificate.SubjectName.RawData);
                tbs.WriteUtcTime(now.AddMinutes(-1));
                tbs.WriteUtcTime(nextUpdate ?? now.AddDays(1));
                if (revokedSerials.Count > 0)
                {
                    using (tbs.PushSequence())
                    {
                        foreach (var serial in revokedSerials)
                        {
                            using (tbs.PushSequence())
                            {
                                tbs.WriteIntegerUnsigned(serial);
                                tbs.WriteUtcTime(now.AddMinutes(-1));
                            }
                        }
                    }
                }

                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (tbs.PushSequence())
                {
                    WriteExtension(tbs, "2.5.29.35", CreateAuthorityKeyIdentifier(issuerCertificate));
                    var number = new AsnWriter(AsnEncodingRules.DER);
                    number.WriteInteger(++_crlNumber);
                    WriteExtension(tbs, "2.5.29.20", number.Encode());
                }

            }

            var encodedTbs = tbs.Encode();
            using var key = issuerCertificate.GetRSAPrivateKey()!;
            var signature = key.SignData(encodedTbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var crl = new AsnWriter(AsnEncodingRules.DER);
            using (crl.PushSequence())
            {
                crl.WriteEncodedValue(encodedTbs);
                WriteSignatureAlgorithm(crl);
                crl.WriteBitString(signature);
            }
            return crl.Encode();
        }

        internal void Revoke(X509Certificate2 certificate) =>
            _crlServer.SetCrl(CreateCrl([Convert.FromHexString(certificate.SerialNumber)]));

        internal void SetUnrevokedCrl(TimeSpan lifetime) =>
            _crlServer.SetCrl(CreateCrl([], DateTimeOffset.UtcNow.Add(lifetime)));

        private static byte[] CreateAuthorityKeyIdentifier(X509Certificate2 ca)
        {
            var subjectKeyIdentifier = ca.Extensions
                .OfType<X509SubjectKeyIdentifierExtension>()
                .Single()
                .SubjectKeyIdentifier!;
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
                writer.WriteOctetString(
                    Convert.FromHexString(subjectKeyIdentifier),
                    new Asn1Tag(TagClass.ContextSpecific, 0));
            return writer.Encode();
        }

        private static byte[] CreateCrlDistributionPoints(Uri crlUri)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            using (writer.PushSequence())
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                writer.WriteCharacterString(
                    UniversalTagNumber.IA5String,
                    crlUri.AbsoluteUri,
                    new Asn1Tag(TagClass.ContextSpecific, 6));
            return writer.Encode();
        }

        private static void WriteExtension(AsnWriter writer, string oid, byte[] value)
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(oid);
                writer.WriteOctetString(value);
            }
        }

        private static void WriteSignatureAlgorithm(AsnWriter writer)
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                writer.WriteNull();
            }
        }

        private static X509Certificate2 CreateCa(RSA key, string subject)
        {
            var request = new CertificateRequest(
                subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(7));
        }

        private static string Fingerprint(X509Certificate2 certificate) =>
            Convert.ToHexString(SHA256.HashData(certificate.RawData));

        public void Dispose()
        {
            EnvironmentClient.Dispose();
            AgentHostClient.Dispose();
            ServerCertificate.Dispose();
            _otherCa.Dispose();
            _otherCaKey.Dispose();
            _ca.Dispose();
            _caKey.Dispose();
            _serverKey.Dispose();
            _crlServer.Dispose();
            Directory.Delete(_fixtureDirectory, recursive: true);
        }
    }

    private sealed class LocalCrlServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly ConcurrentDictionary<string, byte[]> _crls = new(StringComparer.Ordinal);
        private byte[] _crl = [];
        private int _requestCount;

        internal LocalCrlServer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        internal Uri BaseAddress { get; }
        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal void SetCrl(byte[] crl) => Volatile.Write(ref _crl, crl.ToArray());

        internal void SetCrl(string path, byte[] crl) => _crls[path] = crl.ToArray();

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _requestCount);
                    var path = context.Request.Url?.AbsolutePath.Trim('/');
                    var bytes = path is not null && _crls.TryGetValue(path, out var selected)
                        ? selected
                        : Volatile.Read(ref _crl);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/pkix-crl";
                    context.Response.Headers[HttpResponseHeader.CacheControl] = "no-store";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                    context.Response.Close();
                }

            }
            catch (Exception exception) when (
                exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (HttpListenerException)
            {
            }
            _stop.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TransitionTimeProvider(DateTimeOffset before, DateTimeOffset after) : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _calls) == 1 ? before : after;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        internal void SetUtcNow(DateTimeOffset value)
        {
            lock (_gate)
                _utcNow = value;
        }
    }
}
