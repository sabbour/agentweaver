using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentWorkspaceVolumeApiTests
{
    [Theory]
    [InlineData("capacity_exceeded")]
    [InlineData("consistency_unsupported")]
    [InlineData("storage_class_mismatch")]
    [InlineData("storage_class_missing")]
    [InlineData("storage_class_invalid")]
    [InlineData("mount_options_invalid")]
    public void DeterministicAzureFilesRejectionsReturnUnprocessableEntity(string code)
    {
        var result = EnvironmentEndpoints.ToAzureFilesCsiErrorResult(
            new AzureFilesCsiException(code, "preflight rejection", effectMayHaveApplied: false));

        Assert.Equal(
            StatusCodes.Status422UnprocessableEntity,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void TransientAzureFilesFailuresRemainServiceUnavailable()
    {
        var result = EnvironmentEndpoints.ToAzureFilesCsiErrorResult(
            new AzureFilesCsiException("storage_class_unavailable", "Kubernetes API unavailable."));

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void WorkspaceVolumeRoutesRequireAuthenticationAndExcludeUngatedOperations()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EnvironmentLifecycleManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentEgressManager>();
        builder.Services.AddScoped<EnvironmentSandboxManager>();
        builder.Services.AddScoped<EnvironmentWorkspaceVolumeManager>();
        using var app = builder.Build();
        app.MapEnvironmentEndpoints();

        var volumeEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains(
                "/workspace-volumes", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(13, volumeEndpoints.Length);
        Assert.All(volumeEndpoints, endpoint =>
        {
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), _ => true);
            Assert.DoesNotContain(endpoint.Metadata, item => item is IAllowAnonymous);
        });
        Assert.DoesNotContain(volumeEndpoints, endpoint =>
            endpoint.RoutePattern.RawText!.Contains("attach", StringComparison.OrdinalIgnoreCase));
        Assert.Single(volumeEndpoints, endpoint =>
            endpoint.RoutePattern.RawText!.EndsWith("/{volumeId}/flush", StringComparison.Ordinal));
        Assert.Contains(volumeEndpoints, endpoint =>
            endpoint.RoutePattern.RawText ==
            "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes/{volumeId}");
        Assert.Contains(volumeEndpoints, endpoint =>
            endpoint.RoutePattern.RawText ==
            "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes/{volumeId}/provision");
        Assert.Contains(volumeEndpoints, endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') ==
            "/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes");
        Assert.Contains(volumeEndpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes/{volumeId}");
        var resolver = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(), endpoint =>
            endpoint.RoutePattern.RawText == "/internal/runtime/suspend/resolve");
        Assert.NotEmpty(resolver.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.DoesNotContain(resolver.Metadata, item => item is IAllowAnonymous);
    }

    [Fact]
    public async Task RecoveryDiscoveryRequiresSeparateRunSelectionAuthority()
    {
        var permissions = ImmutableArray.Create(
            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.WriteProjects, 1));
        var authorization = new ProjectAuthorizationContextResponse(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://identity.example",
            "actor-a",
            "tenant-a",
            1,
            null,
            null,
            [new EffectiveProjectAuthorization(
                ProjectAuthorityResourceType.Project,
                "project-a",
                permissions)]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(
            authorization,
            EnvironmentEgressManagerTests.Selection());
        var manager = new EnvironmentWorkspaceVolumeManager(projects, null!, null!);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ListRunEnvironmentsAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a", "run-a", null, null, CancellationToken.None));

        Assert.Equal("run_selection_not_authorized", exception.Code);
        Assert.Equal(0, projects.SelectionReads);
    }

    [Fact]
    public async Task RecoveryDiscoveryRejectsAuthorityLossBetweenReads()
    {
        ProjectAuthorizationContextResponse Context(bool canReadSelection)
        {
            var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
            permissions.Add(new(ProjectAuthorizationPermission.WriteProjects, 1));
            if (canReadSelection)
                permissions.Add(new(ProjectAuthorizationPermission.ReadRunSelection, 2));
            return new(
                ProjectAuthorizationContextContract.CurrentVersion,
                "https://identity.example",
                "actor-a",
                "tenant-a",
                1,
                null,
                null,
                [new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project, "project-a", permissions.ToImmutable())]);
        }

        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(
            [Context(canReadSelection: true), Context(canReadSelection: false)],
            EnvironmentEgressManagerTests.Selection());
        var manager = new EnvironmentWorkspaceVolumeManager(projects, null!, null!);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ListRunEnvironmentsAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a", "run-a", null, null, CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(1, projects.SelectionReads);
        Assert.Equal(2, projects.AuthorizationReads);
    }
    [Fact]
    public void RecoveryVolumeProjectionOmitsProviderOptionsAndReleaseDescriptor()
    {
        var fence = new EnvironmentGenerationFence(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a"), 4);
        var spec = new WorkspaceVolumeSpec(
            "volume-a", "project-a", new(WorkspaceVolumeOwnerKind.Run, "run-a"),
            "environment-a", WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany, 8, "azure-files-csi",
            WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "pvc-uid", 3);
        var binding = new WorkspaceVolumeProviderBindingSnapshot(
            "azure-files-csi", "1.0.0", 1, "options-revision",
            JsonSerializer.SerializeToElement(new { marker = "provider-options-marker" }),
            JsonSerializer.SerializeToElement(new { marker = "release-descriptor-marker" }));
        var snapshot = new EnvironmentWorkspaceVolumeSnapshot(
            fence, "volume-a", 7, 3, 5,
            EnvironmentWorkspaceVolumeState.Attached, EnvironmentWorkspaceVolumeOperation.Bind,
            JsonSerializer.SerializeToElement(spec), resource, binding);
        var pending = new EnvironmentWorkspaceVolumeRetryDescriptor(
            Guid.NewGuid(), EnvironmentWorkspaceVolumeOperation.Unbind, "exact-retry-key", 7, 3, 5);

        var status = EnvironmentWorkspaceVolumeProjection.FromSnapshot(snapshot, pending);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("exact-retry-key", json, StringComparison.Ordinal);
        Assert.Contains("azure-files-csi", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-options-marker", json, StringComparison.Ordinal);
        Assert.DoesNotContain("release-descriptor-marker", json, StringComparison.Ordinal);
        Assert.DoesNotContain("specification-marker", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushTransitionMappingPreservesTheSuspendEvidenceContract()
    {
        var fence = new EnvironmentGenerationFence(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a"), 1);
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "pvc-uid", 1);
        var empty = JsonSerializer.SerializeToElement(new { });
        var binding = new WorkspaceVolumeProviderBindingSnapshot(
            "azure-files", "1.0.0", 1, "options-1", empty, empty);
        var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(
            Guid.NewGuid(), fence, "volume-a", 5, 1, 7, resource, binding,
            6, 1, 8, EnvironmentWorkspaceVolumeState.Attached,
            EnvironmentWorkspaceVolumeOperation.Flush,
            EnvironmentWorkspaceVolumeTransitionState.Completed, false);
        var receipt = new WorkspaceVolumeFlushReceipt(
            1, fence, new("project-a", "volume-a", 1), resource, 7, 8,
            "flush-key", new string('a', 64), null);
        var result = new WorkspaceVolumeLifecycleResult(reservation, null, null)
        {
            FlushReceipt = receipt
        };

        var mapped = EnvironmentEndpoints.ToWorkspaceVolumeTransitionResult(result);

        var value = Assert.IsAssignableFrom<IValueHttpResult>(mapped);
        Assert.Same(result, value.Value);
        Assert.Equal(receipt, Assert.IsType<WorkspaceVolumeLifecycleResult>(value.Value).FlushReceipt);
    }

    [Fact]
    public void PublicTransitionMapperUsesPersistedTargetForCompletedReplay()
    {
        var fence = new EnvironmentGenerationFence(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a"), 1);
        var oldResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "old-pvc", 1);
        var newResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "new-pvc", 2);
        var empty = JsonSerializer.SerializeToElement(new { });
        var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(
            Guid.NewGuid(), fence, "volume-a", 1, 1, 0, oldResource,
            new WorkspaceVolumeProviderBindingSnapshot("azure-files", "1.0.0", 1, "options-1", empty, empty),
            2, 2, 0, EnvironmentWorkspaceVolumeState.Ready,
            EnvironmentWorkspaceVolumeOperation.Replace,
            EnvironmentWorkspaceVolumeTransitionState.Completed, true)
        {
            TargetResource = newResource
        };
        var result = new WorkspaceVolumeLifecycleResult(reservation, null, null);
        var request = new WorkspaceVolumeApiTransitionRequest(1, 1, 0, "replace-key");

        var mapped = EnvironmentEndpoints.ToWorkspaceVolumeTransitionResult(result, request);

        var response = Assert.IsType<EnvironmentWorkspaceVolumeMutationStatus>(
            Assert.IsAssignableFrom<IValueHttpResult>(mapped).Value);
        Assert.Equal(oldResource, response.Reservation.FromResource);
        Assert.Equal(newResource, response.Reservation.ToResource);
    }

    [Fact]
    public void RecoveryCursorCannotBeReusedAcrossOwnerScope()
    {
        var cursor = EnvironmentRecoveryCursor.Encode(new(
            1, "tenant-a", "project-a", "run-a", "environment-a", 4, "volume-a"));

        var decoded = EnvironmentRecoveryCursor.Decode(
            cursor, "tenant-a", "project-a", "run-a", "environment-a", 4);

        Assert.Equal("volume-a", decoded.AfterId);
        Assert.Throws<ArgumentException>(() => EnvironmentRecoveryCursor.Decode(
            cursor, "tenant-a", "project-a", "run-a", "environment-b", 4));
        Assert.Throws<ArgumentException>(() => EnvironmentRecoveryCursor.Decode(
            cursor, "tenant-a", "project-a", "run-b", "environment-a", 4));
    }
    [Fact]
    public void ProductionWorkspaceVolumeRegistrationPinsAzureFilesAdapterAndManager()
    {
        var services = new ServiceCollection();
        services.AddAgentweaverWorkspaceVolumeService(
            new AzureFilesCsiOptions(1, "options-1", "agentweaver", "azure-files", 100, 60, 100),
            new Uri("https://kubernetes.default.svc/"),
            @"C:\service-account\token",
            @"C:\service-account\ca.crt");

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IWorkspaceVolumeProvider) &&
            descriptor.ImplementationType == typeof(AzureFilesCsiWorkspaceVolumeProvider));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAzureFilesCsiClient));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(WorkspaceVolumeService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(EnvironmentWorkspaceVolumeManager) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}

public sealed class EnvironmentWorkspaceVolumeManagerTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task MetadataReadsRequireFreshReadProjectsPermission()
    {
        var store = fixture.CreateStore();
        var projects = new FakeProjectsConfigClient(Authorization(canReadProjects: false));
        var service = new WorkspaceVolumeService(store, new RecordingProvider());
        var manager = new EnvironmentWorkspaceVolumeManager(projects, store, service);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.InspectAsync(
                Caller(), ProjectId, RunId, EnvironmentId, VolumeId, CancellationToken.None));

        Assert.Equal("project_read_not_authorized", exception.Code);
    }

    [Fact]
    public async Task MutationRequiresWriteProjectsAndSeparateRunSelectionPermission()
    {
        var projects = new FakeProjectsConfigClient(Authorization(canWrite: true, canReadSelection: false));
        var provider = new RecordingProvider();
        var store = fixture.CreateStore();
        var service = new WorkspaceVolumeService(store, provider);
        var manager = new EnvironmentWorkspaceVolumeManager(projects, store, service);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.CreateAsync(
                Caller(),
                ProjectId,
                RunId,
                EnvironmentId,
                VolumeId,
                CreateRequest(),
                CancellationToken.None));

        Assert.Equal("run_selection_not_authorized", exception.Code);
        Assert.Equal(0, projects.RunSelectionCalls);
        Assert.Equal(0, provider.ProvisionCalls);
    }

    [Fact]
    public async Task RevokedAuthorizationBeforeProviderEffectLeavesProvisionUnapplied()
    {
        var store = fixture.CreateStore();
        var environmentId = $"environment-{Guid.NewGuid():N}";
        var owner = new EnvironmentOwnerIdentity(TenantId, ProjectId, RunId, environmentId);
        var lifecycle = await store.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner, 0, EnvironmentLifecycleState.Active, $"register-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var provider = new RecordingProvider();
        var service = new WorkspaceVolumeService(store, provider);
        _ = await service.CreateAsync(
            lifecycle.Snapshot.Fence,
            new WorkspaceVolumeTransitionRequest(
                VolumeId,
                WorkspaceVolumeTransitionKind.Create,
                0,
                0,
                0,
                null,
                "create-volume",
                Specification(environmentId)).ValidateFor(WorkspaceVolumeTransitionKind.Create));

        var projects = new FakeProjectsConfigClient(
            Authorization(),
            call => call >= 5 ? Authorization(canWrite: false) : Authorization());
        var manager = new EnvironmentWorkspaceVolumeManager(projects, store, service);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ProvisionAsync(
                Caller(),
                ProjectId,
                RunId,
                environmentId,
                VolumeId,
                new WorkspaceVolumeApiTransitionRequest(1, 0, 0, "provision-volume"),
                CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(0, provider.ProvisionCalls);
        Assert.Equal(1, projects.RunSelectionCalls);
    }

    [Fact]
    public async Task RevokedAuthorizationAfterProviderEffectLeavesOwnerTransitionForReconciliation()
    {
        var store = fixture.CreateStore();
        var environmentId = $"environment-{Guid.NewGuid():N}";
        var owner = new EnvironmentOwnerIdentity(TenantId, ProjectId, RunId, environmentId);
        var lifecycle = await store.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner, 0, EnvironmentLifecycleState.Active, $"register-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var provider = new RecordingProvider();
        var service = new WorkspaceVolumeService(store, provider);
        _ = await service.CreateAsync(
            lifecycle.Snapshot.Fence,
            new WorkspaceVolumeTransitionRequest(
                VolumeId,
                WorkspaceVolumeTransitionKind.Create,
                0,
                0,
                0,
                null,
                "create-volume",
                Specification(environmentId)).ValidateFor(WorkspaceVolumeTransitionKind.Create));

        var projects = new FakeProjectsConfigClient(
            Authorization(),
            call => call >= 6 ? Authorization(canWrite: false) : Authorization());
        var manager = new EnvironmentWorkspaceVolumeManager(projects, store, service);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ProvisionAsync(
                Caller(),
                ProjectId,
                RunId,
                environmentId,
                VolumeId,
                new WorkspaceVolumeApiTransitionRequest(1, 0, 0, "provision-after-effect"),
                CancellationToken.None));
        var reservation = await store.ReserveWorkspaceVolumeProvisionAsync(
            lifecycle.Snapshot.Fence,
            VolumeId,
            1,
            0,
            0,
            "provision-after-effect",
            CancellationToken.None);

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(1, provider.ProvisionCalls);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            reservation.TransitionState);
        Assert.Null((await store.GetWorkspaceVolumeAsync(
            lifecycle.Snapshot.Fence, VolumeId, CancellationToken.None))!.Resource);
    }

    private static CurrentCallerRequest Caller() => new("validated.jwt.token", TenantId);

    private static ProjectAuthorizationContextResponse Authorization(
        bool canWrite = true,
        bool canReadSelection = true,
        bool canReadProjects = true)
    {
        var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
        if (canReadProjects)
            permissions.Add(new(ProjectAuthorizationPermission.ReadProjects, 1));
        if (canWrite)
            permissions.Add(new(ProjectAuthorizationPermission.WriteProjects, 1));
        if (canReadSelection)
            permissions.Add(new(ProjectAuthorizationPermission.ReadRunSelection, 1));
        return new ProjectAuthorizationContextResponse(
            ProjectAuthorizationContextContract.CurrentVersion,
            "projects",
            "actor",
            TenantId,
            1,
            ProjectId,
            RunId,
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    ProjectId,
                    permissions.ToImmutable())
            ]);
    }

    private static CreateWorkspaceVolumeApiRequest CreateRequest() =>
        new(Specification(), "create-volume");

    private static WorkspaceVolumeSpec Specification(string environmentId = EnvironmentId) =>
        new(
            VolumeId,
            ProjectId,
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

    private const string ProjectId = "project-api";
    private const string RunId = "run-api";
    private const string EnvironmentId = "environment-api";
    private const string VolumeId = "volume-api";
    private const string TenantId = "tenant-api";

    private sealed class FakeProjectsConfigClient(
        ProjectAuthorizationContextResponse authorization,
        Func<int, ProjectAuthorizationContextResponse>? authorizationForCall = null) : IProjectsConfigClient
    {
        private int _authorizationCalls;

        public int RunSelectionCalls { get; private set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            var call = ++_authorizationCalls;
            return Task.FromResult(authorizationForCall?.Invoke(call) ?? authorization);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            RunSelectionCalls++;
            return Task.FromResult(new EffectiveNetworkPolicySelection(
                projectId,
                runId,
                1,
                1,
                1,
                "selection-1",
                [],
                [],
                null,
                [],
                []));
        }
    }

    private sealed class RecordingProvider : IWorkspaceVolumeProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public int ProvisionCalls { get; private set; }

        public Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceVolumeReleaseReceipt(
                request.Resource,
                request.IdempotencyKey,
                request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete
                    ? WorkspaceVolumeReleaseDisposition.Released
                    : WorkspaceVolumeReleaseDisposition.Retained));
    }
}
