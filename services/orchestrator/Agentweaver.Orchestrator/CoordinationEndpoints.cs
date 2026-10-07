using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public static class CoordinationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCoordinationEndpoints(this IEndpointRouteBuilder app)
    {
        var coordination = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination")
            .RequireAuthorization();
        coordination.MapPost("/root", AcceptRootAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/outcome",
            ProposeCoordinatorOutcomeAsync);
        coordination.MapGet("/status", ReadOwnerRunStatusAsync);
        coordination.MapPost("/recovery", RecoverRunExecutionAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/actions/propose_outcome_spec",
            ProposeCoordinatorOutcomeAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/actions/select_workflow",
            SelectCoordinatorWorkflowAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/actions/propose_work_plan",
            ProposeCoordinatorWorkPlanAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/actions/revise_work_plan",
            ReviseCoordinatorWorkPlanAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/actions/request_assembly",
            RequestCoordinatorAssemblyAsync);
        coordination.MapGet(
            "/sessions/{sessionId}/decisions",
            ReadCoordinatorDecisionStateAsync);
        coordination.MapGet("/sessions/{sessionId}/tree", ReadSessionTreeAsync);
        coordination.MapGet("/sessions/{sessionId}/status", ReadSessionStatusAsync);
        coordination.MapPost("/sessions/{sessionId}/turn-failure", ReportRunFailureAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/gates/{requestId}/answer",
            AnswerCoordinatorGateAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/questions",
            AskCoordinatorQuestionAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/outcome/questions/next",
            AskNextOutcomeClarifyingQuestionAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/approvals",
            RequestCoordinatorApprovalAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/gates/{requestId}/acknowledge",
            AcknowledgeCoordinatorGateAsync);
        coordination.MapPost("/sessions/{parentSessionId}/children", RegisterChildAsync);
        coordination.MapPost("/sessions/{parentSessionId}/spawn", SpawnSessionAsync);
        coordination.MapPost("/sessions/{sessionId}/fork", ForkSessionAsync);
        coordination.MapPost("/sessions/{sessionId}/detach", DetachSessionAsync);
        coordination.MapPost(
            "/sessions/{parentSessionId}/children/{childSessionId}/archive",
            ArchiveChildAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/idle-subscriptions",
            SubscribeToIdleAsync);
        coordination.MapPost("/sessions/{sessionId}/steering", SteerSessionAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/gates/{requestId}/approve",
            ApproveCoordinatorGateAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/decisions/gates/{requestId}/reject",
            RejectCoordinatorGateAsync);
        coordination.MapPost("/sessions/{sessionId}/messages", SendMessageAsync);
        coordination.MapPost("/sessions/{sessionId}/turn-boundary", AdvanceTurnBoundaryAsync);
        coordination.MapPost("/sessions/{sessionId}/turn-completion", FinishTurnAsync);
        coordination.MapGet("/sessions/{sessionId}/notifications", ReadNotificationsAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/notifications/{notificationId:guid}/acknowledge",
            AcknowledgeNotificationAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/messages/{messageId:guid}/acknowledge",
            AcknowledgeMessageAsync);
        coordination.MapGet(
            "/policy-evaluations/{receiptId:guid}",
            ReadPolicyEvaluationReceiptAsync);

        app.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/coordination/message-route",
            ValidateMessageRouteAsync).RequireAuthorization();
        app.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/owner-binding",
            GetSessionBindingAsync).RequireAuthorization();
        app.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/fork-admission",
            ValidateSessionForkAdmissionAsync).RequireAuthorization();
        app.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-owner-context",
            ReadRuntimeOwnerContextAsync).RequireAuthorization();
        return app;
    }

    private static Task<IResult> ReadCoordinatorDecisionStateAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new CoordinatorDecisionStateView(
                current.StateVersion,
                current.State.Fence,
                current.State.OutcomeConfirmed,
                current.State.WorkflowConfirmed,
                current.State.CanDecompose,
                current.State.CanDispatch,
                current.State.PendingGate));
        }, cancellationToken);

    private static Task<IResult> ReadSessionTreeAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var tree = await store.ReadSessionTreeAsync(
                actor, new SessionIdentity(projectId, runId, sessionId), cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Ok(tree);
        }, cancellationToken);

    private static Task<IResult> ReadSessionStatusAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var status = await store.ReadSessionStatusAsync(actor, identity, cancellationToken)
                .ConfigureAwait(false);
            if (status.Kind == CoordinationSessionKind.Coordinator)
            {
                var decision = await decisions.ReadCurrentAsync(
                    actor, identity, selection, cancellationToken).ConfigureAwait(false);
                if (decision.State.Fence != status.ExecutionFence)
                    throw new CoordinationException(
                        "session_status_fence_conflict", StatusCodes.Status409Conflict);
                status = status with
                {
                    Blockers = decision.State.PendingGate is { } gate
                        ? [ToStatusBlocker(gate)]
                        : []
                };
            }
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        }, cancellationToken);

    private static Task<IResult> ReadRuntimeOwnerContextAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var owner = await store.ReadRuntimeOwnerStateAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            var root = new SessionIdentity(projectId, runId, owner.RootSessionId);
            var decision = await decisions.ReadCurrentAsync(
                actor, root, selection, cancellationToken).ConfigureAwait(false);
            if (decision.State.Fence != owner.ExecutionFence ||
                decision.SelectionHash != owner.AcceptedSelectionHash ||
                !decision.State.CanDispatch ||
                decision.State.ConfirmedWorkPlan is not { } confirmedPlan)
                throw new CoordinationException(
                    "runtime_owner_context_unavailable", StatusCodes.Status409Conflict);

            var selectionContext = await runSelectionContexts.ReadAsync(
                    selection.Selection, owner.ExecutionFence, cancellationToken).ConfigureAwait(false)
                ?? CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Selection.Snapshot);
            var validatedPlan = WorkPlanValidator.ValidateAndSnapshot(
                confirmedPlan.Workflow, confirmedPlan.Plan, selectionContext);
            if (!validatedPlan.IsValid || validatedPlan.Value is null)
                throw new CoordinationException(
                    "runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            var workPlanItem = validatedPlan.Value.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, owner.WorkPlanItemId, StringComparison.Ordinal));
            if (workPlanItem is null ||
                string.IsNullOrWhiteSpace(workPlanItem.AgentId) ||
                string.IsNullOrWhiteSpace(workPlanItem.ModelSelectionReference))
                throw new CoordinationException(
                    "runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var currentOwner = await store.ReadRuntimeOwnerStateAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            if (currentOwner != owner)
                throw new CoordinationException(
                    "runtime_owner_context_stale", StatusCodes.Status409Conflict);
            var currentDecision = await decisions.ReadCurrentAsync(
                actor, root, selection, cancellationToken).ConfigureAwait(false);
            var currentWorkPlanItem = currentDecision.State.ConfirmedWorkPlan?.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, owner.WorkPlanItemId, StringComparison.Ordinal));
            if (currentDecision.StateVersion != decision.StateVersion ||
                currentDecision.SelectionHash != decision.SelectionHash ||
                currentDecision.State.Fence != owner.ExecutionFence ||
                !currentDecision.State.CanDispatch ||
                currentWorkPlanItem is null ||
                !string.Equals(currentWorkPlanItem.AgentId, workPlanItem.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    currentWorkPlanItem.ModelSelectionReference,
                    workPlanItem.ModelSelectionReference,
                    StringComparison.Ordinal))
                throw new CoordinationException(
                    "runtime_owner_context_stale", StatusCodes.Status409Conflict);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new RuntimeOwnerContext(
                1,
                actor.Issuer,
                actor.Subject,
                owner.TenantId,
                projectId,
                runId,
                sessionId,
                workPlanItem.AgentId,
                workPlanItem.ModelSelectionReference,
                owner.RuntimeTurnId,
                selection.Selection.ProjectRevision,
                selection.Selection.ProjectConfigurationRevision,
                selection.Selection.PlatformRuntimeRevision,
                selection.Selection.ContextRevision,
                owner.AcceptedSelectionHash.ToLowerInvariant(),
                owner.ExecutionFence,
                owner.LogicalTurnOrdinal,
                owner.StateVersion,
                decision.StateVersion));
        }, cancellationToken);

    private static SessionStatusBlocker ToStatusBlocker(CoordinatorGateRequest gate)
    {
        var kind = gate.Kind switch
        {
            CoordinatorGateKind.Question => CoordinationBlockerKind.AwaitingInput,
            CoordinatorGateKind.Approval => CoordinationBlockerKind.AwaitingApproval,
            CoordinatorGateKind.OutcomeConfirmation => CoordinationBlockerKind.AwaitingOutcomeConfirmation,
            CoordinatorGateKind.GeneratedWorkflowConfirmation or
            CoordinatorGateKind.WorkPlanConfirmation or
            CoordinatorGateKind.ScopeChangeConfirmation => CoordinationBlockerKind.AwaitingPlanApproval,
            _ => throw new CoordinationException(
                "coordinator_gate_state_unavailable", StatusCodes.Status503ServiceUnavailable)
        };
        return new SessionStatusBlocker(
            kind, gate.RequestId, gate.AllowedChoices, gate.AllowsFreeform, gate.Prompt);
    }

    private static Task<IResult> SpawnSessionAsync(
        string projectId,
        string runId,
        string parentSessionId,
        SpawnSessionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            CoordinationOwnerStore.ValidateSpawnRequest(request);
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var parent = new SessionIdentity(projectId, runId, parentSessionId);
            var parentStatus = await store.ReadSessionStatusAsync(actor, parent, cancellationToken)
                .ConfigureAwait(false);
            if (request.Kind == CoordinationSessionKind.ChildRun &&
                parentStatus.Kind != CoordinationSessionKind.OperatorChat)
                throw new CoordinationException(
                    "child_run_spawn_requires_operator_chat", StatusCodes.Status403Forbidden);

            ConfirmedWorkPlanItemAssociation? workPlanItemAssociation = null;
            if (request.WorkPlanItemId is { } workPlanItemId)
            {
                var root = new SessionIdentity(projectId, runId, parentStatus.RootSessionId);
                var decision = await decisions.ReadCurrentAsync(
                    actor, root, selection, cancellationToken).ConfigureAwait(false);
                var isConfirmedAndDispatchable = decision.State.CanDispatch &&
                    decision.State.ConfirmedWorkPlan?.Plan.Items.Any(item =>
                        string.Equals(item.Id, workPlanItemId, StringComparison.Ordinal)) == true;
                if (!isConfirmedAndDispatchable)
                    throw new CoordinationException(
                        "session_work_plan_item_unavailable", StatusCodes.Status409Conflict);
                workPlanItemAssociation = new ConfirmedWorkPlanItemAssociation(
                    workPlanItemId, decision.StateVersion, decision.SelectionHash);
            }

            var childIdentity = new SessionIdentity(projectId, runId, request.SessionId);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            await events.EnsureSessionAsync(context, childIdentity, cancellationToken).ConfigureAwait(false);
            var spawned = await store.SpawnSessionAsync(
                actor,
                parent,
                request,
                CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot),
                CoordinatorWorkflowCatalog.ReadMaxConcurrentChildren(selection.Selection.Snapshot),
                cancellationToken,
                workPlanItemAssociation,
                currentCancellationToken => RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, currentCancellationToken))
                .ConfigureAwait(false);
            return Results.Accepted(value: spawned);
        }, cancellationToken);

    private static Task<IResult> ForkSessionAsync(
        string projectId,
        string runId,
        string sessionId,
        CoordinationSessionForkRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            CoordinationOwnerStore.ValidateForkRequest(request);
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var source = new SessionIdentity(projectId, runId, sessionId);
            var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var maxChildren = CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot);
            var maxConcurrentChildren =
                CoordinatorWorkflowCatalog.ReadMaxConcurrentChildren(selection.Selection.Snapshot);
            var prepared = await store.PrepareSessionForkAsync(
                actor,
                source,
                request,
                selectionHash,
                maxChildren,
                maxConcurrentChildren,
                cancellationToken).ConfigureAwait(false);
            if (prepared.RegistrationState == CoordinationForkRegistrationState.Registered)
                return Results.Ok(prepared);
            if (prepared.RegistrationState == CoordinationForkRegistrationState.Unregistered)
                return Results.Conflict(prepared);

            SessionForkResult eventsFork;
            try
            {
                eventsFork = await events.ForkFromExplicitEventAsync(
                    context,
                    source,
                    new SessionForkRequest(
                        request.TargetSessionId,
                        request.SourceEventId,
                        request.SourceCursor,
                        request.IdempotencyKey),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (
                exception.Code is "events_session_fork_denied" or "events_session_fork_conflict")
            {
                var unregistered = await store.FinalizeSessionForkAdmissionFailureAsync(
                    actor, source, request, selectionHash, cancellationToken).ConfigureAwait(false);
                return Results.Conflict(unregistered);
            }
            var currentSelection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var selectionIsCurrent = AuthorizedSelectionIsUnchanged(projectId, selection, currentSelection);
            var currentSelectionHash = CoordinationOwnerStore.HashSelection(currentSelection.Selection);
            var completed = await store.CompleteSessionForkAsync(
                actor,
                source,
                request,
                selectionHash,
                currentSelectionHash,
                selectionIsCurrent,
                eventsFork,
                maxChildren,
                maxConcurrentChildren,
                prepared.IsDuplicate,
                cancellationToken).ConfigureAwait(false);
            return completed.RegistrationState == CoordinationForkRegistrationState.Registered
                ? Results.Json(completed, statusCode: completed.IsDuplicate
                    ? StatusCodes.Status200OK : StatusCodes.Status201Created)
                : Results.Conflict(completed);
        }, cancellationToken);

    private static Task<IResult> DetachSessionAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionTreeCommandRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var detached = await store.DetachSessionAsync(
                actor, new SessionIdentity(projectId, runId, sessionId), request, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(detached);
        }, cancellationToken);

    private static Task<IResult> ArchiveChildAsync(
        string projectId,
        string runId,
        string parentSessionId,
        string childSessionId,
        SessionTreeCommandRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var archived = await store.ArchiveChildAsync(
                actor,
                new SessionIdentity(projectId, runId, parentSessionId),
                childSessionId,
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(archived);
        }, cancellationToken);

    private static Task<IResult> SubscribeToIdleAsync(
        string projectId,
        string runId,
        string sessionId,
        SubscribeToIdleRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var subscription = await store.SubscribeToIdleAsync(
                actor, new SessionIdentity(projectId, runId, sessionId), request, cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(value: subscription);
        }, cancellationToken);

    private static Task<IResult> SteerSessionAsync(
        string projectId,
        string runId,
        string sessionId,
        SteerSessionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            if (!Enum.IsDefined(request.Action) || request.Instruction.ValueKind != JsonValueKind.Object)
                throw new CoordinationException("steering_request_invalid", StatusCodes.Status400BadRequest);
            var action = request.Action switch
            {
                CoordinationSteeringAction.Stop => "stop",
                CoordinationSteeringAction.Redirect => "redirect",
                CoordinationSteeringAction.Amend => "amend",
                _ => throw new CoordinationException(
                    "steering_request_invalid", StatusCodes.Status400BadRequest)
            };
            var payload = JsonSerializer.SerializeToElement(new
            {
                action,
                instruction = request.Instruction
            }, JsonOptions);
            return await SendMessageAsync(
                projectId,
                runId,
                sessionId,
                new CoordinationMessageRequest(
                    request.RecipientSessionId,
                    request.IdempotencyKey,
                    request.DeliveryMode,
                    AddressedMessagePurpose.Steering,
                    AddressedMessageKind.Steering,
                    payload,
                    UserQuote: request.UserQuote,
                    CoordinatorInstructions: request.CoordinatorInstructions),
                context,
                options,
                projects,
                store,
                events,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static Task<IResult> ApproveCoordinatorGateAsync(
        string projectId,
        string runId,
        string sessionId,
        string requestId,
        ResolveCoordinatorGateRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        AnswerCoordinatorGateAsync(
            projectId,
            runId,
            sessionId,
            requestId,
            new AnswerCoordinatorGateRequest(
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                CoordinatorGateChoices.Approve,
                null),
            context,
            options,
            projects,
            decisions,
            cancellationToken);

    private static Task<IResult> RejectCoordinatorGateAsync(
        string projectId,
        string runId,
        string sessionId,
        string requestId,
        ResolveCoordinatorGateRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        AnswerCoordinatorGateAsync(
            projectId,
            runId,
            sessionId,
            requestId,
            new AnswerCoordinatorGateRequest(
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                CoordinatorGateChoices.Reject,
                null),
            context,
            options,
            projects,
            decisions,
            cancellationToken);

    private static Task<IResult> ProposeCoordinatorOutcomeAsync(
        string projectId,
        string runId,
        string sessionId,
        ProposeCoordinatorOutcomeRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var transition = CoordinatorDecisionFlow.ProposeOutcomeSpec(
                current.State,
                request.Specification,
                request.RequestId,
                selection.Authorization.ActorId);
            var next = transition.State ?? current.State;
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "outcome.propose",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.Specification
                }),
                next,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> AskCoordinatorQuestionAsync(
        string projectId,
        string runId,
        string sessionId,
        AskCoordinatorQuestionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var transition = CoordinatorDecisionFlow.AskQuestion(
                current.State,
                request.RequestId,
                request.QuestionId,
                request.Prompt,
                selection.Authorization.ActorId,
                request.AllowedChoices,
                request.AllowsFreeform);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "question.ask",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.QuestionId,
                    request.Prompt,
                    request.AllowedChoices,
                    request.AllowsFreeform
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> SelectCoordinatorWorkflowAsync(
        string projectId,
        string runId,
        string sessionId,
        SelectCoordinatorWorkflowRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            if (request.ProposedDefinition is not null &&
                !string.Equals(
                    request.WorkflowId ?? request.ProposedDefinition.Id,
                    request.ProposedDefinition.Id,
                    StringComparison.Ordinal))
                throw new CoordinationException(
                    "coordinator_generated_workflow_invalid",
                    StatusCodes.Status400BadRequest);
            var transition = CoordinatorDecisionFlow.SelectWorkflow(
                current.State,
                CoordinatorWorkflowCatalog.ForSelection(
                    selection.Selection, request.ProposedDefinition),
                request.WorkflowId ?? request.ProposedDefinition?.Id,
                request.RequestId,
                selection.Authorization.ActorId);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "workflow.select",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.WorkflowId,
                    request.ProposedDefinition
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> ProposeCoordinatorWorkPlanAsync(
        string projectId,
        string runId,
        string sessionId,
        ProposeCoordinatorWorkPlanRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var selectionContextResolution = RequiresProviderBinding(current.State, request.Plan)
                ? await runSelectionContexts.ResolveForPlanAsync(
                    selection.Selection, current.State.Fence, cancellationToken).ConfigureAwait(false)
                : new CoordinatorRunSelectionContextResolution(
                    CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Selection.Snapshot),
                    PendingBinding: null);
            var selectionContext = selectionContextResolution.Context;
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var transition = CoordinatorDecisionFlow.ProposeWorkPlan(
                current.State,
                request.Plan,
                selectionContext,
                request.RequestId,
                selection.Authorization.ActorId);
            transition = await EnforceRunChildLimitAsync(
                transition,
                current.State,
                request.Plan,
                selection.Selection,
                identity,
                decisions,
                cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "work-plan.propose",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.Plan
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: transition.IsSuccess ? selectionContext : null,
                validatedTransitionValue: transition.Value,
                cancellationToken,
                candidateRunSelectionBinding: transition.IsSuccess
                    ? selectionContextResolution.PendingBinding
                    : null).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> ReviseCoordinatorWorkPlanAsync(
        string projectId,
        string runId,
        string sessionId,
        ReviseCoordinatorWorkPlanRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var selectionContextResolution = RequiresProviderBinding(current.State, request.RevisedPlan)
                ? await runSelectionContexts.ResolveForPlanAsync(
                    selection.Selection, current.State.Fence, cancellationToken).ConfigureAwait(false)
                : new CoordinatorRunSelectionContextResolution(
                    CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Selection.Snapshot),
                    PendingBinding: null);
            var selectionContext = selectionContextResolution.Context;
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var transition = CoordinatorDecisionFlow.ReviseWorkPlan(
                current.State,
                request.RevisedPlan,
                selectionContext,
                request.RequestId,
                selection.Authorization.ActorId);
            transition = await EnforceRunChildLimitAsync(
                transition,
                current.State,
                request.RevisedPlan,
                selection.Selection,
                identity,
                decisions,
                cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "work-plan.revise",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.RevisedPlan
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: transition.IsSuccess ? selectionContext : null,
                validatedTransitionValue: transition.Value,
                cancellationToken,
                candidateRunSelectionBinding: transition.IsSuccess
                    ? selectionContextResolution.PendingBinding
                    : null).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> RequestCoordinatorAssemblyAsync(
        string projectId,
        string runId,
        string sessionId,
        RequestCoordinatorAssemblyRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var transition = CoordinatorDecisionFlow.RequestAssembly(
                current.State, request.Request);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.Request.RequestId,
                "assembly.request",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.Request
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> AskNextOutcomeClarifyingQuestionAsync(
        string projectId,
        string runId,
        string sessionId,
        AskNextOutcomeClarifyingQuestionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var transition = CoordinatorDecisionFlow.AskNextOutcomeClarifyingQuestion(
                current.State,
                request.RequestId,
                selection.Authorization.ActorId);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "outcome.question.next",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> RequestCoordinatorApprovalAsync(
        string projectId,
        string runId,
        string sessionId,
        RequestCoordinatorApprovalRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var transition = CoordinatorDecisionFlow.RequestApproval(
                current.State,
                request.RequestId,
                request.SubjectId,
                selection.Authorization.ActorId,
                request.Prompt);
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                request.RequestId,
                "approval.request",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    request.RequestId,
                    request.SubjectId,
                    request.Prompt
                }),
                transition.State ?? current.State,
                transition.IsSuccess,
                transition.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: transition.Value,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> AnswerCoordinatorGateAsync(
        string projectId,
        string runId,
        string sessionId,
        string requestId,
        AnswerCoordinatorGateRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var gateAnswer = current.State.ApplyGateAnswer(new CoordinatorGateAnswer(
                requestId,
                selection.Authorization.ActorId,
                current.State.Fence,
                request.ChoiceId,
                request.FreeformAnswer));
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                requestId,
                "gate.answer",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    requestId,
                    request.ChoiceId,
                    request.FreeformAnswer
                }),
                gateAnswer.State,
                gateAnswer.IsAccepted,
                gateAnswer.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: gateAnswer.Receipt,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static Task<IResult> AcknowledgeCoordinatorGateAsync(
        string projectId,
        string runId,
        string sessionId,
        string requestId,
        AcknowledgeCoordinatorGateRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var acknowledgment = current.State.AcknowledgeMessage(
                new CoordinatorMessageAcknowledgment(
                    requestId,
                    selection.Authorization.ActorId,
                    current.State.Fence));
            var saved = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                request.ExpectedStateVersion,
                request.IdempotencyKey,
                requestId,
                "gate.acknowledge",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    request.ExpectedStateVersion,
                    requestId
                }),
                acknowledgment.State,
                acknowledgment.IsAcknowledged,
                acknowledgment.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: new
                {
                    acknowledged = acknowledgment.IsAcknowledged,
                    requestId
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDecisionResponse(saved));
        }, cancellationToken);

    private static async Task RequireUnchangedAuthorizedSelectionAsync(
        HttpContext context,
        string projectId,
        string runId,
        AuthorizedRunSelection initial,
        ProjectsRunSelectionClient projects,
        CancellationToken cancellationToken)
    {
        var refreshed = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!AuthorizedSelectionIsUnchanged(projectId, initial, refreshed))
            throw new CoordinationException(
                "coordinator_selection_stale", StatusCodes.Status409Conflict);
    }

    private static bool AuthorizedSelectionIsUnchanged(
        string projectId,
        AuthorizedRunSelection initial,
        AuthorizedRunSelection refreshed)
    {
        if (refreshed.Authorization.ContractVersion != initial.Authorization.ContractVersion ||
            refreshed.Authorization.Issuer != initial.Authorization.Issuer ||
            refreshed.Authorization.ActorId != initial.Authorization.ActorId ||
            refreshed.Authorization.TenantId != initial.Authorization.TenantId ||
            refreshed.Authorization.BoundProjectId != initial.Authorization.BoundProjectId ||
            refreshed.Authorization.BoundRunId != initial.Authorization.BoundRunId ||
            refreshed.Authorization.MembershipRevision != initial.Authorization.MembershipRevision ||
            GetProjectRoleRevision(refreshed.Authorization, projectId, "acceptRunSelection") !=
            GetProjectRoleRevision(initial.Authorization, projectId, "acceptRunSelection") ||
            refreshed.Selection.ProjectRevision != initial.Selection.ProjectRevision ||
            refreshed.Selection.ProjectConfigurationRevision != initial.Selection.ProjectConfigurationRevision ||
            refreshed.Selection.PlatformRuntimeRevision != initial.Selection.PlatformRuntimeRevision ||
            !string.Equals(
                refreshed.Selection.ContextRevision,
                initial.Selection.ContextRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                refreshed.Selection.Snapshot.GetRawText(),
                initial.Selection.Snapshot.GetRawText(),
                StringComparison.Ordinal))
            return false;
        return true;
    }

    private static async Task<CoordinatorDecisionTransition<T>> EnforceRunChildLimitAsync<T>(
        CoordinatorDecisionTransition<T> transition,
        CoordinatorDecisionState currentState,
        WorkPlan? plan,
        EffectiveRunSelection selection,
        SessionIdentity identity,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!transition.IsSuccess)
            return transition;
        var maxChildren = CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Snapshot);
        var registeredChildren = await decisions.ReadRegisteredChildCountAsync(
            identity, cancellationToken).ConfigureAwait(false);
        var proposedChildren = plan?.Items.IsDefaultOrEmpty == false ? plan.Items.Length : 0;
        if (registeredChildren + proposedChildren <= maxChildren)
            return transition;
        return new CoordinatorDecisionTransition<T>(
            currentState,
            null,
            [
                new WorkflowValidationIssue(
                    WorkflowValidationCode.WorkPlanLimitExceeded,
                    "plan.items",
                    "The proposed work plan exceeds the accepted run's remaining child limit.")
            ]);
    }

    private static bool RequiresProviderBinding(CoordinatorDecisionState state, WorkPlan? plan) =>
        plan?.Items.IsDefaultOrEmpty == false ||
        state.SelectedWorkflow?.Definition.Steps.Any(step =>
            step.Mode == WorkflowStepMode.Fixed && step.FixedWork is not null) == true;

    private static long GetProjectRoleRevision(
        ProjectsAuthorizationContext authorization,
        string projectId,
        string permission)
    {
        var grants = authorization.EffectiveAuthority
            .Where(entry => entry.ResourceType == "project" && entry.ResourceId == projectId)
            .SelectMany(entry => entry.Permissions)
            .Where(grant => grant.Permission == permission)
            .Select(grant => grant.RoleRevision)
            .Distinct()
            .ToArray();
        if (grants.Length != 1 || grants[0] < 1)
            throw new CoordinationException(
                "run_selection_permission_denied", StatusCodes.Status403Forbidden);
        return grants[0];
    }

    private static CoordinatorDecisionOperationResponse ToDecisionResponse(
        CoordinatorDecisionPersistedResult saved) =>
        new(
            saved.DecisionId,
            saved.StateVersion,
            saved.TransitionAccepted,
            saved.State.Fence,
            saved.State.PendingGate,
            saved.Issues,
            saved.TransitionValue);

    private static Task<IResult> ReadPolicyEvaluationReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        ExecutableActionGrantOwnerStore grants,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadSelectionForReadAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var receipt = await grants.ReadReceiptAsync(
                actor,
                projectId,
                runId,
                receiptId,
                cancellationToken).ConfigureAwait(false);
            if (receipt is null)
                return Results.NotFound();

            _ = await projects.ReadSelectionForReadAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(receipt);
        }, cancellationToken);

    private static Task<IResult> AcceptRootAsync(
        string projectId,
        string runId,
        AcceptRootRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        EventsAddressedMessageClient events,
        CoordinationOwnerStore store,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var accepted = await store.AcceptRootAsync(
                actor, selection, request.SessionId, cancellationToken).ConfigureAwait(false);
            await decisions.InitializeRootAsync(
                actor,
                new SessionIdentity(projectId, runId, accepted.RootSessionId),
                selection,
                cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            await events.EnsureSessionAsync(
                context,
                new SessionIdentity(projectId, runId, accepted.RootSessionId),
                cancellationToken).ConfigureAwait(false);
            return Results.Created(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/root",
                accepted);
        }, cancellationToken);

    private static Task<IResult> RegisterChildAsync(
        string projectId,
        string runId,
        string parentSessionId,
        RegisterChildRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        EventsAddressedMessageClient events,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var child = await store.RegisterChildAsync(
                actor,
                new SessionIdentity(projectId, runId, parentSessionId),
                request.SessionId,
                CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot),
                CoordinatorWorkflowCatalog.ReadMaxConcurrentChildren(selection.Selection.Snapshot),
                cancellationToken,
                currentCancellationToken => RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, currentCancellationToken))
                .ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            await events.EnsureSessionAsync(context, child.Identity, cancellationToken).ConfigureAwait(false);
            return Results.Created(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/sessions/{Uri.EscapeDataString(child.Identity.SessionId)}",
                child);
        }, cancellationToken);

    private static Task<IResult> SendMessageAsync(
        string projectId,
        string runId,
        string sessionId,
        CoordinationMessageRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.SendMessageAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            var delivery = await store.ClaimOutboundAsync(
                actor, result.OwnerMessageId, cancellationToken).ConfigureAwait(false);
            if (delivery is null)
                return Results.Accepted(value: result);
            var outbound = await store.ReadOutboundMessageAsync(
                result.OwnerMessageId, cancellationToken).ConfigureAwait(false);
            var receipt = await events.AdmitAsync(context, outbound, cancellationToken).ConfigureAwait(false);
            await store.AdmitDeliveryAsync(
                actor,
                new DeliveryIngressRequest(result.OwnerMessageId, receipt),
                cancellationToken).ConfigureAwait(false);
            _ = await store.AcknowledgeOutboundAsync(delivery, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result with { Status = "admitted" });
        }, cancellationToken);

    private static Task<IResult> ValidateMessageRouteAsync(
        string projectId,
        string runId,
        MessageRouteValidationRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.EventsAudience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var outbound = request.Message
                ?? throw new CoordinationException("owner_message_invalid", StatusCodes.Status400BadRequest);
            var binding = await store.ValidateMessageRouteAsync(
                actor,
                new SessionIdentity(projectId, runId, outbound.Sender.SessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(binding);
        }, cancellationToken);

    private static Task<IResult> GetSessionBindingAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.EventsAudience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var binding = await store.GetSessionBindingAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(binding);
        }, cancellationToken);

    private static Task<IResult> ValidateSessionForkAdmissionAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionForkRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.EventsAudience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var receipt = await store.ValidateSessionForkAdmissionAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                CoordinationOwnerStore.HashSelection(selection.Selection),
                cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Ok(receipt);
        }, cancellationToken);

    private static Task<IResult> AdvanceTurnBoundaryAsync(
        string projectId,
        string runId,
        string sessionId,
        TurnBoundaryRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var completed = await store.ReadCompletedTurnBoundaryAsync(
                actor, identity, request.ExecutionFence, request.ExpectedStateVersion, cancellationToken)
                .ConfigureAwait(false);
            if (completed is not null)
                return Results.Ok(completed);
            var boundary = await store.AdvanceTurnBoundaryAsync(
                actor, identity, request.ExecutionFence, request.ExpectedStateVersion, cancellationToken)
                .ConfigureAwait(false);
            var claim = await events.ClaimNextAsync(context, identity, cancellationToken).ConfigureAwait(false);
            if (claim is not null)
            {
                if (claim.Owner != CoordinationIdentity.ClaimOwner(actor) ||
                    claim.Message.Recipient != identity ||
                    claim.Message.RecipientFence != request.ExecutionFence)
                    throw new CoordinationException(
                        "events_claim_contract_invalid", StatusCodes.Status502BadGateway);
                var presented = claim.Message.Status == AddressedMessageStatus.Delivered
                    ? claim.Message
                    : await events.PresentAsync(
                        context,
                        identity,
                        claim.Message.MessageId,
                        claim.Message.ClaimFence,
                        cancellationToken).ConfigureAwait(false);
                await store.RecordPresentationAsync(
                    actor, identity, presented, cancellationToken).ConfigureAwait(false);
                boundary = boundary with { PresentedMessage = presented };
            }
            boundary = await store.CompleteTurnBoundaryAsync(
                actor,
                identity,
                request.ExecutionFence,
                request.ExpectedStateVersion,
                boundary.StateVersion,
                boundary.PresentedMessage,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(boundary);
        }, cancellationToken);

    private static Task<IResult> FinishTurnAsync(
        string projectId,
        string runId,
        string sessionId,
        FinishTurnRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.FinishTurnAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }, cancellationToken);

    private static Task<IResult> ReadOwnerRunStatusAsync(
        string projectId,
        string runId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.ReadOwnerRunStatusAsync(
                actor, projectId, runId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }, cancellationToken);

    private static Task<IResult> ReportRunFailureAsync(
        string projectId,
        string runId,
        string sessionId,
        ReportRunFailureRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.ReportRunFailureAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }, cancellationToken);

    private static Task<IResult> RecoverRunExecutionAsync(
        string projectId,
        string runId,
        RecoverRunExecutionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.RecoverRunExecutionAsync(
                actor, projectId, runId, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }, cancellationToken);

    private static Task<IResult> ReadNotificationsAsync(
        string projectId,
        string runId,
        string sessionId,
        int? limit,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var notifications = await store.ReadParentNotificationsAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                limit ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(notifications);
        }, cancellationToken);

    private static Task<IResult> AcknowledgeNotificationAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid notificationId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            await store.AcknowledgeParentNotificationAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                notificationId,
                cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }, cancellationToken);

    private static Task<IResult> AcknowledgeMessageAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid messageId,
        AddressedMessageClaimFenceRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var acknowledged = await events.AcknowledgeAsync(
                context, identity, messageId, request.ClaimFence, cancellationToken).ConfigureAwait(false);
            await store.RecordAcknowledgementAsync(
                actor, identity, acknowledged, cancellationToken).ConfigureAwait(false);
            return Results.Ok(acknowledged);
        }, cancellationToken);

    private static CoordinationActor RequireOwnerActor(
        HttpContext context,
        OrchestratorOptions options,
        string projectId,
        string runId,
        string audience)
    {
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (scope.ProjectId != projectId || scope.RunId != runId ||
            !CoordinationIdentity.HasAudience(context.User, audience))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        return actor;
    }

    private static async Task<IResult> ExecuteAsync(
        Func<Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
