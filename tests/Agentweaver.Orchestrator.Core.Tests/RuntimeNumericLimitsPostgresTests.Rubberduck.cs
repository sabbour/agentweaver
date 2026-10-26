using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class RuntimeNumericLimitsPostgresTests
{
    [Fact]
    public async Task CoordinatorRootRubberduckAdmissionRequiresItsConfirmedPlanIntentAndOwner()
    {
        var (registration, intent, stateVersion) = await PrepareConfirmedRootRubberduckAsync();
        var prompt = MafRubberduckReviewContract.CreatePrompt(intent);
        var promptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt));
        var rootContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", _actor.Subject),
                new System.Security.Claims.Claim("aud", _options.Audience),
                new System.Security.Claims.Claim("aud", _options.ProjectsAudience),
                new System.Security.Claims.Claim("project_id", _root.ProjectId),
                new System.Security.Claims.Claim("run_id", _root.RunId),
                new System.Security.Claims.Claim("scope", "api.read projects.orchestrator")
            ], "controlled-owner"))
        };
        rootContext.Request.Headers.Authorization = "******";
        rootContext.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        var grants = GrantStore();

        var staleEvidenceFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.AdmitCoordinatorRootModelTurnAsync(
                rootContext,
                registration,
                intent.OperationId,
                RuntimeContractValidation.Hash("unrelated prompt"u8),
                stateVersion,
                default));
        Assert.Equal("runtime_action_owner_unavailable", staleEvidenceFailure.Code);

        var staleOwner = registration with { RuntimeInstanceId = Guid.NewGuid() };
        var staleOwnerFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.AdmitCoordinatorRootModelTurnAsync(
                rootContext, staleOwner, intent.OperationId, promptHash, stateVersion, default));
        Assert.Equal("runtime_action_owner_unavailable", staleOwnerFailure.Code);
        Assert.Equal(0, await CountRuntimeGrantsAsync());

        await grants.AdmitCoordinatorRootModelTurnAsync(
            rootContext, registration, intent.OperationId, promptHash, stateVersion, default);
        var request = new RuntimeActionRequest(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            registration.Binding.ExecutionFence,
            intent.OperationId,
            "model.turn",
            promptHash)
        {
            DispatchId = intent.OperationId
        };
        _registrations[0] = registration;
        foreach (var actionId in new[] { "tool.read", "tool.write" })
        {
            var toolRequest = request with
            {
                ActionId = actionId,
                IsToolInvocation = true,
                DispatchId = null
            };
            var toolFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                grants.IssueRuntimeActionGrantAsync(
                    _context, registration, toolRequest, default, reserveToolInvocation: true));
            Assert.Equal("runtime_action_owner_unavailable", toolFailure.Code);
            Assert.Equal(0, await CountRuntimeGrantsAsync());
        }

        var grant = await grants.IssueRuntimeActionGrantAsync(
            _context, registration, request, default);
        Assert.Equal(ExecutableActionGrantLookupStatus.Current,
            (await grants.GetCurrentAsync(grant)).Status);

        var unrelated = request with
        {
            EventId = Guid.NewGuid(),
            DispatchId = Guid.NewGuid()
        };
        var unrelatedFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.IssueRuntimeActionGrantAsync(_context, registration, unrelated, default));
        Assert.Equal("runtime_action_owner_unavailable", unrelatedFailure.Code);
        Assert.Equal(1, await CountRuntimeGrantsAsync());
    }

    private async Task<(RuntimeRegistration Registration,
        MafExecutionRubberduckIntent Intent, long DecisionStateVersion)>
        PrepareConfirmedRootRubberduckAsync()
    {
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform(
                "independent-review", 1, ["implement"], WorkflowPlatformGate.IndependentReview),
            WorkflowTestData.Platform(
                "rubberduck", 2, ["independent-review"], WorkflowPlatformGate.RubberDuck),
            WorkflowTestData.Platform(
                "merge", 3, ["rubberduck"], WorkflowPlatformGate.Merge));
        var selectionResolution = await _runSelectionContexts.ResolveForPlanAsync(
            _selection.Selection, _registrations[0].Binding.ExecutionFence, CancellationToken.None);
        var selectionContext = selectionResolution.Context;
        if (selectionResolution.PendingBinding is { } pendingBinding)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _runSelectionContexts.PersistBindingAsync(
                connection,
                transaction,
                _selection.Selection,
                _registrations[0].Binding.ExecutionFence,
                pendingBinding,
                CancellationToken.None);
            await transaction.CommitAsync();
        }
        var proposed = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(_registrations[0].Binding.ExecutionFence),
            new CoordinatorOutcomeSpecification(
                "rubberduck-outcome",
                "Implement and review the accepted change.",
                "The implementation is recorded and reviewed.",
                "Complete the workflow steps.",
                "The workspace and project are initialized.",
                []),
            "rubberduck-outcome-confirmation",
            _actor.Subject);
        Assert.True(proposed.IsSuccess);
        var outcomeAnswer = proposed.State!.ApplyGateAnswer(new(
            proposed.State.PendingGate!.RequestId,
            _actor.Subject,
            proposed.State.Fence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(outcomeAnswer.AuthorizesTransition);
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            outcomeAnswer.State,
            new AuthorizedWorkflowCatalog(workflow, []),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        Assert.True(selected.IsSuccess);
        var proposedPlan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("first", modelSelection: "model:selected")),
            selectionContext,
            "rubberduck-plan-confirmation",
            _actor.Subject);
        Assert.True(proposedPlan.IsSuccess);
        var planAnswer = proposedPlan.State!.ApplyGateAnswer(new(
            proposedPlan.State.PendingGate!.RequestId,
            _actor.Subject,
            proposedPlan.State.Fence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(planAnswer.AuthorizesTransition);
        var acceptedPlan = planAnswer.State.ConfirmedWorkPlan!;

        var current = await _decisions.ReadCurrentAsync(
            _actor, _root, _selection, CancellationToken.None);
        var persisted = await _decisions.PersistTransitionAsync(
            _actor,
            _root,
            _selection,
            current.StateVersion,
            "rubberduck-confirmed-plan",
            "rubberduck-confirmed-plan",
            "work_plan.confirmed",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(acceptedPlan.Plan),
            planAnswer.State,
            true,
            [],
            selectionContext,
            null,
            acceptedPlan,
            CancellationToken.None);
        Assert.True(persisted.TransitionAccepted);
        Assert.True(persisted.State.CanDispatch);

        var registration = _registrations[0] with
        {
            RuntimeInstanceId = Guid.NewGuid(),
            Revision = 1,
            State = RuntimeRegistrationState.Active,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Binding = _registrations[0].Binding with
            {
                SessionId = _root.SessionId,
                AgentId = RuntimeActionContract.CoordinatorRootStepId,
                WorkflowStepId = null,
                TurnId = "rubberduck-root-turn",
                AcceptedSelectionHash = RuntimeContractValidation.Hash(
                    Encoding.UTF8.GetBytes(_selection.Selection.Snapshot.GetRawText()))
            }
        };
        var plan = acceptedPlan;
        var checkpointId = Guid.NewGuid().ToString("N");
        // InitializeAsync already started this root binding's single checkpoint chain, so the
        // reviewed state must be its next revision. The earlier pending dispatches are carried
        // forward unchanged because a persisted dispatch cannot be dropped without a result.
        var previous = _checkpoint.State;
        var reviewedRevision = checked(previous.Revision + 1);
        var progress = previous.Progress with
        {
            WorkItems = previous.Progress.WorkItems.SetItem("first", MafExecutionTaskStatus.Succeeded)
        };
        var initialState = previous with
        {
            Revision = reviewedRevision,
            WorkPlanId = plan.Plan.Id,
            DecisionStateVersion = persisted.StateVersion,
            Progress = progress,
            Results = previous.Results.SetItem("first", "Recorded implementation output.")
        };
        var reviewStep = workflow.Steps.Single(
            step => step.PlatformGate == WorkflowPlatformGate.IndependentReview);
        var reviewIntent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(),
            _root,
            checkpointId,
            reviewedRevision,
            plan,
            reviewStep,
            initialState,
            persisted.StateVersion,
            _actor.Subject,
            registration.Binding.ExecutionFence,
            registration.Binding.AcceptedSelectionHash);
        var reviewReceipt = new MafReviewTerminalReceipt(
            reviewIntent.OperationId,
            reviewIntent.ApprovalRequestId,
            reviewIntent.EvidenceHash,
            _actor.Subject,
            registration.Binding.ExecutionFence,
            CoordinatorGateChoices.Approve,
            "approved");
        var reviewedState = initialState with
        {
            Progress = progress with
            {
                NonModelSteps = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(reviewStep.Id, MafExecutionTaskStatus.Succeeded)
            },
            ReviewIntents = ImmutableDictionary<string, MafExecutionReviewIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewIntent),
            ReviewReceipts = ImmutableDictionary<string, MafReviewTerminalReceipt>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(reviewStep.Id, reviewReceipt)
        };
        var checkpointBinding = new MafCheckpointBinding(
            _root,
            _actor,
            registration.Binding.ExecutionFence,
            MafExecutionCheckpointStore.CurrentSdkVersion,
            registration.Binding.ModelSelectionReference!,
            null,
            MafExecutionCheckpointContract.StoreName);
        var execution = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null),
            checkpointBinding);
        var initial = await execution.AppendAsync(
            checkpointId, _checkpoint.Info, reviewedState, CancellationToken.None);
        var rubberduckStep = workflow.Steps.Single(
            step => step.PlatformGate == WorkflowPlatformGate.RubberDuck);
        var intent = MafRubberduckReviewContract.CreateIntent(
            Guid.NewGuid(),
            _root,
            "rubberduck-intent",
            checked(reviewedRevision + 1),
            plan,
            rubberduckStep,
            initial.State,
            persisted.StateVersion,
            _actor.Subject,
            registration.Binding.ExecutionFence,
            registration.Binding.AcceptedSelectionHash,
            registration);
        var running = CoordinationEndpoints.PrepareRubberduckCheckpoint(initial, intent)!;
        _ = await execution.AppendAsync(
            "rubberduck-intent", initial.Info, running, CancellationToken.None);
        return (registration, intent, persisted.StateVersion);
    }

    private sealed class RuntimeNumericSandboxResourceProvider : ICoordinatorSandboxResourceProvider
    {
        public string ProviderId => RubberduckSandboxProviderId;

        public ImmutableArray<string> IsolationChoices => ["worktree", "sandboxed"];

        public Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
            ProviderCandidate candidate,
            EffectiveRunSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Seam != ProviderSeam.Sandbox || candidate.ProviderId != ProviderId)
                return Task.FromResult(ProviderResult<ResourceNegotiation>.Failure(
                    ProviderErrorCode.ProviderSeamMismatch,
                    "The runtime numeric fixture selected an unexpected Sandbox provider."));
            return Task.FromResult(ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Sandbox, ProviderId, RubberduckSandboxResourceId, 1),
                candidate.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal))));
        }
    }
}
