using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Microsoft.Agents.AI.Workflows;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Fact]
    public async Task PostgresMergeApprovalPendingKeepsCheckpointRunningAndNoReceipt()
    {
        var fixture = await PersistMergeIntentAsync();
        var intent = fixture.Intent;
        var source = fixture.Source;
        var owner = new MergeOwnerHandler(intent) { ApprovalPending = true };
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.ConfirmedPlan, intent.DecisionStateVersion);
        using (client)
        {
            var outcome = await executor.ExecuteAsync(intent, CancellationToken.None);

            Assert.Equal(MafMergeExecutionOutcome.AwaitingApproval, outcome);
            Assert.Equal(intent.OwnerIntentId, owner.LastIntentId);
            Assert.Equal(intent.ApprovalRequestId, owner.LastApprovalRequestId);
            Assert.Equal(0, owner.MergeEffects);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
        }
    }

    [Fact]
    public async Task PostgresMergeRecoveryAfterCheckpointRollbackReusesOwnerResultWithoutSecondMerge()
    {
        var fixture = await PersistMergeIntentAsync();
        var intent = fixture.Intent;
        var source = fixture.Source;
        var owner = new MergeOwnerHandler(intent);
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.Approved, intent.DecisionStateVersion + 1);
        using (client)
        {
            await SetMergeTerminalCheckpointFailureAsync(true);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() =>
                    executor.ExecuteAsync(intent, CancellationToken.None));
            }
            finally
            {
                await SetMergeTerminalCheckpointFailureAsync(false);
            }

            var afterRollback = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(afterRollback);
            Assert.Equal(MafExecutionTaskStatus.Running, afterRollback.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(afterRollback.State.MergeReceipts);
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(1, owner.PrepareRequests);

            Assert.Equal(
                MafMergeExecutionOutcome.Completed,
                await executor.ExecuteAsync(intent, CancellationToken.None));
            var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(MafExecutionTaskStatus.Succeeded, recovered.State.Progress.NonModelSteps["merge"]);
            Assert.Equal(new string('b', 40), recovered.State.MergeReceipts["merge"].MergeSha);
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(2, owner.PrepareRequests);
            Assert.Equal(1, owner.ExecuteRequests);

            Assert.Equal(
                MafMergeExecutionOutcome.NoProgress,
                await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(2, owner.PrepareRequests);
            Assert.Equal(1, owner.ExecuteRequests);
        }
    }

    [Fact]
    public async Task PostgresMergeRejectsChangedOwnerHeadBeforeCallingExecuteRoute()
    {
        var fixture = await PersistMergeIntentAsync();
        var intent = fixture.Intent;
        var owner = new MergeOwnerHandler(intent)
        {
            PreparedHeadSha = new string('d', 40)
        };
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.Approved, intent.DecisionStateVersion + 1);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(intent, CancellationToken.None));

            Assert.Equal("source_control_merge_response_invalid", failure.Code);
            Assert.Equal(0, owner.ExecuteRequests);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
        }
    }

    [Fact]
    public async Task PostgresMergeApprovalContinuationRejectsUnrelatedDecisionAdvance()
    {
        var fixture = await PersistMergeIntentAsync();
        var owner = new MergeOwnerHandler(fixture.Intent);
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.Approved, fixture.Intent.DecisionStateVersion + 2);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(fixture.Intent, CancellationToken.None));

            Assert.Equal("source_control_run_binding_changed", failure.Code);
            Assert.Equal(1, owner.PrepareRequests);
            Assert.Equal(0, owner.ExecuteRequests);
            Assert.Equal(0, owner.MergeEffects);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
            AssertMergeDependentBlocked(fixture, latest.State);
        }
    }

    [Fact]
    public async Task PostgresMergeRejectsRevokedAuthorityBeforeOwnerCall()
    {
        var fixture = await PersistMergeIntentAsync();
        var acceptedRun = fixture.Source.Pin.AcceptedRun;
        var revokedRun = new SourceControlAcceptedRunBinding(
            acceptedRun.Issuer,
            "revoked-actor",
            acceptedRun.TenantId,
            acceptedRun.ProjectId,
            acceptedRun.RunId,
            acceptedRun.RootSessionId,
            acceptedRun.AcceptedSelectionHash,
            acceptedRun.ProjectRevision,
            acceptedRun.ProjectConfigurationRevision,
            acceptedRun.PlatformRuntimeRevision,
            acceptedRun.ContextRevision,
            acceptedRun.Fence);
        var revokedPin = new SourceControlRepositoryPin(
            fixture.Source.Pin.PinId,
            revokedRun,
            fixture.Source.Pin.ProviderBinding,
            fixture.Source.Pin.Repository,
            fixture.Source.Pin.ApiCredential,
            fixture.Source.Pin.CheckoutCredential,
            fixture.Source.Pin.WebhookCredential,
            fixture.Source.Pin.ProviderRepositoryId,
            fixture.Source.Pin.DefaultBranch,
            fixture.Source.Pin.IsPrivate,
            fixture.Source.Pin.PinnedAt,
            fixture.Source.Pin.GitHubAppBinding);
        var owner = new MergeOwnerHandler(fixture.Intent);
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.Approved, fixture.Intent.DecisionStateVersion + 1, revokedPin);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(fixture.Intent, CancellationToken.None));

            Assert.Equal("source_control_run_binding_changed", failure.Code);
            Assert.Equal(0, owner.PrepareRequests);
            Assert.Equal(0, owner.MergeEffects);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
            AssertMergeDependentBlocked(fixture, latest.State);
        }
    }

    [Fact]
    public async Task PostgresUncertainOwnerOutcomeStaysBlockedAndIsNotRetried()
    {
        var fixture = await PersistMergeIntentAsync();
        var owner = new MergeOwnerHandler(fixture.Intent) { ExecuteOutcome = "outcome_uncertain" };
        var (executor, client) = CreateMergeExecutor(
            fixture, owner, fixture.Approved, fixture.Intent.DecisionStateVersion + 1);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(fixture.Intent, CancellationToken.None));
            Assert.Equal("source_control_merge_outcome_uncertain", failure.Code);

            var retryFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(fixture.Intent, CancellationToken.None));
            Assert.Equal("source_control_merge_outcome_uncertain", retryFailure.Code);
            Assert.Equal(1, owner.ExecuteRequests);
            Assert.Equal(1, owner.MergeEffects);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
            AssertMergeDependentBlocked(fixture, latest.State);
        }
    }

    private async Task<MergePersistenceFixture> PersistMergeIntentAsync()
    {
        // Start from a real PostgreSQL checkpoint so restart and rollback behavior exercise the production store.
        var source = CreateCurrentSourceControlRun();
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("merge", 1, ["implement"], WorkflowPlatformGate.Merge),
            WorkflowTestData.Open("followup", 2, ["merge"]));
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(workflow),
            WorkflowTestData.Plan(
                WorkflowTestData.Item("implementation"),
                WorkflowTestData.Item("follow-up", "followup", dependsOn: ["implementation"])),
            WorkflowTestData.SelectionContext()).Value!;
        var initialProgress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "merge", MafExecutionTaskStatus.Pending)
        };
        var initial = await _executionStore.AppendAsync(
            "merge-initial",
            null,
            new(1, plan.Plan.Id, 1, initialProgress),
            CancellationToken.None);
        var intent = MafMergeContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            _binding.Identity,
            "merge-intent",
            2,
            plan.Plan.Id,
            workflow.Id,
            workflow.Revision,
            "merge",
            1,
            _binding.ExecutionFence,
            CoordinationOwnerStore.HashSelection(_selection.Selection),
            source.Pin,
            42,
            new string('a', 40),
            SourceControlMergeMethod.Rebase);
        var decisions = CreateMergeDecisionStates(source, intent, workflow, plan);
        var next = CoordinationEndpoints.PrepareMergeCheckpoint(initial, intent)!;
        var running = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, next, CancellationToken.None);
        return new(running, intent, source, workflow, plan, decisions.ConfirmedPlan, decisions.Approved);
    }

    private (MafMergeExecutor Executor, HttpClient Client) CreateMergeExecutor(
        MergePersistenceFixture fixture,
        MergeOwnerHandler owner,
        CoordinatorDecisionState currentDecision,
        long currentDecisionVersion,
        SourceControlRepositoryPin? currentPin = null)
    {
        // Exercise the production binding and approval contract alongside the real PostgreSQL checkpoint store.
        var intent = fixture.Intent;
        var source = fixture.Source;
        var pin = currentPin ?? source.Pin;
        long? currentApprovalStateVersion = currentDecisionVersion == intent.DecisionStateVersion
            ? null
            : currentDecisionVersion;
        Task RequireCurrent(
            MafExecutionMergeIntent currentIntent,
            long? ownerApprovalStateVersion,
            CancellationToken _)
        {
            MafMergeContract.RequireCurrentSource(
                currentIntent,
                source.Actor,
                intent.Identity,
                "tenant-1",
                currentDecisionVersion,
                currentDecision,
                _binding.ExecutionFence,
                CoordinationOwnerStore.HashSelection(_selection.Selection),
                pin,
                ownerApprovalStateVersion);
            return Task.CompletedTask;
        }

        Task RequireDecision(
            Npgsql.NpgsqlConnection _,
            Npgsql.NpgsqlTransaction __,
            CancellationToken ___)
        {
            MafMergeContract.RequireCurrentSource(
                intent,
                source.Actor,
                intent.Identity,
                "tenant-1",
                currentDecisionVersion,
                currentDecision,
                _binding.ExecutionFence,
                CoordinationOwnerStore.HashSelection(_selection.Selection),
                pin,
                currentApprovalStateVersion);
            return Task.CompletedTask;
        }

        var client = new HttpClient(owner) { Timeout = TimeSpan.FromSeconds(2) };
        var sourceControl = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var executor = new MafMergeExecutor(
            _executionStore,
            sourceControl,
            _fixture.DataSource,
            _ => Task.FromResult(new RuntimeActorAuthorization(
                new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
                "tenant-1")),
            RequireCurrent,
            RequireDecision,
            () => false,
            () => { },
            _ => { },
            (_, _) => { });
        return (executor, client);
    }

    private static (CoordinatorDecisionState ConfirmedPlan, CoordinatorDecisionState Approved)
        CreateMergeDecisionStates(
            SourceControlRunContext source,
            MafExecutionMergeIntent intent,
            WorkflowDefinition workflow,
            WorkPlanSnapshot plan)
    {
        // Recreate the confirmed workflow and typed merge-approval decision used by dispatch.
        var outcome = new CoordinatorOutcomeSpecification(
            "outcome-1",
            "Implement the requested change.",
            "The requested change is available.",
            "Only requested workflow behavior changes.",
            "Existing provider pins remain authoritative.",
            []);
        var proposedOutcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(intent.ExecutionFence),
            outcome,
            "outcome-approval-1",
            source.Actor.Subject);
        var confirmedOutcome = proposedOutcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedOutcome.State.PendingGate!.RequestId,
            source.Actor.Subject,
            intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var catalog = new AuthorizedWorkflowCatalog(workflow, [workflow]);
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome, catalog, requestedWorkflowId: null,
            firstUseRequestId: null, authorizedActorId: null);
        var proposedPlan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            plan.Plan,
            WorkflowTestData.SelectionContext(),
            "plan-approval-1",
            source.Actor.Subject);
        var confirmedPlan = proposedPlan.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedPlan.State.PendingGate!.RequestId,
            source.Actor.Subject,
            intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var pending = CoordinatorDecisionFlow.RequestApproval(
            confirmedPlan,
            intent.ApprovalRequestId,
            intent.OwnerIntentId,
            source.Actor.Subject);
        var approved = pending.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            pending.Value!.RequestId,
            source.Actor.Subject,
            intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(approved.IsAccepted);
        return (confirmedPlan, approved.State);
    }

    private static void AssertMergeDependentBlocked(
        MergePersistenceFixture fixture,
        MafExecutionCheckpoint checkpoint)
    {
        // A running Merge gate cannot release workflow steps that depend on its completion.
        var progress = checkpoint.Progress with
        {
            WorkItems = checkpoint.Progress.WorkItems
                .SetItem("implementation", MafExecutionTaskStatus.Succeeded)
                .SetItem("follow-up", MafExecutionTaskStatus.Pending)
        };
        var frontier = MafExecutionPlanner.BuildFrontier(
            fixture.Plan, progress, 0, 0, 10, 10,
            hasNonModelExecutor: step => step.PlatformGate == WorkflowPlatformGate.Merge);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.Step.Id == "followup");
    }

    private async Task SetMergeTerminalCheckpointFailureAsync(bool enabled)
    {
        // Fail only the terminal-receipt append so the test can recover an already-applied owner effect.
        var trigger = $"\"{_schema}\".fail_merge_terminal";
        var table = $"\"{_schema}\".maf_workflow_checkpoints";
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        var sql = enabled
            ? $"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF position('"mergeSha"' in NEW.payload::text) > 0 THEN
                        RAISE EXCEPTION 'intentional Merge checkpoint failure';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER fail_merge_terminal
                    BEFORE INSERT ON {table}
                    FOR EACH ROW EXECUTE FUNCTION {trigger}();
                """
            : $"""
                DROP TRIGGER IF EXISTS fail_merge_terminal ON {table};
                DROP FUNCTION IF EXISTS {trigger}();
                """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MergeOwnerHandler(MafExecutionMergeIntent intent) : HttpMessageHandler
    {
        public bool ApprovalPending { get; init; }
        public string? PreparedHeadSha { get; init; }
        public string? ExecuteOutcome { get; init; }
        public int PrepareRequests { get; private set; }
        public int ExecuteRequests { get; private set; }
        public int MergeEffects { get; private set; }
        public string? LastIntentId { get; private set; }
        public string? LastApprovalRequestId { get; private set; }
        private string _durableState = "approved";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // An exact idempotency replay returns the durable merge result instead of invoking the effect again.
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(HttpMethod.Post, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/merge-intents", StringComparison.Ordinal))
            {
                PrepareRequests++;
                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                Assert.Equal(intent.IdempotencyKey, body.RootElement.GetProperty("idempotencyKey").GetString());
                Assert.Equal(intent.ExpectedHeadSha, body.RootElement.GetProperty("expectedHeadSha").GetString());
                LastIntentId = intent.OwnerIntentId;
                LastApprovalRequestId = intent.ApprovalRequestId;
                if (ApprovalPending)
                    return Response(JsonSerializer.Serialize(new
                    {
                        intentId = intent.OwnerIntentId,
                        approvalRequestId = intent.ApprovalRequestId,
                        state = "approval_pending"
                    }), request, HttpStatusCode.Accepted);

                if (_durableState == "outcome_uncertain")
                    return Response(
                        """{"error":"source_control_merge_outcome_uncertain"}""",
                        request,
                        HttpStatusCode.BadGateway);

                var state = _durableState;
                return Response(JsonSerializer.Serialize(new
                {
                    intentId = intent.OwnerIntentId,
                    approvalRequestId = intent.ApprovalRequestId,
                    state,
                    repository = intent.Repository.FullName,
                    pullRequestNumber = intent.PullRequestNumber,
                    headBranch = "feature",
                    headSha = PreparedHeadSha ?? intent.ExpectedHeadSha,
                    baseBranch = "main",
                    baseSha = new string('c', 40),
                    method = intent.Method,
                    approvalDecisionId = Guid.Parse("daab7d19-85f9-4e68-9bc0-7d3533f355e6"),
                    approvalStateVersion = intent.DecisionStateVersion + 1,
                    grantReference = new { kind = "merge-approval" },
                    mergeSha = state == "merged" ? new string('b', 40) : null,
                    lastFailureCode = (string?)null
                }), request);
            }

            Assert.EndsWith("/merge-intents/" + intent.OwnerIntentId + "/execute",
                path, StringComparison.Ordinal);
            ExecuteRequests++;
            MergeEffects++;
            if (ExecuteOutcome == "outcome_uncertain")
            {
                _durableState = "outcome_uncertain";
                return Response(
                    """{"error":"source_control_merge_outcome_uncertain"}""",
                    request,
                    HttpStatusCode.BadGateway);
            }
            _durableState = "merged";
            return Response(JsonSerializer.Serialize(new
            {
                intentId = intent.OwnerIntentId,
                state = "merged",
                mergeSha = new string('b', 40),
                failureCode = (string?)null
            }), request);
        }

        private static HttpResponseMessage Response(
            string payload,
            HttpRequestMessage request,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }
    }

    private sealed record MergePersistenceFixture(
        MafExecutionCheckpointSnapshot Running,
        MafExecutionMergeIntent Intent,
        SourceControlRunContext Source,
        WorkflowDefinition Workflow,
        WorkPlanSnapshot Plan,
        CoordinatorDecisionState ConfirmedPlan,
        CoordinatorDecisionState Approved);
}
