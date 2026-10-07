extern alias EnvironmentService;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var placementLockOwner = await AddProjectActorAsync(
            "projects.admin projects.orchestrator",
            "sandbox-placement-lock-owner",
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            ProjectAuthorityRole.Orchestrator);
        var placementLockAssignment = Assert.Single(
            placementLockOwner.Assignments,
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
        var runSelectionAssignment = await AssignRoleAsync(
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
        var missingRoleSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(ownerWithoutSelection.Token).Claims, "sub");
        var missingRoleRunId = $"run-{Guid.NewGuid():N}";
        await CreateRunBindingGrantAsync(missingRoleSubject, project.ProjectId, missingRoleRunId);
        var missingRoleRunToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "workspace-volume-owner-without-selection",
            project.ProjectId,
            missingRoleRunId,
            []);

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
        var environmentId = $"environment-{Guid.NewGuid():N}";
        var volumeId = $"volume-{Guid.NewGuid():N}";
        var sandboxKubernetesHandler = new FakeKubernetesHandler(
            project.ProjectId,
            environmentId,
            volumeId,
            "claim-1",
            "claim-uid");
        using var sandboxKubernetesHttpClient = new HttpClient(sandboxKubernetesHandler)
        {
            BaseAddress = new Uri("https://kubernetes.test/")
        };
        var sandboxKubernetesClient = new KubernetesAgentSandboxClient(sandboxKubernetesHttpClient);
        var provider = new CountingWorkspaceVolumeProvider(sandboxKubernetesClient.ClusterIdentity);
        var sandboxProvider = new AgentSandboxProvider(sandboxOptions, sandboxKubernetesClient);
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

        var beforeSandboxBind = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(beforeSandboxBind);
        using (var sandboxBind = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{volumePath}/{volumeId}/bind",
            owner.Token,
            new WorkspaceVolumeApiTransitionRequest(
                beforeSandboxBind.TransitionRevision,
                beforeSandboxBind.ResourceGeneration,
                beforeSandboxBind.DataGeneration,
                "sandbox-bind")))
        {
            await AssertStatusAsync(sandboxBind, HttpStatusCode.OK);
            var bound = await ReadJsonAsync<WorkspaceVolumeLifecycleResult>(sandboxBind);
            Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, bound.Completion!.TargetPhase);
        }

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
        EnvironmentSandboxResult sandboxProvisionResult;
        using (var sandboxProvision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/provision",
            owner.Token,
            sandboxProvisionRequest))
        {
            await AssertStatusAsync(sandboxProvision, HttpStatusCode.Accepted);
            sandboxProvisionResult = await ReadJsonAsync<EnvironmentSandboxResult>(sandboxProvision);
            Assert.Equal(SandboxLeaseState.Active, sandboxProvisionResult.State);
            Assert.False(sandboxProvisionResult.ReadyForDispatch);
        }
        var selectionReadsBeforePlacementProjection = selectionObserver.SelectionReadCount;
        EnvironmentSandboxPlacementProjectionV1 publicPlacementProjection;
        using (var sandboxPlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"{sandboxPath}/v1/placement",
            ownerWithoutSelection.Token,
            [TenantId]))
        {
            await AssertStatusAsync(sandboxPlacement, HttpStatusCode.OK);
            publicPlacementProjection =
                await ReadJsonAsync<EnvironmentSandboxPlacementProjectionV1>(sandboxPlacement);
            var currentLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
            Assert.NotNull(currentLease);
            Assert.Equal(1, publicPlacementProjection.ContractVersion);
            Assert.Equal(TenantId, publicPlacementProjection.TenantId);
            Assert.Equal(project.ProjectId, publicPlacementProjection.ProjectId);
            Assert.Equal(RunId, publicPlacementProjection.RunId);
            Assert.Equal(environmentId, publicPlacementProjection.EnvironmentId);
            Assert.Equal(fence.LifecycleGeneration, publicPlacementProjection.LifecycleGeneration);
            Assert.Equal(currentLease.CurrentFencingGeneration, publicPlacementProjection.CurrentFencingGeneration);
            Assert.Equal(currentLease.ProviderFencingGeneration, publicPlacementProjection.ProviderFencingGeneration);
            Assert.Equal(currentLease.LeaseRevision, publicPlacementProjection.LeaseRevision);
            Assert.Equal(currentLease.LeaseExpiresAt, publicPlacementProjection.LeaseExpiresAt);
            Assert.True(publicPlacementProjection.IsCurrent);
            Assert.Equal(SandboxLeaseState.Active, publicPlacementProjection.State);
            Assert.Equal(currentLease.ProvisionedResource!.Resource, publicPlacementProjection.Resource);
            Assert.Equal(currentLease.ProvisionedResource.Endpoint, publicPlacementProjection.Endpoint);
            Assert.Equal(currentLease.ProvisionedResource.Placement, publicPlacementProjection.Placement);
        }
        Assert.Equal(selectionReadsBeforePlacementProjection, selectionObserver.SelectionReadCount);
        using (var viewerPlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"{sandboxPath}/v1/placement",
            viewer.Token,
            [TenantId]))
            await AssertForbiddenAsync(viewerPlacement, "project_write_not_authorized");

        var internalPlacementPath = $"{sandboxPath}/v1/internal/placement";
        var selectionReadsBeforeInternalPlacement = selectionObserver.SelectionReadCount;
        var authorizationReadsBeforeInternalPlacement = selectionObserver.AuthorizationContextReadCount;
        var sandboxCreatesBeforeInternalPlacement = sandboxKubernetesHandler.CreateCount;
        var sandboxDeletesBeforeInternalPlacement = sandboxKubernetesHandler.DeleteRequests.Count;
        using (var runBoundPlacement = await SendAsync(
            environment.Client, HttpMethod.Get, internalPlacementPath, runToken, [TenantId]))
        {
            await AssertStatusAsync(runBoundPlacement, HttpStatusCode.OK);
            Assert.Equal(
                publicPlacementProjection,
                await ReadJsonAsync<EnvironmentSandboxPlacementProjectionV1>(runBoundPlacement));
        }
        Assert.Equal(
            authorizationReadsBeforeInternalPlacement + 3,
            selectionObserver.AuthorizationContextReadCount);
        using (var publicRunBoundPlacement = await SendAsync(
            environment.Client, HttpMethod.Get, $"{sandboxPath}/v1/placement", runToken, [TenantId]))
            await AssertForbiddenAsync(publicRunBoundPlacement, "project_write_not_authorized");
        using (var unboundOwnerPlacement = await SendAsync(
            environment.Client, HttpMethod.Get, internalPlacementPath, ownerWithoutSelection.Token, [TenantId]))
            await AssertForbiddenAsync(unboundOwnerPlacement, "run_selection_not_authorized");
        using (var viewerRunBoundPlacement = await SendAsync(
            environment.Client, HttpMethod.Get, internalPlacementPath, viewer.Token, [TenantId]))
            await AssertForbiddenAsync(viewerRunBoundPlacement, "run_selection_not_authorized");
        using (var missingRolePlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{missingRoleRunId}/environments/{environmentId}/sandbox/v1/internal/placement",
            missingRoleRunToken,
            [TenantId]))
            await AssertForbiddenAsync(missingRolePlacement, "run_selection_not_authorized");
        using (var foreignProjectPlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"/api/projects/{foreignProject.ProjectId}/runs/{RunId}/environments/{environmentId}/sandbox/v1/internal/placement",
            runToken,
            [TenantId]))
            await AssertForbiddenAsync(foreignProjectPlacement, "authorization_context_mismatch");
        using (var foreignRunPlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}-foreign/environments/{environmentId}/sandbox/v1/internal/placement",
            runToken,
            [TenantId]))
            await AssertForbiddenAsync(foreignRunPlacement, "authorization_context_mismatch");
        using (var foreignTenantPlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            internalPlacementPath,
            runToken,
            [Guid.NewGuid().ToString("D")]))
            Assert.Equal(HttpStatusCode.Forbidden, foreignTenantPlacement.StatusCode);

        var wrongAudienceJwt = new JwtSecurityToken(
            issuer: new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience: "https://wrong-environment.test",
            claims: [new System.Security.Claims.Claim("sub", "placement-wrong-audience")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        wrongAudienceJwt.Header["typ"] = "at+jwt";
        using (var wrongAudiencePlacement = await SendAsync(
            environment.Client,
            HttpMethod.Get,
            internalPlacementPath,
            new JwtSecurityTokenHandler().WriteToken(wrongAudienceJwt),
            [TenantId]))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongAudiencePlacement.StatusCode);

        selectionObserver.RevokeAfterNextAuthorizationContext(() =>
            RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runSelectionAssignment.AssignmentId,
                runSelectionAssignment.Revision));
        using (var revokedRunBoundPlacement = await SendAsync(
            environment.Client, HttpMethod.Get, internalPlacementPath, runToken, [TenantId]))
            await AssertForbiddenAsync(revokedRunBoundPlacement, "authorization_changed");

        Assert.Equal(selectionReadsBeforeInternalPlacement, selectionObserver.SelectionReadCount);
        Assert.Equal(sandboxCreatesBeforeInternalPlacement, sandboxKubernetesHandler.CreateCount);
        Assert.Equal(sandboxDeletesBeforeInternalPlacement, sandboxKubernetesHandler.DeleteRequests.Count);

        var authorizationReadsBeforeOwnerLockWait = selectionObserver.AuthorizationContextReadCount;
        var selectionReadsBeforeOwnerLockWait = selectionObserver.SelectionReadCount;
        var createsBeforeOwnerLockWait = sandboxKubernetesHandler.CreateCount;
        var deletesBeforeOwnerLockWait = sandboxKubernetesHandler.DeleteRequests.Count;
        var workspaceProvisionCallsBeforeOwnerLockWait = provider.ProvisionCalls;
        var workspaceReleaseCallsBeforeOwnerLockWait = provider.ReleaseCalls;
        var ownerLockKey = string.Concat(
            environmentOwner.TenantId.Length, ":", environmentOwner.TenantId,
            environmentOwner.ProjectId.Length, ":", environmentOwner.ProjectId,
            environmentOwner.RunId.Length, ":", environmentOwner.RunId,
            environmentOwner.EnvironmentId.Length, ":", environmentOwner.EnvironmentId);
        await using var ownerLockConnection = await environment.OpenConnectionAsync();
        await using var ownerLockTransaction = await ownerLockConnection.BeginTransactionAsync();
        int ownerLockBackendPid;
        await using (var backendPidCommand = new NpgsqlCommand(
            "SELECT pg_backend_pid()", ownerLockConnection, ownerLockTransaction))
            ownerLockBackendPid = Convert.ToInt32(await backendPidCommand.ExecuteScalarAsync());
        var ownerLockAcquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        selectionObserver.AfterAuthorizationContextRead(
            authorizationReadsBeforeOwnerLockWait + 2,
            async () =>
            {
                await using var acquireOwnerLock = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@owner_lock, 0))",
                    ownerLockConnection,
                    ownerLockTransaction);
                acquireOwnerLock.Parameters.AddWithValue("owner_lock", ownerLockKey);
                await acquireOwnerLock.ExecuteNonQueryAsync();
                ownerLockAcquired.TrySetResult(true);
            });
        var ownerLockWaitResponseTask = SendAsync(
            environment.Client,
            HttpMethod.Get,
            $"{sandboxPath}/v1/placement",
            placementLockOwner.Token,
            [TenantId]);
        await ownerLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var lockWaitObserverConnection = await environment.OpenConnectionAsync())
            await AssertOwnerLockWaitAsync(lockWaitObserverConnection, ownerLockBackendPid);
        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource,
            placementLockAssignment.AssignmentId,
            placementLockAssignment.Revision);
        await ownerLockTransaction.CommitAsync();
        using (var lockWaitDenied = await ownerLockWaitResponseTask.WaitAsync(TimeSpan.FromSeconds(10)))
            await AssertForbiddenAsync(lockWaitDenied, "authorization_changed");
        Assert.Equal(
            authorizationReadsBeforeOwnerLockWait + 3,
            selectionObserver.AuthorizationContextReadCount);
        Assert.Equal(selectionReadsBeforeOwnerLockWait, selectionObserver.SelectionReadCount);
        Assert.Equal(createsBeforeOwnerLockWait, sandboxKubernetesHandler.CreateCount);
        Assert.Equal(deletesBeforeOwnerLockWait, sandboxKubernetesHandler.DeleteRequests.Count);
        Assert.Equal(workspaceProvisionCallsBeforeOwnerLockWait, provider.ProvisionCalls);
        Assert.Equal(workspaceReleaseCallsBeforeOwnerLockWait, provider.ReleaseCalls);

        Assert.Equal(3, sandboxKubernetesHandler.CreateCount);
        sandboxKubernetesHandler.MarkSandboxReady();
        var attachedWorkspace = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(attachedWorkspace);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Attached, attachedWorkspace.Phase);
        await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            lifecycleStore.ReserveWorkspaceVolumeReplaceAsync(
                fence,
                volumeId,
                attachedWorkspace.TransitionRevision,
                attachedWorkspace.ResourceGeneration,
                attachedWorkspace.DataGeneration,
                "sandbox-blocked-replace",
                CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            lifecycleStore.ReserveWorkspaceVolumeReleaseAsync(
                fence,
                volumeId,
                attachedWorkspace.TransitionRevision,
                attachedWorkspace.ResourceGeneration,
                attachedWorkspace.DataGeneration,
                "sandbox-blocked-release",
                CancellationToken.None));

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
            Assert.Contains(
                result.Observation.StartupPhases,
                phase => phase.Phase == SandboxStartupPhase.Started);
            Assert.DoesNotContain(
                result.Observation.StartupPhases,
                phase => phase.Phase is SandboxStartupPhase.Configured or SandboxStartupPhase.Ready);
            Assert.False(result.ReadyForDispatch);
            Assert.Equal(2, sandboxKubernetesHandler.SandboxObjectReadCount);
        }

        var sandboxLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(sandboxLease);
        Assert.Equal(SandboxLeaseState.Active, sandboxLease.State);
        Assert.Equal(sandboxProvisionResult.Resource, sandboxLease.ProvisionedResource!.Resource);
        Assert.Equal(sandboxProvisionResult.Resource!.ResourceId, sandboxLease.ProvisionedResource.Resource.ResourceId);
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
        Assert.Empty(sandboxKubernetesHandler.DeleteRequests);

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
        Assert.Empty(sandboxKubernetesHandler.DeleteRequests);
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
        Assert.Equal(3, sandboxKubernetesHandler.DeleteRequests.Count);
        var detachedWorkspace = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(detachedWorkspace);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, detachedWorkspace.Phase);
        Assert.All(sandboxKubernetesHandler.DeleteRequests, deletion =>
        {
            Assert.True(deletion.UidPreconditionMatched);
            Assert.Equal("Foreground", deletion.PropagationPolicy);
        });
        var firstClaimDelete = Assert.Single(
            sandboxKubernetesHandler.DeleteRequests,
            deletion => deletion.Resource == "sandboxclaims");
        Assert.Equal(sandboxLease.ProvisionedResource.Resource.ResourceId, firstClaimDelete.ExpectedUid);
        Assert.Equal(fence, sandboxLease.Fence);
        Assert.Equal(sandboxLease.ResourceGeneration, sandboxLease.ProvisionedResource.Resource.Generation);
        Assert.Equal(sandboxLease.ProviderFencingGeneration, sandboxLease.CurrentFencingGeneration);
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
        Assert.Equal(3, sandboxKubernetesHandler.DeleteRequests.Count);

        sandboxKubernetesHandler.FailNextClaimCreateResponse = true;
        using (var interruptedProvision = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/provision",
            owner.Token,
            sandboxProvisionRequest with { IdempotencyKey = "sandbox-provision-recovery" }))
            await AssertStatusAsync(interruptedProvision, HttpStatusCode.ServiceUnavailable);
        var interruptedLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(interruptedLease);
        Assert.Equal(SandboxLeaseState.ReconciliationRequired, interruptedLease.State);
        Assert.Null(interruptedLease.ProvisionedResource);
        var attachedForRecovery = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(attachedForRecovery);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Attached, attachedForRecovery.Phase);

        using (var recoverProvision = await SendAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/reconcile?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
        {
            await AssertStatusAsync(recoverProvision, HttpStatusCode.Accepted);
            var recovered = await ReadJsonAsync<EnvironmentSandboxResult>(recoverProvision);
            Assert.Equal(SandboxLeaseState.Active, recovered.State);
            Assert.False(recovered.ReadyForDispatch);
        }
        var recoveredLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(recoveredLease);
        Assert.Equal(SandboxLeaseState.Active, recoveredLease.State);
        Assert.NotNull(recoveredLease.ProvisionedResource);
        using (var retireRecovered = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            new SandboxAbandonApiRequest(
                recoveredLease.ResourceGeneration,
                recoveredLease.ProviderFencingGeneration,
                "retire-recovered-sandbox")))
            await AssertStatusAsync(retireRecovered, HttpStatusCode.OK);
        Assert.Equal(6, sandboxKubernetesHandler.DeleteRequests.Count);
        var workspaceAfterRecoveredRetirement = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(workspaceAfterRecoveredRetirement);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, workspaceAfterRecoveredRetirement.Phase);

        sandboxKubernetesHandler.FailNextClaimCreateResponse = true;
        using (var interruptedRetirement = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/provision",
            owner.Token,
            sandboxProvisionRequest with { IdempotencyKey = "sandbox-provision-partial-retirement" }))
            await AssertStatusAsync(interruptedRetirement, HttpStatusCode.ServiceUnavailable);
        var partialLease = await sandboxLeaseStore.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(partialLease);
        Assert.Equal(SandboxLeaseState.ReconciliationRequired, partialLease.State);
        Assert.Null(partialLease.ProvisionedResource);
        using (var abandonPartial = await SendJsonAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/abandon",
            owner.Token,
            new SandboxAbandonApiRequest(
                partialLease.ResourceGeneration,
                partialLease.ProviderFencingGeneration,
                "abandon-partial-sandbox")))
        {
            await AssertStatusAsync(abandonPartial, HttpStatusCode.Accepted);
            var pending = await ReadJsonAsync<EnvironmentSandboxResult>(abandonPartial);
            Assert.Equal(SandboxLeaseState.Releasing, pending.State);
        }
        Assert.Equal(6, sandboxKubernetesHandler.DeleteRequests.Count);
        using (var reconcilePartialRelease = await SendAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/reconcile?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
        {
            await AssertStatusAsync(reconcilePartialRelease, HttpStatusCode.OK);
            var released = await ReadJsonAsync<EnvironmentSandboxResult>(reconcilePartialRelease);
            Assert.Equal(SandboxLeaseState.Released, released.State);
        }
        Assert.Equal(8, sandboxKubernetesHandler.DeleteRequests.Count);
        var releasedPartialLease = await sandboxLeaseStore.GetAsync(
            fence, partialLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(releasedPartialLease);
        Assert.Equal(SandboxLeaseState.Released, releasedPartialLease.State);
        Assert.Null(releasedPartialLease.ProvisionedResource);
        Assert.NotNull(releasedPartialLease.PartialReleaseReceipt);
        var partialTemplateDelete = Assert.Single(
            sandboxKubernetesHandler.DeleteRequests,
            deletion => deletion.Resource == "sandboxtemplates" &&
                        deletion.ExpectedUid == releasedPartialLease.PartialReleaseReceipt.TemplateUid);
        var partialPoolDelete = Assert.Single(
            sandboxKubernetesHandler.DeleteRequests,
            deletion => deletion.Resource == "sandboxwarmpools" &&
                        deletion.ExpectedUid == releasedPartialLease.PartialReleaseReceipt.WarmPoolUid);
        Assert.True(partialTemplateDelete.UidPreconditionMatched);
        Assert.True(partialPoolDelete.UidPreconditionMatched);
        Assert.All(sandboxKubernetesHandler.DeleteRequests, deletion =>
        {
            Assert.True(deletion.UidPreconditionMatched);
            Assert.Equal("Foreground", deletion.PropagationPolicy);
        });
        var workspaceAfterPartialRetirement = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(workspaceAfterPartialRetirement);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, workspaceAfterPartialRetirement.Phase);
        Assert.Equal(beforeDeniedWrites.ResourceGeneration, workspaceAfterPartialRetirement.ResourceGeneration);
        Assert.Equal(beforeDeniedWrites.DataGeneration, workspaceAfterPartialRetirement.DataGeneration);

        var callbackGate = sandboxKubernetesHandler.DeferNextClaimCreateResponse();
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
        Assert.Equal(8, sandboxKubernetesHandler.DeleteRequests.Count);
        callbackGate.AllowCompletion.TrySetResult();
        EnvironmentSandboxResult lateProvisionResult;
        using (var lateProvision = await lateProvisionTask.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            await AssertStatusAsync(lateProvision, HttpStatusCode.Accepted);
            lateProvisionResult = await ReadJsonAsync<EnvironmentSandboxResult>(lateProvision);
            Assert.Equal(SandboxLeaseState.Releasing, lateProvisionResult.State);
            Assert.False(lateProvisionResult.ReadyForDispatch);
        }
        var fencedLateLease = await sandboxLeaseStore.GetAsync(
            fence, lateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(fencedLateLease);
        Assert.Equal(SandboxLeaseState.Releasing, fencedLateLease.State);
        Assert.True(fencedLateLease.CurrentFencingGeneration > fencedLateLease.ProviderFencingGeneration);
        Assert.NotNull(fencedLateLease.ProvisionedResource);
        Assert.Equal(fencedLateLease.ProvisionedResource.Resource, lateProvisionResult.Resource);
        using (var reconcileLateProvision = await SendAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/reconcile?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
            await AssertStatusAsync(reconcileLateProvision, HttpStatusCode.OK);
        Assert.Equal(11, sandboxKubernetesHandler.DeleteRequests.Count);
        var releasedLateLease = await sandboxLeaseStore.GetAsync(
            fence, lateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(releasedLateLease);
        Assert.Equal(SandboxLeaseState.Released, releasedLateLease.State);
        Assert.False(releasedLateLease.IsCurrent);
        var claimDeletes = sandboxKubernetesHandler.DeleteRequests
            .Where(deletion => deletion.Resource == "sandboxclaims")
            .ToArray();
        Assert.Equal(3, claimDeletes.Length);
        Assert.Equal(
            fencedLateLease.ProvisionedResource.Resource.ResourceId,
            claimDeletes[2].ExpectedUid);
        var lateReleaseDescriptor = JsonNode.Parse(
            fencedLateLease.ProvisionedResource.ProviderBinding.ReleaseDescriptor.GetRawText())!.AsObject();
        lateReleaseDescriptor["claimUid"] = "late-provider-uid";
        var differingLateResource = fencedLateLease.ProvisionedResource with
        {
            Resource = fencedLateLease.ProvisionedResource.Resource with
            {
                ResourceId = "late-provider-uid"
            },
            ProviderBinding = fencedLateLease.ProvisionedResource.ProviderBinding with
            {
                ReleaseDescriptor = JsonSerializer.SerializeToElement(lateReleaseDescriptor)
            }
        };
        var preservedTerminalLease = await sandboxLeaseStore.CompleteProvisionAsync(
            fencedLateLease.OperationId,
            fence,
            differingLateResource,
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, preservedTerminalLease.State);
        Assert.Equal(fencedLateLease.ProvisionedResource.Resource, preservedTerminalLease.ProvisionedResource!.Resource);
        using (var reconcileLateResource = await SendAsync(
            environment.Client,
            HttpMethod.Post,
            $"{sandboxPath}/reconcile?networkPolicyGeneration=1",
            owner.Token,
            [TenantId]))
            await AssertStatusAsync(reconcileLateResource, HttpStatusCode.NotFound);
        Assert.Equal(11, sandboxKubernetesHandler.DeleteRequests.Count);
        Assert.Null(await sandboxLeaseStore.ClaimNextLateResourceCleanupAsync(fence, CancellationToken.None));
        var afterLateResourceCleanup = await sandboxLeaseStore.GetAsync(
            fence, fencedLateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(afterLateResourceCleanup);
        Assert.Equal(SandboxLeaseState.Released, afterLateResourceCleanup.State);
        Assert.Equal(fencedLateLease.ProvisionedResource.Resource, afterLateResourceCleanup.ProvisionedResource!.Resource);
        Assert.Equal(lateLease.ResourceGeneration, fencedLateLease.ProvisionedResource.Resource.Generation);
        Assert.Equal(lateLease.ProviderFencingGeneration, fencedLateLease.ProviderFencingGeneration);
        Assert.Equal(fence, fencedLateLease.Fence);
        var workspaceAfterSandboxRetirement = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(workspaceAfterSandboxRetirement);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, workspaceAfterSandboxRetirement.Phase);
        Assert.Equal(beforeDeniedWrites.ResourceGeneration, workspaceAfterSandboxRetirement.ResourceGeneration);
        Assert.Equal(beforeDeniedWrites.DataGeneration, workspaceAfterSandboxRetirement.DataGeneration);
        Assert.Equal(beforeDeniedWrites.Resource, workspaceAfterSandboxRetirement.Resource);
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

    private static async Task AssertOwnerLockWaitAsync(
        NpgsqlConnection connection,
        int blockingBackendPid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity AS waiting
                    WHERE @blocking_pid = ANY(pg_blocking_pids(waiting.pid))
                      AND waiting.wait_event_type = 'Lock'
                      AND waiting.query LIKE '%pg_advisory_xact_lock%'
                )
                """, connection);
            command.Parameters.AddWithValue("blocking_pid", blockingBackendPid);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync()))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException(
            "The current placement read did not wait on the Environment owner advisory lock.");
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

    private sealed class FakeKubernetesHandler : HttpMessageHandler
    {
        private const string KubernetesNamespace = "agentweaver";
        private const string SandboxApiPrefix = "apis/agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxes";
        private const string ClaimUidLabel = "agents.x-k8s.io/claim-uid";
        private readonly Dictionary<string, JsonNode> _resources = new(StringComparer.Ordinal);
        private readonly List<JsonNode> _pods = [];
        private ClaimCreateResponseGate? _nextClaimGate;
        private int _failNextClaimCreateResponse;
        private int _createCount;
        private int _sandboxObjectReadCount;

        public int CreateCount => Volatile.Read(ref _createCount);
        public int SandboxObjectReadCount => Volatile.Read(ref _sandboxObjectReadCount);
        public List<DeleteRequestObservation> DeleteRequests { get; } = [];
        public bool FailNextClaimCreateResponse
        {
            set => Interlocked.Exchange(ref _failNextClaimCreateResponse, value ? 1 : 0);
        }

        public FakeKubernetesHandler(
            string projectId,
            string environmentId,
            string volumeId,
            string workspaceClaimName,
            string workspaceClaimUid)
        {
            _resources["apis/node.k8s.io/v1/runtimeclasses/kata-vm"] = JsonNode.Parse(
                """
                {"apiVersion":"node.k8s.io/v1","kind":"RuntimeClass","metadata":{"name":"kata-vm"},
                 "handler":"kata-qemu"}
                """)!;
            _resources[$"api/v1/namespaces/{KubernetesNamespace}/persistentvolumeclaims/{workspaceClaimName}"] =
                JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "v1",
                    kind = "PersistentVolumeClaim",
                    metadata = new
                    {
                        name = workspaceClaimName,
                        @namespace = KubernetesNamespace,
                        uid = workspaceClaimUid,
                        annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["agentweaver.dev/project-id"] = projectId,
                            ["agentweaver.dev/volume-id"] = volumeId,
                            ["agentweaver.dev/generation"] = "1",
                            ["agentweaver.dev/environment-id"] = environmentId
                        }
                    },
                    status = new { phase = "Bound" }
                })!;
        }

        public ClaimCreateResponseGate DeferNextClaimCreateResponse()
        {
            var gate = new ClaimCreateResponseGate();
            if (Interlocked.CompareExchange(ref _nextClaimGate, gate, null) is not null)
                throw new InvalidOperationException("A Sandbox claim create response is already deferred.");
            return gate;
        }

        public void MarkSandboxReady()
        {
            var sandbox = _resources.Single(resource =>
                    resource.Key.StartsWith(SandboxApiPrefix + "/", StringComparison.Ordinal))
                .Value;
            sandbox["status"]!["conditions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Ready",
                    ["status"] = "True",
                    ["lastTransitionTime"] = "2026-10-06T12:01:00Z"
                }
            };

            var pod = _pods.Single();
            var status = pod["status"]!.AsObject();
            status["phase"] = "Running";
            var container = status["containerStatuses"]!.AsArray()
                .Single(item => item?["name"]?.GetValue<string>() == "agenthost")!;
            container["imageID"] = "ghcr.io/agentweaver/agenthost@sha256:deadbeef";
            container["ready"] = true;
            container["state"] = new JsonObject
            {
                ["running"] = new JsonObject { ["startedAt"] = "2026-10-06T12:01:00Z" }
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Get)
            {
                if (path == $"api/v1/namespaces/{KubernetesNamespace}/pods")
                    return JsonResponse(HttpStatusCode.OK, CreateList(_pods));
                if (path == SandboxApiPrefix)
                    return JsonResponse(
                        HttpStatusCode.OK,
                        CreateList(_resources
                            .Where(resource => resource.Key.StartsWith(SandboxApiPrefix + "/", StringComparison.Ordinal))
                            .Select(resource => resource.Value)));
                var claimCollectionPath =
                    $"apis/extensions.agents.x-k8s.io/v1beta1/namespaces/{KubernetesNamespace}/sandboxclaims";
                if (path == claimCollectionPath)
                    return JsonResponse(
                        HttpStatusCode.OK,
                        CreateList(_resources
                            .Where(resource => resource.Key.StartsWith(claimCollectionPath + "/", StringComparison.Ordinal))
                            .Select(resource => resource.Value)));
                if (_resources.TryGetValue(path, out var resource))
                {
                    if (path.StartsWith(SandboxApiPrefix + "/", StringComparison.Ordinal))
                        Interlocked.Increment(ref _sandboxObjectReadCount);
                    return JsonResponse(HttpStatusCode.OK, resource.DeepClone());
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!;
                if (path.EndsWith("/sandboxclaims", StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref _failNextClaimCreateResponse, 0) != 0)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var uid = $"kubernetes-uid-{Interlocked.Increment(ref _createCount)}";
                body["metadata"]!["uid"] = uid;
                var name = body["metadata"]!["name"]!.GetValue<string>();
                var resourcePath = $"{path}/{Uri.EscapeDataString(name)}";
                var stored = body.DeepClone();
                _resources[resourcePath] = stored;
                if (path.EndsWith("/sandboxclaims", StringComparison.Ordinal))
                {
                    AddPendingSandbox(stored);
                    var gate = Interlocked.Exchange(ref _nextClaimGate, null);
                    if (gate is not null)
                    {
                        gate.Started.TrySetResult();
                        await gate.AllowCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                return JsonResponse(HttpStatusCode.Created, body);
            }

            if (request.Method == HttpMethod.Patch)
            {
                var patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!.AsArray();
                if (!_resources.TryGetValue(path, out var current))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                var expectedUid = patch[0]!["value"]!.GetValue<string>();
                if (current["metadata"]!["uid"]!.GetValue<string>() != expectedUid)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                var annotationPath = patch[1]!["path"]!.GetValue<string>();
                var annotationName = annotationPath["/metadata/annotations/".Length..]
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                current["metadata"]!["annotations"]![annotationName] =
                    patch[1]!["value"]!.GetValue<string>();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Delete)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!;
                var expectedUid = body["preconditions"]!["uid"]!.GetValue<string>();
                var propagationPolicy = body["propagationPolicy"]!.GetValue<string>();
                var resourceName = path.Split('/')[^2];
                var matched = _resources.TryGetValue(path, out var current) &&
                    current["metadata"]!["uid"]!.GetValue<string>() == expectedUid;
                DeleteRequests.Add(new(
                    resourceName,
                    expectedUid,
                    propagationPolicy,
                    matched));
                if (!matched)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                _resources.Remove(path);
                if (resourceName == "sandboxclaims")
                    RemoveClaimChildren(expectedUid);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private void AddPendingSandbox(JsonNode claim)
        {
            var metadata = claim["metadata"]!;
            var claimUid = metadata["uid"]!.GetValue<string>();
            var claimName = metadata["name"]!.GetValue<string>();
            var warmPoolName = claim["spec"]!["warmPoolRef"]!["name"]!.GetValue<string>();
            var warmPool = _resources[
                $"apis/extensions.agents.x-k8s.io/v1beta1/namespaces/{KubernetesNamespace}/sandboxwarmpools/{warmPoolName}"];
            var templateName = warmPool["spec"]!["sandboxTemplateRef"]!["name"]!.GetValue<string>();
            var template = _resources[
                $"apis/extensions.agents.x-k8s.io/v1beta1/namespaces/{KubernetesNamespace}/sandboxtemplates/{templateName}"];
            var podTemplate = template["spec"]!["podTemplate"]!;
            var podLabels = podTemplate["metadata"]!["labels"]!.DeepClone().AsObject();
            podLabels[ClaimUidLabel] = claimUid;
            var sandboxName = $"sandbox-{claimUid}";
            var sandboxUid = $"sandbox-uid-{claimUid}";
            claim["status"] = new JsonObject
            {
                ["sandbox"] = new JsonObject { ["name"] = sandboxName }
            };
            var sandboxLabels = metadata["labels"]!.DeepClone().AsObject();
            sandboxLabels[ClaimUidLabel] = claimUid;
            _resources[$"{SandboxApiPrefix}/{sandboxName}"] = JsonSerializer.SerializeToNode(new
            {
                apiVersion = "agents.x-k8s.io/v1beta1",
                kind = "Sandbox",
                metadata = new
                {
                    name = sandboxName,
                    @namespace = KubernetesNamespace,
                    uid = sandboxUid,
                    generation = 1,
                    labels = sandboxLabels,
                    ownerReferences = new[]
                    {
                        new { kind = "SandboxClaim", name = claimName, uid = claimUid, controller = true }
                    }
                },
                status = new { conditions = Array.Empty<object>() }
            })!;
            _pods.Add(JsonSerializer.SerializeToNode(new
            {
                apiVersion = "v1",
                kind = "Pod",
                metadata = new
                {
                    name = $"sandbox-pod-{claimUid}",
                    @namespace = KubernetesNamespace,
                    uid = $"pod-uid-{claimUid}",
                    labels = podLabels,
                    ownerReferences = new[]
                    {
                        new { kind = "Sandbox", name = sandboxName, uid = sandboxUid, controller = true }
                    }
                },
                spec = podTemplate["spec"]!.DeepClone(),
                status = new
                {
                    phase = "Pending",
                    conditions = new[]
                    {
                        new
                        {
                            type = "PodScheduled",
                            status = "True",
                            lastTransitionTime = "2026-10-06T12:00:00Z"
                        }
                    },
                    containerStatuses = new[]
                    {
                        new
                        {
                            name = "agenthost",
                            ready = false,
                            state = new { waiting = new { reason = "ContainerCreating" } }
                        }
                    }
                }
            })!);
        }

        private void RemoveClaimChildren(string claimUid)
        {
            var sandboxPaths = _resources
                .Where(resource =>
                    resource.Key.StartsWith(SandboxApiPrefix + "/", StringComparison.Ordinal) &&
                    HasOwnerUid(resource.Value["metadata"]!, claimUid))
                .Select(resource => resource.Key)
                .ToArray();
            var sandboxUids = sandboxPaths.Select(path =>
                _resources[path]["metadata"]!["uid"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var path in sandboxPaths)
                _resources.Remove(path);
            _pods.RemoveAll(pod =>
                HasOwnerUid(pod["metadata"]!, claimUid) ||
                pod["metadata"]!["ownerReferences"]!.AsArray().Any(owner =>
                    owner?["uid"]?.GetValue<string>() is { } uid && sandboxUids.Contains(uid)));
        }

        private static bool HasOwnerUid(JsonNode metadata, string uid) =>
            metadata["ownerReferences"]!.AsArray().Any(owner =>
                owner?["uid"]?.GetValue<string>() == uid);

        private static JsonObject CreateList(IEnumerable<JsonNode> resources)
        {
            var items = new JsonArray();
            foreach (var resource in resources)
                items.Add(resource.DeepClone());
            return new JsonObject { ["items"] = items };
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, JsonNode body) =>
            new(status)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

        public sealed record DeleteRequestObservation(
            string Resource,
            string ExpectedUid,
            string PropagationPolicy,
            bool UidPreconditionMatched);

        public sealed class ClaimCreateResponseGate
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
        private Func<Task>? _afterNextAuthorizationContextRead;
        private int _selectionReadCount;
        private int _authorizationContextReadCount;
        private int _authorizationContextActionReadCount;

        public int SelectionReadCount => Volatile.Read(ref _selectionReadCount);
        public int AuthorizationContextReadCount => Volatile.Read(ref _authorizationContextReadCount);

        public void RevokeAfterNextSelectionRead(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (Interlocked.CompareExchange(ref _afterNextSelectionRead, action, null) is not null)
                throw new InvalidOperationException("A run-selection revocation is already armed.");
        }

        public void RevokeAfterNextAuthorizationContext(Func<Task> action)
        {
            AfterAuthorizationContextRead(AuthorizationContextReadCount + 1, action);
        }

        public void AfterAuthorizationContextRead(int readCount, Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (readCount <= AuthorizationContextReadCount)
                throw new ArgumentOutOfRangeException(nameof(readCount));
            if (Interlocked.CompareExchange(ref _afterNextAuthorizationContextRead, action, null) is not null)
                throw new InvalidOperationException("An authorization-context revocation is already armed.");
            Volatile.Write(ref _authorizationContextActionReadCount, readCount);
        }

        public async Task OnResponseAsync(
            HttpRequestMessage request,
            HttpResponseMessage response)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri)
                return;

            if (string.Equals(uri.AbsolutePath, "/api/authorization/context", StringComparison.Ordinal))
            {
                var readCount = Interlocked.Increment(ref _authorizationContextReadCount);
                if (readCount == Volatile.Read(ref _authorizationContextActionReadCount))
                {
                    var authorizationAction =
                        Interlocked.Exchange(ref _afterNextAuthorizationContextRead, null);
                    if (response.IsSuccessStatusCode && authorizationAction is not null)
                        await authorizationAction().ConfigureAwait(false);
                }
                return;
            }
            if (!uri.AbsolutePath.EndsWith("/selection", StringComparison.Ordinal))
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

        public ValueTask<NpgsqlConnection> OpenConnectionAsync(
            CancellationToken cancellationToken = default) =>
            _dataSource.OpenConnectionAsync(cancellationToken);

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
