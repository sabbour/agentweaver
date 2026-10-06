extern alias ProjectsConfig;

using Agentweaver.Identity;
using ProjectsConfig::Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
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
    private readonly HttpClient _client;

    private ProjectsConfigResourceServer(IHost host, NpgsqlDataSource dataSource)
    {
        _host = host;
        _dataSource = dataSource;
        _client = host.GetTestClient();
        _client.BaseAddress = new Uri("https://projects.test");
    }

    public HttpClient Client => _client;

    public static async Task<ProjectsConfigResourceServer> StartAsync(
        string connectionString,
        SecurityKey signingKey,
        string audience = "https://api.test")
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var dbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.MigrateAsync(dataSource, dbOptions);

        var catalog = ProviderCatalogConfiguration.Load(new ConfigurationBuilder()
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
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(dataSource);
                services.AddDbContext<ProjectsConfigDbContext>((_, options) =>
                    options.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                        "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                services.AddSingleton(catalog);
                services.AddSingleton(TimeProvider.System);
                services.AddScoped<ProjectsConfigService>();
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                            ValidateAudience = true,
                            ValidAudience = audience,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = signingKey,
                            ValidateLifetime = true,
                            RequireSignedTokens = true,
                            ClockSkew = TimeSpan.FromSeconds(30),
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                            RoleClaimType = IdentityAuthorizationContext.RoleClaimType,
                        };
                    });
                services.AddAuthorization();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapProjectConfigEndpoints());
            });
        }).StartAsync();

        return new ProjectsConfigResourceServer(host, dataSource);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
    }
}
