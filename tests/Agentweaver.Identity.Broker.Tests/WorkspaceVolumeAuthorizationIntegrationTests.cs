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
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;
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
        var sandboxOptions = new AgentSandboxOptions(
            1,
            "sandbox-options-1",
            "agentweaver",
            AzureFilesCsiProviderMetadata.ProviderId,
            "ghcr.io/agentweaver/agenthost:1",
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            60,
            100);
        var storageOptions = new AzureFilesCsiOptions(
            1, "options-1", "agentweaver", "azure-files", 100, 60, 100);
        var ciliumOptions = new CiliumEgressProviderOptions(
            "agentweaver",
            new Version(1, 0, 0),
            1,
            "cilium-options-1",
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .WithComparers(StringComparer.Ordinal));
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            signingKey,
            providerCatalog: CreateSandboxProviderCatalog(sandboxOptions, storageOptions, ciliumOptions));

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
        var sandboxRevocationOwner = await AddProjectActorAsync(
            "projects.admin projects.orchestrator",
            "sandbox-revocation-owner",
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            ProjectAuthorityRole.Orchestrator);
        var ownerSelectionAssignment = Assert.Single(
            owner.Assignments,
            assignment => assignment.Role == ProjectAuthorityRole.Orchestrator);
        var sandboxRevocationAssignment = Assert.Single(
            sandboxRevocationOwner.Assignments,
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
                        StringComparer.Ordinal, "platform-model"),
                    ProviderRequirements =
                    [
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.Sandbox,
                            RequiredAdapterVersion = AgentSandboxProviderMetadata.AdapterVersion.ToString(),
                            RequiredOptionsSchemaVersion = AgentSandboxOptions.CurrentOptionsSchemaVersion,
                            RequiredCapabilities = ImmutableHashSet.Create(
                                StringComparer.Ordinal,
                                SandboxCapabilities.VmIsolation,
                                SandboxCapabilities.WorkspacePersistentVolumeClaim)
                        },
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.Storage,
                            RequiredAdapterVersion = AzureFilesCsiProviderMetadata.AdapterVersion.ToString(),
                            RequiredOptionsSchemaVersion = AzureFilesCsiOptions.CurrentOptionsSchemaVersion,
                            RequiredCapabilities = ImmutableHashSet.Create(
                                StringComparer.Ordinal,
                                WorkspaceVolumeCapabilities.ReadWriteOnce)
                        },
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.NetworkPolicy,
                            RequiredAdapterVersion = ciliumOptions.AdapterVersion.ToString(),
                            RequiredOptionsSchemaVersion = ciliumOptions.OptionsSchemaVersion,
                            RequiredL3L4Capabilities = ImmutableHashSet.Create(
                                StringComparer.Ordinal,
                                CiliumEgressCapabilities.L3L4)
                        }
                    ]
                }
            })
        })
        {
            AddBearerAndTenant(acceptSelection, runToken, TenantId);
            using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
            await AssertStatusAsync(acceptedSelection, HttpStatusCode.OK);
        }

        var selectionObserver = new RunSelectionObserver();
        using var sandboxKubernetesHttpClient = new HttpClient
        {
            BaseAddress = new Uri("https://kubernetes.test/")
        };
        var sandboxKubernetesClient = new KubernetesAgentSandboxClient(sandboxKubernetesHttpClient);
        var provider = new CountingWorkspaceVolumeProvider(sandboxKubernetesClient.ClusterIdentity);
        var sandboxProvider = new CountingSandboxProvider(
            sandboxOptions,
            sandboxKubernetesClient.ClusterIdentity);
        var ciliumStore = new InMemoryCiliumPolicyResourceStore();
        await using var environment = await WorkspaceVolumeApiTestServer.StartAsync(
            _connectionString,
            signingKey,
            () => new ObservingProjectsConfigHandler(
                projects.CreateHandler(),
                selectionObserver),
            provider,
            sandboxProvider,
            sandboxOptions,
            sandboxKubernetesClient,
            ciliumOptions,
            ciliumStore);
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
        using (var applyEgress = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            "/api/network-egress/apply",
            owner.Token,
            new ApplyEnvironmentEgressRequest(fence, 1, 0, "apply-network-policy")))
        {
            await AssertStatusAsync(applyEgress, HttpStatusCode.OK);
            using var applied = JsonDocument.Parse(await applyEgress.Content.ReadAsStringAsync());
            Assert.True(applied.RootElement.GetProperty("readyForDispatch").GetBoolean());
            Assert.True(applied.RootElement
                .GetProperty("appliedState")
                .GetProperty("objectVerified")
                .GetBoolean());
        }
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

        var selectionReadsBeforeWorkspaceProvision = selectionObserver.SelectionReadCount;
        using var provision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/provision",
            owner.Token,
            new WorkspaceVolumeApiTransitionRequest(1, 0, 0, "provision-volume"));
        await AssertStatusAsync(provision, HttpStatusCode.OK);
        Assert.Equal(1, provider.ProvisionCalls);
        Assert.Equal(selectionReadsBeforeWorkspaceProvision + 1, selectionObserver.SelectionReadCount);

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

        var sandboxLeaseStore = environment.CreateSandboxStore();
        var sandboxPath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/environments/{environmentId}/sandbox";
        var sandboxProvisionRequest = new SandboxProvisionApiRequest(
            volumeId,
            beforeDeniedWrites.ResourceGeneration,
            beforeDeniedWrites.DataGeneration,
            "/workspace/agentweaver/project",
            ReadOnly: false,
            NetworkPolicyGeneration: 1,
            "sandbox-provision");
        using (var sandboxProvision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/provision",
            owner.Token,
            sandboxProvisionRequest))
        {
            await AssertStatusAsync(sandboxProvision, HttpStatusCode.Accepted);
            var result = await ReadJsonAsync<EnvironmentSandboxResult>(sandboxProvision);
            Assert.Equal(SandboxLeaseState.Active, result.State);
            Assert.False(result.ReadyForDispatch);
            Assert.Equal(1, sandboxProvider.ProvisionCalls);
        }

        using (var sandboxInspect = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"{sandboxPath}/?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
        {
            await AssertStatusAsync(sandboxInspect, HttpStatusCode.Accepted);
            var result = await ReadJsonAsync<EnvironmentSandboxResult>(sandboxInspect);
            Assert.Equal(SandboxObservedState.Pending, result.Observation!.State);
            Assert.False(result.ReadyForDispatch);
            Assert.Equal(2, sandboxProvider.DescribeCalls);
        }

        var sandboxLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(sandboxLease);
        Assert.Equal(SandboxLeaseState.Active, sandboxLease.State);
        var abandon = new SandboxAbandonApiRequest(
            sandboxLease.ResourceGeneration,
            sandboxLease.ProviderFencingGeneration,
            "sandbox-abandon");
        using (var viewerAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            viewer.Token,
            abandon))
            await AssertForbiddenAsync(viewerAbandon, "project_write_not_authorized");
        using (var ownerWithoutSelectionAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            ownerWithoutSelection.Token,
            abandon))
            await AssertForbiddenAsync(ownerWithoutSelectionAbandon, "run_selection_not_authorized");
        using (var foreignOwnerAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            foreignOwner.Token,
            abandon))
            await AssertForbiddenAsync(foreignOwnerAbandon, "project_write_not_authorized");
        using (var staleFence = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            abandon with
            {
                ProviderFencingGeneration = abandon.ProviderFencingGeneration + 1,
                IdempotencyKey = "stale-sandbox-fence"
            }))
            await AssertConflictAsync(staleFence, "sandbox_fence_stale");

        var selectionReadsBeforeSandboxRevocation = selectionObserver.SelectionReadCount;
        selectionObserver.RevokeAfterNextSelectionRead(() =>
            RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                sandboxRevocationAssignment.AssignmentId,
                1));
        using (var revokedSandboxAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            sandboxRevocationOwner.Token,
            abandon with { IdempotencyKey = "revoked-sandbox-abandon" }))
            await AssertForbiddenAsync(revokedSandboxAbandon, "authorization_changed");
        Assert.Equal(selectionReadsBeforeSandboxRevocation + 1, selectionObserver.SelectionReadCount);
        Assert.Equal(0, sandboxProvider.ReleaseCalls);
        var unchangedSandboxLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(unchangedSandboxLease);
        Assert.Equal(sandboxLease.State, unchangedSandboxLease.State);
        Assert.Equal(sandboxLease.CurrentFencingGeneration, unchangedSandboxLease.CurrentFencingGeneration);

        using (var sandboxAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            abandon))
        {
            await AssertStatusAsync(sandboxAbandon, HttpStatusCode.OK);
            var result = await ReadJsonAsync<EnvironmentSandboxResult>(sandboxAbandon);
            Assert.Equal(SandboxLeaseState.Released, result.State);
        }
        Assert.Equal(1, sandboxProvider.ReleaseCalls);
        var exactRelease = Assert.IsType<SandboxReleaseRequest>(sandboxProvider.LastReleaseRequest);
        Assert.Equal(fence, exactRelease.Fence);
        Assert.Equal("sandbox-claim-uid", exactRelease.Resource.ResourceId);
        Assert.Equal(sandboxLease.ResourceGeneration, exactRelease.Resource.Generation);
        Assert.Equal(sandboxLease.ProviderFencingGeneration, exactRelease.FencingGeneration);
        var releasedSandboxLease = await sandboxLeaseStore.GetAsync(
            fence, sandboxLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(releasedSandboxLease);
        Assert.Equal(SandboxLeaseState.Released, releasedSandboxLease.State);
        Assert.False(releasedSandboxLease.IsCurrent);
        using (var replayAbandon = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            abandon))
            await AssertStatusAsync(replayAbandon, HttpStatusCode.OK);
        Assert.Equal(1, sandboxProvider.ReleaseCalls);

        var callbackGate = sandboxProvider.DeferNextProvision();
        var lateProvisionRequest = sandboxProvisionRequest with
        {
            IdempotencyKey = "late-sandbox-provision"
        };
        var lateProvisionTask = SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/provision",
            owner.Token,
            lateProvisionRequest);
        await callbackGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var lateLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(lateLease);
        Assert.Equal(SandboxLeaseState.Provisioning, lateLease.State);
        using (var abandonDuringProvision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            new SandboxAbandonApiRequest(
                lateLease.ResourceGeneration,
                lateLease.ProviderFencingGeneration,
                "abandon-during-provider-call")))
        {
            await AssertStatusAsync(abandonDuringProvision, HttpStatusCode.Accepted);
            var result = await ReadJsonAsync<EnvironmentSandboxResult>(abandonDuringProvision);
            Assert.Equal(SandboxLeaseState.Releasing, result.State);
        }
        Assert.Equal(1, sandboxProvider.ReleaseCalls);
        callbackGate.AllowCompletion.TrySetResult();
        using (var lateProvision = await lateProvisionTask.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            await AssertStatusAsync(lateProvision, HttpStatusCode.Accepted);
            var result = await ReadJsonAsync<EnvironmentSandboxResult>(lateProvision);
            Assert.Equal(SandboxLeaseState.Releasing, result.State);
            Assert.False(result.ReadyForDispatch);
        }
        var fencedLateLease = await sandboxLeaseStore.GetAsync(
            fence, lateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(fencedLateLease);
        Assert.Equal(SandboxLeaseState.Releasing, fencedLateLease.State);
        Assert.True(fencedLateLease.CurrentFencingGeneration > fencedLateLease.ProviderFencingGeneration);
        Assert.NotNull(fencedLateLease.ProvisionedResource);
        using (var reconcileLateProvision = await SendAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/reconcile?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
            await AssertStatusAsync(reconcileLateProvision, HttpStatusCode.OK);
        Assert.Equal(2, sandboxProvider.ReleaseCalls);
        var releasedLateLease = await sandboxLeaseStore.GetAsync(
            fence, lateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(releasedLateLease);
        Assert.Equal(SandboxLeaseState.Released, releasedLateLease.State);
        Assert.False(releasedLateLease.IsCurrent);
        Assert.Equal(
            fencedLateLease.ProvisionedResource!.Resource,
            sandboxProvider.LastReleaseRequest!.Resource);
        Assert.Equal(lateLease.ResourceGeneration, sandboxProvider.LastReleaseRequest!.Resource.Generation);
        Assert.Equal(lateLease.ProviderFencingGeneration, sandboxProvider.LastReleaseRequest.FencingGeneration);
        Assert.Equal(fence, sandboxProvider.LastReleaseRequest.Fence);
        await AssertVolumeUnchangedAsync(lifecycleStore, fence, volumeId, beforeDeniedWrites);
        var activeAfterSandboxRetirement = await lifecycleStore.GetAsync(
            environmentOwner, CancellationToken.None);
        Assert.NotNull(activeAfterSandboxRetirement);
        Assert.Equal(EnvironmentLifecycleState.Active, activeAfterSandboxRetirement.State);
        Assert.Equal(fence, activeAfterSandboxRetirement.Fence);

        var selectionReadsBeforeRevocation = selectionObserver.SelectionReadCount;
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
        Assert.Equal(selectionReadsBeforeRevocation + 1, selectionObserver.SelectionReadCount);
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

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static ProviderCatalog CreateSandboxProviderCatalog(
        AgentSandboxOptions sandboxOptions,
        AzureFilesCsiOptions storageOptions,
        CiliumEgressProviderOptions ciliumOptions)
    {
        var result = ProviderCatalog.Create(
            [
                AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions),
                AzureFilesCsiProviderMetadata.CreateRegistration(storageOptions),
                ciliumOptions.CreateRegistration()
            ],
            [
                new ProviderSelection(ProviderSeam.Sandbox, AgentSandboxProviderMetadata.ProviderId),
                new ProviderSelection(ProviderSeam.Storage, AzureFilesCsiProviderMetadata.ProviderId)
            ],
            [],
            layerSelections:
            [
                new ProviderLayerSelection(
                    NetworkPolicyLayer.L3L4,
                    CiliumEgressPolicyAdapter.ProviderId)
            ]);
        return Assert.IsType<ProviderCatalog>(result.Value);
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

    private sealed class CountingSandboxProvider(
        AgentSandboxOptions options,
        string clusterIdentity) : ISandboxProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private ProvisionGate? _nextProvisionGate;
        private int _provisionCalls;
        private int _describeCalls;
        private int _releaseCalls;
        private SandboxProvisionedResource? _lastProvisionedResource;
        private Guid _lastOperationId;

        public int ProvisionCalls => Volatile.Read(ref _provisionCalls);
        public int DescribeCalls => Volatile.Read(ref _describeCalls);
        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);
        public SandboxReleaseRequest? LastReleaseRequest { get; private set; }

        public ProvisionGate DeferNextProvision()
        {
            var gate = new ProvisionGate();
            if (Interlocked.CompareExchange(ref _nextProvisionGate, gate, null) is not null)
                throw new InvalidOperationException("A Sandbox provider call is already deferred.");
            return gate;
        }

        public async Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _provisionCalls);
            _lastOperationId = request.OperationId;
            var claimUid = request.ResourceGeneration == 1
                ? "sandbox-claim-uid"
                : $"sandbox-claim-uid-{request.ResourceGeneration}";
            var resource = new ProviderResourceRef(
                ProviderSeam.Sandbox,
                request.Candidate.ProviderId,
                claimUid,
                request.ResourceGeneration);
            var binding = new SandboxProviderBindingSnapshot(
                request.Candidate.ProviderId,
                request.Candidate.AdapterVersion.ToString(),
                request.Candidate.OptionsSchemaVersion,
                request.Candidate.OptionsRevision,
                JsonSerializer.SerializeToElement(options, JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    clusterIdentity,
                    @namespace = options.Namespace,
                    claimUid = resource.ResourceId,
                    operationId = request.OperationId.ToString("N")
                }, JsonOptions));
            var provisioned = new SandboxProvisionedResource(
                resource,
                new SandboxEndpointReference(Guid.NewGuid()),
                new SandboxPlacementReference("test-placement"),
                request.Candidate.AdvertisedCapabilities,
                [],
                binding).Validate();
            _lastProvisionedResource = provisioned;

            var gate = Interlocked.Exchange(ref _nextProvisionGate, null);
            if (gate is not null)
            {
                gate.Started.TrySetResult();
                await gate.AllowCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return provisioned;
        }

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _describeCalls);
            var resource = _lastProvisionedResource
                ?? throw new InvalidOperationException("No test Sandbox resource has been provisioned.");
            if (resource.Resource != request.Resource)
                throw new InvalidOperationException("The described Sandbox resource is not the test provider's current resource.");
            return Task.FromResult(new SandboxObservation(
                request.Resource,
                SandboxObservedState.Pending,
                request.FencingGeneration,
                VmIsolationVerified: true,
                WorkspaceAttachmentVerified: true,
                VerifiedNetworkGeneration: null,
                StartupPhases: [],
                ProvisionOperationId: _lastOperationId,
                ProvisionedResource: resource).ValidateFor(request));
        }

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The integration scenario does not exercise provider recovery.");

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _releaseCalls);
            LastReleaseRequest = request;
            return Task.FromResult(new SandboxReleaseReceipt(
                request.Resource,
                request.IdempotencyKey,
                SandboxReleaseDisposition.Released).ValidateFor(request));
        }

        public sealed class ProvisionGate
        {
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource AllowCompletion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class InMemoryCiliumPolicyResourceStore : ICiliumPolicyResourceStore
    {
        private readonly Dictionary<(string Namespace, string Name), CiliumNetworkPolicyDocument> _resources = [];
        private long _version;

        public Task<CiliumNetworkPolicyDocument?> GetAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken) =>
            Task.FromResult(_resources.GetValueOrDefault((@namespace, name)));

        public Task<CiliumNetworkPolicyDocument> CreateAsync(
            CiliumNetworkPolicyDocument policy,
            CancellationToken cancellationToken)
        {
            var key = (policy.Metadata.Namespace, policy.Metadata.Name);
            if (_resources.ContainsKey(key))
                throw new CiliumPolicyException("kubernetes_conflict", "The test policy already exists.");
            var persisted = policy with
            {
                Metadata = policy.Metadata with
                {
                    ResourceVersion = NextVersion(),
                    Generation = 1
                }
            };
            _resources.Add(key, persisted);
            return Task.FromResult(persisted);
        }

        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy,
            string expectedResourceVersion,
            CancellationToken cancellationToken)
        {
            var key = (policy.Metadata.Namespace, policy.Metadata.Name);
            if (!_resources.TryGetValue(key, out var current) ||
                current.Metadata.ResourceVersion != expectedResourceVersion)
                throw new CiliumPolicyException("stale_generation", "The test policy resource version changed.");
            var persisted = policy with
            {
                Metadata = policy.Metadata with
                {
                    ResourceVersion = NextVersion(),
                    Generation = (current.Metadata.Generation ?? 0) + 1
                }
            };
            _resources[key] = persisted;
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(
            string @namespace,
            string name,
            string expectedResourceVersion,
            CancellationToken cancellationToken)
        {
            var key = (@namespace, name);
            if (!_resources.TryGetValue(key, out var current) ||
                current.Metadata.ResourceVersion != expectedResourceVersion)
                throw new CiliumPolicyException("stale_generation", "The test policy resource version changed.");
            _resources.Remove(key);
            return Task.CompletedTask;
        }

        private string NextVersion() =>
            (++_version).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    private sealed class CountingWorkspaceVolumeProvider(string clusterIdentity) : IWorkspaceVolumeProvider
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
                JsonSerializer.SerializeToElement(
                    new AzureFilesCsiOptions(1, "options-1", "agentweaver", "azure-files", 100, 60, 100),
                    JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    @namespace = "agentweaver",
                    claimName = "claim-1",
                    claimUid = "claim-uid",
                    pvName = "pv-1",
                    pvUid = "pv-uid",
                    clusterIdentity
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

        public ISandboxLeaseStore CreateSandboxStore() =>
            new EnvironmentSandboxLeaseStore(_dataSource, TimeProvider.System);

        public static async Task<WorkspaceVolumeApiTestServer> StartAsync(
            string connectionString,
            SecurityKey signingKey,
            Func<HttpMessageHandler> projectsHandlerFactory,
            IWorkspaceVolumeProvider provider,
            ISandboxProvider sandboxProvider,
            AgentSandboxOptions sandboxOptions,
            KubernetesAgentSandboxClient sandboxKubernetesClient,
            CiliumEgressProviderOptions ciliumOptions,
            ICiliumPolicyResourceStore ciliumStore)
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
                            services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
                            services.AddScoped<WorkspaceVolumeService>();
                            services.AddScoped<EnvironmentEgressManager>();
                            services.AddScoped<CiliumEgressPolicyAdapter>();
                            services.AddScoped<EnvironmentSandboxManager>();
                            services.AddScoped<EnvironmentWorkspaceVolumeManager>();
                            services.AddSingleton<IWorkspaceVolumeProvider>(provider);
                            services.AddSingleton<ISandboxProvider>(sandboxProvider);
                            services.AddSingleton(sandboxOptions);
                            services.AddSingleton(sandboxKubernetesClient);
                            services.AddSingleton(ciliumOptions);
                            services.AddSingleton<ICiliumPolicyResourceStore>(ciliumStore);
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
