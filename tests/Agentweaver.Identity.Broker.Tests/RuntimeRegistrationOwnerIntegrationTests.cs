extern alias EnvironmentService;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using EnvironmentService::Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private async Task VerifyRunBoundRuntimeRegistrationIsDeniedByEnvironmentControlPolicyAsync(
        string ownerSchema, SecurityKey signingKey, ProjectsConfigResourceServer projects,
        EventsIntegrationFactory events, string runToken, RuntimeOwnerContext owner, Guid membershipId,
        ICoordinatorSandboxResourceProvider sandboxProvider)
    {
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membershipId,
            ProjectAuthorityResourceType.Project, owner.ProjectId, ProjectAuthorityRole.Owner);
        using var selectionResponse = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{owner.ProjectId}/runs/{owner.RunId}/selection", runToken, [owner.TenantId]);
        await AssertStatusAsync(selectionResponse, HttpStatusCode.OK);
        var selectionBytes = await selectionResponse.Content.ReadAsByteArrayAsync();
        var selection = JsonSerializer.Deserialize<ProjectRunSelectionResponse>(
            selectionBytes, CoordinationJsonOptions)!;
        var candidate = Assert.Single(Assert.Single(
            selection.Providers, provider => provider.Seam == ProviderSeam.Sandbox).Candidates);
        using var selectionDocument = JsonDocument.Parse(selectionBytes);
        var environmentOwner = new EnvironmentOwnerIdentity(
            owner.TenantId, owner.ProjectId, owner.RunId, "runtime-environment");
        await using var environment = await RuntimePlacementTestServer.StartAsync(
            _connectionString, signingKey, projects.CreateHandler, environmentOwner,
            candidate, selectionDocument.RootElement.Clone());
        using var runtimeConfiguration = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["Orchestrator__RuntimeRegistration__EnvironmentOwnerAddress"] = "https://environment.test/"
        });
        using var environmentClient = new HttpClient(environment.CreateHandler())
        {
            BaseAddress = new Uri("https://environment.test/")
        };
        using var placementResponse = await SendAsync(environmentClient, HttpMethod.Get,
            $"/internal/projects/{owner.ProjectId}/runs/{owner.RunId}/environments/{environmentOwner.EnvironmentId}" +
            "/runtime-bootstrap/profiles/runtime-profile", runToken, [owner.TenantId]);
        await AssertStatusAsync(placementResponse, HttpStatusCode.Forbidden);
        Assert.True(placementResponse.Headers.CacheControl?.NoStore);
        Assert.Contains("project_write_not_authorized", await placementResponse.Content.ReadAsStringAsync());
        await using var factory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => events.Server.CreateHandler(), sandboxProvider: sandboxProvider,
            environmentHandler: environment.CreateHandler);
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://orchestrator.test/"), AllowAutoRedirect = false
        });
        var enrollmentPath = $"/internal/projects/{owner.ProjectId}/runs/{owner.RunId}" +
            $"/coordination/sessions/{owner.SessionId}/runtime-registrations";
        using var response = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath,
            runToken, new RegisterRuntimeRequest(environmentOwner.EnvironmentId, "runtime-profile"));
        await AssertStatusAsync(response, HttpStatusCode.Forbidden);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("runtime_owner_denied", await response.Content.ReadAsStringAsync());
        await using var source = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{ownerSchema}\".runtime_registration_heads", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        using var forged = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath, runToken,
            new { environmentOwner.EnvironmentId, ProfileId = "runtime-profile", DesiredModel = "foreign" });
        await AssertStatusAsync(forged, HttpStatusCode.BadRequest);
    }

    private sealed class RuntimePlacementTestServer(IHost host, NpgsqlDataSource dataSource,
        SandboxLeaseSnapshot lease) : IAsyncDisposable
    {
        public SandboxLeaseSnapshot Lease { get; } = lease;
        public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

        public static async Task<RuntimePlacementTestServer> StartAsync(
            string connectionString, SecurityKey signingKey, Func<HttpMessageHandler> projectsHandler,
            EnvironmentOwnerIdentity owner, EffectiveProviderCandidate candidate, JsonElement selection)
        {
            var dataSource = NpgsqlDataSource.Create(connectionString);
            IHost? host = null;
            try
            {
                var dbOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
                    .UseNpgsql(dataSource, options => options.MigrationsHistoryTable(
                        "__ef_migrations_history", EnvironmentDbContext.Schema)).Options;
                await EnvironmentMigrator.MigrateAsync(dataSource, dbOptions);
                var lifecycle = await new EnvironmentLifecycleProducer(
                    new EnvironmentLifecycleStore(dataSource, TimeProvider.System))
                    .RegisterAsync(owner, "runtime-register", default);
                var leaseStore = new EnvironmentSandboxLeaseStore(dataSource, TimeProvider.System);
                var optionsSnapshot = JsonSerializer.SerializeToElement(new { namespaceName = "agentweaver" });
                var intent = new SandboxLeaseProvisionIntent(
                    candidate.ProviderId, candidate.AdapterVersion, candidate.OptionsSchemaVersion,
                    candidate.OptionsRevision, optionsSnapshot, selection,
                    JsonSerializer.SerializeToElement(new { transport = "controlled-external-placement" }));
                var reserved = await leaseStore.ReserveProvisionAsync(lifecycle.Snapshot.Fence, "runtime-provision", intent, default);
                var lease = reserved.Lease;
                var resource = SandboxResourceIdentity.CreatePlannedReference(
                    candidate.ProviderId, candidate.OptionsRevision, lease.Fence,
                    lease.ResourceGeneration, lease.ProviderFencingGeneration, lease.OperationId);
                var provisioned = new SandboxProvisionedResource(
                    resource, new(Guid.NewGuid()), new("controlled-placement"),
                    candidate.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal), [],
                    new(candidate.ProviderId, candidate.AdapterVersion, candidate.OptionsSchemaVersion,
                        candidate.OptionsRevision, optionsSnapshot,
                        JsonSerializer.SerializeToElement(new { claim = resource.ResourceId })));
                lease = await leaseStore.CompleteProvisionAsync(lease.OperationId, lease.Fence, provisioned, true, default);
                var profile = new EnvironmentRuntimeBootstrapProfile(
                    "runtime-profile", new("https://runtime.test/configure"), new("https://runtime.test/observations"));
                var networkOptions = new CiliumEgressProviderOptions(
                    "agentweaver", new Version(1, 0, 0), 1, "unused-network-options",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
                host = await new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(dataSource);
                        services.AddSingleton(TimeProvider.System);
                        services.AddScoped<IEnvironmentLifecycleStore, EnvironmentLifecycleStore>();
                        services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
                        services.AddScoped<EnvironmentRuntimePlacementReader>();
                        services.AddScoped<EnvironmentEgressManager>();
                        services.AddScoped<EnvironmentWorkspaceVolumeManager>();
                        services.AddSingleton(networkOptions);
                        services.AddSingleton<ICiliumPolicyResourceStore, UnusedRuntimeNetworkTransport>();
                        services.AddScoped<CiliumEgressPolicyAdapter>();
                        services.AddSingleton(new EnvironmentRuntimeBootstrapProfileRegistry(
                            [new(owner, profile, resource)]));
                        services.AddHttpClient<IProjectsConfigClient, ProjectsConfigHttpClient>(
                            client => client.BaseAddress = new Uri("https://projects.test/"))
                            .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                        services.ConfigureHttpJsonOptions(options =>
                        {
                            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                        });
                        AddJwtBearer(services, signingKey);
                        services.AddAuthorization();
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapEnvironmentEndpoints());
                    });
                }).StartAsync();
                return new(host, dataSource, lease);
            }
            catch
            {
                host?.Dispose();
                await dataSource.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            await dataSource.DisposeAsync();
        }
    }

    private sealed class UnusedRuntimeNetworkTransport : ICiliumPolicyResourceStore
    {
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string version, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task DeleteAsync(string ns, string name, string version, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
    }
}
