using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafMergeCheckpointTests
{
    [Fact]
    public void MergeIntentAndOwnerReceiptSurviveCheckpointRecovery()
    {
        var fixture = CreateFixture();
        var running = CoordinationEndpoints.PrepareMergeCheckpoint(
            fixture.Checkpoint, fixture.Intent)!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(running));
        var retained = recovered.MergeIntents["merge"];

        Assert.Equal(MafExecutionTaskStatus.Running, recovered.Progress.NonModelSteps["merge"]);
        Assert.True(MafExecutionCheckpointContract.MergeIntentsMatch(fixture.Intent, retained));
        Assert.Null(CoordinationEndpoints.PrepareMergeCheckpoint(
            new(fixture.Checkpoint.Info, recovered), fixture.Intent));

        var prepared = OwnerIntent(fixture.Intent, "approved");
        var executed = OwnerIntent(fixture.Intent, "merged") with { MergeSha = new string('b', 40) };
        var receipt = MafMergeContract.CreateReceipt(fixture.Intent, prepared, executed);
        var completed = CoordinationEndpoints.PrepareMergeCompletionCheckpoint(
            new(fixture.Checkpoint.Info, recovered), fixture.Intent, receipt)!;
        var restored = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(completed));

        Assert.Equal(MafExecutionTaskStatus.Succeeded, restored.Progress.NonModelSteps["merge"]);
        Assert.Equal(receipt, restored.MergeReceipts["merge"]);
        Assert.Contains("\"kind\":\"merge\"", MafMergeContract.ResultSummary(receipt), StringComparison.Ordinal);
        Assert.Contains("\"mergeSha\"", MafMergeContract.ResultSummary(receipt), StringComparison.Ordinal);
        Assert.DoesNotContain("grant", MafMergeContract.ResultSummary(receipt), StringComparison.OrdinalIgnoreCase);
        Assert.Null(CoordinationEndpoints.PrepareMergeCompletionCheckpoint(
            new(fixture.Checkpoint.Info, restored), fixture.Intent, receipt));
    }

    [Fact]
    public void ChangedOwnerHeadOrMethodCannotProduceAReceipt()
    {
        var fixture = CreateFixture();
        var prepared = OwnerIntent(fixture.Intent, "approved");
        var executed = OwnerIntent(fixture.Intent, "merged") with { MergeSha = new string('b', 40) };

        var changedHead = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.ValidatePreparedOwnerIntent(
                fixture.Intent, prepared with { HeadSha = new string('c', 40) }));
        var changedMethod = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.ValidatePreparedOwnerIntent(
                fixture.Intent, prepared with { Method = SourceControlMergeMethod.Squash }));
        var changedMerge = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.CreateReceipt(
                fixture.Intent, prepared, executed with { MergeSha = "not-a-git-sha" }));
        var durableAuthorityFailure = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.ValidatePreparedOwnerIntent(
                fixture.Intent,
                executed with { FailureCode = "source_control_merge_grant_not_current" }));
        var executionAuthorityFailure = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.CreateReceipt(
                fixture.Intent,
                prepared,
                executed with { FailureCode = "source_control_merge_grant_not_current" }));

        Assert.Equal("source_control_merge_response_invalid", changedHead.Code);
        Assert.Equal("source_control_merge_response_invalid", changedMethod.Code);
        Assert.Equal("source_control_merge_response_invalid", changedMerge.Code);
        Assert.Equal("source_control_merge_outcome_uncertain", durableAuthorityFailure.Code);
        Assert.Equal("source_control_merge_outcome_uncertain", executionAuthorityFailure.Code);
    }

    [Fact]
    public void DurableMergedIntentCanRecoverAfterItsExecutionGrantExpires()
    {
        var fixture = CreateFixture();
        var merged = OwnerIntent(fixture.Intent, "merged") with { HasGrantReference = false };

        MafMergeContract.ValidatePreparedOwnerIntent(fixture.Intent, merged);

        var receipt = MafMergeContract.CreateReceipt(fixture.Intent, merged, merged);
        Assert.Equal(fixture.Intent.OwnerIntentId, receipt.OwnerIntentId);
        Assert.Equal(new string('b', 40), receipt.MergeSha);
    }

    [Fact]
    public void CurrentMergeApprovalMustBeTheExactTypedDecisionForThisActorAndIntent()
    {
        var fixture = CreateFixture();
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("merge", 1, ["implement"], WorkflowPlatformGate.Merge));
        var catalog = new AuthorizedWorkflowCatalog(workflow, [workflow]);
        var outcome = new CoordinatorOutcomeSpecification(
            "outcome-1",
            "Implement the requested change.",
            "The requested change is available.",
            "Only requested workflow behavior changes.",
            "Existing provider pins remain authoritative.",
            []);
        var proposedOutcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(fixture.Intent.ExecutionFence),
            outcome,
            "outcome-approval-1",
            fixture.Source.Actor.Subject);
        var confirmedOutcome = proposedOutcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedOutcome.State.PendingGate!.RequestId,
            fixture.Source.Actor.Subject,
            fixture.Intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome,
            catalog,
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        var plan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext(),
            "plan-approval-1",
            fixture.Source.Actor.Subject);
        var confirmedPlan = plan.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            plan.State.PendingGate!.RequestId,
            fixture.Source.Actor.Subject,
            fixture.Intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var pending = CoordinatorDecisionFlow.RequestApproval(
            confirmedPlan,
            fixture.Intent.ApprovalRequestId,
            fixture.Intent.OwnerIntentId,
            fixture.Source.Actor.Subject);
        var approved = pending.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            pending.Value!.RequestId,
            fixture.Source.Actor.Subject,
            fixture.Intent.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null));

        Assert.True(approved.IsAccepted);
        var approvalStateVersion = fixture.Intent.DecisionStateVersion + 1;
        MafMergeContract.RequireCurrentSource(
            fixture.Intent,
            fixture.Source.Actor,
            fixture.Intent.Identity,
            "tenant-1",
            approvalStateVersion,
            approved.State,
            fixture.Intent.ExecutionFence,
            fixture.Intent.AcceptedSelectionHash,
            fixture.Source.Pin,
            approvalStateVersion);

        var missingReceipt = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.RequireCurrentSource(
                fixture.Intent,
                fixture.Source.Actor,
                fixture.Intent.Identity,
                "tenant-1",
                fixture.Intent.DecisionStateVersion + 1,
                pending.State,
                fixture.Intent.ExecutionFence,
                fixture.Intent.AcceptedSelectionHash,
                fixture.Source.Pin,
                approvalStateVersion));
        Assert.Equal("source_control_run_binding_changed", missingReceipt.Code);

        var unrelatedDecisionAdvance = Assert.Throws<CoordinationException>(() =>
            MafMergeContract.RequireCurrentSource(
                fixture.Intent,
                fixture.Source.Actor,
                fixture.Intent.Identity,
                "tenant-1",
                approvalStateVersion + 1,
                approved.State,
                fixture.Intent.ExecutionFence,
                fixture.Intent.AcceptedSelectionHash,
                fixture.Source.Pin,
                approvalStateVersion));
        Assert.Equal("source_control_run_binding_changed", unrelatedDecisionAdvance.Code);
    }

    [Fact]
    public void CompletedPullRequestReceiptSurvivesMergeApprovalWithoutRelaxingSourceFences()
    {
        var source = MafBuildTestSourceInputTests.Fixture().Run;
        var workspace = new SourceControlPullRequestWorkspaceLocator(
            "workspace",
            Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            new string('a', 40),
            "feature/agent-change",
            new string('c', 40),
            source.Pin.DefaultBranch);
        var intent = MafOpenPullRequestContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            source.Identity,
            "open-pr-checkpoint",
            2,
            "plan-1",
            "publish",
            3,
            source.Pin.AcceptedRun.Fence,
            source.Pin.AcceptedRun.AcceptedSelectionHash,
            source.Pin,
            workspace,
            1);
        var initialProgress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                intent.StepId, MafExecutionTaskStatus.Pending)
        };
        var initial = new MafExecutionCheckpoint(1, intent.WorkPlanId, intent.DecisionStateVersion, initialProgress);
        var running = CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(
            new(new CheckpointInfo(source.Identity.SessionId, "initial"), initial), intent)!;
        var receipt = MafOpenPullRequestContract.CreateReceipt(intent, new SourceControlPullRequest(
            42,
            new Uri("https://github.com/octo/repo/pull/42"),
            "open",
            intent.Request.HeadBranch,
            intent.Request.ExpectedHeadSha,
            intent.Request.BaseBranch,
            intent.Request.ExpectedBaseSha,
            false,
            SourceControlPullRequestDisposition.Created));
        var completed = CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(
            new(new CheckpointInfo(source.Identity.SessionId, "open-pr-intent"), running), intent, receipt)!;

        CoordinationEndpoints.RequireOpenPullRequestDispatchSource(
            intent,
            completed,
            source.Actor,
            source.Identity,
            "tenant-1",
            intent.DecisionStateVersion + 1,
            intent.ExecutionFence,
            intent.AcceptedSelectionHash,
            source.Pin);

        var staleSelection = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireOpenPullRequestDispatchSource(
                intent,
                completed,
                source.Actor,
                source.Identity,
                "tenant-1",
                intent.DecisionStateVersion + 1,
                intent.ExecutionFence,
                new string('b', 64),
                source.Pin));
        Assert.Equal("source_control_run_binding_changed", staleSelection.Code);

        var staleRunningOperation = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireOpenPullRequestDispatchSource(
                intent,
                running,
                source.Actor,
                source.Identity,
                "tenant-1",
                intent.DecisionStateVersion + 1,
                intent.ExecutionFence,
                intent.AcceptedSelectionHash,
                source.Pin));
        Assert.Equal("source_control_run_binding_changed", staleRunningOperation.Code);
    }

    [Fact]
    public void MergeInputRequiresCompleteOptionalPullRequestBinding()
    {
        Assert.True(MafMergeContract.IsValidInput(
            new(SourceControlMergeMethod.Rebase)));
        Assert.True(MafMergeContract.IsValidInput(
            new(SourceControlMergeMethod.Rebase, 42, new string('a', 40))));
        Assert.False(MafMergeContract.IsValidInput(null));
        Assert.False(MafMergeContract.IsValidInput(
            new(SourceControlMergeMethod.Rebase, 42)));
        Assert.False(MafMergeContract.IsValidInput(
            new(SourceControlMergeMethod.Rebase, ExpectedHeadSha: new string('a', 40))));
        Assert.False(MafMergeContract.IsValidInput(
            new(SourceControlMergeMethod.Rebase, 42, "not-a-git-sha")));
    }

    [Fact]
    public async Task OwnerClientUsesThePersistedMergeIntentRoutesAndExactExpectedHead()
    {
        var fixture = CreateFixture();
        var handler = new MergeRouteHandler(fixture.Intent);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var owner = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
            "tenant-1");
        var request = new PrepareSourceControlMergeIntentRequest(
            fixture.Intent.IdempotencyKey,
            fixture.Intent.DecisionStateVersion,
            fixture.Intent.WorkflowId,
            fixture.Intent.DefinitionRevision,
            fixture.Intent.WorkPlanId,
            fixture.Intent.StepId,
            fixture.Intent.PullRequestNumber,
            fixture.Intent.Method)
        {
            ExpectedHeadSha = fixture.Intent.ExpectedHeadSha
        };

        var prepared = await owner.PrepareMergeIntentAsync(
            actor, fixture.Intent.Identity, request, CancellationToken.None);
        var executed = await owner.ExecuteMergeIntentAsync(
            actor, fixture.Intent.Identity, fixture.Intent.OwnerIntentId, CancellationToken.None);

        Assert.Equal("approval_pending", prepared.State);
        Assert.Equal("merged", executed.State);
        Assert.Equal(fixture.Intent.IdempotencyKey, handler.IntentId);
        Assert.Equal(fixture.Intent.ExpectedHeadSha, handler.ExpectedHeadSha);
        Assert.EndsWith("/merge-intents", handler.Paths[0], StringComparison.Ordinal);
        Assert.EndsWith(
            "/merge-intents/" + Uri.EscapeDataString(fixture.Intent.OwnerIntentId) + "/execute",
            handler.Paths[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerClientAcceptsThePersistedMergeIntentViewShape()
    {
        var fixture = CreateFixture();
        using var client = new HttpClient(new MergeIntentViewHandler(fixture.Intent))
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        var owner = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
            "tenant-1");
        var request = new PrepareSourceControlMergeIntentRequest(
            fixture.Intent.IdempotencyKey,
            fixture.Intent.DecisionStateVersion,
            fixture.Intent.WorkflowId,
            fixture.Intent.DefinitionRevision,
            fixture.Intent.WorkPlanId,
            fixture.Intent.StepId,
            fixture.Intent.PullRequestNumber,
            fixture.Intent.Method)
        {
            ExpectedHeadSha = fixture.Intent.ExpectedHeadSha
        };

        var prepared = await owner.PrepareMergeIntentAsync(
            actor, fixture.Intent.Identity, request, CancellationToken.None);

        Assert.Equal("approved", prepared.State);
        Assert.Equal(fixture.Intent.ExpectedHeadSha, prepared.HeadSha);
        Assert.Equal(fixture.Intent.Repository.FullName, prepared.Repository);
        Assert.Equal(fixture.Intent.PullRequestNumber, prepared.PullRequestNumber);
        Assert.Equal(fixture.Intent.Method, prepared.Method);
        Assert.Equal(fixture.Intent.DecisionStateVersion + 1, prepared.ApprovalStateVersion);
        Assert.True(prepared.HasGrantReference);
    }

    private static FixtureState CreateFixture()
    {
        // Use the shared accepted-run fixture so the test intent carries a realistic owner pin and selection hash.
        var source = MafBuildTestSourceInputTests.Fixture().Run;
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("merge", 1, ["implement"], WorkflowPlatformGate.Merge));
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(workflow),
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext()).Value!;
        var selectionHash = source.Pin.AcceptedRun.AcceptedSelectionHash;
        var intent = MafMergeContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            source.Identity,
            "merge-intent",
            2,
            plan.Plan.Id,
            workflow.Id,
            workflow.Revision,
            "merge",
            1,
            source.Pin.AcceptedRun.Fence,
            selectionHash,
            source.Pin,
            42,
            new string('a', 40),
            SourceControlMergeMethod.Rebase);
        var progress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "merge", MafExecutionTaskStatus.Pending)
        };
        return new(
            source,
            workflow,
            plan,
            intent,
            new(
                new CheckpointInfo(source.Identity.SessionId, "initial"),
                new(1, plan.Plan.Id, intent.DecisionStateVersion, progress)));
    }

    private static MafMergeOwnerIntent OwnerIntent(
        MafExecutionMergeIntent intent,
        string state) =>
        new(
            intent.OwnerIntentId,
            state,
            intent.ApprovalRequestId,
            intent.Repository.FullName,
            intent.PullRequestNumber,
            intent.ExpectedHeadSha,
            intent.Method,
            Guid.Parse("daab7d19-85f9-4e68-9bc0-7d3533f355e6"),
            intent.DecisionStateVersion,
            HasGrantReference: true,
            MergeSha: state == "merged" ? new string('b', 40) : null,
            FailureCode: null);

    private sealed record FixtureState(
        SourceControlRunContext Source,
        WorkflowDefinition Workflow,
        WorkPlanSnapshot Plan,
        MafExecutionMergeIntent Intent,
        MafExecutionCheckpointSnapshot Checkpoint);

    private sealed class MergeRouteHandler(MafExecutionMergeIntent intent) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal string? IntentId { get; private set; }
        internal string? ExpectedHeadSha { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Return the route-specific owner contracts while recording the expected-head request field.
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            string payload;
            if (path.EndsWith("/merge-intents", StringComparison.Ordinal))
            {
                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                IntentId = body.RootElement.GetProperty("idempotencyKey").GetString();
                ExpectedHeadSha = body.RootElement.GetProperty("expectedHeadSha").GetString();
                payload = JsonSerializer.Serialize(new
                {
                    intentId = intent.OwnerIntentId,
                    approvalRequestId = intent.ApprovalRequestId,
                    state = "approval_pending",
                    decisionId = Guid.Parse("daab7d19-85f9-4e68-9bc0-7d3533f355e6"),
                    stateVersion = intent.DecisionStateVersion + 1,
                    pendingGate = new
                    {
                        requestId = intent.ApprovalRequestId,
                        subjectId = intent.OwnerIntentId
                    }
                });
                return Response(payload, request, HttpStatusCode.Accepted);
            }

            Assert.EndsWith("/merge-intents/" + intent.OwnerIntentId + "/execute",
                path, StringComparison.Ordinal);
            payload = JsonSerializer.Serialize(new
            {
                intentId = intent.OwnerIntentId,
                state = "merged",
                mergeSha = new string('b', 40),
                failureCode = (string?)null
            });
            return Response(payload, request);
        }

        private static HttpResponseMessage Response(
            string payload,
            HttpRequestMessage request,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            // The runtime transport requires a no-store response bound to the exact request URI.
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }
    }

    private sealed class MergeIntentViewHandler(MafExecutionMergeIntent intent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Match the full persisted intent view, including fields absent from the approval-pending response.
            Assert.EndsWith("/merge-intents", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var payload = JsonSerializer.Serialize(new
            {
                intentId = intent.OwnerIntentId,
                approvalRequestId = intent.ApprovalRequestId,
                state = "approved",
                repository = intent.Repository.FullName,
                pullRequestNumber = intent.PullRequestNumber,
                headBranch = "feature",
                headSha = intent.ExpectedHeadSha,
                baseBranch = "main",
                baseSha = new string('c', 40),
                method = intent.Method,
                approvalDecisionId = Guid.Parse("daab7d19-85f9-4e68-9bc0-7d3533f355e6"),
                approvalStateVersion = intent.DecisionStateVersion + 1,
                grantReference = new { kind = "merge-approval" },
                mergeSha = (string?)null,
                lastFailureCode = (string?)null
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
