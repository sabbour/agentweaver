using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class BacklogPlanPromotionPostgresTests : IAsyncLifetime
{
    private const string ProjectId = "backlog-project";
    private const string RunId = "run-1";
    private const string RootSessionId = "root";
    private static readonly JsonSerializerOptions DecisionJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "promotion_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private BacklogOwnerStore _backlog = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;
    private AuthorizedRunSelection _selection = null!;
    private long _fence;

    public BacklogPlanPromotionPostgresTests(CoordinationPostgresFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(_fixture.DataSource, _schema);

        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var resolver = new ProviderResolver(catalog);
        var contexts = new CoordinatorRunSelectionContextStore(
            _fixture.DataSource, _schema, catalog, resolver, []);
        _decisions = new CoordinatorDecisionOwnerStore(
            _fixture.DataSource, _schema, contexts, TimeProvider.System);
        var coordination = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, _decisions);
        _backlog = new BacklogOwnerStore(_fixture.DataSource, _schema);
        _selection = Selection();
        var root = await coordination.AcceptRootAsync(
            _actor, _selection, RootSessionId, CancellationToken.None);
        _fence = root.ExecutionFence;
        var initialized = await _decisions.InitializeRootAsync(
            _actor, Identity, _selection, CancellationToken.None);
        Assert.Equal(1, initialized.StateVersion);
        await SeedConfirmedPlanAsync(stateVersion: 2);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static SessionIdentity Identity => new(ProjectId, RunId, RootSessionId);

    [Fact]
    public async Task PromotionCreatesTasksEdgesAndOriginsAndReplaysAfterRestart()
    {
        await RequireDispatchStateAsync();

        var result = await PromoteAsync("promotion-1", expectedGraphRevision: 0);

        Assert.True(result.IsSuccess);
        var promotion = result.Value!;
        Assert.Equal(ProjectId, promotion.ProjectId);
        Assert.Equal(RunId, promotion.RunId);
        Assert.Equal(RootSessionId, promotion.RootSessionId);
        Assert.Equal("plan-1", promotion.WorkPlanId);
        Assert.Equal("workflow.core", promotion.WorkflowId);
        Assert.Equal("revision-1", promotion.WorkflowRevision);
        Assert.Equal(2, promotion.DecisionStateVersion);
        Assert.Equal(_fence, promotion.ExecutionFence);
        Assert.Equal(new[] { "build", "design" }, promotion.Tasks.Select(task => task.WorkItemId));
        var designTaskId = BacklogPlanPromotionIds.CreateTaskId(ProjectId, RunId, "plan-1", "design");
        var buildTaskId = BacklogPlanPromotionIds.CreateTaskId(ProjectId, RunId, "plan-1", "build");
        Assert.Matches("^plan-[0-9a-f]{32}$", designTaskId);
        Assert.Contains(promotion.Tasks, task => task.TaskId == designTaskId && task.WorkItemId == "design");

        var restarted = new BacklogOwnerStore(_fixture.DataSource, _schema);
        var graph = (await restarted.ReadGraphAsync(ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(promotion.GraphRevision, graph.Revision);
        var design = Assert.Single(graph.Tasks, task => task.Reference.TaskId == designTaskId);
        Assert.Equal("Work item design", design.Title);
        Assert.Equal("Implement the requested change.", design.Description);
        Assert.Equal(BacklogTaskLifecycle.Open, design.Lifecycle);
        var edge = Assert.Single(graph.Dependencies);
        Assert.Equal(buildTaskId, edge.Task.TaskId);
        Assert.Equal(designTaskId, edge.Prerequisite.TaskId);

        var origins = await restarted.ReadTaskOriginsAsync(ProjectId, CancellationToken.None);
        Assert.Equal(2, origins.Length);
        Assert.All(origins, origin =>
        {
            Assert.Equal(promotion.PromotionId, origin.PromotionId);
            Assert.Equal(RunId, origin.RunId);
            Assert.Equal(RootSessionId, origin.RootSessionId);
            Assert.Equal("plan-1", origin.WorkPlanId);
        });
        Assert.Contains(origins, origin => origin.Task.TaskId == buildTaskId && origin.WorkItemId == "build");
        var stored = Assert.Single(await restarted.ReadPromotionsAsync(ProjectId, CancellationToken.None));
        AssertSamePromotion(promotion, stored);

        var replay = await restarted.PromoteWorkPlanAsync(
            _actor, _selection, RootSessionId, "plan-1", 2, _fence, 0, "promotion-1",
            _ => throw new InvalidOperationException("A completed replay must not read current decision context."),
            _decisions, _ => Task.CompletedTask, CancellationToken.None);
        Assert.True(replay.IsSuccess);
        AssertSamePromotion(promotion, replay.Value!);
        Assert.Equal(1, await CountAsync("backlog_plan_promotions"));
        Assert.Equal(2, await CountAsync("backlog_task_origins"));
    }

    [Fact]
    public async Task DispatchRejectsAPromotedPlan()
    {
        Assert.True((await PromoteAsync("promotion-1", 0)).IsSuccess);

        var error = await Assert.ThrowsAsync<CoordinationException>(RequireDispatchStateAsync);
        Assert.Equal("maf_execution_dispatch_stale", error.Code);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentValuesAndSecondKeyForSamePlanConflict()
    {
        Assert.True((await PromoteAsync("promotion-1", 0)).IsSuccess);
        var graphRevision = (await _backlog.ReadGraphAsync(ProjectId, CancellationToken.None)).Value!.Revision;

        var reused = await Assert.ThrowsAsync<CoordinationException>(
            () => PromoteAsync("promotion-1", graphRevision));
        Assert.Equal("backlog_promotion_idempotency_conflict", reused.Code);
        var second = await Assert.ThrowsAsync<CoordinationException>(
            () => PromoteAsync("promotion-2", graphRevision));
        Assert.Equal("backlog_promotion_conflict", second.Code);
        Assert.Equal(1, await CountAsync("backlog_plan_promotions"));
        Assert.Equal(2, await CountAsync("backlog_tasks"));
    }

    [Fact]
    public async Task StaleDecisionFenceOrPlanAndStaleGraphWriteNothing()
    {
        var staleVersion = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.PromoteWorkPlanAsync(
            _actor, _selection, RootSessionId, "plan-1", 1, _fence, 0, "stale-version",
            WorkflowTestData.SelectionContext(), _decisions, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("backlog_promotion_stale", staleVersion.Code);
        var staleFence = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.PromoteWorkPlanAsync(
            _actor, _selection, RootSessionId, "plan-1", 2, _fence + 1, 0, "stale-fence",
            WorkflowTestData.SelectionContext(), _decisions, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("backlog_promotion_stale", staleFence.Code);
        var otherPlan = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.PromoteWorkPlanAsync(
            _actor, _selection, RootSessionId, "plan-2", 2, _fence, 0, "other-plan",
            WorkflowTestData.SelectionContext(), _decisions, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("backlog_promotion_stale", otherPlan.Code);

        var staleGraph = await PromoteAsync("stale-graph", expectedGraphRevision: 7);
        Assert.False(staleGraph.IsSuccess);
        Assert.Contains(staleGraph.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Equal(0, await CountAsync("backlog_tasks"));
        Assert.Equal(0, await CountAsync("backlog_plan_promotions"));
    }

    [Fact]
    public async Task AuthorityChangeBeforeCommitRollsBackTasksAndOrigins()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.PromoteWorkPlanAsync(
            _actor, _selection, RootSessionId, "plan-1", 2, _fence, 0, "promotion-1",
            WorkflowTestData.SelectionContext(), _decisions,
            _ => ++calls == 2
                ? throw new CoordinationException("backlog_project_authority_changed", 409)
                : Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal("backlog_project_authority_changed", error.Code);
        Assert.Equal(0, await CountAsync("backlog_tasks"));
        Assert.Equal(0, await CountAsync("backlog_dependencies"));
        Assert.Equal(0, await CountAsync("backlog_plan_promotions"));
        Assert.Equal(0, await CountAsync("backlog_task_origins"));
        Assert.True((await PromoteAsync("promotion-1", 0)).IsSuccess);
    }

    [Fact]
    public async Task OrdinaryCaptureRecordsNoOrigin()
    {
        var captured = await _backlog.AddTaskAsync(
            new BacklogTaskReference(ProjectId, "captured"),
            new BacklogTaskContent("Captured work"),
            0,
            CancellationToken.None);
        Assert.True(captured.IsSuccess);
        Assert.True((await PromoteAsync("promotion-1", captured.Value!.Revision)).IsSuccess);

        var origins = await _backlog.ReadTaskOriginsAsync(ProjectId, CancellationToken.None);
        Assert.DoesNotContain(origins, origin => origin.Task.TaskId == "captured");
        Assert.Equal(2, origins.Length);
    }

    [Fact]
    public async Task OtherActorCannotPromote()
    {
        var other = new CoordinationActor(_actor.Issuer, Guid.NewGuid().ToString("D"));
        var error = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.PromoteWorkPlanAsync(
            other, _selection, RootSessionId, "plan-1", 2, _fence, 0, "promotion-1",
            WorkflowTestData.SelectionContext(), _decisions, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, await CountAsync("backlog_tasks"));
    }

    [Fact]
    public async Task CheckpointRecoveryAdvancesFenceAndRebindsTheSamePendingGate()
    {
        var current = await _decisions.ReadCurrentAsync(
            _actor, Identity, _selection, CancellationToken.None);
        var requestId = "checkpoint-recovery-question";
        var question = CoordinatorDecisionFlow.AskQuestion(
            current.State, requestId, "scope-question", "Which repository should be changed?",
            _actor.Subject, ["repository-a", "repository-b"], allowsFreeform: false);
        Assert.True(question.IsSuccess);
        var pending = await _decisions.PersistTransitionAsync(
            _actor, Identity, _selection, current.StateVersion, requestId, requestId, "question.ask",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(question.Value),
            question.State!, true, question.Issues, null, null, question.Value, CancellationToken.None);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await _decisions.AdvanceExecutionFenceAsync(
                connection, transaction, _actor, Identity, _selection, _fence, _fence + 1,
                Guid.NewGuid(), "execution.checkpoint_recovery", CancellationToken.None,
                preservePendingGate: true, expectedDecisionStateVersion: pending.StateVersion);
            await transaction.CommitAsync();
        }

        var recovered = await ReadLatestDecisionEnvelopeAsync();
        Assert.Equal(_fence + 1, recovered.Fence);
        Assert.Equal(requestId, recovered.PendingGate!.RequestId);
        Assert.Equal(_actor.Subject, recovered.PendingGate.AuthorizedActorId);
        Assert.Equal(_fence + 1, recovered.PendingGate.Fence);

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT gate_state, execution_fence, state_version, allowed_choices, created_by_subject
            FROM "{_schema}".coordinator_gates
            WHERE project_id = @project AND run_id = @run AND request_id = @request
            """, verifyConnection);
        verify.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
        verify.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
        verify.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
        await using var gate = await verify.ExecuteReaderAsync();
        Assert.True(await gate.ReadAsync());
        Assert.Equal("pending", gate.GetString(0));
        Assert.Equal(_fence + 1, gate.GetInt64(1));
        Assert.Equal(pending.StateVersion + 1, gate.GetInt64(2));
        Assert.Equal(
            new[] { "repository-a", "repository-b" },
            JsonSerializer.Deserialize<string[]>(gate.GetString(3)));
        Assert.Equal(_actor.Subject, gate.GetString(4));
    }

    [Fact]
    public async Task CheckpointRecoveryRejectsACancelledGateInsteadOfRecreatingIt()
    {
        var current = await _decisions.ReadCurrentAsync(
            _actor, Identity, _selection, CancellationToken.None);
        const string requestId = "cancelled-recovery-question";
        var question = CoordinatorDecisionFlow.AskQuestion(
            current.State, requestId, "scope-question", "Which repository should be changed?",
            _actor.Subject, ["repository-a", "repository-b"], allowsFreeform: false);
        var pending = await _decisions.PersistTransitionAsync(
            _actor, Identity, _selection, current.StateVersion, requestId, requestId, "question.ask",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(question.Value),
            question.State!, true, question.Issues, null, null, question.Value, CancellationToken.None);
        var askedState = question.State!;
        var clearedState = CoordinatorDecisionState.Restore(
            askedState.Fence, askedState.OutcomeSpec, askedState.OutcomeConfirmed,
            askedState.NextClarifyingQuestionIndex, askedState.SelectedWorkflow, askedState.WorkflowConfirmed,
            askedState.ConfirmedWorkPlan, askedState.CandidateWorkPlan, askedState.LastScopeDiff,
            pendingGate: null, decisionReceipts: askedState.DecisionReceipts,
            workPlanRevisionAttempts: askedState.WorkPlanRevisionAttempts);
        Assert.True(clearedState.IsValid);
        var cleared = await _decisions.PersistTransitionAsync(
            _actor, Identity, _selection, pending.StateVersion, "clear-cancelled-gate",
            requestId, "test.gate.clear", CoordinatorDecisionOwnerStore.ComputeCommandHash("clear"),
            clearedState.Value!, true, [], null, null, null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CoordinationException>(async () =>
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _decisions.AdvanceExecutionFenceAsync(
                connection, transaction, _actor, Identity, _selection, _fence, _fence + 1,
                Guid.NewGuid(), "execution.checkpoint_recovery", CancellationToken.None,
                preservePendingGate: true, expectedDecisionStateVersion: cleared.StateVersion);
        });
        Assert.Equal("checkpoint_recovery_gate_unavailable", error.Code);
        Assert.Equal(1, await CountAsync("coordinator_gates"));
        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT gate_state FROM "{_schema}".coordinator_gates
            WHERE project_id = @project AND run_id = @run AND request_id = @request
            """, verifyConnection);
        verify.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
        verify.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
        verify.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
        Assert.Equal("cancelled", await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CheckpointRecoveryAdvancesABoundaryThatHasNoGate()
    {
        var current = await _decisions.ReadCurrentAsync(
            _actor, Identity, _selection, CancellationToken.None);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await _decisions.AdvanceExecutionFenceAsync(
                connection, transaction, _actor, Identity, _selection, _fence, _fence + 1,
                Guid.NewGuid(), "execution.checkpoint_recovery", CancellationToken.None,
                preservePendingGate: true, expectedDecisionStateVersion: current.StateVersion);
            await transaction.CommitAsync();
        }

        var recovered = await ReadLatestDecisionEnvelopeAsync();
        Assert.Equal(_fence + 1, recovered.Fence);
        Assert.Null(recovered.PendingGate);
        Assert.Equal(0, await CountAsync("coordinator_gates"));
    }

    [Fact]
    public async Task RecoveryCheckpointUsesTheNextFenceAndLinksItsImmutableSource()
    {
        var postgres = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null);
        var binding = new MafCheckpointBinding(
            Identity, _actor, _fence, MafExecutionCheckpointStore.CurrentSdkVersion, "model:selected", null);
        var sourceStore = new MafExecutionCheckpointStore(postgres, binding);
        var sourceState = new MafExecutionCheckpoint(
            1, "plan-1", 2, MafExecutionProgress.Empty);
        var source = await sourceStore.AppendAsync(
            "recovery-source", null, sourceState, CancellationToken.None);
        var sourceSnapshot = await sourceStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(sourceSnapshot);
        var nextFence = _fence + 1;
        var operationId = Guid.NewGuid();

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var advanceRun = new NpgsqlCommand($"""
                UPDATE "{_schema}".accepted_runs
                SET execution_fence = @fence, execution_state = 'active',
                    state_version = state_version + 1
                WHERE project_id = @project AND run_id = @run
                """, connection, transaction))
            {
                advanceRun.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, nextFence);
                advanceRun.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
                advanceRun.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
                Assert.Equal(1, await advanceRun.ExecuteNonQueryAsync());
            }
            await using (var advanceSession = new NpgsqlCommand($"""
                UPDATE "{_schema}".coordination_sessions
                SET execution_fence = @fence, state_version = state_version + 1
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                """, connection, transaction))
            {
                advanceSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, nextFence);
                advanceSession.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
                advanceSession.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
                advanceSession.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, RootSessionId);
                Assert.Equal(1, await advanceSession.ExecuteNonQueryAsync());
            }

            var recovered = await sourceStore.AppendRecoveryCheckpointInTransactionAsync(
                connection, transaction, sourceSnapshot, operationId, 3, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Equal("recovery-" + operationId.ToString("N"), recovered.Info.CheckpointId);
            Assert.Equal(source.Info.CheckpointId, recovered.ParentCheckpointId);
            Assert.Equal(sourceSnapshot.PayloadHash, recovered.State.RecoveryLineage!.SourceCheckpointHash);
        }

        var nextStore = new MafExecutionCheckpointStore(
            postgres, binding with { ExecutionFence = nextFence });
        var current = await nextStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(2, current.State.Revision);
        Assert.Equal(3, current.State.DecisionStateVersion);
        Assert.Equal(sourceSnapshot.Info.CheckpointId, current.State.RecoveryLineage!.SourceCheckpointId);
        Assert.Equal(sourceSnapshot.PayloadHash, current.State.RecoveryLineage.SourceCheckpointHash);
        Assert.Equal(_fence, current.State.RecoveryLineage.SourceExecutionFence);
        Assert.Equal(nextFence, current.State.RecoveryLineage.RecoveryExecutionFence);
    }

    private async Task<CoordinatorDecisionStateEnvelope> ReadLatestDecisionEnvelopeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT decision::text
            FROM "{_schema}".coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            ORDER BY state_version DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, RootSessionId);
        var payload = (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException("The recovery decision was not persisted."));
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("envelope").Deserialize<CoordinatorDecisionStateEnvelope>(DecisionJson)
            ?? throw new InvalidOperationException("The recovery decision envelope was not persisted.");
    }

    private Task<BacklogCoreResult<BacklogPlanPromotion>> PromoteAsync(
        string idempotencyKey, long expectedGraphRevision) =>
        _backlog.PromoteWorkPlanAsync(
            _actor,
            _selection,
            RootSessionId,
            "plan-1",
            2,
            _fence,
            expectedGraphRevision,
            idempotencyKey,
            WorkflowTestData.SelectionContext(),
            _decisions,
            _ => Task.CompletedTask,
            CancellationToken.None);

    private static void AssertSamePromotion(BacklogPlanPromotion expected, BacklogPlanPromotion actual)
    {
        Assert.Equal(expected with { Tasks = [] }, actual with { Tasks = [] });
        Assert.Equal(expected.Tasks.ToArray(), actual.Tasks.ToArray());
    }

    private async Task RequireDispatchStateAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await _decisions.RequireCurrentDispatchStateInTransactionAsync(
            connection, transaction, _actor, Identity, _selection, WorkflowTestData.SelectionContext(),
            2, _fence, "plan-1", CancellationToken.None);
        await transaction.RollbackAsync();
    }

    private async Task SeedConfirmedPlanAsync(long stateVersion)
    {
        var workflow = WorkflowTestData.Definition();
        var catalog = new AuthorizedWorkflowCatalog(workflow, [workflow]);
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(_fence),
            new CoordinatorOutcomeSpecification(
                "outcome-1",
                "Implement the requested change.",
                "The requested change is available.",
                "Only requested workflow behavior changes.",
                "Existing provider pins remain authoritative.",
                []),
            "outcome-approval-1",
            _actor.Subject);
        var confirmedOutcome = outcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            outcome.State.PendingGate!.RequestId, _actor.Subject, _fence,
            CoordinatorGateChoices.Approve, null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome, catalog, requestedWorkflowId: null, firstUseRequestId: null,
            authorizedActorId: null);
        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(
                WorkflowTestData.Item("design"),
                WorkflowTestData.Item("build", dependsOn: ["design"])),
            WorkflowTestData.SelectionContext(),
            "plan-approval-1",
            _actor.Subject);
        Assert.True(proposed.IsSuccess, string.Join("; ", proposed.Issues.Select(issue => issue.Message)));
        var confirmed = proposed.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposed.State.PendingGate!.RequestId, _actor.Subject, _fence,
            CoordinatorGateChoices.Approve, null)).State;
        Assert.True(confirmed.CanDispatch);

        var envelope = CoordinatorDecisionStateEnvelope.Capture(
            confirmed,
            CoordinatorDecisionBinding.Create(_actor, Identity, _selection, _fence),
            WorkflowTestData.SelectionContext());
        var payload = JsonSerializer.Serialize(new
        {
            envelope,
            transitionAccepted = true,
            issues = Array.Empty<object>(),
            transitionValue = (object?)null
        }, DecisionJson);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id,
                 actor_issuer, actor_subject, execution_fence, state_version,
                 action_kind, idempotency_key, command_hash, decision_state, decision)
            VALUES (@project, @run, @session, 'plan-confirmed', @decision,
                    @issuer, @subject, @fence, @version, 'test', 'plan-confirmed',
                    @hash, 'accepted', @payload)
            """, connection);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, RunId);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, RootSessionId);
        insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, Guid.NewGuid());
        insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, _actor.Issuer);
        insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, _actor.Subject);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, _fence);
        insert.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
        insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('a', 64));
        insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".{table} WHERE project_id = @project", connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, ProjectId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private AuthorizedRunSelection Selection()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              },
              "modelSelection": {
                "reference": "model:selected",
                "sourceMode": "hostedCopilot"
              },
              "runLimits": {
                "maxChildren": 100,
                "maxConcurrentChildren": 32
              }
            }
            """);
        var runSelection = new EffectiveRunSelection(
            ProjectId, RunId, 1, 1, 1, "context-v1", snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(
            runSelection,
            new ProjectsAuthorizationContext(
                1,
                _actor.Issuer,
                _actor.Subject,
                "tenant-1",
                1,
                ProjectId,
                RunId,
                [
                    new ProjectsAuthority(
                        "project",
                        ProjectId,
                        [new ProjectsPermissionGrant("acceptRunSelection", 1)])
                ]));
    }
}
