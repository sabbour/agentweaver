using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentLifecyclePostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task LifecycleCompareAndSwapAllowsOnlyOneConcurrentTransition()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);
        var requests = new[]
        {
            Transition(owner, registered.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance-a"),
            Transition(owner, registered.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance-b"),
        };

        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                return (Result: await store.TransitionAsync(request, CancellationToken.None), Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Result: (EnvironmentLifecycleTransitionResult?)null, Error: exception);
            }
        }));

        Assert.Single(outcomes, outcome => outcome.Result is not null);
        var conflict = Assert.IsType<EnvironmentLifecycleException>(
            Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
        Assert.Equal("environment_generation_conflict", conflict.Code);
        var current = await store.GetAsync(owner, CancellationToken.None);
        Assert.Equal(registered.Snapshot.Fence.LifecycleGeneration + 1, current!.Fence.LifecycleGeneration);
    }

    [Fact]
    public async Task LifecycleIdempotencyReplaysOnlyTheExactOwnerTransition()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var producer = new EnvironmentLifecycleProducer(store);

        var first = await producer.RegisterAsync(owner, "register", CancellationToken.None);
        var replay = await producer.RegisterAsync(owner, "register", CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Snapshot, replay.Snapshot);
        Assert.Equal(first.Snapshot, await store.GetAsync(owner, CancellationToken.None));
        var conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            producer.TransitionAsync(
                first.Snapshot.Fence,
                EnvironmentLifecycleState.Active,
                "register",
                CancellationToken.None));
        Assert.Equal("environment_idempotency_conflict", conflict.Code);
    }

    [Fact]
    public async Task RequireActiveRejectsUnknownForeignStaleAndReleasedFences()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);

        var unknown = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.RequireActiveAsync(
                new EnvironmentGenerationFence(NewOwner(), registered.Snapshot.Fence.LifecycleGeneration),
                CancellationToken.None));
        Assert.Equal("environment_unknown", unknown.Code);

        var advanced = await store.TransitionAsync(
            Transition(owner, registered.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"),
            CancellationToken.None);
        var stale = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.RequireActiveAsync(registered.Snapshot.Fence, CancellationToken.None));
        Assert.Equal("environment_fence_stale", stale.Code);

        var released = await store.TransitionAsync(
            Transition(owner, advanced.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Released, "release"),
            CancellationToken.None);
        var denied = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.RequireActiveAsync(released.Snapshot.Fence, CancellationToken.None));
        Assert.Equal("environment_released", denied.Code);
        var oldFence = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.RequireActiveAsync(registered.Snapshot.Fence, CancellationToken.None));
        Assert.Equal("environment_released", oldFence.Code);
    }

    [Fact]
    public async Task StaleProviderCompletionCannotBecomeCurrentAfterLifecycleAdvance()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);
        var reservation = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            "namespace/policy",
            policyGeneration: 41,
            expectedPreviousPolicyGeneration: 0,
            EnvironmentNetworkEffectKind.Apply,
            "apply-41",
            CancellationToken.None);

        await store.TransitionAsync(
            Transition(owner, registered.Snapshot.Fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"),
            CancellationToken.None);
        var completion = await store.CompleteNetworkEffectAsync(
            reservation.OperationId,
            registered.Snapshot.Fence,
            effectMayHaveApplied: true,
            exactGenerationVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentNetworkEffectState.ReconciliationRequired, completion.State);
        var currentFence = new EnvironmentGenerationFence(owner, registered.Snapshot.Fence.LifecycleGeneration + 1);
        var blocked = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveNetworkEffectAsync(
                currentFence,
                "namespace/policy",
                policyGeneration: 42,
                expectedPreviousPolicyGeneration: 0,
                EnvironmentNetworkEffectKind.Apply,
                "apply-42",
                CancellationToken.None));
        Assert.Equal("environment_effect_reconciliation_required", blocked.Code);

        var pending = await store.GetNetworkEffectAsync(
            reservation.OperationId,
            currentFence,
            CancellationToken.None);
        Assert.Equal(EnvironmentNetworkEffectState.ReconciliationRequired, pending.State);
        var mismatch = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.MarkNetworkEffectReconciledAsync(
                reservation.OperationId,
                currentFence,
                new EnvironmentNetworkEffectObservation(
                    ObjectVerified: true,
                    AppliedIntentGeneration: 42,
                    Revoked: false,
                    IntentHash: new string('a', 64)),
                CancellationToken.None));
        Assert.Equal("environment_effect_observation_mismatch", mismatch.Code);
        var reconciled = await store.MarkNetworkEffectReconciledAsync(
            reservation.OperationId,
            currentFence,
            new EnvironmentNetworkEffectObservation(
                ObjectVerified: true,
                AppliedIntentGeneration: 41,
                Revoked: false,
                IntentHash: new string('a', 64)),
            CancellationToken.None);
        Assert.Equal(EnvironmentNetworkEffectState.Reconciled, reconciled.State);

        var next = await store.ReserveNetworkEffectAsync(
            currentFence,
            "namespace/policy",
            policyGeneration: 42,
            expectedPreviousPolicyGeneration: 41,
            EnvironmentNetworkEffectKind.Apply,
            "apply-42",
            CancellationToken.None);
        Assert.Equal(EnvironmentNetworkEffectState.Reserved, next.State);
    }

    [Fact]
    public async Task PolicyGenerationAdvancesIndependentlyOfLifecycleGeneration()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);
        var first = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            "namespace/policy",
            policyGeneration: 41,
            expectedPreviousPolicyGeneration: 0,
            EnvironmentNetworkEffectKind.Apply,
            "apply-41",
            CancellationToken.None);
        var firstCompletion = await store.CompleteNetworkEffectAsync(
            first.OperationId,
            registered.Snapshot.Fence,
            effectMayHaveApplied: true,
            exactGenerationVerified: true,
            CancellationToken.None);
        Assert.Equal(EnvironmentNetworkEffectState.Completed, firstCompletion.State);

        var second = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            "namespace/policy",
            policyGeneration: 42,
            expectedPreviousPolicyGeneration: 41,
            EnvironmentNetworkEffectKind.Apply,
            "apply-42",
            CancellationToken.None);

        Assert.Equal(registered.Snapshot.Fence.LifecycleGeneration, second.Fence.LifecycleGeneration);
        Assert.Equal(42, second.PolicyGeneration);
        Assert.Equal(41, second.ExpectedPreviousPolicyGeneration);
    }

    [Fact]
    public async Task FailedBeforeProviderCallDoesNotBlockEnvironmentRelease()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);
        var reservation = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            "namespace/policy",
            policyGeneration: 41,
            expectedPreviousPolicyGeneration: 0,
            EnvironmentNetworkEffectKind.Apply,
            "apply-before-provider",
            CancellationToken.None);

        var failed = await store.CompleteNetworkEffectAsync(
            reservation.OperationId,
            registered.Snapshot.Fence,
            effectMayHaveApplied: false,
            exactGenerationVerified: false,
            CancellationToken.None);
        Assert.Equal(EnvironmentNetworkEffectState.Failed, failed.State);

        var released = await store.TransitionAsync(
            Transition(
                owner,
                registered.Snapshot.Fence.LifecycleGeneration,
                EnvironmentLifecycleState.Released,
                "release-after-failed-policy"),
            CancellationToken.None);

        Assert.Equal(EnvironmentLifecycleState.Released, released.Snapshot.State);
    }

    [Fact]
    public async Task VerifiedPolicyGenerationRequiresAResolvedOwnerEffect()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var registered = await RegisterAsync(store, owner);
        const string resourceId = "namespace/policy";
        var applied = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            resourceId,
            policyGeneration: 41,
            expectedPreviousPolicyGeneration: 0,
            EnvironmentNetworkEffectKind.Apply,
            "apply-41",
            CancellationToken.None);
        _ = await store.CompleteNetworkEffectAsync(
            applied.OperationId,
            registered.Snapshot.Fence,
            effectMayHaveApplied: true,
            exactGenerationVerified: true,
            CancellationToken.None);
        await store.RequireVerifiedNetworkPolicyGenerationAsync(
            registered.Snapshot.Fence,
            resourceId,
            41,
            CancellationToken.None);

        var unresolved = await store.ReserveNetworkEffectAsync(
            registered.Snapshot.Fence,
            resourceId,
            policyGeneration: 47,
            expectedPreviousPolicyGeneration: 41,
            EnvironmentNetworkEffectKind.Apply,
            "apply-47",
            CancellationToken.None);
        var blocked = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.RequireVerifiedNetworkPolicyGenerationAsync(
                registered.Snapshot.Fence,
                resourceId,
                41,
                CancellationToken.None));
        Assert.Equal("environment_effect_reconciliation_required", blocked.Code);

        _ = await store.CompleteNetworkEffectAsync(
            unresolved.OperationId,
            registered.Snapshot.Fence,
            effectMayHaveApplied: false,
            exactGenerationVerified: false,
            CancellationToken.None);
        await store.RequireVerifiedNetworkPolicyGenerationAsync(
            registered.Snapshot.Fence,
            resourceId,
            41,
            CancellationToken.None);
    }

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
    }

    private static Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentLifecycleStore store,
        EnvironmentOwnerIdentity owner) =>
        store.TransitionAsync(
            Transition(owner, 0, EnvironmentLifecycleState.Active, "register"),
            CancellationToken.None);

    private static EnvironmentLifecycleTransitionRequest Transition(
        EnvironmentOwnerIdentity owner,
        long expectedGeneration,
        EnvironmentLifecycleState state,
        string idempotencyKey) =>
        new(owner, expectedGeneration, state, idempotencyKey);
}

public sealed class EnvironmentPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema))
            .Options;
        await EnvironmentMigrator.MigrateAsync(DataSource, options);
    }

    public EnvironmentLifecycleStore CreateStore() => new(DataSource, TimeProvider.System);

    public NpgsqlDataSource CreateDataSource(string applicationName) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { ApplicationName = applicationName }.ConnectionString);

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
