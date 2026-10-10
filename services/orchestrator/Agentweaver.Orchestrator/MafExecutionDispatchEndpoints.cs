using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Npgsql;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    private static readonly JsonSerializerOptions RuntimeHostJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static MafCheckpointBinding CreateExecutionCheckpointBinding(
        SessionIdentity identity,
        CoordinationActor actor,
        long executionFence,
        string sdkVersion,
        string pinnedModelReference) =>
        new(
            identity,
            actor,
            executionFence,
            sdkVersion,
            pinnedModelReference,
            CacheReference: null,
            StoreName: MafExecutionCheckpointContract.StoreName);

    private static Task<IResult> DispatchCoordinatorWorkPlanAsync(
        string projectId,
        string runId,
        string sessionId,
        MafExecutionDispatchRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinationOwnerStore store,
        CoordinatorRunSelectionContextStore runSelectionContexts,
        PostgresMafCheckpointStore checkpoints,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.ExpectedStateVersion < 1 || string.IsNullOrWhiteSpace(request.WorkPlanId))
                throw new CoordinationException(
                    "maf_execution_dispatch_invalid", StatusCodes.Status400BadRequest);
            if (request.BuildTestEnvironmentId is { } requestedEnvironment)
                RuntimeContractValidation.ValidateIdentifier(requestedEnvironment);

            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var decision = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            if (decision.StateVersion != request.ExpectedStateVersion ||
                !decision.State.CanDispatch ||
                decision.State.ConfirmedWorkPlan is not { } confirmedPlan ||
                !string.Equals(confirmedPlan.Plan.Id, request.WorkPlanId, StringComparison.Ordinal))
                throw new CoordinationException(
                    "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var selectionContext = await runSelectionContexts.ReadAsync(
                    selection.Selection, decision.State.Fence, cancellationToken).ConfigureAwait(false);
            if (selectionContext is null &&
                RequiresProviderBinding(decision.State, confirmedPlan.Plan))
                throw new CoordinationException(
                    "maf_execution_selection_unavailable", StatusCodes.Status409Conflict);
            selectionContext ??= CoordinatorWorkflowCatalog.CreateRunSelectionContext(
                selection.Selection.Snapshot);
            var validated = WorkPlanValidator.ValidateAndSnapshot(
                confirmedPlan.Workflow, confirmedPlan.Plan, selectionContext);
            if (!validated.IsValid || validated.Value is null)
                throw new CoordinationException(
                    "maf_execution_work_plan_unavailable", StatusCodes.Status409Conflict);

            var plan = validated.Value;
            var checkpointBinding = CreateExecutionCheckpointBinding(
                identity,
                actor,
                decision.State.Fence,
                MafExecutionCheckpointStore.CurrentSdkVersion,
                PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot));
            var execution = new MafExecutionCheckpointStore(
                checkpoints,
                checkpointBinding);
            var checkpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null &&
                !string.Equals(checkpoint.State.WorkPlanId, plan.Plan.Id, StringComparison.Ordinal))
                throw new CoordinationException(
                    "maf_execution_plan_changed", StatusCodes.Status409Conflict);
            if (checkpoint is not null &&
                checkpoint.State.FixedWorkAssociations.Values.Any(association =>
                    checkpoint.State.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId) ==
                        MafExecutionTaskStatus.Running &&
                    association.DecisionStateVersion != decision.StateVersion))
                throw new CoordinationException(
                    "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);
            if (checkpoint is not null &&
                checkpoint.State.BuildTestIntents.Values.Any(intent =>
                    intent.Identity != identity || intent.ExecutionFence != decision.State.Fence ||
                    intent.AcceptedSelectionHash != decision.SelectionHash ||
                    checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                        MafExecutionTaskStatus.Running && intent.DecisionStateVersion != decision.StateVersion))
                throw new CoordinationException(
                    "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);

            var progress = checkpoint?.State.Progress ?? MafExecutionProgress.Empty;
            var fixedAssociations = checkpoint?.State.FixedWorkAssociations ??
                ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
                    .WithComparers(StringComparer.Ordinal);
            var children = await store.ReadMafExecutionChildrenAsync(
                actor, identity, cancellationToken, fixedAssociations).ConfigureAwait(false);
            var pendingDispatches = checkpoint?.State.PendingDispatches ??
                ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                    .WithComparers(StringComparer.Ordinal);
            var results = checkpoint?.State.Results ??
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
            var reconciled = MafExecutionPlanner.ReconcileChildren(
                plan, progress, identity, children, decision.State.Fence, fixedAssociations,
                pendingDispatches.Keys.ToHashSet(StringComparer.Ordinal),
                results.Keys.ToHashSet(StringComparer.Ordinal));
            if (checkpoint is null || reconciled != progress ||
                checkpoint.State.DecisionStateVersion != decision.StateVersion)
            {
                var next = (checkpoint?.State ??
                    new MafExecutionCheckpoint(0, plan.Plan.Id, decision.StateVersion, reconciled)) with
                {
                    Revision = checkpoint is null ? 1 : checked(checkpoint.State.Revision + 1),
                    DecisionStateVersion = decision.StateVersion,
                    Progress = reconciled
                };
                checkpoint = await execution.AppendAsync(
                    Guid.NewGuid().ToString("N"), checkpoint?.Info, next, cancellationToken)
                    .ConfigureAwait(false);
            }

            var maxChildren = CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot);
            var maxConcurrentChildren =
                CoordinatorWorkflowCatalog.ReadMaxConcurrentChildren(selection.Selection.Snapshot);
            var maxWallTimeSeconds =
                CoordinatorWorkflowCatalog.ReadMaxWallTimeSeconds(selection.Selection.Snapshot);
            _ =
                CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection.Selection.Snapshot);
            var sessions = await store.ReadSessionTreeAsync(actor, identity, cancellationToken)
                .ConfigureAwait(false);
            var rootSession = sessions.Nodes.SingleOrDefault(
                node => node.Identity.SessionId == sessions.RootSessionId);
            if (sessions.RootSessionId != identity.SessionId || rootSession is null)
                throw new CoordinationException(
                    "maf_execution_run_start_unavailable", StatusCodes.Status503ServiceUnavailable);

            var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
            var runStartedAt = rootSession.CreatedAt;
            bool IsWallTimeLimitReached() =>
                CoordinatorWorkflowCatalog.IsWallTimeLimitReached(
                    runStartedAt, maxWallTimeSeconds, timeProvider.GetUtcNow());

            var registrationOwner = context.RequestServices.GetService<RuntimeRegistrationOwner>();
            var runtimeSourceOwner = context.RequestServices.GetService<RuntimeUsageSourceOwner>();
            var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
            var buildTestClient = context.RequestServices.GetService<MafBuildTestEnvironmentClient>();
            bool HasBuildTestExecutor(WorkflowStepDefinition step) =>
                buildTestClient is not null && MafBuildTestCommandContract.IsExecutable(step) &&
                (request.BuildTestEnvironmentId is not null ||
                 checkpoint.State.BuildTestIntents.ContainsKey(step.Id));
            var hostClient = context.RequestServices.GetRequiredService<IHttpClientFactory>()
                .CreateClient("maf-execution-host");
            var dispatched = ImmutableArray.CreateBuilder<string>();
            var unavailable = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

            async Task RevalidateDispatchAsync(CancellationToken token)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                var current = await decisions.ReadCurrentAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                if (current.StateVersion != decision.StateVersion ||
                    current.State.Fence != decision.State.Fence ||
                    !current.State.CanDispatch ||
                    current.State.ConfirmedWorkPlan?.Plan.Id != plan.Plan.Id)
                    throw new CoordinationException(
                        "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);
            }

            async Task<(MafExecutionCheckpointSnapshot Snapshot, bool ShouldSend)> ReadCurrentDispatchIntentAsync(
                MafExecutionDispatchIntent intent, CancellationToken token)
            {
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    decision.StateVersion, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(connection, transaction, token)
                    .ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(connection, transaction, token)
                    .ConfigureAwait(false) ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.WorkPlanId != plan.Plan.Id ||
                    latest.State.DecisionStateVersion != decision.StateVersion)
                    throw new CoordinationException(
                        "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
                var expectedIntent = RebuildExpectedDispatchIntent(latest.State, intent);
                var shouldSend = ShouldSendDispatchIntent(latest.State, intent, expectedIntent);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return (latest, shouldSend);
            }

            async Task<RuntimeActorAuthorization> CreateRuntimeActorAsync(CancellationToken token)
            {
                var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
                var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
                var bearer = CoordinationIdentity.RequireBearer(context).Parameter!;
                if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow() ||
                    CoordinationIdentity.RequireActor(context.User, options.Issuer) != actor)
                    throw new RuntimeAuthorizationException("runtime_actor_expired");
                token.ThrowIfCancellationRequested();
                return new(new SecretCredential(bearer, expiry.Value, timeProvider),
                    CoordinationIdentity.ReadTenantSelector(context));
            }

            async Task<RuntimeRegistration?> ReadDispatchRegistrationAsync(
                MafExecutionDispatchIntent intent, CancellationToken token)
            {
                if (registrationOwner is null)
                    return null;
                try
                {
                    var registration = await registrationOwner.ReadCurrentForSessionAsync(
                        context, projectId, runId, intent.ChildSessionId, token).ConfigureAwait(false);
                    if (registration.State != RuntimeRegistrationState.Active ||
                        registration.Binding.ProjectId != projectId || registration.Binding.RunId != runId ||
                        registration.Binding.SessionId != intent.ChildSessionId ||
                        registration.Binding.ExecutionFence != decision.State.Fence ||
                        registration.Binding.AcceptedSelectionHash != decision.SelectionHash ||
                        registration.ExpiresAt <= timeProvider.GetUtcNow())
                        throw new CoordinationException(
                            "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                    return registration;
                }
                catch (CoordinationException failure) when (
                    failure.Code == "runtime_registration_unavailable")
                {
                    return null;
                }
                catch (RuntimeAuthorizationException failure) when (
                    failure.Code is "runtime_registration_unavailable" or
                        "runtime_owner_context_unavailable" or "runtime_placement_not_ready")
                {
                    return null;
                }
            }

            async Task<string?> DeliverIntentAsync(
                MafExecutionDispatchIntent intent, string prompt, CancellationToken token)
            {
                if (IsWallTimeLimitReached())
                    return null;
                if (RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)) != intent.PromptHash)
                    throw new CoordinationException(
                        "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
                var pending = await ReadCurrentDispatchIntentAsync(intent, token).ConfigureAwait(false);
                if (!pending.ShouldSend)
                    return pending.Snapshot.State.Results[intent.AssociationId];
                var registration = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (registration is null)
                    return null;
                var readinessEndpoint = RuntimeHostEndpoint(
                    registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness");
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var current = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (current is null)
                    return null;
                if (current != registration)
                    throw new CoordinationException(
                        "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                RuntimeHostReadinessReceipt? readiness;
                var readinessActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                try
                {
                    readiness = await SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                        hostClient, readinessEndpoint, readinessActor, null, allowNotReady: true, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    readinessActor.Bearer.Invalidate();
                }
                if (readiness is null)
                    return null;
                ValidateHostReadiness(readiness, registration, timeProvider);

                current = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (current is null)
                    return null;
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                if (current != registration)
                    throw new CoordinationException(
                        "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                var proof = new RuntimeHostSessionProof(
                    1,
                    registration,
                    readiness.SourceGrant.GrantId,
                    readiness.SourceGrant.Revision,
                    RuntimeCredentialPurpose.Observe);
                var message = new RuntimeA2ASendRequest(new RuntimeA2AMessage(
                    "message",
                    intent.MessageId,
                    RuntimeContractValidation.NativeSessionId(registration.Binding),
                    "user",
                    [new("text", prompt)],
                    new(proof, AddressedMessageDeliveryMode.Enqueue)));
                if (IsWallTimeLimitReached())
                    return null;
                var sendActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                RuntimeA2AResponse response;
                try
                {
                    if (IsWallTimeLimitReached())
                        return null;
                    var currentIntent = await ReadCurrentDispatchIntentAsync(intent, token).ConfigureAwait(false);
                    if (!currentIntent.ShouldSend)
                        return currentIntent.Snapshot.State.Results[intent.AssociationId];
                    if (runtimeSourceOwner is null)
                        throw new CoordinationException(
                            "maf_execution_native_source_unavailable", StatusCodes.Status503ServiceUnavailable);
                    await runtimeSourceOwner.PrepareNativeTurnAsync(
                        context, registration, identity, currentIntent.Snapshot, intent, message,
                        (connection, transaction, currentToken) =>
                            decisions.RequireCurrentDispatchStateInTransactionAsync(
                                connection, transaction, actor, identity, selection, selectionContext,
                                decision.StateVersion, decision.State.Fence, plan.Plan.Id, currentToken), token)
                        .ConfigureAwait(false);
                    response = await SendRuntimeHostMessageAsync(
                        hostClient,
                        registration,
                        readiness,
                        sendActor,
                        message,
                        timeProvider,
                        token).ConfigureAwait(false);
                }
                finally
                {
                    sendActor.Bearer.Invalidate();
                }

                var expectedContext = RuntimeContractValidation.NativeSessionId(registration.Binding);
                if (response.Kind != "message" || response.MessageId == Guid.Empty ||
                    response.ContextId != expectedContext || response.Role != "agent" ||
                    response.Parts is not [{ Kind: "text", Text: { Length: > 0 } answer }] ||
                    answer.Length > AddressedMessageValidation.MaximumTextLength ||
                    answer.Any(character => char.IsControl(character) &&
                        character is not ('\r' or '\n' or '\t')))
                    throw new CoordinationException(
                        "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
                await runtimeSourceOwner!.RequireNativeTurnAccountedAsync(
                    context, registration, message, answer,
                    (connection, transaction, currentToken) =>
                        decisions.RequireCurrentDispatchStateInTransactionAsync(
                            connection, transaction, actor, identity, selection, selectionContext,
                            decision.StateVersion, decision.State.Fence, plan.Plan.Id, currentToken), token)
                    .ConfigureAwait(false);
                return answer;
            }

            MafExecutionAction ResolveIntentAction(
                MafExecutionDispatchIntent intent, MafExecutionCheckpoint state)
            {
                var workItem = plan.Plan.Items.SingleOrDefault(item => item.Id == intent.AssociationId);
                if (workItem is not null)
                {
                    var step = plan.Workflow.Definition.Steps.SingleOrDefault(
                        candidate => candidate.Id == workItem.WorkflowStepId);
                    if (step is not null)
                        return new(step, workItem);
                }
                if (state.FixedWorkAssociations.TryGetValue(intent.AssociationId, out var fixedAssociation) &&
                    fixedAssociation.ChildSessionId == intent.ChildSessionId)
                {
                    var step = plan.Workflow.Definition.Steps.SingleOrDefault(
                        candidate => candidate.Id == fixedAssociation.StepId);
                    if (step is not null)
                        return new(step, null);
                }
                throw new CoordinationException(
                    "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
            }

            string BuildPrompt(
                MafExecutionAction action,
                MafExecutionCheckpoint state,
                string associationId,
                MafExecutionFixedWorkAssociation? newFixedAssociation = null)
            {
                var workItem = action.WorkItem;
                var requiredResults = new HashSet<string>(
                    workItem?.DependsOn ?? ImmutableArray<string>.Empty, StringComparer.Ordinal);
                var dependencySteps = action.Step.DependsOn.ToHashSet(StringComparer.Ordinal);
                foreach (var item in plan.Plan.Items)
                    if (dependencySteps.Contains(item.WorkflowStepId))
                        requiredResults.Add(item.Id);
                foreach (var fixedAssociation in state.FixedWorkAssociations.Values)
                    if (dependencySteps.Contains(fixedAssociation.StepId))
                        requiredResults.Add(fixedAssociation.AssociationId);
                var platformResults = plan.Workflow.Definition.Steps
                    .Where(step => dependencySteps.Contains(step.Id) && step.BuildTestCommand is not null)
                    .Select(step => state.BuildTestReceipts.TryGetValue(step.Id, out var receipt)
                        ? (step.Id, Receipt: receipt)
                        : throw new CoordinationException(
                            "maf_execution_dependency_result_unavailable", StatusCodes.Status409Conflict))
                    .ToArray();

                if (requiredResults.Count == 0 && platformResults.Length == 0)
                {
                    if (workItem is not null)
                        return workItem.Task;
                    var fixedWork = newFixedAssociation ??
                        state.FixedWorkAssociations.GetValueOrDefault(associationId)
                        ?? throw new CoordinationException(
                            "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
                    return fixedWork.Specification.Task;
                }

                var task = workItem?.Task ?? (newFixedAssociation ??
                    state.FixedWorkAssociations.GetValueOrDefault(associationId)
                    ?? throw new CoordinationException(
                        "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict))
                    .Specification.Task;
                var completed = requiredResults.Order(StringComparer.Ordinal)
                    .Where(state.Results.ContainsKey).ToArray();
                if (completed.Length != requiredResults.Count)
                    throw new CoordinationException(
                        "maf_execution_dependency_result_unavailable", StatusCodes.Status409Conflict);
                var prompt = new StringBuilder(task).Append("\n\nCompleted prerequisite results:");
                foreach (var completedAssociationId in completed)
                    prompt.Append("\n\n[").Append(completedAssociationId).Append("]\n")
                        .Append(state.Results[completedAssociationId]);
                foreach (var (stepId, receipt) in platformResults)
                    prompt.Append("\n\n[").Append(stepId).Append("]\n")
                        .Append(BuildTestResultSummary(receipt));
                var value = prompt.ToString();
                if (value.Length > AddressedMessageValidation.MaximumTextLength ||
                    value.Any(character => char.IsControl(character) &&
                        character is not ('\r' or '\n' or '\t')))
                    throw new CoordinationException(
                        "maf_execution_prompt_limit_exceeded", StatusCodes.Status409Conflict);
                return value;
            }

            MafExecutionDispatchIntent RebuildExpectedDispatchIntent(
                MafExecutionCheckpoint state, MafExecutionDispatchIntent intent)
            {
                var action = ResolveIntentAction(intent, state);
                var prompt = BuildPrompt(action, state, intent.AssociationId);
                return new MafExecutionDispatchIntent(
                    intent.AssociationId,
                    MafExecutionPlanner.CreateChildSessionId(identity, plan.Plan.Id, intent.AssociationId),
                    MafExecutionPlanner.CreateDispatchMessageId(identity, plan.Plan.Id, intent.AssociationId),
                    RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)));
            }

            async Task CompleteDispatchAsync(
                MafExecutionDispatchIntent intent, string response, CancellationToken token)
            {
                var latestCheckpoint = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (IsCompletedDispatchReplay(
                        latestCheckpoint.State,
                        intent,
                        RebuildExpectedDispatchIntent(latestCheckpoint.State, intent),
                        response))
                {
                    checkpoint = latestCheckpoint;
                    dispatched.Add(intent.AssociationId);
                    return;
                }

                var childIdentity = new SessionIdentity(projectId, runId, intent.ChildSessionId);
                var child = await store.ReadSessionStatusAsync(actor, childIdentity, token).ConfigureAwait(false);
                if (child.ExecutionFence != decision.State.Fence ||
                    child.Lifecycle != CoordinationLifecycleState.Active ||
                    child.Activity != CoordinationActivityState.Busy)
                {
                    var replay = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                        ?? throw new CoordinationException(
                            "maf_execution_checkpoint_unavailable",
                            StatusCodes.Status503ServiceUnavailable);
                    if (replay.State.Results.ContainsKey(intent.AssociationId) &&
                        IsCompletedDispatchReplay(
                            replay.State,
                            intent,
                            RebuildExpectedDispatchIntent(replay.State, intent),
                            response))
                    {
                        checkpoint = replay;
                        dispatched.Add(intent.AssociationId);
                        return;
                    }
                    throw new CoordinationException(
                        "maf_execution_child_completion_stale", StatusCodes.Status409Conflict);
                }
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                try
                {
                    _ = await store.FinishTurnAsync(
                        actor,
                        childIdentity,
                        new(child.ExecutionFence, child.StateVersion, LogicalTurnCompletion.Completed),
                        token,
                        async (connection, transaction, transactionToken) =>
                        {
                            await decisions.RequireCurrentDispatchStateInTransactionAsync(
                                connection,
                                transaction,
                                actor,
                                identity,
                                selection,
                                selectionContext,
                                decision.StateVersion,
                                decision.State.Fence,
                                plan.Plan.Id,
                                transactionToken).ConfigureAwait(false);
                            var latest = await execution.ReadLatestInTransactionAsync(
                                connection, transaction, transactionToken).ConfigureAwait(false)
                                ?? throw new CoordinationException(
                                    "maf_execution_checkpoint_unavailable",
                                    StatusCodes.Status503ServiceUnavailable);
                            if (!latest.State.PendingDispatches.TryGetValue(
                                    intent.AssociationId, out var currentIntent) ||
                                currentIntent != intent ||
                                latest.State.Results.ContainsKey(intent.AssociationId))
                                throw new CoordinationException(
                                    "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);

                            var nextProgress = latest.State.Progress;
                            if (nextProgress.WorkItems.GetValueOrDefault(intent.AssociationId) ==
                                MafExecutionTaskStatus.Running)
                                nextProgress = nextProgress with
                                {
                                    WorkItems = nextProgress.WorkItems.SetItem(
                                        intent.AssociationId, MafExecutionTaskStatus.Succeeded)
                                };
                            else if (nextProgress.FixedWorkItems.GetValueOrDefault(intent.AssociationId) ==
                                MafExecutionTaskStatus.Running)
                                nextProgress = nextProgress with
                                {
                                    FixedWorkItems = nextProgress.FixedWorkItems.SetItem(
                                        intent.AssociationId, MafExecutionTaskStatus.Succeeded)
                                };
                            else
                                throw new CoordinationException(
                                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);

                            var next = latest.State with
                            {
                                Revision = checked(latest.State.Revision + 1),
                                DecisionStateVersion = decision.StateVersion,
                                Progress = nextProgress,
                                PendingDispatches = latest.State.PendingDispatches.Remove(intent.AssociationId),
                                Results = latest.State.Results.Add(intent.AssociationId, response)
                            };
                            _ = await execution.AppendInTransactionAsync(
                                connection,
                                transaction,
                                Guid.NewGuid().ToString("N"),
                                latest.Info,
                                next,
                                transactionToken).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                }
                catch (CoordinationException failure) when (failure.Code == "turn_completion_conflict")
                {
                    var replay = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                        ?? throw new CoordinationException(
                            "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                    if (!IsCompletedDispatchReplay(
                            replay.State,
                            intent,
                            RebuildExpectedDispatchIntent(replay.State, intent),
                            response))
                        throw;
                    checkpoint = replay;
                    dispatched.Add(intent.AssociationId);
                    return;
                }
                dispatched.Add(intent.AssociationId);
                checkpoint = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
            }

            async Task AppendCompletedPlanWitnessAsync(CancellationToken token)
            {
                var expectedCheckpoint = checkpoint
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                var sourceControl = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                var witnessStore = new MafExecutionOutputWitnessStore(options.Schema);
                var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection,
                    transaction,
                    actor,
                    identity,
                    selection,
                    selectionContext,
                    decision.StateVersion,
                    decision.State.Fence,
                    plan.Plan.Id,
                    token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.Info != expectedCheckpoint.Info ||
                    latest.State.Revision != expectedCheckpoint.State.Revision ||
                    latest.State.WorkPlanId != plan.Plan.Id ||
                    latest.State.DecisionStateVersion != decision.StateVersion)
                    throw new CoordinationException(
                        "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);

                var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(
                    plan, latest, identity);
                if (!outputSet.MissingFixedAssociationIds.IsEmpty)
                    throw new CoordinationException(
                        "maf_execution_output_witness_plan_incomplete", StatusCodes.Status409Conflict);
                var root = new MafExecutionOwnerEvidenceBinding(
                    actor,
                    identity,
                    decision.SelectionHash,
                    decision.State.Fence,
                    decision,
                    latest,
                    plan,
                    outputSet.OutputSet);
                var captureProofs =
                    await sourceControl.ReadPrerequisiteOutputCaptureProofsInTransactionAsync(
                        connection,
                        transaction,
                        actor,
                        identity,
                        selection.Authorization.TenantId,
                        decision.SelectionHash,
                        decision.State.Fence,
                        token).ConfigureAwait(false);
                var mergeIntentIds = ImmutableArray.CreateBuilder<string>();
                foreach (var candidateId in decision.State.DecisionReceipts
                             .Where(receipt =>
                                 receipt.Kind == CoordinatorGateKind.Approval &&
                                 receipt.ChoiceId == CoordinatorGateChoices.Approve &&
                                 receipt.FreeformAnswer is null)
                             .Select(receipt => receipt.SubjectId)
                             .Where(SourceControlOwnerStore.IsIdentifier)
                             .Distinct(StringComparer.Ordinal)
                             .Order(StringComparer.Ordinal))
                {
                    var candidate = await sourceControl.ReadPrerequisiteMergeIntentInTransactionAsync(
                        connection,
                        transaction,
                        actor,
                        identity,
                        selection.Authorization.TenantId,
                        decision.SelectionHash,
                        decision.State.Fence,
                        candidateId,
                        token).ConfigureAwait(false);
                    if (candidate.Intent is { } intent &&
                        intent.WorkPlanId == plan.Plan.Id &&
                        intent.WorkflowId == plan.Workflow.Definition.Id &&
                        intent.DefinitionRevision == plan.Workflow.Definition.Revision)
                        mergeIntentIds.Add(intent.IntentId);
                }

                _ = await witnessStore.AppendOutputWitnessWithCurrentOwnerEvidenceInTransactionAsync(
                    connection,
                    transaction,
                    checkpoints,
                    checkpointBinding,
                    sourceControl,
                    root,
                    selection.Authorization.TenantId,
                    captureProofs.Select(proof => proof.CaptureId).ToImmutableArray(),
                    mergeIntentIds.ToImmutable(),
                    token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                checkpoint = latest;
            }

            var wallTimeLimitReached = false;
            async Task<bool> ExecuteBuildTestAsync(
                MafExecutionBuildTestIntent intent, CancellationToken token)
            {
                if (buildTestClient is null)
                    throw new CoordinationException(
                        "maf_execution_executor_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (IsWallTimeLimitReached())
                {
                    wallTimeLimitReached = true;
                    return false;
                }
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var runtimeActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                SandboxBuildTestOperationSnapshot operation;
                try
                {
                    operation = await buildTestClient.ExecuteOrReconcileAsync(
                        runtimeActor, intent, token).ConfigureAwait(false);
                }
                finally
                {
                    runtimeActor.Bearer.Invalidate();
                }
                if (MafBuildTestCommandContract.Classify(intent, operation) == MafExecutionTaskStatus.Running)
                    return false;
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    decision.StateVersion, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                var next = PrepareBuildTestCompletionCheckpoint(latest, intent, operation);
                checkpoint = next is null
                    ? latest
                    : await execution.AppendInTransactionAsync(
                        connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, token)
                        .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }

            async Task<MafExecutionBuildTestIntent> PrepareBuildTestAsync(
                WorkflowStepDefinition step, CancellationToken token)
            {
                var environmentId = request.BuildTestEnvironmentId
                    ?? throw new CoordinationException(
                        "maf_execution_build_test_environment_unavailable", StatusCodes.Status409Conflict);
                if (buildTestClient is null || step.BuildTestCommand is null)
                    throw new CoordinationException(
                        "maf_execution_executor_unavailable", StatusCodes.Status503ServiceUnavailable);
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var runtimeActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                SandboxBuildTestBindingPreparation preparation;
                try
                {
                    preparation = await buildTestClient.PrepareAsync(
                        runtimeActor, identity, environmentId, step.BuildTestCommand.ExecutionProfileReference, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    runtimeActor.Bearer.Invalidate();
                }
                MafBuildTestCommandContract.ValidatePreparation(
                    preparation, identity, selection.Authorization.TenantId, environmentId, step,
                    plan.IsolationProviderBinding);
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    decision.StateVersion, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.BuildTestIntents.TryGetValue(step.Id, out var retained))
                {
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }
                var checkpointId = Guid.NewGuid().ToString("N");
                var intent = new MafExecutionBuildTestIntent(
                    Guid.NewGuid(), identity, checkpointId, checked(latest.State.Revision + 1),
                    plan.Plan.Id, step.Id, decision.StateVersion, decision.State.Fence, decision.SelectionHash,
                    step.BuildTestCommand, preparation.ExecutionOptions, preparation.ExpectedBinding);
                var next = PrepareBuildTestCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, checkpointId, latest.Info, next, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return intent;
            }

            foreach (var intent in checkpoint.State.BuildTestIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Running)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
                _ = await ExecuteBuildTestAsync(intent, cancellationToken).ConfigureAwait(false);

            var pendingDeliveries = checkpoint.State.PendingDispatches.Values
                .OrderBy(intent => intent.AssociationId, StringComparer.Ordinal)
                .Select(intent =>
                {
                    var action = ResolveIntentAction(intent, checkpoint.State);
                    return (Intent: intent, Action: action,
                        Prompt: BuildPrompt(action, checkpoint.State, intent.AssociationId));
                })
                .ToArray();
            var pendingResults = await Task.WhenAll(pendingDeliveries.Select(async delivery =>
                (delivery.Intent, delivery.Action,
                    Answer: await DeliverIntentAsync(
                        delivery.Intent, delivery.Prompt, cancellationToken).ConfigureAwait(false))))
                .ConfigureAwait(false);
            foreach (var result in pendingResults)
            {
                if (result.Answer is null)
                {
                    if (IsWallTimeLimitReached())
                        wallTimeLimitReached = true;
                    else
                        unavailable.Add(result.Action.Step.Id);
                }
                else
                    await CompleteDispatchAsync(
                        result.Intent, result.Answer, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                if (IsWallTimeLimitReached())
                {
                    wallTimeLimitReached = true;
                    break;
                }
                var capacity = await store.ReadMafExecutionCapacityAsync(
                    actor, identity, cancellationToken).ConfigureAwait(false);
                var frontier = MafExecutionPlanner.BuildFrontier(
                    plan,
                    checkpoint.State.Progress,
                    capacity.RegisteredChildren,
                    capacity.ActiveChildren,
                    maxChildren,
                    maxConcurrentChildren,
                    hasNonModelExecutor: HasBuildTestExecutor,
                    checkpoint.State.FixedWorkAssociations);
                if (frontier.ReadyActions.IsDefaultOrEmpty)
                    break;

                var readyDispatches = new List<(
                    MafExecutionAction Action, MafExecutionDispatchIntent Intent, string Prompt)>();
                var platformAdvanced = false;
                foreach (var action in frontier.ReadyActions)
                {
                    if (IsWallTimeLimitReached())
                    {
                        wallTimeLimitReached = true;
                        break;
                    }
                    if (MafBuildTestCommandContract.IsExecutable(action.Step))
                    {
                        var commandIntent = await PrepareBuildTestAsync(action.Step, cancellationToken)
                            .ConfigureAwait(false);
                        platformAdvanced |= await ExecuteBuildTestAsync(commandIntent, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                    MafExecutionFixedWorkAssociation? fixedAssociation = null;
                    ConfirmedWorkPlanItemAssociation? workPlanAssociation = null;
                    string associationId;
                    string task;
                    if (action.WorkItem is { } workItem)
                    {
                        associationId = workItem.Id;
                        task = workItem.Task;
                        workPlanAssociation = new ConfirmedWorkPlanItemAssociation(
                            workItem.Id, decision.StateVersion, decision.SelectionHash);
                    }
                    else if (action.Step.Mode == WorkflowStepMode.Fixed)
                    {
                        var activationRevision = checkpoint.State.FixedWorkAssociations.Values
                            .Where(association =>
                                association.WorkPlanId == plan.Plan.Id && association.StepId == action.Step.Id)
                            .Select(association => association.ActivationRevision)
                            .DefaultIfEmpty(0)
                            .Max() + 1;
                        associationId = MafExecutionPlanner.CreateFixedWorkAssociationId(
                            identity, plan.Plan.Id, action.Step.Id, activationRevision);
                        var fixedChildSessionId = MafExecutionPlanner.CreateChildSessionId(
                            identity, plan.Plan.Id, associationId);
                        fixedAssociation = MafExecutionPlanner.CreateFixedWorkAssociation(
                            plan,
                            selectionContext,
                            identity,
                            action.Step,
                            decision.SelectionHash,
                            decision.State.Fence,
                            decision.StateVersion,
                            activationRevision,
                            fixedChildSessionId);
                        task = fixedAssociation.Specification.Task;
                    }
                    else
                    {
                        throw new CoordinationException(
                            "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
                    }

                    var childSessionId = fixedAssociation?.ChildSessionId ??
                        MafExecutionPlanner.CreateChildSessionId(identity, plan.Plan.Id, associationId);
                    var prompt = BuildPrompt(action, checkpoint.State, associationId, fixedAssociation);
                    var intent = new MafExecutionDispatchIntent(
                        associationId,
                        childSessionId,
                        MafExecutionPlanner.CreateDispatchMessageId(identity, plan.Plan.Id, associationId),
                        RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)));
                    var childIdentity = new SessionIdentity(projectId, runId, childSessionId);
                    var spawn = new SpawnSessionRequest(
                        childSessionId,
                        CoordinationSessionKind.ChildWork,
                        childSessionId,
                        task,
                        WorkPlanItemId: action.WorkItem?.Id);
                    await RevalidateDispatchAsync(cancellationToken).ConfigureAwait(false);
                    var checkedActor = await CreateRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
                    checkedActor.Bearer.Invalidate();
                    await events.EnsureSessionAsync(context, childIdentity, cancellationToken).ConfigureAwait(false);
                    await RevalidateDispatchAsync(cancellationToken).ConfigureAwait(false);
                    await store.SpawnSessionAsync(
                        actor,
                        identity,
                        spawn,
                        maxChildren,
                        maxConcurrentChildren,
                        cancellationToken,
                        workPlanAssociation,
                        persistSpawnedInTransaction: async (connection, transaction, spawned, token) =>
                        {
                            if (spawned.Node.Identity != childIdentity ||
                                spawned.Node.ParentSessionId != identity.SessionId ||
                                spawned.Node.RootSessionId != identity.SessionId ||
                                spawned.Node.Kind != CoordinationSessionKind.ChildWork ||
                                spawned.Node.ExecutionFence != decision.State.Fence)
                                throw new CoordinationException(
                                    "maf_execution_child_identity_conflict", StatusCodes.Status409Conflict);
                            await decisions.RequireCurrentDispatchStateInTransactionAsync(
                                connection, transaction, actor, identity, selection, selectionContext,
                                decision.StateVersion, decision.State.Fence, plan.Plan.Id, token)
                                .ConfigureAwait(false);
                            var latest = await execution.ReadLatestInTransactionAsync(
                                connection, transaction, token).ConfigureAwait(false);
                            if (latest is not null &&
                                (latest.State.WorkPlanId != plan.Plan.Id ||
                                 latest.State.DecisionStateVersion > decision.StateVersion))
                                throw new CoordinationException(
                                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
                            var next = PrepareRunningCheckpoint(
                                latest, plan.Plan.Id, decision.StateVersion, associationId, fixedAssociation, intent);
                            if (next is null)
                                return;
                            _ = await execution.AppendInTransactionAsync(
                                connection,
                                transaction,
                                Guid.NewGuid().ToString("N"),
                                latest?.Info,
                                next,
                                token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    checkpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new CoordinationException(
                            "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                    dispatched.Add(associationId);
                    readyDispatches.Add((action, intent, prompt));
                }

                if (readyDispatches.Count == 0)
                {
                    if (platformAdvanced)
                        continue;
                    break;
                }
                if (IsWallTimeLimitReached())
                {
                    wallTimeLimitReached = true;
                    break;
                }
                var deliveryResults = await Task.WhenAll(readyDispatches.Select(async dispatch =>
                    (dispatch.Action, dispatch.Intent,
                        Answer: await DeliverIntentAsync(
                            dispatch.Intent, dispatch.Prompt, cancellationToken).ConfigureAwait(false))))
                    .ConfigureAwait(false);
                foreach (var result in deliveryResults)
                {
                    if (result.Answer is null)
                    {
                        if (IsWallTimeLimitReached())
                            wallTimeLimitReached = true;
                        else
                            unavailable.Add(result.Action.Step.Id);
                    }
                    else
                        await CompleteDispatchAsync(
                            result.Intent, result.Answer, cancellationToken).ConfigureAwait(false);
                }
            }

            var finalCapacity = await store.ReadMafExecutionCapacityAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            var finalFrontier = MafExecutionPlanner.BuildFrontier(
                plan,
                checkpoint.State.Progress,
                finalCapacity.RegisteredChildren,
                finalCapacity.ActiveChildren,
                maxChildren,
                maxConcurrentChildren,
                hasNonModelExecutor: HasBuildTestExecutor,
                checkpoint.State.FixedWorkAssociations);
            unavailable.UnionWith(finalFrontier.UnavailableExecutorStepIds);
            if (finalFrontier.IsComplete)
                await AppendCompletedPlanWitnessAsync(cancellationToken).ConfigureAwait(false);
            await RevalidateDispatchAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new MafExecutionDispatchResponse(
                plan.Plan.Id,
                decision.StateVersion,
                checkpoint.State.Revision,
                dispatched.ToImmutable(),
                unavailable.Order(StringComparer.Ordinal).ToImmutableArray(),
                finalFrontier.FailedDependencyIds,
                finalFrontier.IsComplete)
            {
                CompletedResults = checkpoint.State.Results,
                WallTimeLimitReached = wallTimeLimitReached || IsWallTimeLimitReached()
            });
        }, cancellationToken);

    internal static MafExecutionCheckpoint? PrepareRunningCheckpoint(
        MafExecutionCheckpointSnapshot? latest,
        string workPlanId,
        long decisionStateVersion,
        string associationId,
        MafExecutionFixedWorkAssociation? fixedAssociation,
        MafExecutionDispatchIntent? intent = null)
    {
        if (latest is not null &&
            !string.Equals(latest.State.WorkPlanId, workPlanId, StringComparison.Ordinal))
            throw new CoordinationException(
                "maf_execution_plan_changed", StatusCodes.Status409Conflict);

        var state = latest?.State ?? new MafExecutionCheckpoint(
            0, workPlanId, decisionStateVersion, MafExecutionProgress.Empty);
        if (intent is not null &&
            (intent.AssociationId != associationId || intent.MessageId == Guid.Empty ||
             intent.PromptHash is not { Length: 64 } ||
             !intent.PromptHash.All(Uri.IsHexDigit)))
            throw new CoordinationException("maf_execution_dispatch_intent_invalid", StatusCodes.Status409Conflict);
        var pendingDispatches = state.PendingDispatches;
        var progress = state.Progress;
        var fixedAssociations = state.FixedWorkAssociations;
        if (fixedAssociation is null)
        {
            var status = progress.WorkItems.GetValueOrDefault(associationId);
            if (status == MafExecutionTaskStatus.Succeeded)
                return null;
            if (status == MafExecutionTaskStatus.Running)
            {
                if (intent is null)
                    return null;
                if (pendingDispatches.TryGetValue(associationId, out var existingIntent))
                {
                    if (existingIntent != intent)
                        throw new CoordinationException(
                            "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
                    return null;
                }
            }
            else if (status != MafExecutionTaskStatus.Pending)
                throw new CoordinationException(
                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
            else
            {
                progress = progress with
                {
                    WorkItems = progress.WorkItems.SetItem(associationId, MafExecutionTaskStatus.Running)
                };
            }
        }
        else
        {
            if (fixedAssociations.TryGetValue(associationId, out var existingAssociation) &&
                !FixedAssociationsMatch(existingAssociation, fixedAssociation))
                throw new CoordinationException(
                    "maf_execution_fixed_association_conflict", StatusCodes.Status409Conflict);
            var status = progress.FixedWorkItems.GetValueOrDefault(associationId);
            if (status == MafExecutionTaskStatus.Succeeded)
                return null;
            if (status == MafExecutionTaskStatus.Running)
            {
                if (intent is null)
                    return null;
                if (pendingDispatches.TryGetValue(associationId, out var existingIntent))
                {
                    if (existingIntent != intent)
                        throw new CoordinationException(
                            "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
                    return null;
                }
            }
            else if (status != MafExecutionTaskStatus.Pending)
                throw new CoordinationException(
                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
            fixedAssociations = fixedAssociations.SetItem(associationId, fixedAssociation);
            if (status == MafExecutionTaskStatus.Pending)
            {
                progress = progress with
                {
                    FixedWorkItems = progress.FixedWorkItems.SetItem(
                        associationId, MafExecutionTaskStatus.Running)
                };
            }
        }

        if (intent is not null)
            pendingDispatches = pendingDispatches.SetItem(associationId, intent);
        return state with
        {
            Revision = latest is null ? 1 : checked(state.Revision + 1),
            DecisionStateVersion = decisionStateVersion,
            Progress = progress,
            FixedWorkAssociations = fixedAssociations,
            PendingDispatches = pendingDispatches
        };
    }

    internal static MafExecutionCheckpoint? PrepareBuildTestCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionBuildTestIntent intent)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        var state = latest.State;
        if (state.BuildTestIntents.TryGetValue(intent.StepId, out var existing))
        {
            if (!MafExecutionCheckpointContract.BuildTestIntentsMatch(existing, intent))
                throw new CoordinationException(
                    "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (intent.CheckpointRevision != checked(state.Revision + 1) ||
            intent.WorkPlanId != state.WorkPlanId ||
            intent.Identity.SessionId != latest.Info.SessionId ||
            intent.DecisionStateVersion != state.DecisionStateVersion ||
            state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending ||
            state.Progress.NonModelSteps.Values.Any(status => status == MafExecutionTaskStatus.Running))
            throw new CoordinationException(
                "maf_execution_build_test_checkpoint_stale", StatusCodes.Status409Conflict);

        var next = state with
        {
            Revision = intent.CheckpointRevision,
            BuildTestIntents = state.BuildTestIntents.Add(intent.StepId, intent),
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(state, next);
        return next;
    }

    internal static MafExecutionCheckpoint? PrepareBuildTestCompletionCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation)
    {
        var status = MafBuildTestCommandContract.Classify(intent, operation);
        if (status == MafExecutionTaskStatus.Running)
            return null;
        if (!latest.State.BuildTestIntents.TryGetValue(intent.StepId, out var retained) ||
            !MafExecutionCheckpointContract.BuildTestIntentsMatch(intent, retained))
            throw new CoordinationException(
                "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
        var receipt = MafBuildTestTerminalReceipt.Create(intent, operation);
        _ = receipt.ValidateFor(intent);
        if (latest.State.BuildTestReceipts.TryGetValue(intent.StepId, out var previous))
        {
            var normalized = receipt with
            {
                UpdatedAt = previous.UpdatedAt,
                OperationEvidenceSha256 = previous.OperationEvidenceSha256
            };
            if (status != latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ||
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(previous), JsonSerializer.SerializeToElement(normalized)))
                throw new CoordinationException(
                    "maf_execution_build_test_terminal_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            BuildTestReceipts = latest.State.BuildTestReceipts.Add(intent.StepId, receipt),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(intent.StepId, status)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    internal static string BuildTestResultSummary(MafBuildTestTerminalReceipt receipt) =>
        JsonSerializer.Serialize(new
        {
            kind = "build-test",
            receipt.OperationId,
            receipt.Status,
            receipt.ImmutableHash,
            receipt.RequestFingerprint,
            exitCode = receipt.Terminal?.ExitCode,
            receipt.OutputSha256,
            receipt.OutputBytes,
            receipt.CollectorManifestSha256,
            outputCount = receipt.Outputs.Length,
            receipt.FailureCode
        });

    internal static bool IsCompletedDispatchReplay(
        MafExecutionCheckpoint state,
        MafExecutionDispatchIntent intent,
        MafExecutionDispatchIntent expectedIntent,
        string response)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(expectedIntent);
        ArgumentNullException.ThrowIfNull(response);
        if (intent != expectedIntent)
            throw new CoordinationException(
                "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
        var hasPendingIntent = state.PendingDispatches.TryGetValue(intent.AssociationId, out var pendingIntent);
        var hasResult = state.Results.TryGetValue(intent.AssociationId, out var recordedResult);
        if (hasPendingIntent && hasResult)
            throw new CoordinationException(
                "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
        if (hasPendingIntent)
        {
            if (pendingIntent != intent)
                throw new CoordinationException(
                    "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
            return false;
        }
        if (!hasResult || !string.Equals(recordedResult, response, StringComparison.Ordinal))
            throw new CoordinationException(
                "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
        return true;
    }

    internal static bool ShouldSendDispatchIntent(
        MafExecutionCheckpoint state, MafExecutionDispatchIntent intent, MafExecutionDispatchIntent expectedIntent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(expectedIntent);
        if (intent != expectedIntent)
            throw new CoordinationException("maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
        var hasPending = state.PendingDispatches.TryGetValue(intent.AssociationId, out var pending);
        var hasResult = state.Results.ContainsKey(intent.AssociationId);
        var status = state.Progress.WorkItems.TryGetValue(intent.AssociationId, out var workStatus)
            ? workStatus : state.Progress.FixedWorkItems.GetValueOrDefault(intent.AssociationId);
        if (hasPending && hasResult)
            throw new CoordinationException("maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
        if (hasPending)
        {
            if (pending != intent || status != MafExecutionTaskStatus.Running)
                throw new CoordinationException("maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
            return true;
        }
        if (hasResult && status == MafExecutionTaskStatus.Succeeded)
            return false;
        throw new CoordinationException("maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
    }

    private static bool FixedAssociationsMatch(
        MafExecutionFixedWorkAssociation expected,
        MafExecutionFixedWorkAssociation actual) =>
        expected.AssociationId == actual.AssociationId &&
        expected.ActivationRevision == actual.ActivationRevision &&
        expected.WorkPlanId == actual.WorkPlanId &&
        expected.WorkflowId == actual.WorkflowId &&
        expected.DefinitionRevision == actual.DefinitionRevision &&
        expected.CatalogVersion == actual.CatalogVersion &&
        expected.StepId == actual.StepId &&
        expected.AgentId == actual.AgentId &&
        expected.ModelSelectionReference == actual.ModelSelectionReference &&
        expected.IsolationProviderId == actual.IsolationProviderId &&
        expected.AcceptedSelectionHash == actual.AcceptedSelectionHash &&
        expected.ExecutionFence == actual.ExecutionFence &&
        expected.DecisionStateVersion == actual.DecisionStateVersion &&
        expected.ChildSessionId == actual.ChildSessionId &&
        expected.Specification.Title == actual.Specification.Title &&
        expected.Specification.Task == actual.Specification.Task &&
        expected.Specification.RoleId == actual.Specification.RoleId &&
        expected.Specification.Phase == actual.Specification.Phase &&
        expected.Specification.IsolationChoice == actual.Specification.IsolationChoice &&
        expected.Specification.DeclaredOutputs.SequenceEqual(
            actual.Specification.DeclaredOutputs, StringComparer.Ordinal);

    internal static Uri RuntimeHostEndpoint(Uri configureEndpoint, string path)
    {
        if (!RuntimeContractValidation.IsHttpsEndpoint(configureEndpoint) ||
            !path.StartsWith("/runtime/v1/", StringComparison.Ordinal) ||
            !Uri.TryCreate(configureEndpoint, path, out var endpoint) ||
            !RuntimeContractValidation.IsHttpsEndpoint(endpoint))
            throw new CoordinationException(
                "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
        return endpoint;
    }

    internal static async Task<TResponse?> SendRuntimeHostAsync<TResponse>(
        HttpClient client,
        Uri endpoint,
        RuntimeActorAuthorization actor,
        object? body,
        bool allowNotReady,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new() { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: RuntimeHostJsonOptions);

        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            throw new CoordinationException(
                body is null ? "maf_execution_host_unavailable" : "maf_execution_host_outcome_unknown",
                StatusCodes.Status503ServiceUnavailable,
                failure);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoordinationException(
                body is null ? "maf_execution_host_unavailable" : "maf_execution_host_outcome_unknown",
                StatusCodes.Status503ServiceUnavailable,
                failure);
        }

        using var response = sentResponse;
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri != endpoint)
            throw new CoordinationException(
                "maf_execution_host_redirect_rejected", StatusCodes.Status503ServiceUnavailable);
        if (response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException(
                "maf_execution_host_cache_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        if (allowNotReady && response.StatusCode == HttpStatusCode.Forbidden)
        {
            RuntimeHostError? error;
            try
            {
                error = await response.Content.ReadFromJsonAsync<RuntimeHostError>(
                    RuntimeHostJsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new CoordinationException(
                    "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
            }
            if (error?.Code is "runtime_host_not_ready" or "runtime_placement_not_ready")
                return null;
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            if (response.IsSuccessStatusCode)
                throw new CoordinationException(
                    "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
            throw new CoordinationException(
                "maf_execution_host_request_rejected",
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? (int)response.StatusCode
                    : StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(
                RuntimeHostJsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new CoordinationException(
                "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static async Task<RuntimeA2AResponse> SendRuntimeHostMessageAsync(
        HttpClient client,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness,
        RuntimeActorAuthorization actor,
        RuntimeA2ASendRequest message,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ValidateHostReadiness(readiness, registration, timeProvider);
        return await SendRuntimeHostAsync<RuntimeA2AResponse>(
                client,
                RuntimeHostEndpoint(
                    registration.Binding.ConfigureEndpoint, "/runtime/v1/a2a/message:send"),
                actor,
                message,
                allowNotReady: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_host_unavailable", StatusCodes.Status503ServiceUnavailable);
    }

    internal static void ValidateHostReadiness(
        RuntimeHostReadinessReceipt receipt,
        RuntimeRegistration registration,
        TimeProvider timeProvider)
    {
        var grant = receipt.SourceGrant;
        var expectedPhaseCount = Enum.GetValues<SandboxStartupPhase>().Length;
        if (receipt.ContractVersion != 1 ||
            receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExecutionFence != registration.Binding.ExecutionFence ||
            receipt.Image != registration.Binding.Image ||
            receipt.StartupPhases.IsDefault ||
            receipt.StartupPhases.Length != expectedPhaseCount ||
            receipt.StartupPhases.Any(phase => phase is null || !Enum.IsDefined(phase.Phase)) ||
            receipt.StartupPhases.Select(phase => phase.Phase).Distinct().Count() != expectedPhaseCount ||
            grant is null ||
            grant.GrantId == Guid.Empty ||
            grant.Revision < 1 ||
            grant.Purpose != RuntimeCredentialPurpose.Observe ||
            grant.State != RuntimeCredentialState.Active ||
            grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            grant.Audience != registration.Binding.ConfigureEndpoint ||
            grant.ExpiresAt <= timeProvider.GetUtcNow() ||
            grant.ExpiresAt > registration.ExpiresAt)
            throw new CoordinationException(
                "maf_execution_host_readiness_invalid", StatusCodes.Status503ServiceUnavailable);
    }

    private sealed record RuntimeHostError(string Code);
}
