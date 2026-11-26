extern alias EventsService;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using EventsEndpoint = EventsService::Agentweaver.EventsAndSessions.PlatformComponentOwnerEndpoint;
using EventsMigrator = EventsService::Agentweaver.EventsAndSessions.EventsAndSessionsMigrator;
using EventsPostgresOptions = EventsService::Agentweaver.EventsAndSessions.PostgresSessionsProviderOptions;
using EventsRateCardEndpoint = EventsService::Agentweaver.EventsAndSessions.Cost.RuntimeRateCardOwnerReadEndpoint;
using EventsRateCard = EventsService::Agentweaver.EventsAndSessions.Cost.RuntimeRateCard;
using EventsRateCardStore = EventsService::Agentweaver.EventsAndSessions.Cost.RuntimeRateCardStore;
using EventsManualProvenance = EventsService::Agentweaver.EventsAndSessions.Cost.ManualRateCardProvenance;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ProjectsToEvents_ProjectedTokenWithSeparateJwksHost_EnforcesTrustAndCompletesDraftToActivate()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            const string audience = "https://events-and-sessions.agentweaver.svc.cluster.local/internal/platform-components";
            const string callerSubject = "system:serviceaccount:agentweaver:projects-config";
            const string cardId = "manual:integration-card";
            const string cardVersion = "2026-11-26";
            const string eventsSchema = "events_sessions";

            await using var eventsPostgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await eventsPostgres.StartAsync();
            await using var eventsDataSource = NpgsqlDataSource.Create(eventsPostgres.GetConnectionString());
            await EventsMigrator.MigrateAsync(eventsDataSource, eventsSchema);
            var rateCardStore = new EventsRateCardStore(eventsDataSource, eventsSchema);
            await rateCardStore.SaveAsync(
                new EventsRateCard(
                    new CostRateCard(
                        cardId, cardVersion, SdkMeterSources.ByokTokens, "USD", 1_000_000m,
                        ImmutableDictionary<string, decimal>.Empty
                            .Add("gpt-5|input", 1.25m)
                            .Add("gpt-5|output", 5.00m)),
                    new EventsManualProvenance("https://example.com/prices", new DateOnly(2026, 11, 26))),
                CancellationToken.None);

            using var rsa = RSA.Create(2048);
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa) { KeyId = "k1" });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            var jwksJson = JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = jwk.Kty, use = jwk.Use, kid = jwk.Kid, alg = jwk.Alg, n = jwk.N, e = jwk.E } },
            });

            using var oidcCert = CreateLoopbackCert();
            using var jwksCert = CreateLoopbackCert();
            using var eventsCert = CreateLoopbackCert();

            await using var jwksApp = StartHttpsServer(jwksCert, app =>
            {
                app.MapGet("/keys", () => Results.Text(jwksJson, "application/json"));
            });
            var jwksUri = $"{jwksApp.BaseAddress}keys";

            string? issuerUrl = null;
            await using var oidcApp = StartHttpsServer(oidcCert, app =>
            {
                app.MapGet("/.well-known/openid-configuration", () => Results.Text(
                    JsonSerializer.Serialize(new { issuer = issuerUrl, jwks_uri = jwksUri }),
                    "application/json"));
            });
            issuerUrl = oidcApp.BaseAddress.AbsoluteUri;

            var authOptions = new PlatformComponentServiceAuthenticationOptions(
                new Uri(issuerUrl),
                new Uri(audience),
                callerSubject);

            await using var eventsApp = StartEventsServer(eventsCert, authOptions, rateCardStore, oidcCert, jwksCert);

            var tokenFile = Path.Combine(Path.GetTempPath(), $"aw-2635-token-{Guid.NewGuid():N}.jwt");
            try
            {
                var collectorOptions = new PlatformComponentCollectorOptions(
                    tokenFile,
                    new Dictionary<string, Uri>(StringComparer.Ordinal)
                    {
                        [PlatformComponentOwners.EventsAndSessions] = eventsApp.BaseAddress,
                    });

                HttpClient CreateEventsClient()
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                            cert is not null && string.Equals(cert.Thumbprint, eventsCert.Thumbprint, StringComparison.OrdinalIgnoreCase),
                    };
                    return new HttpClient(handler, disposeHandler: true);
                }

                var rateCardClient = new RuntimeRateCardOwnerClient(
                    CreateEventsClient,
                    collectorOptions,
                    async ct => (string?)await File.ReadAllTextAsync(tokenFile, ct),
                    TimeSpan.FromSeconds(5));

                await using var db = tests.CreateDbContext();
                var factory = new CountingDbContextFactory(tests.CreateDbContext);
                var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
                var configService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var tenantId = "int-tenant-" + suffix;
                var admin = await SeedCallerAsync(db, store, "int-admin-" + suffix, tenantId,
                    ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
                var draftService = new ModelProviderDraftService(db, TimeProvider.System);
                var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System, rateCardClient);
                var providerId = "int-prov-" + suffix;

                // 1. Create Draft without rate card (rev 1).
                await draftService.SaveAsync(admin, ModelProviderBindingScope.Platform, null, providerId,
                    new SaveModelProviderDraftRequest(
                        Guid.NewGuid(), 0, "Integration Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                        WireApi: "responses", ReasoningEfforts: ["high"]),
                    CancellationToken.None);

                // 2. Initial credential publish without rate card -> Draft (rev 2).
                var slotId = Guid.NewGuid();
                var credVersion = new string('e', 32);
                var credIdentity = new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId);
                var credRef = new SecretRef($"byok-platform-{providerId}", credVersion);
                var op1 = Guid.NewGuid();
                var pre1 = await pubService.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        op1, ModelProviderBindingScope.Platform, null, providerId,
                        admin.Issuer, admin.ActorId, tenantId, null, null, "draft-snapshot"),
                    CancellationToken.None);
                var rec1 = await pubService.PublishAsync(
                    new ModelProviderCredentialPublicationV2Request(
                        op1, pre1.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                        credVersion, credIdentity, credRef, admin.Issuer, admin.ActorId, tenantId,
                        pre1.MembershipId, pre1.MembershipRevision, tenantId, null),
                    CancellationToken.None);
                Assert.Equal("Draft", rec1.ProviderState);

                // 3. Attach price on Draft (rev 3).
                await draftService.SaveAsync(admin, ModelProviderBindingScope.Platform, null, providerId,
                    new SaveModelProviderDraftRequest(
                        Guid.NewGuid(), 2, "Integration Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                        WireApi: "responses", ReasoningEfforts: ["high"],
                        RateCardId: cardId, RateCardVersion: cardVersion),
                    CancellationToken.None);

                // 4. Trust failures: wrong subject, wrong audience, wrong issuer leave provider Draft.
                foreach (var (badIss, badAud, badSub) in new[]
                {
                    (issuerUrl, audience, "system:serviceaccount:agentweaver:wrong-sa"),
                    (issuerUrl, "https://wrong-audience.example/", callerSubject),
                    ("https://wrong-issuer.example/", audience, callerSubject),
                })
                {
                    await File.WriteAllTextAsync(tokenFile, SignJwt(rsa, "k1", badIss, badAud, badSub));
                    Assert.Null(await rateCardClient.ReadPinAsync(cardId, cardVersion, CancellationToken.None));
                }

                // 5. Valid projected token -> recheck publish transitions Draft to Available with RateCardPin (rev 4).
                await File.WriteAllTextAsync(tokenFile, SignJwt(rsa, "k1", issuerUrl, audience, callerSubject));
                var op2 = Guid.NewGuid();
                var pre2 = await pubService.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        op2, ModelProviderBindingScope.Platform, null, providerId,
                        admin.Issuer, admin.ActorId, tenantId, null, null, "draft-snapshot"),
                    CancellationToken.None);
                var rec2 = await pubService.PublishAsync(
                    new ModelProviderCredentialPublicationV2Request(
                        op2, pre2.PreflightId, 3, ModelProviderBindingScope.Platform, null, providerId,
                        credVersion, credIdentity, credRef, admin.Issuer, admin.ActorId, tenantId,
                        pre2.MembershipId, pre2.MembershipRevision, tenantId, null),
                    CancellationToken.None);
                Assert.Equal("Available", rec2.ProviderState);
                Assert.Equal(4, rec2.ProviderRevision);

                // 6. Initialize platform defaults and activate platform provider.
                var initialDefaults = await configService.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
                var settings = await configService.UpdatePlatformRuntimeDefaultsAsync(
                    admin, initialDefaults.Revision, PlatformDefaults(), CancellationToken.None);
                var activated = await configService.ActivatePlatformModelProviderAsync(
                    admin, providerId, settings.Revision, 4, CancellationToken.None);
                Assert.Equal(providerId, activated.ActiveProviderId);

                // 7. Observable PlatformComponentCollector behavior: Events areas report ready.
                using var componentClient = new HttpPlatformComponentOwnerClient(collectorOptions, CreateEventsClient);
                var collector = new PlatformComponentCollector(
                    componentClient, TimeProvider.System, NullLogger<PlatformComponentCollector>.Instance);
                var report = await collector.CollectAsync(CancellationToken.None);
                var costArea = report.Areas.Single(a => a.Area == PlatformComponentAreaIds.Cost);
                Assert.Equal(PlatformComponentReadiness.Disabled, costArea.Readiness);
                var sessionsArea = report.Areas.Single(a => a.Area == PlatformComponentAreaIds.Sessions);
                Assert.Equal(PlatformComponentReadiness.Ready, sessionsArea.Readiness);
                var sandboxArea = report.Areas.Single(a => a.Area == PlatformComponentAreaIds.Sandbox);
                Assert.Equal(PlatformComponentReadiness.Unknown, sandboxArea.Readiness);
            }
            finally
            {
                if (File.Exists(tokenFile))
                    File.Delete(tokenFile);
            }
        });
    }

    private static string SignJwt(RSA rsa, string keyId, string issuer, string audience, string subject)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", subject)],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static X509Certificate2 CreateLoopbackCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    private static TestHttpsHost StartHttpsServer(X509Certificate2 cert, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
        var app = builder.Build();
        map(app);
        app.StartAsync().GetAwaiter().GetResult();
        var address = new Uri(app.Urls.Single());
        return new TestHttpsHost(app, address);
    }

    private static TestHttpsHost StartEventsServer(
        X509Certificate2 cert,
        PlatformComponentServiceAuthenticationOptions authOptions,
        EventsRateCardStore store,
        X509Certificate2 oidcCert,
        X509Certificate2 jwksCert)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAuthorization();
        EventsEndpoint.AddPlatformComponentServiceAuthentication(builder.Services, authOptions);
        builder.Services.Configure<JwtBearerOptions>(PlatformComponentServiceAuthenticationOptions.SchemeName, jwt =>
        {
            jwt.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (presented is not null &&
                     (string.Equals(presented.Thumbprint, oidcCert.Thumbprint, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(presented.Thumbprint, jwksCert.Thumbprint, StringComparison.OrdinalIgnoreCase))),
            };
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        EventsRateCardEndpoint.MapRuntimeRateCardOwnerRead(app, authOptions);
        var sessionsDescriptor = PlatformComponentCatalogReporter.BuiltInDescriptor(
            ProviderSeam.Sessions, "native-postgres", typeof(EventsRateCardStore));
        EventsEndpoint.MapPlatformComponentOwner(app, authOptions, (_, _) =>
            Task.FromResult(new PlatformComponentOwnerReport(
                PlatformComponentOwners.EventsAndSessions,
                DateTimeOffset.UtcNow,
                [
                    PlatformComponentCatalogReporter.DescribeSelected(
                        PlatformComponentAreaIds.Sessions,
                        sessionsDescriptor,
                        new PlatformComponentReadinessResult(PlatformComponentReadiness.Ready, null),
                        DateTimeOffset.UtcNow),
                    PlatformComponentCatalogReporter.DescribeSelected(
                        PlatformComponentAreaIds.Cost,
                        null,
                        new PlatformComponentReadinessResult(PlatformComponentReadiness.Ready, null),
                        DateTimeOffset.UtcNow),
                ])));
        app.StartAsync().GetAwaiter().GetResult();
        return new TestHttpsHost(app, new Uri(app.Urls.Single()));
    }

    private sealed class TestHttpsHost(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
