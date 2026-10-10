using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class MafCheckpointStorePostgresTests : IAsyncLifetime
{
    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "maf_checkpoint_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private AuthorizedRunSelection _selection = null!;
    private MafCheckpointBinding _binding = null!;
    private PostgresMafCheckpointStore _store = null!;
    private MafExecutionCheckpointStore _executionStore = null!;

    public MafCheckpointStorePostgresTests(CoordinationPostgresFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        using var snapshot = JsonDocument.Parse(
            """{"modelSelection":{"reference":"model:provider/alpha"}}""");
        var selection = new EffectiveRunSelection(
            "project-1", Guid.NewGuid().ToString("D"), 1, 1, 1, "context-v1",
            snapshot.RootElement.Clone());
        _selection = new AuthorizedRunSelection(
            selection,
            new ProjectsAuthorizationContext(
                1,
                _actor.Issuer,
                _actor.Subject,
                "tenant-1",
                1,
                selection.ProjectId,
                selection.RunId,
                ImmutableArray.Create(new ProjectsAuthority(
                    "project",
                    selection.ProjectId,
                    ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
        var identity = new SessionIdentity(selection.ProjectId, selection.RunId, "root");
        var root = await new CoordinationOwnerStore(_fixture.DataSource, _schema)
            .AcceptRootAsync(_actor, _selection, identity.SessionId, CancellationToken.None);
        _binding = new MafCheckpointBinding(
            identity, _actor, root.ExecutionFence, "maf-test-sdk", "model:provider/alpha", null);
        var checkpoints = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null);
        _store = checkpoints.ForRun(_binding);
        _executionStore = new MafExecutionCheckpointStore(checkpoints, _binding);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AppendUsesCallerTransactionAndReplaysOnlyAnIdenticalCheckpoint()
    {
        using var payload = JsonDocument.Parse("""{"state":"ready"}""");
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var staged = await AppendAsync(
                connection, transaction, "checkpoint-1", payload.RootElement);
            Assert.Equal("checkpoint-1", staged.CheckpointId);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await ReadCheckpointCountAsync());

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            _ = await AppendAsync(connection, transaction, "checkpoint-1", payload.RootElement);
            await transaction.CommitAsync();
        }

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var replay = await AppendAsync(connection, transaction, "checkpoint-1", payload.RootElement);
            Assert.Equal("checkpoint-1", replay.CheckpointId);
            await transaction.RollbackAsync();
        }

        using var differentPayload = JsonDocument.Parse("""{"state":"different"}""");
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
                AppendAsync(connection, transaction, "checkpoint-1", differentPayload.RootElement));
            Assert.Equal("checkpoint_idempotency_conflict", conflict.Code);
            await transaction.RollbackAsync();
        }

        Assert.Equal(1, await ReadCheckpointCountAsync());
    }

    [Fact]
    public async Task AppendRejectsAStaleExecutionFence()
    {
        using var payload = JsonDocument.Parse("""{"state":"ready"}""");
        var staleBinding = _binding with { ExecutionFence = _binding.ExecutionFence + 1 };
        var staleStore = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null).ForRun(staleBinding);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            staleStore.AppendInTransactionAsync(
                connection,
                transaction,
                staleBinding,
                staleBinding.Identity.SessionId,
                "stale-checkpoint",
                payload.RootElement,
                null,
                CancellationToken.None));
        Assert.Equal("checkpoint_execution_fence_stale", conflict.Code);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ExecutionProgressIsAppendOnlyBoundedAndRecoverable()
    {
        var initial = new MafExecutionCheckpoint(
            1, "plan-1", 1, MafExecutionProgress.Empty);
        var first = await _executionStore.AppendAsync(
            "execution-1", null, initial, CancellationToken.None);
        Assert.Equal(initial, first.State);
        Assert.Equal(first.Info, (await _executionStore.AppendAsync(
            "execution-1", null, initial, CancellationToken.None)).Info);

        var changedPayload = initial with { WorkPlanId = "different-plan" };
        var payloadConflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync(
                "execution-1", null, changedPayload, CancellationToken.None));
        Assert.Equal("checkpoint_idempotency_conflict", payloadConflict.Code);
        var changedParent = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync(
                "execution-1", first.Info, initial, CancellationToken.None));
        Assert.Equal("checkpoint_idempotency_conflict", changedParent.Code);

        var running = initial with
        {
            Revision = 2,
            Progress = initial.Progress with
            {
                WorkItems = initial.Progress.WorkItems.Add("implementation", MafExecutionTaskStatus.Running)
            }
        };
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var staged = await _executionStore.AppendInTransactionAsync(
                connection, transaction, "execution-2", first.Info, running, CancellationToken.None);
            Assert.Equal(running, staged.State);
            await transaction.RollbackAsync();
        }

        var afterRollback = await _executionStore.ReadLatestAsync(CancellationToken.None);
        AssertCheckpointEqual(first, afterRollback);

        var second = await _executionStore.AppendAsync(
            "execution-2", first.Info, running, CancellationToken.None);
        Assert.Equal(running, second.State);
        var recovered = await new MafExecutionCheckpointStore(
                new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding)
            .ReadLatestAsync(CancellationToken.None);
        AssertCheckpointEqual(second, recovered);
        Assert.Equal(second.Info, (await _executionStore.AppendAsync(
            "execution-2", first.Info, running, CancellationToken.None)).Info);

        var historicalReplay = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync("execution-1", null, initial, CancellationToken.None));
        Assert.Equal("maf_execution_checkpoint_stale", historicalReplay.Code);
    }

    [Fact]
    public async Task ExecutionWitnessAppendUsesProductionCheckpointStoreBinding()
    {
        var identity = _binding.Identity;
        var fence = _binding.ExecutionFence;
        const long decisionStateVersion = 1;
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var checkpointBinding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
            identity, _actor, fence, _binding.SdkVersion, _binding.PinnedModelReference);
        var checkpoints = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null);
        var execution = new MafExecutionCheckpointStore(checkpoints, checkpointBinding);
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("optional", 0, [], minimum: 0));
        var planResult = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(),
            WorkflowTestData.SelectionContext());
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        var plan = planResult.Value!;
        var checkpoint = await execution.AppendAsync(
            "execution-witness",
            null,
            new MafExecutionCheckpoint(
                1, plan.Plan.Id, decisionStateVersion, MafExecutionProgress.Empty),
            CancellationToken.None);
        var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(
            plan, checkpoint, identity).OutputSet;
        var currentDecision = new CoordinatorDecisionCurrentState(
            CoordinatorDecisionState.Create(fence), decisionStateVersion, selectionHash);
        var root = new MafExecutionOwnerEvidenceBinding(
            _actor,
            identity,
            selectionHash,
            fence,
            currentDecision,
            checkpoint,
            plan,
            outputSet);
        var ownerEvidence = MafExecutionOwnerEvidenceContract.Serialize(
            root,
            _selection.Authorization.TenantId,
            ImmutableArray<SourceControlOutputCaptureRecord>.Empty,
            ImmutableArray<SourceControlMergeIntentSnapshot>.Empty);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var saved = await new MafExecutionOutputWitnessStore(_schema)
            .AppendOutputWitnessInTransactionAsync(
                connection,
                transaction,
                checkpoints,
                checkpointBinding,
                identity,
                fence,
                plan,
                checkpoint,
                decisionStateVersion,
                selectionHash,
                outputSet,
                ownerEvidence,
                CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(MafExecutionCheckpointContract.StoreName, checkpointBinding.StoreName);
        Assert.Equal(checkpoint.Info.CheckpointId, saved.CheckpointId);
        Assert.Equal(checkpoint.State.Revision, saved.CheckpointRevision);
        Assert.Equal(outputSet.Digest, saved.OutputSetSha256);
        Assert.Equal("no-output", saved.OwnerEvidenceJson[0].GetProperty("kind").GetString());
        Assert.Equal(64, saved.ProofSha256.Length);

        var currentCheckpoint = await execution.AppendAsync(
            "execution-witness-current",
            checkpoint.Info,
            checkpoint.State with { Revision = 2 },
            CancellationToken.None);
        await using (var staleConnection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var staleTransaction = await staleConnection.BeginTransactionAsync())
        {
            var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
                new MafExecutionOutputWitnessStore(_schema).AppendOutputWitnessInTransactionAsync(
                    staleConnection,
                    staleTransaction,
                    checkpoints,
                    checkpointBinding,
                    identity,
                    fence,
                    plan,
                    checkpoint,
                    decisionStateVersion,
                    selectionHash,
                    outputSet,
                    ownerEvidence,
                    CancellationToken.None));
            Assert.Equal("maf_execution_output_witness_stale", stale.Code);
            Assert.Equal(409, stale.StatusCode);
            await staleTransaction.RollbackAsync();
        }

        Assert.Equal(2, currentCheckpoint.State.Revision);
        AssertCheckpointEqual(currentCheckpoint, await execution.ReadLatestAsync(CancellationToken.None));
        await using var witnessCount = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".maf_execution_output_witnesses", connection);
        Assert.Equal(1L, Assert.IsType<long>(await witnessCount.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task FixedWorkAssociationAndStatusSurviveFreshStoreRecovery()
    {
        var association = new MafExecutionFixedWorkAssociation(
            "fixed-association",
            1,
            "plan-1",
            "workflow-1",
            "revision-1",
            "catalog-1",
            "fixed-step",
            new FixedWorkSpecification(
                "Implement feature", "Implement the feature.", "role-dev", "implementation", "workspace",
                ImmutableArray.Create("src/feature.cs")),
            "agent-dev",
            "model:provider/alpha",
            "isolation-provider",
            CoordinationOwnerStore.HashSelection(_selection.Selection),
            _binding.ExecutionFence,
            1,
            "maf-fixed-child");
        var state = new MafExecutionCheckpoint(
            1,
            "plan-1",
            1,
            MafExecutionProgress.Empty with
            {
                FixedWorkItems = MafExecutionProgress.Empty.FixedWorkItems.Add(
                    association.AssociationId, MafExecutionTaskStatus.Running)
            })
        {
            FixedWorkAssociations = ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(association.AssociationId, association)
        };
        _ = await _executionStore.AppendAsync("execution-fixed", null, state, CancellationToken.None);

        var recoveredStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding);
        var recovered = await recoveredStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(
            MafExecutionTaskStatus.Running,
            recovered.State.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId));
        var recoveredAssociation = await recoveredStore.ReadFixedWorkAssociationAsync(
            association.ChildSessionId, "plan-1", CancellationToken.None);
        Assert.NotNull(recoveredAssociation);
        Assert.Equal(association.AssociationId, recoveredAssociation.AssociationId);
        Assert.Equal(association.AgentId, recoveredAssociation.AgentId);
        Assert.Equal(association.ModelSelectionReference, recoveredAssociation.ModelSelectionReference);
        Assert.Equal(association.Specification.DeclaredOutputs.ToArray(),
            recoveredAssociation.Specification.DeclaredOutputs.ToArray());
    }

    [Fact]
    public async Task ExecutionProgressRejectsInvalidFixedWorkStatus()
    {
        var invalid = new MafExecutionCheckpoint(
            1,
            "plan-1",
            1,
            MafExecutionProgress.Empty with
            {
                FixedWorkItems = MafExecutionProgress.Empty.FixedWorkItems.Add(
                    "fixed-association", (MafExecutionTaskStatus)99)
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _executionStore.AppendAsync("execution-invalid-fixed-status", null, invalid, CancellationToken.None));
    }

    [Fact]
    public async Task ExecutionProgressRejectsStaleParentAndInvalidRevision()
    {
        var initial = await _executionStore.AppendAsync(
            "execution-1",
            null,
            new MafExecutionCheckpoint(1, "plan-1", 1, MafExecutionProgress.Empty),
            CancellationToken.None);
        var staleParent = new CheckpointInfo(_binding.Identity.SessionId, "another-checkpoint");
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync(
                "execution-2", staleParent,
                new MafExecutionCheckpoint(2, "plan-1", 1, MafExecutionProgress.Empty),
                CancellationToken.None));
        Assert.Equal("maf_execution_checkpoint_stale", stale.Code);

        var invalidRevision = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync(
                "execution-2", initial.Info,
                new MafExecutionCheckpoint(3, "plan-1", 1, MafExecutionProgress.Empty),
                CancellationToken.None));
        Assert.Equal("maf_execution_checkpoint_revision_invalid", invalidRevision.Code);
        AssertCheckpointEqual(initial, await _executionStore.ReadLatestAsync(CancellationToken.None));
    }

    private Task<CheckpointInfo> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string checkpointId,
        JsonElement payload) =>
        _store.AppendInTransactionAsync(
            connection,
            transaction,
            _binding,
            _binding.Identity.SessionId,
            checkpointId,
            payload,
            null,
            CancellationToken.None);

    private static void AssertCheckpointEqual(
        MafExecutionCheckpointSnapshot expected,
        MafExecutionCheckpointSnapshot? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Info.SessionId, actual.Info.SessionId);
        Assert.Equal(expected.Info.CheckpointId, actual.Info.CheckpointId);
        Assert.Equal(expected.State.Revision, actual.State.Revision);
        Assert.Equal(expected.State.WorkPlanId, actual.State.WorkPlanId);
        Assert.Equal(expected.State.DecisionStateVersion, actual.State.DecisionStateVersion);
        Assert.Equal(expected.State.Progress.WorkItems.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            actual.State.Progress.WorkItems.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.State.Progress.NonModelSteps.OrderBy(step => step.Key, StringComparer.Ordinal).ToArray(),
            actual.State.Progress.NonModelSteps.OrderBy(step => step.Key, StringComparer.Ordinal).ToArray());
    }

    private async Task<long> ReadCheckpointCountAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".maf_workflow_checkpoints", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
