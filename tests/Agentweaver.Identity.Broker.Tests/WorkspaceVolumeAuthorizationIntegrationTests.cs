extern alias EnvironmentService;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using EnvironmentService::Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects()
    {
        using var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(signingCertificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);

        var platformAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "workspace-volume-admin", null, null, ["platform_admin"]);
        var platformAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformAdminToken).Claims, "sub");
        var platformAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        async Task<ProjectSummary> CreateProjectAsync(string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
            {
                Content = JsonContent.Create(new CreateProjectRequest { Name = name })
            };
            AddBearerAndTenant(request, platformAdminToken, TenantId);
            using var response = await projects.Client.SendAsync(request);
            await AssertStatusAsync(response, HttpStatusCode.Created);
            return await ReadJsonAsync<ProjectSummary>(response);
        }

        var project = await CreateProjectAsync("Workspace volume authorization");
        var foreignProject = await CreateProjectAsync("Foreign workspace volume owner");

        async Task<(string Token, ProjectTenantMembershipRecord Membership,
            IReadOnlyList<ProjectRoleAssignmentRecord> Assignments)> AddProjectActorAsync(
                string scopes,
                string subjectName,
                string projectId,
                params ProjectAuthorityRole[] roles)
        {
            var token = await IssueTokenAsync(scopes, [TenantId], subjectName, null, null, []);
            var subject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "sub");
            var membership = await AddMembershipAsync(
                projects.PrivilegedFixtureDataSource, subject, TenantId);
            var assignments = new List<ProjectRoleAssignmentRecord>();
            foreach (var role in roles)
            {
                assignments.Add(await AssignRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    membership.MembershipId,
                    ProjectAuthorityResourceType.Project,
                    projectId,
                    role));
            }
            return (token, membership, assignments);
        }

        var owner = await AddProjectActorAsync(
            "projects.admin projects.orchestrator",
            "workspace-volume-owner",
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            ProjectAuthorityRole.Orchestrator);
        var ownerSelectionAssignment = Assert.Single(
            owner.Assignments,
            assignment => assignment.Role == ProjectAuthorityRole.Orchestrator);
        var viewer = await AddProjectActorAsync(
            "api.read",
            "workspace-volume-viewer",
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
        var ownerWithoutSelection = await AddProjectActorAsync(
            "projects.admin",
            "workspace-volume-owner-without-selection",
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        var foreignOwner = await AddProjectActorAsync(
            "projects.admin",
            "foreign-workspace-volume-owner",
            foreignProject.ProjectId,
            ProjectAuthorityRole.Owner);

        var runnerBootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "workspace-volume-selection-runner", null, null, ["orchestrator"]);
        var runnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(runnerBootstrapToken).Claims, "sub");
        var runnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, runnerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await CreateRunBindingGrantAsync(runnerSubject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "workspace-volume-selection-runner",
            project.ProjectId,
            RunId,
            ["platform_admin"]);

        using (var updateDefaults = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings("platform-model"),
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 12,
                        MaxToolCalls = 100,
                        MaxChildren = 4,
                        MaxConcurrentChildren = 2,
                        MaxWallTimeSeconds = 3600,
                        MaxPromptTokens = 20000
                    }
                }
            })
        })
        {
            AddBearerAndTenant(updateDefaults, platformAdminToken, TenantId);
            using var updatedDefaults = await projects.Client.SendAsync(updateDefaults);
            await AssertStatusAsync(updatedDefaults, HttpStatusCode.OK);
        }

        using (var acceptSelection = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(new AcceptRunSelectionRequest
            {
                ExpectedProjectConfigRevision = project.ConfigurationRevision,
                ExpectedPlatformRuntimeRevision = 1,
                Context = new RunSelectionContext
                {
                    Revision = "provider-catalog-v1",
                    AvailableModelSelectionReferences = ImmutableHashSet.Create(
                        StringComparer.Ordinal, "platform-model")
                }
            })
        })
        {
            AddBearerAndTenant(acceptSelection, runToken, TenantId);
            using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
            await AssertStatusAsync(acceptedSelection, HttpStatusCode.OK);
        }

        var selectionObserver = new RunSelectionObserver();
        var provider = new CountingWorkspaceVolumeProvider();
        await using var environment = await WorkspaceVolumeApiTestServer.StartAsync(
            _connectionString,
            signingKey,
            () => new ObservingProjectsConfigHandler(
                projects.CreateHandler(),
                selectionObserver),
            provider);
        var lifecycleStore = environment.CreateStore();
        var environmentId = $"environment-{Guid.NewGuid():N}";
        var volumeId = $"volume-{Guid.NewGuid():N}";
        var environmentOwner = new EnvironmentOwnerIdentity(
            TenantId, project.ProjectId, RunId, environmentId);
        var registration = await lifecycleStore.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                environmentOwner, 0, EnvironmentLifecycleState.Active, "register-environment"),
            CancellationToken.None);
        var fence = registration.Snapshot.Fence;
        var volumePath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/environments/{environmentId}/workspace-volumes";
        var specification = new WorkspaceVolumeSpec(
            volumeId,
            project.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, RunId),
            environmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteOnce,
            10,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Delete,
            []);

        using var create = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}",
            owner.Token,
            new CreateWorkspaceVolumeApiRequest(specification, "create-volume"));
        await AssertStatusAsync(create, HttpStatusCode.OK);
        var createdVolume = await ReadJsonAsync<EnvironmentWorkspaceVolumeSnapshot>(create);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, createdVolume.Phase);

        using var provision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/provision",
            owner.Token,
            new WorkspaceVolumeApiTransitionRequest(1, 0, 0, "provision-volume"));
        await AssertStatusAsync(provision, HttpStatusCode.OK);
        Assert.Equal(1, provider.ProvisionCalls);
        Assert.Equal(2, selectionObserver.SelectionReadCount);

        using var viewerRead = await SendAsync(
            environment.Client, HttpMethod.Get, $"{volumePath}/{volumeId}", viewer.Token, [TenantId]);
        await AssertStatusAsync(viewerRead, HttpStatusCode.OK);
        var viewerSnapshot = await ReadJsonAsync<EnvironmentWorkspaceVolumeSnapshot>(viewerRead);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, viewerSnapshot.Phase);

        var beforeDeniedWrites = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(beforeDeniedWrites);
        var selectionReadsBeforeDeniedWrites = selectionObserver.SelectionReadCount;
        var release = new WorkspaceVolumeApiTransitionRequest(
            beforeDeniedWrites.TransitionRevision,
            beforeDeniedWrites.ResourceGeneration,
            beforeDeniedWrites.DataGeneration,
            "denied-release");

        using (var viewerMutation = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/release",
            viewer.Token,
            release))
            await AssertForbiddenAsync(viewerMutation, "project_write_not_authorized");
        using (var missingSelection = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/release",
            ownerWithoutSelection.Token,
            release with { IdempotencyKey = "missing-selection-release" }))
            await AssertForbiddenAsync(missingSelection, "run_selection_not_authorized");
        using (var foreignProjectOwner = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/release",
            foreignOwner.Token,
            release with { IdempotencyKey = "foreign-project-release" }))
            await AssertForbiddenAsync(foreignProjectOwner, "project_write_not_authorized");

        Assert.Equal(selectionReadsBeforeDeniedWrites, selectionObserver.SelectionReadCount);
        Assert.Equal(1, provider.ProvisionCalls);
        Assert.Equal(0, provider.ReleaseCalls);
        await AssertVolumeUnchangedAsync(lifecycleStore, fence, volumeId, beforeDeniedWrites);

        selectionObserver.RevokeAfterNextSelectionRead(() =>
            RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                ownerSelectionAssignment.AssignmentId,
                1));
        var beforeRevocation = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(beforeRevocation);
        using var revokedBeforeEffect = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/release",
            owner.Token,
            new WorkspaceVolumeApiTransitionRequest(
                beforeRevocation.TransitionRevision,
                beforeRevocation.ResourceGeneration,
                beforeRevocation.DataGeneration,
                "revoked-before-provider-effect"));
        await AssertForbiddenAsync(revokedBeforeEffect, "authorization_changed");
        Assert.Equal(selectionReadsBeforeDeniedWrites + 1, selectionObserver.SelectionReadCount);
        Assert.Equal(1, provider.ProvisionCalls);
        Assert.Equal(0, provider.ReleaseCalls);
        await AssertVolumeUnchangedAsync(lifecycleStore, fence, volumeId, beforeRevocation);

        var currentEnvironment = await lifecycleStore.GetAsync(environmentOwner, CancellationToken.None);
        Assert.NotNull(currentEnvironment);
        Assert.Equal(fence, currentEnvironment.Fence);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertVolumeUnchangedAsync(
        IEnvironmentLifecycleStore store,
        EnvironmentGenerationFence fence,
        string volumeId,
        EnvironmentWorkspaceVolumeSnapshot expected)
    {
        var actual = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(actual);
        Assert.Equal(expected.TransitionRevision, actual.TransitionRevision);
        Assert.Equal(expected.ResourceGeneration, actual.ResourceGeneration);
        Assert.Equal(expected.DataGeneration, actual.DataGeneration);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Resource, actual.Resource);
    }

    private sealed class RunSelectionObserver
    {
        private Func<Task>? _afterNextSelectionRead;
        private int _selectionReadCount;

        public int SelectionReadCount => Volatile.Read(ref _selectionReadCount);

        public void RevokeAfterNextSelectionRead(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (Interlocked.CompareExchange(ref _afterNextSelectionRead, action, null) is not null)
                throw new InvalidOperationException("A run-selection revocation is already armed.");
        }

        public async Task OnResponseAsync(
            HttpRequestMessage request,
            HttpResponseMessage response)
        {
            if (request.Method != HttpMethod.Get ||
                request.RequestUri is not { } uri ||
                !uri.AbsolutePath.EndsWith("/selection", StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _selectionReadCount);
            var action = Interlocked.Exchange(ref _afterNextSelectionRead, null);
            if (response.IsSuccessStatusCode && action is not null)
                await action().ConfigureAwait(false);
        }
    }

    private sealed class ObservingProjectsConfigHandler(
        HttpMessageHandler innerHandler,
        RunSelectionObserver observer) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                await observer.OnResponseAsync(request, response).ConfigureAwait(false);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private sealed class CountingWorkspaceVolumeProvider : IWorkspaceVolumeProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private int _provisionCalls;
        private int _releaseCalls;

        public int ProvisionCalls => Volatile.Read(ref _provisionCalls);
        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _provisionCalls);
            var resource = new ProviderResourceRef(
                ProviderSeam.Storage, "azure-files-csi", "claim-uid", request.ResourceGeneration);
            var binding = new WorkspaceVolumeProviderBindingSnapshot(
                resource.ProviderId,
                "1.0.0",
                1,
                "options-1",
                JsonSerializer.SerializeToElement(new { namespaceName = "agentweaver" }, JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    @namespace = "agentweaver",
                    claimName = "claim-1",
                    claimUid = "claim-uid",
                    pvName = "pv-1",
                    pvUid = "pv-uid"
                }, JsonOptions));
            return Task.FromResult(new WorkspaceVolumeResource(
                resource,
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    WorkspaceVolumeCapabilities.ForAccessMode(request.Spec.AccessMode)),
                binding));
        }

        public Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
            WorkspaceVolumeReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _releaseCalls);
            return Task.FromResult(new WorkspaceVolumeReleaseReceipt(
                request.Resource,
                request.IdempotencyKey,
                request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete
                    ? WorkspaceVolumeReleaseDisposition.Released
                    : WorkspaceVolumeReleaseDisposition.Retained));
        }
    }

    private sealed class WorkspaceVolumeApiTestServer : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly NpgsqlDataSource _dataSource;

        private WorkspaceVolumeApiTestServer(IHost host, NpgsqlDataSource dataSource)
        {
            _host = host;
            _dataSource = dataSource;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public IEnvironmentLifecycleStore CreateStore() =>
            new EnvironmentLifecycleStore(_dataSource, TimeProvider.System);

        public static async Task<WorkspaceVolumeApiTestServer> StartAsync(
            string connectionString,
            SecurityKey signingKey,
            Func<HttpMessageHandler> projectsHandlerFactory,
            IWorkspaceVolumeProvider provider)
        {
            var dataSource = NpgsqlDataSource.Create(connectionString);
            try
            {
                var dbOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
                    .UseNpgsql(
                        dataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", "environment"))
                    .Options;
                await EnvironmentMigrator.MigrateAsync(dataSource, dbOptions);

                var host = await new HostBuilder()
                    .ConfigureWebHost(web =>
                    {
                        web.UseTestServer();
                        web.ConfigureServices(services =>
                        {
                            services.AddRouting();
                            services.AddSingleton(dataSource);
                            services.AddSingleton(TimeProvider.System);
                            services.AddScoped<IEnvironmentLifecycleStore, EnvironmentLifecycleStore>();
                            services.AddScoped<WorkspaceVolumeService>();
                            services.AddScoped<EnvironmentEgressManager>();
                            services.AddScoped<EnvironmentWorkspaceVolumeManager>();
                            services.AddSingleton<IWorkspaceVolumeProvider>(provider);
                            services.AddHttpClient<IProjectsConfigClient, ProjectsConfigHttpClient>(client =>
                                client.BaseAddress = new Uri("https://projects.test/"))
                                .ConfigurePrimaryHttpMessageHandler(projectsHandlerFactory);
                            services.ConfigureHttpJsonOptions(options =>
                            {
                                options.SerializerOptions.UnmappedMemberHandling =
                                    JsonUnmappedMemberHandling.Disallow;
                                options.SerializerOptions.Converters.Add(
                                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
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
                    })
                    .StartAsync();
                return new WorkspaceVolumeApiTestServer(host, dataSource);
            }
            catch
            {
                await dataSource.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _dataSource.DisposeAsync();
        }
    }
}
