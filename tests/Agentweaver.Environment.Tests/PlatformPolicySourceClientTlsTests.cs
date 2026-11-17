using System.Collections.Immutable;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformPolicySourceClientTlsTests
{
    [Fact]
    public async Task ProductionHandlerReadsThroughPinnedKestrelTlsAndSendsItsClientCertificate()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificates.ProjectsServer,
            identityCertificates.Client);
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            server.Address,
            serverCertificates.ProjectsServer);

        var result = await client.ReadAsync();
        Assert.True(result.IsSuccess,
            $"{result.FailureCode}; requests={server.RequestCount}; " +
            $"client certificate={server.PresentedClientCertificateSha256}; " +
            $"CRL requests={serverCertificates.CrlRequestCount}");
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(
            PlatformPolicySourceClientCertificateFixture.Fingerprint(identityCertificates.Client)
                .ToUpperInvariant(),
            server.PresentedClientCertificateSha256);
        Assert.True(serverCertificates.CrlRequestCount > 0);
    }

    [Fact]
    public async Task ProductionHandlerRejectsAProjectsHostnameMismatch()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificates.ProjectsServer,
            identityCertificates.Client);
        var wrongHost = new UriBuilder(server.Address) { Host = "127.0.0.1" }.Uri;
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            wrongHost,
            serverCertificates.ProjectsServer);

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_tls_denied", result.FailureCode);
        Assert.Equal(0, server.RequestCount);
    }

    [LinuxOnlyFact]
    public async Task ProductionHandlerRejectsAWrongLeafPinBeforeFetchingAnIssuer()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        using var serverCertificate = serverCertificates.CreateServerWithUntrustedIssuerFetch();
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificate,
            identityCertificates.Client);
        var wrongPin = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wrong pin")));
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            server.Address,
            serverCertificate,
            projectsPin: wrongPin);

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("policy_source_tls_denied", result.FailureCode);
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(0, serverCertificates.AiaRequestCount);
    }

    internal sealed class LinuxOnlyFactAttribute : FactAttribute
    {
        public LinuxOnlyFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "AIA retrieval is platform-specific; the Linux CI job runs this case.";
        }
    }

    [Fact]
    public async Task ProductionHandlerRejectsAProjectsCertificateFromTheWrongBackendCa()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificates.ProjectsServer,
            identityCertificates.Client);
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            server.Address,
            serverCertificates.ProjectsServer,
            backendCaPath: serverCertificates.OtherCaPath,
            backendCaSha256: serverCertificates.OtherCaSha256);

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("policy_source_tls_denied", result.FailureCode);
        Assert.Equal(0, server.RequestCount);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("unknown")]
    public async Task ProductionHandlerRejectsRevokedOrUnknownProjectsRevocation(
        string revocationState)
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        var certificate = revocationState == "revoked"
            ? serverCertificates.ProjectsServer
            : serverCertificates.CreateServerWithoutCrl();
        if (revocationState == "revoked")
            serverCertificates.Revoke(certificate);
        await using var server = await ControlledProjectsServer.StartAsync(
            certificate,
            identityCertificates.Client);
        var client = CreateClient(
            identityCertificates, serverCertificates, server.Address, certificate);

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_tls_denied", result.FailureCode);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task ProductionHandlerRejectsAnExpiredProjectsPinWithoutConnecting()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificates.ProjectsServer,
            identityCertificates.Client);
        var retirement = DateTimeOffset.UtcNow.AddDays(1);
        var retirementText = retirement.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            server.Address,
            serverCertificates.ProjectsServer,
            timeProvider: clock,
            pinRetirement: DateTimeOffset.Parse(
                retirementText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal));
        clock.SetUtcNow(DateTimeOffset.Parse(
            retirementText,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal).AddTicks(1));

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_tls_denied", result.FailureCode);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task CanceledReadKeepsTheGateAndCertificatesUntilTlsValidationFinishes()
    {
        using var identityCertificates = new PlatformPolicySourceClientCertificateFixture();
        using var serverCertificates = new PlatformPolicySourceTlsServerFixture();
        var (requestStarted, release) =
            serverCertificates.BlockCrlRequests(serverCertificates.ProjectsServerCrlPath);
        await using var server = await ControlledProjectsServer.StartAsync(
            serverCertificates.ProjectsServer,
            identityCertificates.Client);
        var client = CreateClient(
            identityCertificates,
            serverCertificates,
            server.Address,
            serverCertificates.ProjectsServer);
        using var cancellation = new CancellationTokenSource();
        var firstRead = client.ReadAsync(cancellation.Token);
        Task<PlatformPolicySourceReadResult>? secondRead = null;

        try
        {
            await requestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await firstRead.WaitAsync(TimeSpan.FromSeconds(5)));

            secondRead = client.ReadAsync();
            var second = await secondRead.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("policy_source_read_in_progress", second.FailureCode);
            Assert.Equal(1, serverCertificates.CrlRequestCount);
        }
        finally
        {
            release();
            try
            {
                await firstRead.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            if (secondRead is not null)
            {
                try
                {
                    await secondRead.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        var retry = await ReadAfterGateReleaseAsync(client);
        Assert.True(retry.IsSuccess, retry.FailureCode);
        Assert.Equal(1, server.RequestCount);
    }

    private static PlatformPolicySourceHttpClient CreateClient(
        PlatformPolicySourceClientCertificateFixture identityCertificates,
        PlatformPolicySourceTlsServerFixture serverCertificates,
        Uri projectsAddress,
        X509Certificate2 pinnedCertificate,
        string? projectsPin = null,
        string? backendCaPath = null,
        string? backendCaSha256 = null,
        TimeProvider? timeProvider = null,
        DateTimeOffset? pinRetirement = null)
    {
        var values = identityCertificates.Configuration(projectsPin ??
            PlatformPolicySourceTlsServerFixture.Fingerprint(pinnedCertificate));
        values["Environment:PlatformEgress:Source:Tls:BackendCaPemPath"] =
            backendCaPath ?? serverCertificates.BackendCaPath;
        values["Environment:PlatformEgress:Source:Tls:BackendCaSha256"] =
            backendCaSha256 ?? serverCertificates.BackendCaSha256;
        if (pinRetirement is { } retirement)
            values["Environment:PlatformEgress:Source:Tls:ProjectsPins:0:RetireAtUtc"] =
                retirement.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var tlsIdentity = PlatformPolicySourceClientTls.CreateIdentity(
            configuration, projectsAddress, timeProvider ?? TimeProvider.System);
        return new PlatformPolicySourceHttpClient(projectsAddress, tlsIdentity);
    }

    private static async Task<PlatformPolicySourceReadResult> ReadAfterGateReleaseAsync(
        PlatformPolicySourceHttpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var result = await client.ReadAsync(timeout.Token);
            if (result.FailureCode != "policy_source_read_in_progress")
                return result;
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static string ObservationJson()
    {
        var artifact = new NativeAcsArtifact(
            Encoding.UTF8.GetBytes("{}").ToImmutableArray(),
            ImmutableArray<NativeAcsArtifactFile>.Empty,
            "");
        artifact = artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
        return JsonSerializer.Serialize(new
        {
            scopeType = "platform",
            scopeId = "default",
            policyId = "platform-policy-tls-test",
            revision = 1L,
            contentSha256 = artifact.ContentSha256,
            updatedAt = DateTimeOffset.UtcNow,
            artifact,
            admission = new { state = "pending", checkpoint = (long?)null },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class ControlledProjectsServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RequestState _state;

        private ControlledProjectsServer(WebApplication app, Uri address, RequestState state)
        {
            _app = app;
            Address = address;
            _state = state;
        }

        internal Uri Address { get; private set; }
        internal int RequestCount => Volatile.Read(ref _state.RequestCount);
        internal string? PresentedClientCertificateSha256 =>
            Volatile.Read(ref _state.PresentedClientCertificateSha256);

        internal static async Task<ControlledProjectsServer> StartAsync(
            X509Certificate2 serverCertificate,
            X509Certificate2 clientCertificate)
        {
            var expectedClientDigest = SHA256.HashData(clientCertificate.RawData);
            var state = new RequestState();
            var serverCertificateContext = SslStreamCertificateContext.Create(
                serverCertificate,
                additionalCertificates: null,
                offline: true);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenAnyIP(0, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = _ => new ValueTask<SslServerAuthenticationOptions>(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = serverCertificateContext,
                            ClientCertificateRequired = true,
                            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                            {
                                if (certificate is not null)
                                {
                                    var certificateDigest = SHA256.HashData(certificate.GetRawCertData());
                                    if (CryptographicOperations.FixedTimeEquals(
                                            certificateDigest, expectedClientDigest))
                                        Volatile.Write(ref state.PresentedClientCertificateSha256,
                                            Convert.ToHexString(certificateDigest));
                                }
                                return true;
                            },
                        }),
                })));
            var app = builder.Build();
            app.MapGet(PlatformPolicySourceClientTls.RoutePath, (HttpContext context) =>
            {
                Interlocked.Increment(ref state.RequestCount);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Content(ObservationJson(), "application/json");
            });
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            return new ControlledProjectsServer(
                app, new UriBuilder("https", "localhost", new Uri(address).Port).Uri, state);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class RequestState
        {
            internal int RequestCount;
            internal string? PresentedClientCertificateSha256;
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        internal void SetUtcNow(DateTimeOffset value) =>
            Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }
}
