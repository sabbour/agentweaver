using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.Agents.AI.Workflows;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Theory]
    [InlineData(CoordinatorGateChoices.Approve, true)]
    [InlineData(CoordinatorGateChoices.Reject, false)]
    public async Task PostgresReviewDecisionSurvivesRestartWithoutDuplicateGate(string choice, bool approved)
    {
        var (plan, intent, decisions) = await PrepareReviewOwnerAsync();
        var first = CreateReviewExecutor(plan, decisions);
        Assert.Null(await first.ExecuteAsync(intent, CancellationToken.None));
        Assert.Null(await first.ExecuteAsync(intent, CancellationToken.None));
        var pending = await decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        Assert.Equal(intent.ApprovalRequestId, pending.State.PendingGate?.RequestId);
        Assert.Equal(intent.EvidenceHash, pending.State.PendingGate?.SubjectId);
        Assert.Equal(1, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));

        // A new store and executor represent an endpoint restart with no retained in-memory gate.
        var restarted = CreateReviewDecisionOwner();
        var recovery = CreateReviewExecutor(plan, restarted,
            new MafExecutionCheckpointStore(
                new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding));
        Assert.Null(await recovery.ExecuteAsync(intent, CancellationToken.None));
        Assert.Equal(1, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));

        var answer = pending.State.ApplyGateAnswer(new(
            intent.ApprovalRequestId, _actor.Subject, intent.ExecutionFence, choice, null));
        Assert.True(answer.IsAccepted);
        _ = await restarted.PersistTransitionAsync(
            _actor, _binding.Identity, _selection, pending.StateVersion,
            "review-answer-" + intent.OperationId.ToString("N"), intent.ApprovalRequestId,
            "gate.answer", CoordinatorDecisionOwnerStore.ComputeCommandHash(new
            {
                requestId = intent.ApprovalRequestId,
                choice,
                expectedVersion = pending.StateVersion
            }), answer.State, true, answer.Issues, null, null, answer.Receipt, CancellationToken.None);
        var status = await recovery.ExecuteAsync(intent, CancellationToken.None);
        Assert.Equal(approved ? MafExecutionTaskStatus.Succeeded : MafExecutionTaskStatus.Failed, status);
        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(status, latest.State.Progress.NonModelSteps[intent.StepId]);
        Assert.Equal(choice, latest.State.ReviewReceipts[intent.StepId].ChoiceId);
        Assert.Equal(status, await recovery.ExecuteAsync(intent, CancellationToken.None));
        Assert.Equal(1, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));
        var frontier = MafExecutionPlanner.BuildFrontier(plan, latest.State.Progress, 0, 0, 10, 10, _ => true);
        Assert.Equal(approved, frontier.ReadyActions.Any(action => action.Step.Id == "merge"));
    }

    [Fact]
    public async Task PostgresReviewRejectsUnrelatedDecisionAndChangedEvidence()
    {
        var (plan, intent, decisions) = await PrepareReviewOwnerAsync();
        var executor = CreateReviewExecutor(plan, decisions);
        // Simulate a fresh owner observation of changed source before a gate is opened.
        var staleEvidence = CreateReviewExecutor(plan, decisions, changedEvidence: true);
        var staleFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
            staleEvidence.ExecuteAsync(intent, CancellationToken.None));
        Assert.Equal("maf_execution_review_evidence_changed", staleFailure.Code);
        Assert.Equal(0, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));
        var before = await decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);

        // A different decision may occupy the owner, but cannot inherit the checkpointed intent.
        var unrelated = CoordinatorDecisionFlow.RequestApproval(
            before.State, "unrelated-request", "unrelated-subject", _actor.Subject);
        Assert.True(unrelated.IsSuccess);
        _ = await decisions.PersistTransitionAsync(
            _actor, _binding.Identity, _selection, before.StateVersion,
            "unrelated-" + intent.OperationId.ToString("N"), "unrelated-request",
            "approval.request", CoordinatorDecisionOwnerStore.ComputeCommandHash("unrelated"),
            unrelated.State!, true, unrelated.Issues, null, null, unrelated.Value,
            CancellationToken.None);
        var unrelatedFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
            executor.ExecuteAsync(intent, CancellationToken.None));
        Assert.Equal("maf_execution_review_decision_conflict", unrelatedFailure.Code);
        Assert.Equal(0, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));

        // A changed evidence hash is rejected even when the original operation ID is reused.
        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            executor.ExecuteAsync(intent with { EvidenceHash = new string('b', 64) }, CancellationToken.None));
        Assert.Equal("maf_execution_review_intent_conflict", conflict.Code);

        // Revoked actor authority is rejected by the existing decision owner before a receipt is appended.
        var revoked = _actor with { Subject = "revoked-actor" };
        await Assert.ThrowsAsync<CoordinationException>(() =>
            decisions.ReadCurrentAsync(revoked, _binding.Identity, _selection, CancellationToken.None));
        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Empty(latest.State.ReviewReceipts);
        Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps[intent.StepId]);
    }

    private async Task<(WorkPlanSnapshot Plan, MafExecutionReviewIntent Intent,
        CoordinatorDecisionOwnerStore Decisions)> PrepareReviewOwnerAsync()
    {
        // Seed the accepted confirmed plan with the same decision envelope used by the owner store.
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, []),
                WorkflowTestData.Platform("review", 1, ["implement"]),
                WorkflowTestData.Platform("merge", 2, ["review"], WorkflowPlatformGate.Merge))),
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext()).Value!;
        var decisions = CreateReviewDecisionOwner();
        _ = await decisions.InitializeRootAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(_binding.ExecutionFence),
            new("outcome-1", "Implement the change.", "The change is reviewed.",
                "Only the requested scope.", "Source is available.", []),
            "outcome-request", _actor.Subject);
        var acceptedOutcome = outcome.State!.ApplyGateAnswer(new(
            "outcome-request", _actor.Subject, _binding.ExecutionFence,
            CoordinatorGateChoices.Approve, null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            acceptedOutcome, new(plan.Workflow.Definition, [plan.Workflow.Definition]), null, null, null);
        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State, plan.Plan, WorkflowTestData.SelectionContext(),
            "plan-request", _actor.Subject);
        Assert.True(proposed.IsSuccess);
        var confirmed = proposed.State!.ApplyGateAnswer(new(
            "plan-request", _actor.Subject, _binding.ExecutionFence,
            CoordinatorGateChoices.Approve, null)).State;
        var envelope = CoordinatorDecisionStateEnvelope.Capture(
            confirmed, CoordinatorDecisionBinding.Create(
                _actor, _binding.Identity, _selection, _binding.ExecutionFence),
            WorkflowTestData.SelectionContext());
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var payload = JsonSerializer.Serialize(new
        {
            envelope,
            transitionAccepted = true,
            issues = Array.Empty<object>(),
            transitionValue = (object?)null
        }, json);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id,
                 actor_issuer, actor_subject, execution_fence, state_version,
                 action_kind, idempotency_key, command_hash, decision_state, decision)
            VALUES (@project, @run, @session, 'plan-confirmed', @decision,
                    @issuer, @subject, @fence, 2, 'test', 'plan-confirmed',
                    @hash, 'accepted', @payload)
            """, connection))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _binding.Identity.ProjectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _binding.Identity.RunId);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, _binding.Identity.SessionId);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, Guid.NewGuid());
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, _actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, _actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, _binding.ExecutionFence);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('a', 64));
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            await insert.ExecuteNonQueryAsync();
        }

        var initial = await _executionStore.AppendAsync("review-initial", null,
            new MafExecutionCheckpoint(1, plan.Plan.Id, 2, MafExecutionProgress.Empty with
            {
                WorkItems = MafExecutionProgress.Empty.WorkItems.Add("first", MafExecutionTaskStatus.Succeeded)
            })
            {
                Results = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                    .Add("first", "Reviewed source revision A.")
            }, CancellationToken.None);
        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), _binding.Identity, "review-intent", 2,
            plan, plan.Workflow.Definition.Steps[1], initial.State, 2,
            _actor.Subject, _binding.ExecutionFence,
            CoordinationOwnerStore.HashSelection(_selection.Selection));
        var next = CoordinationEndpoints.PrepareReviewCheckpoint(initial, intent)!;
        _ = await _executionStore.AppendAsync(intent.CheckpointId, initial.Info, next, CancellationToken.None);
        return (plan, intent, decisions);
    }

    private CoordinatorDecisionOwnerStore CreateReviewDecisionOwner()
    {
        // Fresh owner instances share only the PostgreSQL schema, not any in-memory gate.
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var contexts = new CoordinatorRunSelectionContextStore(
            _fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        return new CoordinatorDecisionOwnerStore(_fixture.DataSource, _schema, contexts);
    }

    private MafIndependentReviewExecutor CreateReviewExecutor(
        WorkPlanSnapshot plan, CoordinatorDecisionOwnerStore decisions,
        MafExecutionCheckpointStore? execution = null, bool changedEvidence = false)
    {
        execution ??= _executionStore;
        async Task<CoordinatorDecisionCurrentState> Current(CancellationToken token) =>
            await decisions.ReadCurrentAsync(_actor, _binding.Identity, _selection, token);
        async Task Open(MafExecutionReviewIntent intent, CancellationToken token)
        {
            // Opening uses the same idempotent durable transition and exact actor as the endpoint.
            var current = await Current(token);
            var transition = CoordinatorDecisionFlow.RequestApproval(
                current.State, intent.ApprovalRequestId, intent.EvidenceHash, _actor.Subject,
                "Review the recorded evidence: " + intent.EvidenceSummary);
            Assert.True(transition.IsSuccess);
            _ = await decisions.PersistTransitionAsync(
                _actor, _binding.Identity, _selection, current.StateVersion,
                intent.ApprovalRequestId, intent.ApprovalRequestId, "approval.request",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(intent),
                transition.State!, true, transition.Issues, null, null, transition.Value, token);
        }
        async Task RequireDecision(
            NpgsqlConnection connection, NpgsqlTransaction transaction, long version, CancellationToken token) =>
            await decisions.RequireCurrentDispatchStateInTransactionAsync(
                connection, transaction, _actor, _binding.Identity, _selection,
                WorkflowTestData.SelectionContext(), version, _binding.ExecutionFence,
                plan.Plan.Id, token);
        Task Evidence(MafExecutionReviewIntent intent, MafExecutionCheckpoint state, CancellationToken _)
        {
            // Recompute evidence even after restart; a different result cannot inherit the approval.
            var current = MafIndependentReviewContract.CreateIntent(
                intent.OperationId, intent.Identity, intent.CheckpointId, intent.CheckpointRevision,
                plan, plan.Workflow.Definition.Steps[1],
                changedEvidence ? state with
                {
                    Results = state.Results.SetItem("first", "Changed source revision.")
                } : state, intent.DecisionStateVersion,
                intent.AuthorizedActorId, intent.ExecutionFence, intent.AcceptedSelectionHash);
            if (current != intent)
                throw new CoordinationException("maf_execution_review_evidence_changed", 409);
            return Task.CompletedTask;
        }
        return new MafIndependentReviewExecutor(
            execution, _fixture.DataSource, Current, Open, RequireDecision, Evidence,
            _actor.Subject, _ => { }, (_, _) => { });
    }

    private async Task<int> CountReviewGateRequestsAsync(string requestId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".coordinator_decisions
            WHERE project_id = @project AND run_id = @run
              AND request_id = @request AND action_kind = 'approval.request'
            """, connection);
        query.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _binding.Identity.ProjectId);
        query.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _binding.Identity.RunId);
        query.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
        return Convert.ToInt32(await query.ExecuteScalarAsync());
    }
}
