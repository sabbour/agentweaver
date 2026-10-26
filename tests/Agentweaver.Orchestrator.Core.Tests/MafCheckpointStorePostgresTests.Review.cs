using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
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
    private const string ReviewSandboxProviderId = "sandbox.review-test";
    private const string ReviewSandboxResourceId = "review-test-resource";
    private WorkPlanRunSelectionContext _reviewSelectionContext = null!;

    [Theory]
    [InlineData(CoordinatorGateChoices.Approve, true)]
    [InlineData(CoordinatorGateChoices.Reject, false)]
    public async Task PostgresReviewDecisionSurvivesRestartWithoutDuplicateGate(string choice, bool approved)
    {
        var (plan, intent, decisions) = await PrepareReviewOwnerAsync();
        var first = CreateReviewExecutor(plan, decisions);
        Assert.Null(await first.ExecuteAsync(intent, CancellationToken.None));
        Assert.Null(await first.ExecuteAsync(intent, CancellationToken.None));
        var waiting = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(waiting);
        Assert.Equal(MafExecutionTaskStatus.Running,
            waiting.State.Progress.NonModelSteps[intent.StepId]);
        Assert.Empty(waiting.State.ReviewReceipts);
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
    }

    [Fact]
    public async Task PostgresReviewRevokedAuthorityDoesNotDuplicateDecision()
    {
        var (plan, intent, decisions) = await PrepareReviewOwnerAsync();
        var executor = CreateReviewExecutor(plan, decisions);
        Assert.Null(await executor.ExecuteAsync(intent, CancellationToken.None));
        var pending = await decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        Assert.Equal(intent.ApprovalRequestId, pending.State.PendingGate?.RequestId);
        Assert.Equal(1, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));

        // A caller who no longer owns the accepted run cannot reopen or answer its pending request.
        var revoked = _actor with { Subject = "revoked-actor" };
        var revokedExecutor = CreateReviewExecutor(plan, decisions, actor: revoked);
        await Assert.ThrowsAsync<CoordinationException>(() =>
            revokedExecutor.ExecuteAsync(intent, CancellationToken.None));
        var after = await decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(intent.ApprovalRequestId, after.State.PendingGate?.RequestId);
        Assert.Equal(1, await CountReviewGateRequestsAsync(intent.ApprovalRequestId));
        Assert.Empty(latest.State.ReviewReceipts);
        Assert.Equal(MafExecutionTaskStatus.Running,
            latest.State.Progress.NonModelSteps[intent.StepId]);
    }

    [Fact]
    public async Task PostgresReviewBuildTestThenReviewThenMergeRequiresApproval()
    {
        var (plan, intent, decisions) = await PrepareReviewOwnerAsync(includeBuildTest: true);
        var beforeReview = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(beforeReview);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            beforeReview.State.Progress.NonModelSteps["build-test"]);
        // BuildTest is complete, but its Merge descendant remains blocked until the human review terminates.
        Assert.False(MafExecutionPlanner.BuildFrontier(
            plan, beforeReview.State.Progress, 0, 0, 10, 10, _ => true)
            .ReadyActions.Any(action => action.Step.Id == "merge"));

        var executor = CreateReviewExecutor(plan, decisions);
        Assert.Null(await executor.ExecuteAsync(intent, CancellationToken.None));
        var waiting = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(waiting);
        Assert.Equal(MafExecutionTaskStatus.Running,
            waiting.State.Progress.NonModelSteps[intent.StepId]);
        Assert.False(MafExecutionPlanner.BuildFrontier(
            plan, waiting.State.Progress, 0, 0, 10, 10, _ => true)
            .ReadyActions.Any(action => action.Step.Id == "merge"));

        var pending = await decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        // Persist only the real owner's approval; a waiting gate never makes Merge eligible.
        var answer = pending.State.ApplyGateAnswer(new(
            intent.ApprovalRequestId, _actor.Subject, intent.ExecutionFence,
            CoordinatorGateChoices.Approve, null));
        Assert.True(answer.IsAccepted);
        _ = await decisions.PersistTransitionAsync(
            _actor, _binding.Identity, _selection, pending.StateVersion,
            "review-answer-" + intent.OperationId.ToString("N"), intent.ApprovalRequestId,
            "gate.answer", CoordinatorDecisionOwnerStore.ComputeCommandHash(new
            {
                requestId = intent.ApprovalRequestId,
                choice = CoordinatorGateChoices.Approve,
                expectedVersion = pending.StateVersion
            }), answer.State, true, answer.Issues, null, null, answer.Receipt, CancellationToken.None);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            await executor.ExecuteAsync(intent, CancellationToken.None));

        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.True(MafExecutionPlanner.BuildFrontier(
            plan, latest.State.Progress, 0, 0, 10, 10, _ => true)
            .ReadyActions.Any(action => action.Step.Id == "merge"));
    }

    private async Task<(WorkPlanSnapshot Plan, MafExecutionReviewIntent Intent,
        CoordinatorDecisionOwnerStore Decisions)> PrepareReviewOwnerAsync(bool includeBuildTest = false)
    {
        // Resolve and persist the same trusted Sandbox binding the decision owner must restore after restart.
        _reviewSelectionContext = await InitializeReviewSelectionAsync();
        var steps = includeBuildTest
            ? new[]
            {
                WorkflowTestData.Open("implement", 0, []),
                WorkflowTestData.Platform("build-test", 1, ["implement"], WorkflowPlatformGate.BuildTest) with
                {
                    BuildTestCommand = MafBuildTestCheckpointTests.Intent().Command
                },
                WorkflowTestData.Platform("review", 2, ["build-test"]),
                WorkflowTestData.Platform("merge", 3, ["review"], WorkflowPlatformGate.Merge)
            }
            : new[]
            {
                WorkflowTestData.Open("implement", 0, []),
                WorkflowTestData.Platform("review", 1, ["implement"]),
                WorkflowTestData.Platform("merge", 2, ["review"], WorkflowPlatformGate.Merge)
            };
        var planResult = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn, steps)),
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            _reviewSelectionContext);
        Assert.True(planResult.IsValid,
            string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        var plan = planResult.Value!;
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
            selected.State, plan.Plan, _reviewSelectionContext,
            "plan-request", _actor.Subject);
        Assert.True(proposed.IsSuccess);
        var confirmed = proposed.State!.ApplyGateAnswer(new(
            "plan-request", _actor.Subject, _binding.ExecutionFence,
            CoordinatorGateChoices.Approve, null)).State;
        var envelope = CoordinatorDecisionStateEnvelope.Capture(
            confirmed, CoordinatorDecisionBinding.Create(
                _actor, _binding.Identity, _selection, _binding.ExecutionFence),
            _reviewSelectionContext);
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
        if (includeBuildTest)
            initial = await AppendSuccessfulBuildTestAsync(initial, plan);
        var reviewStep = plan.Workflow.Definition.Steps.Single(
            step => step.PlatformGate == WorkflowPlatformGate.IndependentReview);
        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), _binding.Identity, initial.Info.CheckpointId, initial.State.Revision,
            plan, reviewStep, initial.State, 2,
            _actor.Subject, _binding.ExecutionFence,
            CoordinationOwnerStore.HashSelection(_selection.Selection));
        var next = CoordinationEndpoints.PrepareReviewCheckpoint(initial, intent)!;
        _ = await _executionStore.AppendAsync(intent.CheckpointId, initial.Info, next, CancellationToken.None);
        return (plan, intent, decisions);
    }

    private CoordinatorDecisionOwnerStore CreateReviewDecisionOwner()
    {
        // Fresh owner instances share only the PostgreSQL schema, not any in-memory gate.
        return new CoordinatorDecisionOwnerStore(
            _fixture.DataSource, _schema, CreateReviewSelectionContextStore());
    }

    private async Task<WorkPlanRunSelectionContext> InitializeReviewSelectionAsync()
    {
        // Give this PostgreSQL case a realistic provider snapshot so restart recovery can revalidate the pin.
        var selection = new EffectiveRunSelection(
            "project-1", Guid.NewGuid().ToString("D"), 1, 1, 1, "context-v1",
            CreateReviewSelectionSnapshot());
        _selection = new AuthorizedRunSelection(
            selection,
            new ProjectsAuthorizationContext(
                1, _actor.Issuer, _actor.Subject, "tenant-1", 1,
                selection.ProjectId, selection.RunId,
                [new ProjectsAuthority(
                    "project", selection.ProjectId,
                    [new ProjectsPermissionGrant("acceptRunSelection", 1)])]));
        var identity = new SessionIdentity(selection.ProjectId, selection.RunId, "root");
        var root = await new CoordinationOwnerStore(_fixture.DataSource, _schema)
            .AcceptRootAsync(_actor, _selection, identity.SessionId, CancellationToken.None);
        _binding = new MafCheckpointBinding(
            identity, _actor, root.ExecutionFence, "maf-test-sdk", "model:provider/alpha", null);
        var checkpoints = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null);
        _store = checkpoints.ForRun(_binding);
        _executionStore = new MafExecutionCheckpointStore(checkpoints, _binding);

        var contexts = CreateReviewSelectionContextStore();
        var resolution = await contexts.ResolveForPlanAsync(
            selection, _binding.ExecutionFence, CancellationToken.None);
        var pendingBinding = resolution.PendingBinding ??
            throw new InvalidOperationException("The review test requires a newly negotiated Sandbox binding.");
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await contexts.PersistBindingAsync(
            connection, transaction, selection, _binding.ExecutionFence, pendingBinding, CancellationToken.None);
        await transaction.CommitAsync();
        return resolution.Context;
    }

    private static JsonElement CreateReviewSelectionSnapshot()
    {
        // The accepted snapshot and server catalog must agree exactly for durable binding recovery.
        var capabilities = ReviewSandboxCapabilities();
        var candidate = new EffectiveProviderCandidate(
            ProviderSeam.Sandbox, ReviewSandboxProviderId, "1.0.0", 1, "options-v1",
            ProviderHostingPattern.InProcess, capabilities.Order(StringComparer.Ordinal).ToImmutableArray(),
            capabilities.Order(StringComparer.Ordinal).ToImmutableArray());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        return JsonSerializer.SerializeToElement(new
        {
            projectConfiguration = new
            {
                blueprintWorkflowReferences = Array.Empty<object>(),
                agentCharters = new[]
                {
                    new { agentId = "agent-1", role = "engineer", charter = "Implement the requested change." }
                },
                casting = new[] { new { agentId = "agent-1", role = "engineer" } }
            },
            modelSelection = new { reference = "model:provider/alpha" },
            providers = new[]
            {
                new EffectiveProviderSelection(
                    ProviderCardinality.Exclusive, ProviderSeam.Sandbox, [candidate])
            }
        }, options);
    }

    private CoordinatorRunSelectionContextStore CreateReviewSelectionContextStore()
    {
        // Recreate the same trusted registration and provider for every fresh decision-owner instance.
        var capabilities = ReviewSandboxCapabilities();
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox, ReviewSandboxProviderId, new Version(1, 0, 0), 1,
                ProviderHostingPattern.InProcess, capabilities),
            true, "options-v1", 1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, ReviewSandboxProviderId)],
            []).Value!;
        return new CoordinatorRunSelectionContextStore(
            _fixture.DataSource, _schema, catalog, new ProviderResolver(catalog),
            [new ReviewSandboxResourceProvider()]);
    }

    private static ImmutableHashSet<string> ReviewSandboxCapabilities() =>
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "sandbox.workspace.write",
            SandboxCapabilities.BuildTestCommandPod);

    private async Task<MafExecutionCheckpointSnapshot> AppendSuccessfulBuildTestAsync(
        MafExecutionCheckpointSnapshot initial,
        WorkPlanSnapshot plan)
    {
        // Bind a valid terminal BuildTest receipt to this run's exact accepted provider and checkpoint.
        var template = MafBuildTestCheckpointTests.Intent();
        var expected = template.ExpectedBinding with
        {
            Fence = new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity(
                    "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId, "environment-1"),
                1),
            SandboxResource = new ProviderResourceRef(
                ProviderSeam.Sandbox, ReviewSandboxProviderId, ReviewSandboxResourceId, 1),
            SandboxProviderBinding = CreateReviewSandboxBindingSnapshot()
        };
        var intent = template with
        {
            OperationId = Guid.NewGuid(),
            Identity = _binding.Identity,
            CheckpointId = "review-build-test",
            CheckpointRevision = initial.State.Revision + 1,
            WorkPlanId = plan.Plan.Id,
            StepId = "build-test",
            DecisionStateVersion = initial.State.DecisionStateVersion,
            ExecutionFence = _binding.ExecutionFence,
            AcceptedSelectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection),
            ExpectedBinding = expected
        };
        var runningState = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent) ??
            throw new InvalidOperationException("The review test BuildTest intent was not accepted.");
        var running = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, runningState, CancellationToken.None);
        var operation = MafBuildTestExecutionTests.Operation(intent);
        var completedState = CoordinationEndpoints.PrepareBuildTestCompletionCheckpoint(
            running, intent, operation) ??
            throw new InvalidOperationException("The review test BuildTest operation did not complete.");
        return await _executionStore.AppendAsync(
            intent.CheckpointId, running.Info, completedState, CancellationToken.None);
    }

    private static SandboxProviderBindingSnapshot CreateReviewSandboxBindingSnapshot()
    {
        // The persisted provider metadata must match the exact resource identity pinned by the selection context.
        using var emptyOptions = JsonDocument.Parse("{}");
        using var emptyRelease = JsonDocument.Parse("{}");
        return new SandboxProviderBindingSnapshot(
            ReviewSandboxProviderId, "1.0.0", 1, "options-v1",
            emptyOptions.RootElement.Clone(), emptyRelease.RootElement.Clone());
    }

    private MafIndependentReviewExecutor CreateReviewExecutor(
        WorkPlanSnapshot plan, CoordinatorDecisionOwnerStore decisions,
        MafExecutionCheckpointStore? execution = null, bool changedEvidence = false,
        CoordinationActor? actor = null)
    {
        execution ??= _executionStore;
        actor ??= _actor;
        async Task<CoordinatorDecisionCurrentState> Current(CancellationToken token) =>
            await decisions.ReadCurrentAsync(actor, _binding.Identity, _selection, token);
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
                _reviewSelectionContext, version, _binding.ExecutionFence,
                plan.Plan.Id, token);
        Task Evidence(MafExecutionReviewIntent intent, MafExecutionCheckpoint state, CancellationToken _)
        {
            // Recompute evidence even after restart; a different result cannot inherit the approval.
            var current = MafIndependentReviewContract.CreateIntent(
                intent.OperationId, intent.Identity, intent.CheckpointId, intent.CheckpointRevision,
                plan, plan.Workflow.Definition.Steps.Single(step => step.Id == intent.StepId),
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

    private sealed class ReviewSandboxResourceProvider : ICoordinatorSandboxResourceProvider
    {
        public string ProviderId => ReviewSandboxProviderId;

        public ImmutableArray<string> IsolationChoices => ["worktree", "sandboxed"];

        public Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
            ProviderCandidate candidate,
            EffectiveRunSelection selection,
            CancellationToken cancellationToken)
        {
            // The fixture negotiates only the single catalogued resource and never provisions it.
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Seam != ProviderSeam.Sandbox || candidate.ProviderId != ProviderId)
                return Task.FromResult(ProviderResult<ResourceNegotiation>.Failure(
                    ProviderErrorCode.ProviderSeamMismatch,
                    "The review test selected an unexpected Sandbox provider."));
            return Task.FromResult(ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Sandbox, ProviderId, ReviewSandboxResourceId, 1),
                candidate.RequiredCapabilities)));
        }
    }
}
