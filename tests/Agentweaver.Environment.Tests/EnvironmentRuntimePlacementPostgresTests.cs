using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

// Current authority is isolated here; the combined Broker/Core runtime proof is separate.
public sealed class EnvironmentRuntimePlacementPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ExactLeaseIsDurableAcrossStoreRestartAndDoesNotRenewOnReplay()
    {
        var (owner, lifecycle, store) = await CreateOwnerAsync();
        var first = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        var replay = await store.ReserveProvisionAsync(lifecycle.Fence, "provision", Intent(), default);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Lease.OperationId, replay.Lease.OperationId);
        Assert.Equal(first.Lease.LeaseExpiresAt, replay.Lease.LeaseExpiresAt);
        var resource = Resource(first.Lease);
        var completed = await store.CompleteProvisionAsync(
            first.Lease.OperationId, lifecycle.Fence, resource, true, default);
        Assert.Equal(2, completed.LeaseRevision);
        Assert.Equal(SandboxLeaseState.Active, completed.State);
        var restarted = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var current = await restarted.GetCurrentAsync(lifecycle.Fence, default);
        Assert.Equal(completed.LeaseRevision, current!.LeaseRevision);
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
        await store.CompleteProvisionAsync(lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default);
        var foreign = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetCurrentAsync(new EnvironmentGenerationFence(
                new(owner.TenantId, owner.ProjectId, "foreign", owner.EnvironmentId), 1), default));
        Assert.Equal("environment_unknown", foreign.Code);
        var lifecycleStore = fixture.CreateStore();
        var advanced = await lifecycleStore.TransitionAsync(
            new(owner, lifecycle.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"), default);
        var stale = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetCurrentAsync(lifecycle.Fence, default));
        Assert.Equal("environment_fence_stale", stale.Code);
        var projects = new ControlledAuthority(owner);
        var stalePlacement = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Reader(projects, store).GetCurrentPlacementAsync(
                Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
        Assert.Equal("sandbox_lease_stale", stalePlacement.Code);
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
        await store.CompleteProvisionAsync(lease.Lease.OperationId, lifecycle.Fence, Resource(lease.Lease), true, default);
        var projects = new ControlledAuthority(owner)
        {
            BeforeSecondRead = async () =>
                await fixture.CreateStore().TransitionAsync(
                    new(owner, lifecycle.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"),
                    default)
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
        var expired = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Reader(new ControlledAuthority(owner), store, new FixedTimeProvider(active.LeaseExpiresAt!.Value))
                .GetCurrentPlacementAsync(Caller(owner), owner.ProjectId, owner.RunId, owner.EnvironmentId, default));
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
        using var app = builder.Build();
        app.MapEnvironmentEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText!.EndsWith("/sandbox/v1/placement", StringComparison.Ordinal));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
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

    private EnvironmentRuntimePlacementReader Reader(
        ControlledAuthority projects,
        ISandboxLeaseStore store,
        TimeProvider? time = null)
    {
        var options = new CiliumEgressProviderOptions(
            "agentweaver", new Version(1, 0, 0), 1, "options-1",
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
        var lifecycle = fixture.CreateStore();
        var egress = new EnvironmentEgressManager(
            projects, new CiliumEgressPolicyAdapter(new UnusedNetworkTransport(), options), options, lifecycle);
        return new(lifecycle, store, egress, time ?? TimeProvider.System);
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlledAuthority(EnvironmentOwnerIdentity owner) : IProjectsConfigClient
    {
        private int _reads;
        public bool CanWrite { get; set; } = true;
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
                    [new(ProjectAuthorizationPermission.WriteProjects, 1)])] : []);
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
}
