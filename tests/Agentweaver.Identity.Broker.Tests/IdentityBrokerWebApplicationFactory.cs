using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>
/// Boots the real broker host (<c>Program.cs</c>, unmodified) against: a fresh, isolated
/// Postgres database; a freshly generated signing certificate (never a baked-in test cert
/// shared across tests); and a <see cref="FakeIdentityProvider"/> wired in as the external
/// OIDC backchannel so discovery/token calls are dispatched in-process rather than over the
/// network. One operator-seeded test client ("test-client", public, PKCE) is configured.
/// </summary>
/// <remarks>
/// Configuration is supplied via process environment variables, set in the constructor,
/// NOT via <c>ConfigureWebHost</c>/<c>ConfigureAppConfiguration</c>. <c>Program.cs</c>
/// deliberately performs eager, fail-closed configuration validation immediately after
/// <c>WebApplication.CreateBuilder(args)</c> — before <c>builder.Build()</c> runs. Test
/// host overrides registered through <c>ConfigureAppConfiguration</c>/<c>ConfigureWebHost</c>
/// are only applied by the ASP.NET Core test harness's deferred host-factory machinery
/// during <c>Build()</c>, which is too late for that eager read to observe them. Environment
/// variables, by contrast, are read synchronously by <c>CreateBuilder</c> itself, so they
/// must be set before the host is ever created (i.e. before <see cref="CreateClient()"/> is
/// first called) — hence setting them here, in the constructor, rather than in
/// <see cref="ConfigureWebHost"/>. All tests that use this factory run within the single
/// "IdentityBrokerPostgres" xUnit collection, which disables inter-test parallelism, so
/// these process-global environment variables are never mutated concurrently.
/// </remarks>
public sealed class IdentityBrokerWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://broker.test";
    public const string TestClientId = "test-client";
    public const string TestClientRedirectUri = "https://client.test/callback";
    public static readonly string[] TestClientScopes = ["openid", "profile", "email", "api.read", "offline_access"];
    public static readonly string[] TestClientResources = ["https://api.test"];

    private readonly string _connectionString;
    private readonly FakeIdentityProvider _fakeIdp;
    private readonly (string PfxPath, string Password) _signingCertificate;
    private readonly bool _ownsSigningCertificate;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly InMemoryLogSink _logSink = new();

    /// <summary>
    /// Every formatted log message written by this test's broker host instance, so a test can
    /// assert no raw token/secret value ever reached the logs (redaction is a logging-only
    /// concern — success response bodies legitimately contain real tokens).
    /// </summary>
    public IReadOnlyCollection<string> LogMessages => _logSink.Messages;

    /// <summary>
    /// The connection string this factory's host is bound to, so a test can dispose this
    /// factory and construct a fresh one against the same underlying database to prove
    /// persistence survives a host restart.
    /// </summary>
    public string ConnectionStringForRestartTest => _connectionString;

    public (string PfxPath, string Password) SigningCertificateForTest => _signingCertificate;

    private readonly Dictionary<string, string?> _previousSettings = new();
    private bool _cleanedUp;

    public IdentityBrokerWebApplicationFactory(string connectionString, FakeIdentityProvider fakeIdp,
        bool confidential = false, string clientSecret = "generated-test-client-secret",
        string redirectUri = TestClientRedirectUri, (string PfxPath, string Password)? signingCertificate = null,
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _connectionString = connectionString;
        _fakeIdp = fakeIdp;
        _signingCertificate = signingCertificate ?? TestSigningCertificate.Create();
        _ownsSigningCertificate = signingCertificate is null;
        _configureServices = configureServices;

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings__IdentityBroker"] = _connectionString,
            ["Logging__LogLevel__OpenIddict.Server.OpenIddictServerDispatcher"] = "Trace",
            ["Logging__LogLevel__OpenIddict.Validation.OpenIddictValidationDispatcher"] = "Trace",
            ["Logging__LogLevel__Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler"] = "Trace",
            ["IdentityBroker__Issuer"] = Issuer,
            ["IdentityBroker__Signing__PfxPath"] = _signingCertificate.PfxPath,
            ["IdentityBroker__Signing__PfxPassword"] = _signingCertificate.Password,
            ["IdentityBroker__DataProtectionKeyPath"] = _signingCertificate.PfxPath + ".keys",
            ["IdentityBroker__ExternalProvider__Authority"] = FakeIdentityProvider.Authority,
            ["IdentityBroker__ExternalProvider__MetadataAddress"] =
                $"{FakeIdentityProvider.Authority}/.well-known/openid-configuration",
            ["IdentityBroker__ExternalProvider__ClientId"] = "broker-to-fake-idp",
            ["IdentityBroker__ExternalProvider__ClientSecret"] = "fake-idp-client-secret",
            ["IdentityBroker__SecretRedemption__Audience"] = "https://api.test",
            ["IdentityBroker__SecretRedemption__VaultUri"] = "https://identity-test-vault.vault.azure.net/",
            ["IdentityBroker__SecretRedemption__WorkloadIdentityTenantId"] = Guid.NewGuid().ToString(),
            ["IdentityBroker__SecretRedemption__WorkloadIdentityClientId"] = Guid.NewGuid().ToString(),
            ["IdentityBroker__SecretRedemption__WorkloadIdentityTokenFilePath"] =
                Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity-tests", "projected-token.jwt"),
            ["IdentityBroker__Clients__0__ClientId"] = TestClientId,
            ["IdentityBroker__Clients__0__DisplayName"] = "Test Client",
            ["IdentityBroker__Clients__0__Type"] = confidential ? nameof(BrokerClientType.Confidential) : nameof(BrokerClientType.Public),
            ["IdentityBroker__Clients__0__ClientSecret"] = confidential ? clientSecret : null,
            ["IdentityBroker__Clients__0__RedirectUris__0"] = redirectUri,
        };

        for (var i = 0; i < TestClientScopes.Length; i++)
            settings[$"IdentityBroker__Clients__0__Scopes__{i}"] = TestClientScopes[i];
        for (var i = 0; i < TestClientResources.Length; i++)
            settings[$"IdentityBroker__Clients__0__Resources__{i}"] = TestClientResources[i];

        configure?.Invoke(settings);
        foreach (var (key, value) in settings)
        {
            _previousSettings[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new InMemoryLoggerProvider(_logSink));
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        builder.ConfigureServices(services =>
        {
            // Must be a `Configure` call, not `PostConfigure`: the framework's own internal
            // `PostConfigureOpenIdConnectOptions` only builds `options.ConfigurationManager`
            // when `options.Backchannel` is still null at that point, and caches the result.
            // `Configure` callbacks always run before `PostConfigure` callbacks in the options
            // pipeline (regardless of DI registration order), so this guarantees our in-process
            // backchannel is already set before the framework decides what HttpClient the
            // discovery/token document retrievers should use.
            services.Configure<OpenIdConnectOptions>(IdentityBrokerEndpoints.ExternalScheme, options =>
            {
                options.Backchannel = new HttpClient(_fakeIdp.Server.CreateHandler())
                {
                    BaseAddress = new Uri(FakeIdentityProvider.Authority),
                };
            });
            _configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Cleanup();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        if (_ownsSigningCertificate && File.Exists(_signingCertificate.PfxPath))
            File.Delete(_signingCertificate.PfxPath);
        if (_ownsSigningCertificate && Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
        foreach (var (key, value) in _previousSettings)
            Environment.SetEnvironmentVariable(key, value);
    }
}
