using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agentweaver.Environment.Tests;

// Current authority is isolated here; the combined Broker/Core runtime proof is separate.
public sealed class EnvironmentRuntimePlacementPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>, IDisposable
{
    private readonly HttpClient _kubernetes = new(new UnusedPlacementTransport())
    {
        BaseAddress = new Uri("https://kubernetes.test/")
    };

    public void Dispose() => _kubernetes.Dispose();

    [Fact]
    public async Task ExactLeaseIsDurableAcrossStoreRestartAndDoesNotRenewOnReplay()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var first = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        Assert.NotNull(first.Lease.CreatedAt);
        var replay = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Lease.OperationId, replay.Lease.OperationId);
        Assert.Equal(first.Lease.LeaseExpiresAt, replay.Lease.LeaseExpiresAt);
        Assert.Equal(first.Lease.CreatedAt, replay.Lease.CreatedAt);
        var resource = Resource(first.Lease);
        var completed = await store.CompleteProvisionAsync(
            first.Lease.OperationId, lifecycle.Fence, resource, true, default);
        Assert.Equal(2, completed.LeaseRevision);
        Assert.Equal(SandboxLeaseState.Active, completed.State);
        var restarted = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var current = await restarted.GetCurrentAsync(lifecycle.Fence, default);
        Assert.Equal(completed.LeaseRevision, current!.LeaseRevision);
        Assert.Equal(first.Lease.CreatedAt, current.CreatedAt);
        Assert.Equal(resource.Resource, current.ProvisionedResource!.Resource);
        var completeReplay = await restarted.CompleteProvisionAsync(
            first.Lease.OperationId, lifecycle.Fence, resource, true, default);
        Assert.Equal(completed.LeaseExpiresAt, completeReplay.LeaseExpiresAt);
        Assert.Equal(completed.LeaseRevision, completeReplay.LeaseRevision);

        var projects = new ControlledAuthority(owner);
        var projection = await Reader(projects, restarted).GetCurrentPlacementAsync(
            Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default);
        Assert.Equal(resource.Resource, projection!.Resource);
        Assert.Equal(resource.Endpoint, projection.Endpoint);
        Assert.Equal(resource.Placement, projection.Placement);
        Assert.Equal(completed.LeaseRevision, projection.LeaseRevision);
        Assert.Equal(completed.LeaseExpiresAt, projection.LeaseExpiresAt);
        Assert.Equal(resource.ProviderBinding.ProviderId, projection.ProviderPin!.ProviderId);
        Assert.Equal(resource.ProviderBinding.OptionsRevision, projection.ProviderPin.OptionsRevision);
        Assert.Equal(resource.Resource, projection.ProviderPin.Resource);
        Assert.True(resource.NegotiatedCapabilities.SetEquals(projection.ProviderPin.NegotiatedCapabilities));
        var projected = JsonSerializer.SerializeToElement(projection);
        Assert.False(projected.GetProperty("ProviderPin").TryGetProperty("OptionsSnapshot", out _));
        Assert.False(projected.GetProperty("ProviderPin").TryGetProperty("RecoveryMetadata", out _));
        Assert.Equal(0, projects.SelectionReads);
    }

    [Fact]
    public async Task ConcurrentReservationsHaveExactlyOneCurrentWinner()
    {
        var (_, lifecycle, store) = await CreateOwnerAsync();
        var outcomes = await Task.WhenAll(new[] { "one", "two" }.Select(async key =>
        {
            try
            {
                return (Lease: (await store.ReserveProvisionAsync(
                    lifecycle.Fence, key, Intent(), default)).Lease, Error: (Exception?)null);
            }
            catch (EnvironmentLifecycleException exception)
            {
                return (Lease: (SandboxLeaseSnapshot?)null, Error: (Exception)exception);
            }
        }));
        var winner = Assert.Single(outcomes, result => result.Lease is not null).Lease!;
        var loser = Assert.IsType<EnvironmentLifecycleException>(
            Assert.Single(outcomes, result => result.Error is not null).Error);
        Assert.Equal("sandbox_lease_capacity_exceeded", loser.Code);
        Assert.Equal(winner.OperationId, (await store.GetCurrentAsync(lifecycle.Fence, default))!.OperationId);
    }

    [Fact]
    public async Task ForeignStaleAndReleasedOwnersCannotReturnPlacement()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var lease = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var active = await store.CompleteProvisionAsync(
            lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default);
        var foreign = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetCurrentAsync(new EnvironmentGenerationFence(
                new(owner.TenantId, owner.ProjectId, "foreign", owner.EnvironmentId), 1), default));
        Assert.Equal("environment_unknown", foreign.Code);
        var lifecycleStore = fixture.CreateStore();
        var blocked = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            lifecycleStore.TransitionAsync(
                new(owner, lifecycle.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"), default));
        Assert.Equal("environment_sandbox_lease_active", blocked.Code);
        await RetireAsync(store, active);
        var advanced = await lifecycleStore.TransitionAsync(
            new(owner, lifecycle.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"), default);
        var stale = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetCurrentAsync(lifecycle.Fence, default));
        Assert.Equal("environment_fence_stale", stale.Code);
        var projects = new ControlledAuthority(owner);
        Assert.Null(await Reader(projects, store).GetCurrentPlacementAsync(
            Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        var released = await lifecycleStore.TransitionAsync(
            new(owner, advanced.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Released, "release"),
            default);
        var denied = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetCurrentAsync(released.Snapshot.Fence, default));
        Assert.Equal("environment_released", denied.Code);
    }

    [Fact]
    public async Task PlacementRejectsLeaseCompletionDuringFinalAuthorizationAwait()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var lease = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var projects = new ControlledAuthority(owner)
        {
            BeforeSecondRead = async () =>
                await store.CompleteProvisionAsync(
                    lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default)
        };
        var rejected = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Reader(projects, store).GetCurrentPlacementAsync(
                Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        Assert.Equal("sandbox_lease_stale", rejected.Code);
        Assert.Equal(2, (await store.GetCurrentAsync(lifecycle.Fence, default))!.LeaseRevision);
    }

    [Fact]
    public async Task PlacementRejectsLifecycleChangeDuringFinalAuthorizationAwait()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var lease = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var active = await store.CompleteProvisionAsync(
            lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default);
        var projects = new ControlledAuthority(owner)
        {
            BeforeSecondRead = async () =>
            {
                await RetireAsync(store, active);
                await fixture.CreateStore().TransitionAsync(
                    new(owner, lifecycle.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"),
                    default);
            }
        };
        var rejected = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Reader(projects, store).GetCurrentPlacementAsync(
                Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        Assert.Equal("environment_fence_stale", rejected.Code);
    }

    [Fact]
    public async Task PlacementRequiresFreshWriteAuthorityAndUnexpiredActiveLease()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var lease = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var projects = new ControlledAuthority(owner);
        var reader = Reader(projects, store);
        var notReady = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            reader.GetCurrentPlacementAsync(Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        Assert.Equal("sandbox_placement_unavailable", notReady.Code);
        var active = await store.CompleteProvisionAsync(
            lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default);
        var expired = Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, lifecycle.Fence, active, active.LeaseExpiresAt!.Value));
        Assert.Equal("sandbox_lease_expired", expired.Code);
        var revoked = new ControlledAuthority(owner);
        revoked.BeforeSecondRead = () =>
        {
            revoked.CanWrite = false;
            return Task.CompletedTask;
        };
        var denied = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            Reader(revoked, store).GetCurrentPlacementAsync(
                Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        Assert.Equal("authorization_changed", denied.Code);
    }

    [Fact]
    public async Task ConflictingIntentAndForeignProviderCompletionAreRejected()
    {
        var (_, lifecycle, store) = await CreateOwnerAsync();
        var lease = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent() with { OptionsRevision = "foreign" },
                default));
        Assert.Equal("sandbox_idempotency_conflict", conflict.Code);
        var resource = Resource(lease.Lease);
        var foreign = resource with { ProviderBinding = resource.ProviderBinding with { OptionsRevision = "foreign" } };
        var rejected = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.CompleteProvisionAsync(lease.Lease.OperationId, lifecycle.Fence, foreign, true, default));
        Assert.Equal("sandbox_provider_binding_mismatch", rejected.Code);
        Assert.Equal(SandboxLeaseState.Provisioning, (await store.GetCurrentAsync(lifecycle.Fence, default))!.State);
    }

    [Fact]
    public void PlacementRouteIsReadOnlyAndRequiresAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EnvironmentEgressManager>();
        builder.Services.AddScoped<EnvironmentWorkspaceVolumeManager>();
        builder.Services.AddScoped<EnvironmentSandboxManager>();
        using var app = builder.Build();
        app.MapEnvironmentEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText!.EndsWith("/sandbox/v1/placement", StringComparison.Ordinal));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var workspace = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText!.EndsWith("/runtime-bootstrap/profiles/{profileId}/workspace",
                StringComparison.Ordinal));
        Assert.NotEmpty(workspace.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(["GET"], workspace.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public async Task RuntimeWorkspaceReadRetainsTheActualLeaseAndAttachedVolumeWithoutProviderEffects()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var volumes = fixture.CreateStore();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var spec = new WorkspaceVolumeSpec("workspace", owner.ProjectId,
            new(WorkspaceVolumeOwnerKind.Run, owner.RunId), owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany, 8,
            "azure-files", WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
        await volumes.CreateWorkspaceVolumeAsync(
            lifecycle.Fence, spec.VolumeId, JsonSerializer.SerializeToElement(spec, json), "create", timeout.Token);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "workspace-claim", 1);
        var binding = new WorkspaceVolumeProviderBindingSnapshot(
            storage.ProviderId, "1.0.0", 1, "storage-options",
            JsonSerializer.SerializeToElement(new { endpoint = "controlled" }),
            JsonSerializer.SerializeToElement(new
            {
                @namespace = "agentweaver",
                claimName = storage.ResourceId, claimUid = "workspace-claim-uid"
            }));
        var provision = await volumes.ReserveWorkspaceVolumeProvisionAsync(
            lifecycle.Fence, spec.VolumeId, 1, 0, 0, "provision", timeout.Token);
        await volumes.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId, lifecycle.Fence, true, storage, binding, true, timeout.Token);
        var bind = await volumes.ReserveWorkspaceVolumeBindAsync(
            lifecycle.Fence, spec.VolumeId, 2, 1, 0, "bind", timeout.Token);
        await volumes.CompleteWorkspaceVolumeBindAsync(
            bind.OperationId, lifecycle.Fence, true, storage, true, timeout.Token);
        var attach = await volumes.ReserveWorkspaceVolumeAttachAsync(
            lifecycle.Fence, spec.VolumeId, 3, 1, 0, "attach", timeout.Token);
        await volumes.CompleteWorkspaceVolumeAttachAsync(
            attach.OperationId, lifecycle.Fence, true, storage, true, timeout.Token);
        var request = new SandboxProvisionApiRequest(spec.VolumeId, 1, 0,
            "/workspace/agentweaver/project", false, 7, "sandbox");
        var reserved = await store.ReserveProvisionAsync(lifecycle.Fence, "sandbox",
            Intent() with { ProviderRequest = JsonSerializer.SerializeToElement(request, json) }, timeout.Token);
        var sandbox = Resource(reserved.Lease);
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(owner.ProjectId, owner.EnvironmentId, owner.RunId,
            new(owner.ProjectId, spec.VolumeId, 1), sandbox.Resource, storage, lifecycle.Fence, 0,
            request.MountPath, false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        var attachment = new SandboxWorkspaceAttachment(negotiation, JsonSerializer.SerializeToElement(
            new AgentSandboxPersistentVolumeClaimAttachment(1, storage.ProviderId, "agentweaver",
                storage.ResourceId, "workspace-claim-uid"), json));
        await store.SaveProviderRequestAsync(reserved.Lease.OperationId, lifecycle.Fence,
            JsonSerializer.SerializeToElement(new
            {
                contractVersion = 1, request, workspace = attachment,
                egressSelectorLabels = ImmutableDictionary<string, string>.Empty,
                workspaceAttachmentTransitionRevision = 4
            }, json), timeout.Token);
        var lease = await store.CompleteProvisionAsync(
            reserved.Lease.OperationId, lifecycle.Fence, sandbox, true, timeout.Token);
        var projects = new ControlledAuthority(owner) { CanReadSelection = true };
        var manager = Reader(projects, new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System));
        var snapshot = (await volumes.GetWorkspaceVolumeAsync(lifecycle.Fence, spec.VolumeId, timeout.Token))!;
        EnvironmentWorkspaceVolumeSnapshot ChangedWorkspace(long revision, long dataGeneration,
            EnvironmentWorkspaceVolumeState phase, ProviderResourceRef resource) =>
            new(snapshot.EnvironmentFence, snapshot.VolumeId, revision, resource.Generation, dataGeneration,
                phase, snapshot.LastOperation, snapshot.Specification, resource, snapshot.ProviderBinding);
        foreach (var changed in new[]
        {
            ChangedWorkspace(5, 0, snapshot.Phase, storage),
            ChangedWorkspace(4, 1, snapshot.Phase, storage),
            ChangedWorkspace(4, 0, EnvironmentWorkspaceVolumeState.Bound, storage),
            ChangedWorkspace(4, 0, snapshot.Phase, storage with { ResourceId = "different-claim" }),
            ChangedWorkspace(4, 0, snapshot.Phase, storage with { Generation = 2 })
        })
            Assert.Equal("workspace_attachment_stale",
                Assert.Throws<EnvironmentLifecycleException>(() =>
                    EnvironmentSandboxManager.ProjectRuntimeWorkspace(lifecycle.Fence, lease, changed)).Code);
        var credential = new SecretCredential(
            "isolated.module.token", DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System);
        var core = new RuntimeOwnerContext(1, "https://module-identity.test", "module-actor",
            owner.TenantId, owner.ProjectId, owner.RunId, "session", "agent", "model", "turn",
            1, 1, 1, "context", new string('a', 64), 1, 1, 1, 1);
        using var coreTransport = new WorkspaceOwnerTransport(core);
        using var coreClient = new HttpClient(coreTransport);
        var reader = new EnvironmentRuntimePlacementReader(manager,
            new EnvironmentRuntimeOwnerContextClient(coreClient,
                new(new Uri("https://orchestrator.test/"), new Uri("https://broker.test/"))), TimeProvider.System);
        var profile = new EnvironmentRuntimeBootstrapProfile(
            "runtime", new Uri("https://runtime.test/runtime/v1/configure"), new Uri("https://orchestrator.test/observe"));
        var profiles = new EnvironmentRuntimeBootstrapProfileRegistry([new(owner, profile, sandbox.Resource)]);
        EnvironmentRuntimeWorkspaceContext? context;
        try
        {
            context = await reader.GetWorkspaceContextAsync(new(credential, owner.TenantId),
                owner.ProjectId, owner.RunId, core.SessionId, owner.EnvironmentId, profile.ProfileId, profiles, timeout.Token);
        }
        finally
        {
            credential.Invalidate();
        }
        Assert.NotNull(context);
        Assert.Equal(negotiation, context.Workspace);
        Assert.Equal(4, context.TransitionRevision);
        Assert.Equal(core, context.Placement.RuntimeOwnerContext);
        Assert.Equal(sandbox.Resource, context.Placement.Resource);
        Assert.Equal(1, coreTransport.Reads);
        var responseJson = JsonSerializer.Serialize(context, json);
        Assert.DoesNotContain("providerAttachmentDescriptor", responseJson);
        Assert.DoesNotContain("workspaceTreeSha256", responseJson);
        Assert.DoesNotContain("workspaceProviderCheckpointId", responseJson);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = manager.GetCurrentPlacementCoreAsync(
            Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, true, async (projection, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return projection;
            }, timeout.Token, includeRuntimeWorkspace: true);
        await Task.WhenAny(entered.Task, read).WaitAsync(timeout.Token);
        if (!entered.Task.IsCompleted)
        {
            await read;
            throw new InvalidOperationException("The Workspace read did not reach its retained callback.");
        }
        var application = "runtime-workspace-detach-" + Guid.NewGuid().ToString("N");
        await using var writerSource = fixture.CreateDataSource(application);
        var detach = new EnvironmentLifecycleStore(writerSource, TimeProvider.System).ReserveWorkspaceVolumeDetachAsync(
            lifecycle.Fence, spec.VolumeId, 4, 1, 0, "detach", timeout.Token);
        var exerciseError = await Record.ExceptionAsync(async () =>
        {
            await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            await using var blocked = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks locks JOIN pg_stat_activity activity ON activity.pid = locks.pid " +
                "WHERE activity.application_name = @application AND locks.locktype = 'advisory' AND NOT locks.granted",
                observer);
            blocked.Parameters.AddWithValue("application", application);
            while (Convert.ToInt64(await blocked.ExecuteScalarAsync(timeout.Token)) == 0)
            {
                if (detach.IsCompleted)
                    throw new InvalidOperationException("Workspace detachment bypassed the retained read.",
                        await Record.ExceptionAsync(() => detach));
                await using var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", observer);
                await clear.ExecuteNonQueryAsync(timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
            release.TrySetResult();
            var projection = await read;
            Assert.NotNull(projection?.RuntimeWorkspace);
            Assert.Equal(lease.LeaseRevision, projection.LeaseRevision);
            Assert.Equal(negotiation, projection.RuntimeWorkspace.Workspace);
            Assert.Equal(4, projection.RuntimeWorkspace.TransitionRevision);
            Assert.Equal(0, projects.SelectionReads);
            var detached = await detach;
            await volumes.CompleteWorkspaceVolumeDetachAsync(
                detached.OperationId, lifecycle.Fence, true, storage, true, timeout.Token);
            var stale = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
                manager.GetCurrentPlacementCoreAsync(Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId,
                    true, (current, _) => Task.FromResult(current), timeout.Token, includeRuntimeWorkspace: true));
            Assert.Equal("workspace_attachment_stale", stale.Code);
        });
        release.TrySetResult();
        var cleanupError = await Record.ExceptionAsync(() => Task.WhenAll(read, detach));
        if (exerciseError is not null && cleanupError is not null)
            throw new AggregateException("Workspace read and cleanup failed.", exerciseError, cleanupError);
        if (exerciseError is not null || cleanupError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exerciseError ?? cleanupError!).Throw();
    }

    [Fact]
    public async Task RuntimeReadinessReadsPinnedEvidenceWithoutRecursiveSelectionAndRetainsTheActualLeaseLock()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var caller = Caller(owner);
        var projects = new ControlledAuthority(owner) { CanReadSelection = true };
        var networkOptions = new CiliumEgressProviderOptions(
            "agentweaver", new Version(1, 0, 0), 1, "network-options",
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
        var capability = ImmutableArray.Create(CiliumEgressCapabilities.L3L4, CiliumEgressCapabilities.Fqdn,
            CiliumEgressCapabilities.Cidr, CiliumEgressCapabilities.Dns);
        var rules = ImmutableArray.Create(new NetworkEgressRule(NetworkEgressPurpose.SourceControl,
            NetworkEgressDestinationKind.Cidr, "203.0.113.0/24", 443, EgressProtocol.Tcp));
        var selection = new EffectiveNetworkPolicySelection(owner.ProjectId, owner.RunId, 1, 1, 1, "context-1",
            [new(ProviderCardinality.Layered, ProviderSeam.NetworkPolicy,
                [new(ProviderSeam.NetworkPolicy, CiliumEgressPolicyAdapter.ProviderId, "1.0.0", 1,
                    networkOptions.OptionsRevision, ProviderHostingPattern.KubernetesController,
                    capability, capability, NetworkPolicyLayer.L3L4)])], rules, null, rules, rules);
        var network = new ReadinessNetworkStore();
        var adapter = new CiliumEgressPolicyAdapter(network, networkOptions);
        var selector = EnvironmentEgressSelector.Create(owner.EnvironmentId, owner.TenantId, owner.ProjectId,
            owner.RunId, networkOptions.Namespace);
        const long policyGeneration = 7;
        var effect = await fixture.CreateStore().ReserveNetworkEffectAsync(lifecycle.Fence,
            $"{selector.Namespace}/{selector.PolicyName}", policyGeneration, 0, EnvironmentNetworkEffectKind.Apply,
            "network-ready", timeout.Token);
        var compilation = EgressIntentCompiler.Compile(selection);
        Assert.True(compilation.IsSuccess, compilation.Failure?.Message);
        await adapter.ApplyAsync(selector, compilation.Intent!, policyGeneration, 0, timeout.Token);
        await fixture.CreateStore().CompleteNetworkEffectAsync(effect.OperationId, lifecycle.Fence, true, true, timeout.Token);
        var sandboxOptions = new AgentSandboxOptions(AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options", "agentweaver", "azure-files-csi", "runtime-test@sha256:" + new string('a', 64),
            "linux/amd64", 123456, "kata-test", "kata-test", "100m", "128Mi", 20, 100,
            new(30, 30, 30, 30, 30, 120));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var provision = new SandboxProvisionApiRequest("workspace", 1, 0,
            "/workspace/agentweaver/project", false, policyGeneration, "runtime-ready");
        var intent = new SandboxLeaseProvisionIntent("agent-sandbox", "1.0.0",
            sandboxOptions.OptionsSchemaVersion, sandboxOptions.OptionsRevision,
            JsonSerializer.SerializeToElement(sandboxOptions, json), JsonSerializer.SerializeToElement(selection, json),
            JsonSerializer.SerializeToElement(provision, json));
        var reserved = await store.ReserveProvisionAsync(lifecycle.Fence, "runtime-ready", intent, timeout.Token);
        var resource = Resource(reserved.Lease);
        var workspace = new SandboxWorkspaceAttachment(
            new(owner.ProjectId, owner.EnvironmentId, owner.RunId,
                new(owner.ProjectId, provision.VolumeId, provision.VolumeResourceGeneration),
                resource.Resource,
                new(ProviderSeam.Storage, sandboxOptions.WorkspaceStorageProviderId, "workspace-claim",
                    provision.VolumeResourceGeneration),
                lifecycle.Fence, provision.DataGeneration, provision.MountPath, provision.ReadOnly,
                WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            JsonSerializer.SerializeToElement(new AgentSandboxPersistentVolumeClaimAttachment(
                1, sandboxOptions.WorkspaceStorageProviderId, sandboxOptions.Namespace, "workspace-claim",
                "workspace-claim-uid"), json)).Validate();
        var recoveryIntent = JsonSerializer.SerializeToElement(new
        {
            contractVersion = 1,
            request = provision,
            workspace,
            egressSelectorLabels = selector.MatchLabels,
            workspaceAttachmentTransitionRevision = 2
        }, json);
        await store.SaveProviderRequestAsync(
            reserved.Lease.OperationId, lifecycle.Fence, recoveryIntent, timeout.Token);
        var lease = await store.CompleteProvisionAsync(reserved.Lease.OperationId, lifecycle.Fence,
            resource, true, timeout.Token);
        var restarted = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var retained = await restarted.GetCurrentAsync(lifecycle.Fence, timeout.Token);
        Assert.True(JsonElement.DeepEquals(recoveryIntent, retained!.ProvisionIntent.ProviderRequest));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ReadinessSandboxProvider(async (request, token) =>
        {
            observed.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(request.Resource, SandboxObservedState.Pending, request.FencingGeneration, true, true, null,
                [new(SandboxStartupPhase.Scheduled, 1, request.LeaseCreatedAt),
                 new(SandboxStartupPhase.ImageReady, 1, request.LeaseCreatedAt,
                     "sha256:" + new string('a', 64), 123456),
                 new(SandboxStartupPhase.Started, 1, request.LeaseCreatedAt)]);
        });
        var egress = new EnvironmentEgressManager(projects, adapter, networkOptions, fixture.CreateStore());
        var manager = new EnvironmentSandboxManager(projects, fixture.CreateStore(), restarted, provider,
            sandboxOptions, new KubernetesAgentSandboxClient(_kubernetes), networkOptions, egress);
        var read = manager.GetCurrentPlacementCoreAsync(caller, owner.ProjectId, owner.RunId, owner.EnvironmentId,
            runBoundRead: true, (projection, _) => Task.FromResult(projection), timeout.Token, includeRuntimeReadiness: true);
        await Task.WhenAny(observed.Task, read).WaitAsync(timeout.Token);
        if (!observed.Task.IsCompleted)
        {
            await read;
            throw new InvalidOperationException("Readiness completed without observing its exact Sandbox resource.");
        }
        var application = "readiness-retirement-" + Guid.NewGuid().ToString("N");
        await using var retirementDataSource = fixture.CreateDataSource(application);
        var retirement = new EnvironmentSandboxLeaseStore(retirementDataSource, TimeProvider.System).BeginRetirementAsync(
            lifecycle.Fence, lease.ResourceGeneration, lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon, "readiness-retirement",
            new("https://module-identity.test", "module-actor", 1), null, timeout.Token);
        var exerciseError = await Record.ExceptionAsync(async () =>
        {
            await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks locks JOIN pg_stat_activity activity ON activity.pid = locks.pid " +
                "WHERE activity.application_name = @application AND locks.locktype = 'advisory' AND NOT locks.granted",
                observer);
            command.Parameters.AddWithValue("application", application);
            while (Convert.ToInt64(await command.ExecuteScalarAsync(timeout.Token)) == 0)
            {
                if (retirement.IsCompleted)
                    throw new InvalidOperationException(
                        "Retirement did not wait for the retained readiness lease.", await Record.ExceptionAsync(() => retirement));
                await using var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", observer);
                await clear.ExecuteNonQueryAsync(timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            var projection = await read;
            Assert.NotNull(projection?.RuntimeReadiness);
            Assert.Equal(lease.LeaseRevision, projection.LeaseRevision);
            Assert.Equal(resource.Resource, projection.Resource);
            Assert.Equal(resource.Resource, projection.RuntimeReadiness.Observation.Resource);
            Assert.Equal(lease.CreatedAt, projection.RuntimeReadiness.LeaseCreatedAt);
            Assert.Equal(policyGeneration, projection.RuntimeReadiness.Observation.VerifiedNetworkGeneration);
            Assert.Equal(provision.MountPath, projection.RuntimeReadiness.WorkspaceMountPath);
            Assert.Equal(sandboxOptions.StartupBudgets.ToContract(), projection.RuntimeReadiness.StartupBudgets);
            Assert.Equal(0, projects.SelectionReads);
            Assert.Equal(1, network.Creates);
            Assert.Equal(2, network.Reads - network.ReadsBeforeVerification);
            Assert.Equal(SandboxLeaseState.Releasing, (await retirement).State);
        });
        release.TrySetResult();
        var cleanupError = await Record.ExceptionAsync(() => Task.WhenAll(read, retirement));
        if (exerciseError is not null && cleanupError is not null)
            throw new AggregateException("Readiness exercise and cleanup failed.", exerciseError, cleanupError);
        if (exerciseError is not null || cleanupError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exerciseError ?? cleanupError!).Throw();
    }

    private async Task<(EnvironmentOwnerIdentity Owner, EnvironmentLifecycleSnapshot Lifecycle,
        EnvironmentSandboxLeaseStore Store)> CreateOwnerAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var owner = new EnvironmentOwnerIdentity("tenant-" + id, "project-" + id, "run-" + id, "environment-" + id);
        var lifecycle = await fixture.CreateStore().TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "register"), default);
        return (owner, lifecycle.Snapshot, new(fixture.DataSource, TimeProvider.System));
    }

    private EnvironmentSandboxManager Reader(
        ControlledAuthority projects,
        ISandboxLeaseStore store)
    {
        var options = new CiliumEgressProviderOptions(
            "agentweaver", new Version(1, 0, 0), 1, "options-1",
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
        var lifecycle = fixture.CreateStore();
        var egress = new EnvironmentEgressManager(
            projects, new CiliumEgressPolicyAdapter(new UnusedNetworkTransport(), options), options, lifecycle);
        var sandboxOptions = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion, "unused-sandbox-options", "agentweaver", "azure-files-csi",
            "runtime-test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "linux/amd64", 1, "kata-test", "kata-test", "100m", "128Mi", 20, 100,
            new(30, 30, 30, 30, 30, 120));
        var client = new KubernetesAgentSandboxClient(_kubernetes);
        return new(projects, lifecycle, store, new AgentSandboxProvider(sandboxOptions, client),
            sandboxOptions, client, options, egress);
    }

    private static async Task RetireAsync(EnvironmentSandboxLeaseStore store, SandboxLeaseSnapshot active)
    {
        var retiring = await store.BeginRetirementAsync(
            active.Fence, active.ResourceGeneration, active.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon, "abandon",
            new("https://module-identity.test", "module-actor", 1), null, default);
        await store.CompleteReleaseAsync(
            retiring.OperationId, active.Fence, retiring.ProviderFencingGeneration,
            new(active.ProvisionedResource!.Resource, "abandon", SandboxReleaseDisposition.Released), default);
    }

    private static CurrentCallerRequest Caller(EnvironmentOwnerIdentity owner) => new("isolated.module.token", owner.TenantId);

    private static SandboxLeaseProvisionIntent Intent() => new(
        "agent-sandbox", "1.0.0", 1, "options-1",
        JsonSerializer.SerializeToElement(new { namespaceName = "agentweaver" }),
        JsonSerializer.SerializeToElement(new { acceptedSelection = "module-only" }),
        JsonSerializer.SerializeToElement(new { workspace = "controlled-external-placement" }));

    private static SandboxProvisionedResource Resource(SandboxLeaseSnapshot lease)
    {
        var reference = SandboxResourceIdentity.CreatePlannedReference(
            lease.ProvisionIntent.ProviderId, lease.ProvisionIntent.OptionsRevision, lease.Fence,
            lease.ResourceGeneration, lease.ProviderFencingGeneration, lease.OperationId);
        return new(reference, new(Guid.NewGuid()), new("controlled-placement"),
            ImmutableHashSet.Create(SandboxCapabilities.VmIsolation), [],
            new(lease.ProvisionIntent.ProviderId, lease.ProvisionIntent.AdapterVersion,
                lease.ProvisionIntent.OptionsSchemaVersion, lease.ProvisionIntent.OptionsRevision,
                lease.ProvisionIntent.OptionsSnapshot, JsonSerializer.SerializeToElement(new { claim = reference.ResourceId })));
    }

    private sealed class UnusedPlacementTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A placement read must not dispatch a provider effect.");
    }

    private sealed class WorkspaceOwnerTransport(RuntimeOwnerContext owner) : HttpMessageHandler
    {
        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri($"https://orchestrator.test/internal/projects/{owner.ProjectId}/runs/{owner.RunId}" +
                $"/coordination/sessions/{owner.SessionId}/runtime-owner-context"), request.RequestUri);
            Assert.Equal("isolated.module.token", request.Headers.Authorization?.Parameter);
            Reads++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = System.Net.Http.Json.JsonContent.Create(owner)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class ControlledAuthority(EnvironmentOwnerIdentity owner) : IProjectsConfigClient
    {
        private int _reads;
        public bool CanWrite { get; set; } = true;
        public bool CanReadSelection { get; init; }
        public Func<Task>? BeforeSecondRead { get; set; }
        public int SelectionReads { get; private set; }

        public async Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller, CancellationToken cancellationToken)
        {
            if (++_reads == 2 && BeforeSecondRead is not null)
                await BeforeSecondRead();
            return new(1, "https://module-identity.test", "module-actor", owner.TenantId, 1,
                owner.ProjectId, owner.RunId,
                CanWrite ? [new(ProjectAuthorityResourceType.Project, owner.ProjectId,
                    [new(CanReadSelection ? ProjectAuthorizationPermission.ReadRunSelection :
                        ProjectAuthorizationPermission.WriteProjects, 1)])] : []);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller, string projectId, string runId, CancellationToken cancellationToken)
        {
            SelectionReads++;
            throw new InvalidOperationException("Placement reads must not recursively request the run selection.");
        }
    }

    private sealed class UnusedNetworkTransport : ICiliumPolicyResourceStore
    {
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token) =>
            throw new InvalidOperationException("Placement reads must not call Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken token) =>
            throw new InvalidOperationException("Placement reads must not call Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string version, CancellationToken token) =>
            throw new InvalidOperationException("Placement reads must not call Kubernetes.");
        public Task DeleteAsync(string ns, string name, string version, CancellationToken token) =>
            throw new InvalidOperationException("Placement reads must not call Kubernetes.");
    }

    private sealed class ReadinessNetworkStore : ICiliumPolicyResourceStore
    {
        private CiliumNetworkPolicyDocument? _policy;
        public int Reads { get; private set; }
        public int Creates { get; private set; }
        public int ReadsBeforeVerification { get; private set; }
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token)
        {
            Reads++;
            return Task.FromResult(_policy);
        }
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken token)
        {
            Creates++;
            _policy = policy with { Metadata = policy.Metadata with { ResourceVersion = "1", Generation = 1 } };
            ReadsBeforeVerification = Reads;
            return Task.FromResult(_policy);
        }
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(CiliumNetworkPolicyDocument policy, string version, CancellationToken token) =>
            throw new InvalidOperationException("Readiness must not replace a network policy.");
        public Task DeleteAsync(string ns, string name, string version, CancellationToken token) =>
            throw new InvalidOperationException("Readiness must not delete a network policy.");
    }

    private sealed class ReadinessSandboxProvider(Func<SandboxDescribeRequest, CancellationToken, Task<SandboxObservation>> describe)
        : ISandboxProvider
    {
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request, CancellationToken token = default) => describe(request, token);
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("Readiness must not provision a Sandbox.");
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("Readiness must not list Sandboxes.");
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("Readiness must not release a Sandbox.");
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(SandboxPartialReleaseRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("Readiness must not partially release a Sandbox.");
    }
}
