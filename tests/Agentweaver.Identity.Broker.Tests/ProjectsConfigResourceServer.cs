extern alias ProjectsConfig;

using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Security;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers;
using ProjectsConfig::Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class ProjectsConfigResourceServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlDataSource _privilegedDataSource;
    private readonly string _runtimeRole;
    private readonly HttpClient _client;

    private ProjectsConfigResourceServer(
        IHost host,
        NpgsqlDataSource dataSource,
        NpgsqlDataSource privilegedDataSource,
        string runtimeRole,
        Uri baseAddress,
        X509Certificate2? brokerRunBindingServerCertificate)
    {
        _host = host;
        _dataSource = dataSource;
        _privilegedDataSource = privilegedDataSource;
        _runtimeRole = runtimeRole;
        _client = brokerRunBindingServerCertificate is null
            ? host.GetTestClient()
            : new HttpClient(CreatePinnedTestHandler(brokerRunBindingServerCertificate));
        _client.BaseAddress = baseAddress;
    }

    public HttpClient Client => _client;
    public Uri BaseAddress => _client.BaseAddress
        ?? throw new InvalidOperationException("Projects Config test client has no base address.");
    public HttpMessageHandler CreateHandler() => _host.GetTestServer().CreateHandler();
    public NpgsqlDataSource RuntimeDataSource => _dataSource;
    public NpgsqlDataSource PrivilegedFixtureDataSource => _privilegedDataSource;

    public static async Task<ProjectsConfigResourceServer> StartAsync(
        string connectionString,
        SecurityKey signingKey,
        string audience = "https://api.test",
        ProviderCatalog? providerCatalog = null,
        IReadOnlyList<string>? additionalAudiences = null,
        IObjectStore? skillContentObjects = null,
        bool usePublicWriterFixture = false,
        string? brokerRunBindingAudience = null,
        X509Certificate2? brokerRunBindingServerCertificate = null,
        bool grantRoleAssignmentWrites = false)
    {
        if ((brokerRunBindingAudience is null) != (brokerRunBindingServerCertificate is null))
            throw new ArgumentException(
                "Projects run-binding HTTPS requires both a private audience and a server certificate.");
        if (brokerRunBindingAudience is not null &&
            (!Uri.TryCreate(brokerRunBindingAudience, UriKind.Absolute, out var privateAudience) ||
                privateAudience.Scheme != Uri.UriSchemeHttps ||
                string.Equals(
                    privateAudience.AbsoluteUri,
                    new Uri(audience).AbsoluteUri,
                    StringComparison.Ordinal)))
            throw new ArgumentException(
                "Projects run-binding HTTPS requires a distinct absolute HTTPS audience.");

        var privilegedDataSource = NpgsqlDataSource.Create(connectionString);
        var privilegedDbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(privilegedDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.MigrateAsync(privilegedDataSource, privilegedDbOptions);

        var runtimeRole = $"projects_config_runtime_{Guid.NewGuid():N}";
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await CreateRuntimeRoleAsync(
            privilegedDataSource, connectionString, runtimeRole, runtimePassword, grantRoleAssignmentWrites);
        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = runtimeRole,
            Password = runtimePassword,
        }.ConnectionString;
        var dataSource = NpgsqlDataSource.Create(runtimeConnectionString);
        var dbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(dataSource, dbOptions);
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(dataSource);
        var serviceDataSource = usePublicWriterFixture ? privilegedDataSource : dataSource;

        var catalog = providerCatalog ?? ProviderCatalogConfiguration.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Id"] = "sandbox-platform",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdapterVersion"] = "1.0.0",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsSchemaVersion"] = "1",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Hosting"] = "KubernetesController",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdvertisedCapabilities:0"] = "container.create",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Enabled"] = "true",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsRevision"] = "options-v1",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:ProviderId"] = "sandbox-platform",
            })
            .Build());

        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            if (brokerRunBindingServerCertificate is null)
                web.UseTestServer();
            else
            {
                web.UseKestrel();
                web.ConfigureKestrel(server =>
                    server.Listen(IPAddress.Loopback, 0, listen =>
                        listen.UseHttps(brokerRunBindingServerCertificate, https =>
                            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13)));
            }
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.ConfigureHttpJsonOptions(json =>
                {
                    json.SerializerOptions.UnmappedMemberHandling =
                        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
                    json.SerializerOptions.Converters.Add(
                        new System.Text.Json.Serialization.JsonStringEnumConverter(
                            System.Text.Json.JsonNamingPolicy.CamelCase));
                });
                services.AddSingleton(serviceDataSource);
                services.AddDbContextFactory<ProjectsConfigDbContext>((_, options) =>
                    options.UseNpgsql(serviceDataSource, npgsql => npgsql.MigrationsHistoryTable(
                        "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                services.AddSingleton(catalog);
                services.AddSingleton(TimeProvider.System);
                services.AddScoped<ProjectsConfigService>();
                services.AddScoped<ProjectRoleAssignmentService>();
                if (skillContentObjects is not null)
                    services.AddScoped(_ => new SkillContentObjectStore(skillContentObjects));
                services.AddScoped<SkillContentService>(provider => new SkillContentService(
                    provider.GetRequiredService<ProjectsConfigDbContext>(),
                    provider.GetService<SkillContentObjectStore>(),
                    provider.GetRequiredService<ProjectsConfigService>(),
                    provider.GetRequiredService<TimeProvider>()));
                services.AddScoped<ISkillContentService>(provider =>
                    provider.GetRequiredService<SkillContentService>());
                services.AddScoped<ISkillAssignmentService>(provider =>
                    provider.GetRequiredService<SkillContentService>());
                services.AddSingleton(new ProjectsConfigIdentityOptions(
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri));
                services.AddScoped<ProjectAuthorizationOwner>();
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                            ValidateAudience = true,
                            ValidAudiences = new[] { audience }
                                .Concat(additionalAudiences ?? Array.Empty<string>())
                                .ToArray(),
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = signingKey,
                            ValidateLifetime = true,
                            RequireSignedTokens = true,
                            ClockSkew = TimeSpan.FromSeconds(30),
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        };
                    });
                services.AddAuthorization();
                if (brokerRunBindingAudience is not null)
                {
                    services.AddAuthentication().AddJwtBearer(
                        ProjectAuthorizationOwner.BrokerRunBindingAuthenticationScheme,
                        options =>
                        {
                            options.MapInboundClaims = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                                ValidateAudience = true,
                                ValidAudience = brokerRunBindingAudience,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = signingKey,
                                ValidateLifetime = true,
                                RequireExpirationTime = true,
                                RequireSignedTokens = true,
                                ValidTypes = [ProjectAuthorizationOwner.BrokerRunBindingTokenType],
                                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                                ClockSkew = TimeSpan.FromSeconds(5),
                            };
                        });
                    services.AddAuthorization(options =>
                        options.AddPolicy(
                            ProjectAuthorizationOwner.BrokerRunBindingAuthorizationPolicy,
                            policy => policy
                                .AddAuthenticationSchemes(
                                    ProjectAuthorizationOwner.BrokerRunBindingAuthenticationScheme)
                                .RequireAuthenticatedUser()));
                }
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapProjectConfigEndpoints(
                        enableBrokerRunBinding: brokerRunBindingAudience is not null);
                    endpoints.MapSkillContentEndpoints();
                    endpoints.MapIssueSyncAuthority(internalEnabled: false);
                });
            });
        }).StartAsync();

        var baseAddress = brokerRunBindingServerCertificate is null
            ? new Uri("https://projects.test")
            : GetKestrelBaseAddress(host);
        return new ProjectsConfigResourceServer(
            host, dataSource, privilegedDataSource, runtimeRole, baseAddress,
            brokerRunBindingServerCertificate);
    }

    private static Uri GetKestrelBaseAddress(IHost host)
    {
        var addresses = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not { Count: 1 })
            throw new InvalidOperationException("Projects Config HTTPS test host did not expose one address.");
        return new Uri(addresses.Single().TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static HttpClientHandler CreatePinnedTestHandler(X509Certificate2 expectedCertificate)
    {
        var pin = SHA256.HashData(expectedCertificate.RawData);
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                var now = DateTimeOffset.UtcNow;
                return certificate is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                    now >= expectedCertificate.NotBefore.ToUniversalTime() &&
                    now < expectedCertificate.NotAfter.ToUniversalTime() &&
                    CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(certificate.GetRawCertData()), pin);
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
        await using (var connection = await _privilegedDataSource.OpenConnectionAsync())
        {
            await using var cleanup = new NpgsqlCommand(
                $"DROP OWNED BY \"{_runtimeRole}\"; DROP ROLE \"{_runtimeRole}\"",
                connection);
            await cleanup.ExecuteNonQueryAsync();
        }
        await _privilegedDataSource.DisposeAsync();
    }

    private static async Task CreateRuntimeRoleAsync(
        NpgsqlDataSource privilegedDataSource,
        string ownerConnectionString,
        string runtimeRole,
        string runtimePassword,
        bool grantRoleAssignmentWrites)
    {
        var database = new NpgsqlConnectionStringBuilder(ownerConnectionString).Database;
        await using var connection = await privilegedDataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand(
            $"CREATE ROLE \"{runtimeRole}\" LOGIN PASSWORD '{runtimePassword}'",
            connection))
            await create.ExecuteNonQueryAsync();

        var statements = new[]
        {
            $"GRANT CONNECT ON DATABASE \"{database}\" TO \"{runtimeRole}\"",
            $"GRANT USAGE ON SCHEMA projects_config TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.__ef_migrations_history TO \"{runtimeRole}\"",
            $"GRANT SELECT, INSERT, UPDATE ON projects_config.projects TO \"{runtimeRole}\"",
            $"GRANT SELECT, UPDATE ON projects_config.platform_runtime_heads TO \"{runtimeRole}\"",
            $"GRANT SELECT, INSERT ON projects_config.project_configuration_revisions TO \"{runtimeRole}\"",
            $"GRANT SELECT, INSERT ON projects_config.platform_runtime_revisions TO \"{runtimeRole}\"",
            $"GRANT SELECT, INSERT ON projects_config.project_run_selections TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.project_skill_content_revisions TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.project_skill_content_revocations TO \"{runtimeRole}\"",
            $"GRANT USAGE, SELECT ON SEQUENCE projects_config.platform_runtime_revisions_revision_seq TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.tenant_memberships TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.project_role_assignments TO \"{runtimeRole}\"",
            $"GRANT SELECT ON projects_config.authority_audit TO \"{runtimeRole}\"",
            $"GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean) TO \"{runtimeRole}\"",
        };
        if (grantRoleAssignmentWrites)
            statements =
            [
                .. statements,
                $"GRANT INSERT ON projects_config.project_role_assignments TO \"{runtimeRole}\"",
                $"GRANT UPDATE (state, revision, revoked_by, revoked_at) ON projects_config.project_role_assignments TO \"{runtimeRole}\"",
                $"GRANT INSERT ON projects_config.authority_audit TO \"{runtimeRole}\"",
            ];
        foreach (var statement in statements)
        {
            await using var grant = new NpgsqlCommand(statement, connection);
            await grant.ExecuteNonQueryAsync();
        }
    }
}
