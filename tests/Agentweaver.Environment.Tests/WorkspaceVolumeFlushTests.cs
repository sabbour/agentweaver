using System.Reflection;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment.Migrations;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Agentweaver.Environment.Tests;

public sealed class WorkspaceVolumeFlushTests
{
    [Fact]
    public async Task VerifiedFlushCommitsOnceAndReplayReturnsSavedEvidenceWithoutRepeatingTheProducer()
    {
        var (service, store, provider) = Create();
        var produced = 0;
        Task<WorkspaceVolumeFlushEvidence> Produce(WorkspaceVolumeFlushRequest request, CancellationToken token)
        {
            produced++;
            return Task.FromResult(Evidence(request));
        }

        var first = await service.FlushAsync(store.Fence, Request(), Produce);
        var replay = await service.FlushAsync(store.Fence, Request(), Produce);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, first.Completion!.TransitionState);
        Assert.Equal(8, first.FlushReceipt!.DataGeneration);
        Assert.Equal(5, store.Snapshot.TransitionRevision);
        Assert.Equal(8, store.Snapshot.DataGeneration);
        Assert.True(replay.Reservation.Replayed);
        Assert.Null(replay.Completion);
        Assert.Equal(first.FlushReceipt, replay.FlushReceipt);
        Assert.Equal(1, produced);
        Assert.Equal(1, provider.Calls);
        Assert.False(store.LastCompletionToken.CanBeCanceled);
    }

    [Fact]
    public async Task UncertainProviderFailureSavesEvidenceWithoutAdvancingAndExactRetryCanComplete()
    {
        var (service, store, provider) = Create();
        provider.AfterProducer = _ => throw new InvalidOperationException("provider reply lost");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FlushAsync(store.Fence, Request(), Produce));
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, store.State);
        Assert.NotNull(store.SavedEvidence);
        Assert.Equal(7, store.Snapshot.DataGeneration);
        provider.AfterProducer = null;

        var completed = await service.FlushAsync(store.Fence, Request(), Produce);
        Assert.True(completed.Reservation.Replayed);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, completed.Completion!.TransitionState);
        Assert.Equal(8, store.Snapshot.DataGeneration);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task InvalidProducerEvidenceLeavesReconciliationWithoutSavingInvalidGenerations()
    {
        var (service, store, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.FlushAsync(
            store.Fence, Request(), (request, _) => Task.FromResult(Evidence(request) with
            {
                Receipt = Evidence(request).Receipt with
                {
                    ExpectedDataGeneration = 8, DataGeneration = 9
                }
            })));
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, store.State);
        Assert.Null(store.SavedEvidence);
        Assert.Equal(7, store.Snapshot.DataGeneration);
    }

    [Fact]
    public async Task AuthorityLossBeforeProviderStartsRecordsNoEffectAndDoesNotLeaveAPendingReservation()
    {
        var (service, store, provider) = Create();
        var checks = 0;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => service.FlushAsync(
            store.Fence, Request(), Produce, authorizationAndFenceCheck: _ =>
            {
                if (++checks == 2)
                    throw new RuntimeAuthorizationException("actor_revoked");
                return Task.CompletedTask;
            }));
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Failed, store.State);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(7, store.Snapshot.DataGeneration);
    }

    [Fact]
    public async Task AuthorityLossDuringOwnerCommitRetainsEvidenceForReconciliationWithoutAdvancing()
    {
        var (service, store, _) = Create();
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => service.FlushAsync(
            store.Fence, Request(), Produce, beforeCommitCheck: _ =>
                throw new RuntimeAuthorizationException("selection_changed")));
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, store.State);
        Assert.NotNull(store.SavedEvidence);
        Assert.Equal(7, store.Snapshot.DataGeneration);
    }

    [Fact]
    public async Task ChangedProviderRequestCannotReachTheTrustedProducer()
    {
        var (service, store, provider) = Create();
        provider.ChangeRequest = request => request with { IdempotencyKey = "other-operation" };
        var produced = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FlushAsync(
            store.Fence, Request(), (request, token) =>
            {
                produced = true;
                return Produce(request, token);
            }));
        Assert.False(produced);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, store.State);
        Assert.Equal(7, store.Snapshot.DataGeneration);
    }

    [Fact]
    public async Task DeterministicProviderRejectionRecordsFailedWithoutCallingTheProducer()
    {
        var (service, store, provider) = Create();
        provider.BeforeProducer = () => throw new AzureFilesCsiException(
            "claim_missing", "The workspace claim is missing.", effectMayHaveApplied: false);
        await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            service.FlushAsync(store.Fence, Request(), Produce));
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Failed, store.State);
        Assert.Equal(7, store.Snapshot.DataGeneration);
    }

    [Fact]
    public void FlushMigrationAddsOnlyNullableEvidenceAndTheSnapshotMatchesTheModel()
    {
        var column = Assert.IsType<AddColumnOperation>(Assert.Single(new WorkspaceFlushEvidence().UpOperations));
        Assert.Equal("environment", column.Schema);
        Assert.Equal("owner_effects", column.Table);
        Assert.Equal("flush_evidence_json", column.Name);
        Assert.Equal("jsonb", column.ColumnType);
        Assert.True(column.IsNullable);
        using var context = new EnvironmentDbContext(new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused;Username=unused;Password=unused").Options);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private static WorkspaceVolumeTransitionRequest Request() =>
        new("workspace", WorkspaceVolumeTransitionKind.Flush, 4, 1, 7, 8, "flush-once");

    private static Task<WorkspaceVolumeFlushEvidence> Produce(
        WorkspaceVolumeFlushRequest request, CancellationToken token) =>
        Task.FromResult(Evidence(request));

    private static WorkspaceVolumeFlushEvidence Evidence(WorkspaceVolumeFlushRequest request) =>
        new(new(1, request.EnvironmentFence, new(request.Spec.ProjectId, request.Spec.VolumeId, 1),
            request.Resource, request.ExpectedDataGeneration, request.ExpectedDataGeneration + 1,
            request.IdempotencyKey, new string('a', 64), null), null);

    private static (WorkspaceVolumeService Service, FlushStore Store, FlushProvider Provider) Create()
    {
        var storeInterface = DispatchProxy.Create<IEnvironmentLifecycleStore, FlushStore>();
        var store = (FlushStore)storeInterface;
        var provider = new FlushProvider();
        return (new(storeInterface, provider), store, provider);
    }

    public sealed class FlushProvider : IWorkspaceVolumeProvider
    {
        public int Calls { get; private set; }
        public Action? BeforeProducer { get; set; }
        public Action<WorkspaceVolumeFlushReceipt>? AfterProducer { get; set; }
        public Func<WorkspaceVolumeFlushRequest, WorkspaceVolumeFlushRequest>? ChangeRequest { get; set; }

        public Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
            WorkspaceVolumeReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<WorkspaceVolumeFlushReceipt> FlushAsync(
            WorkspaceVolumeFlushRequest request,
            Func<WorkspaceVolumeFlushRequest, CancellationToken, Task<WorkspaceVolumeFlushReceipt>> producer,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeProducer?.Invoke();
            var receipt = await producer(ChangeRequest?.Invoke(request) ?? request, cancellationToken);
            AfterProducer?.Invoke(receipt);
            return receipt;
        }
    }

    public class FlushStore : DispatchProxy
    {
        public EnvironmentGenerationFence Fence { get; } = new(new("tenant", "project", "run", "environment"), 1);
        public EnvironmentWorkspaceVolumeSnapshot Snapshot { get; private set; } = null!;
        public EnvironmentWorkspaceVolumeTransitionState State { get; private set; } =
            EnvironmentWorkspaceVolumeTransitionState.Reserved;
        public WorkspaceVolumeFlushEvidence? SavedEvidence { get; private set; }
        public CancellationToken LastCompletionToken { get; private set; }
        private readonly Guid _operationId = Guid.NewGuid();
        private bool _reserved;

        public FlushStore()
        {
            var spec = new WorkspaceVolumeSpec("workspace", "project", new(WorkspaceVolumeOwnerKind.Run, "run"),
                "environment", WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany,
                8, "azure-files", WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Retain,
                WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
            var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "claim-1", 1);
            var binding = new WorkspaceVolumeProviderBindingSnapshot(
                "azure-files", "1.0.0", 1, "options-1",
                JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));
            Snapshot = new(Fence, "workspace", 4, 1, 7, EnvironmentWorkspaceVolumeState.Attached,
                EnvironmentWorkspaceVolumeOperation.Attach, JsonSerializer.SerializeToElement(spec), resource, binding);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            if (targetMethod.Name == nameof(IEnvironmentLifecycleStore.GetWorkspaceVolumeAsync))
                return Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(Snapshot);
            if (targetMethod.Name == nameof(IEnvironmentLifecycleStore.ReserveWorkspaceVolumeFlushAsync))
            {
                var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(
                    _operationId, Fence, "workspace", 4, 1, 7, Snapshot.Resource, Snapshot.ProviderBinding,
                    5, 1, 8, EnvironmentWorkspaceVolumeState.Attached, EnvironmentWorkspaceVolumeOperation.Flush,
                    State, _reserved) { FlushEvidence = SavedEvidence };
                _reserved = true;
                return Task.FromResult(reservation);
            }
            if (targetMethod.Name == nameof(IEnvironmentLifecycleStore.CompleteWorkspaceVolumeFlushAsync))
                return CompleteAsync(args, targetMethod.GetParameters()[3].ParameterType ==
                    typeof(WorkspaceVolumeFlushEvidence));
            throw new NotSupportedException(targetMethod.Name);
        }

        private async Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteAsync(object?[] args, bool typed)
        {
            var mayHaveApplied = (bool)args[2]!;
            var verified = (bool)args[4]!;
            LastCompletionToken = (CancellationToken)args[typed ? 5 : 6]!;
            if (typed && args[6] is Func<CancellationToken, Task> check)
                await check(LastCompletionToken);
            SavedEvidence = typed ? (WorkspaceVolumeFlushEvidence?)args[3] ?? SavedEvidence : SavedEvidence;
            State = mayHaveApplied && verified ? EnvironmentWorkspaceVolumeTransitionState.Completed :
                mayHaveApplied ? EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired :
                EnvironmentWorkspaceVolumeTransitionState.Failed;
            if (State == EnvironmentWorkspaceVolumeTransitionState.Completed)
                Snapshot = new(Fence, "workspace", 5, 1, 8, EnvironmentWorkspaceVolumeState.Attached,
                    EnvironmentWorkspaceVolumeOperation.Flush, Snapshot.Specification,
                    Snapshot.Resource, Snapshot.ProviderBinding);
            return new(_operationId, Fence, "workspace", 4, 1, 7, Snapshot.Resource,
                5, 1, 8, Snapshot.Resource, EnvironmentWorkspaceVolumeState.Attached,
                EnvironmentWorkspaceVolumeOperation.Flush, State, false) { FlushEvidence = SavedEvidence };
        }
    }
}
