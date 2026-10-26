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
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Agents.AI.Workflows;
using Npgsql;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Coordinates MAF work-plan dispatch, platform gate execution, and runtime-host message delivery.
/// The endpoints revalidate accepted selection and decision state around each durable or remote side effect.
/// </summary>
public static partial class CoordinationEndpoints
{
    private static readonly JsonSerializerOptions RuntimeHostJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Creates the checkpoint binding for one accepted session and selected model.
    /// </summary>
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

    internal static void RequireOpenPullRequestDispatchSource(
        MafExecutionOpenPullRequestIntent intent,
        MafExecutionCheckpoint checkpoint,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        long decisionStateVersion,
        long executionFence,
        string acceptedSelectionHash,
        SourceControlRepositoryPin pin)
    {
        // A completed PR receipt remains valid across a later Merge approval, but all source bindings stay current.
        var sourceDecisionVersion = decisionStateVersion;
        if (checkpoint.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
            MafExecutionTaskStatus.Succeeded)
        {
            if (!checkpoint.OpenPullRequestReceipts.TryGetValue(intent.StepId, out var receipt) ||
                MafOpenPullRequestContract.ValidateReceipt(intent, receipt) != MafExecutionTaskStatus.Succeeded)
                throw new CoordinationException(
                    "source_control_run_binding_changed", StatusCodes.Status409Conflict);
            sourceDecisionVersion = intent.DecisionStateVersion;
        }
        MafOpenPullRequestContract.RequireCurrentSource(
            intent, actor, identity, tenantId, sourceDecisionVersion,
            executionFence, acceptedSelectionHash, pin);
    }

    /// <summary>
    /// Advances a confirmed work plan by reconciling children, executing eligible platform steps, and dispatching ready work.
    /// </summary>
    /// <remarks>The handler persists dispatch intents before calling child hosts, rechecks authorization before completion, and appends an output witness only after the final frontier is complete.</remarks>
    private static Task<IResult> DispatchCoordinatorWorkPlanAsync(
        string projectId,
        string runId,
        string sessionId,
        MafExecutionDispatchRequest request,
        HttpContext context,
        [FromServices] OrchestratorOptions options,
        [FromServices] ProjectsRunSelectionClient projects,
        [FromServices] CoordinatorDecisionOwnerStore decisions,
        [FromServices] CoordinationOwnerStore store,
        [FromServices] CoordinatorRunSelectionContextStore runSelectionContexts,
        [FromServices] PostgresMafCheckpointStore checkpoints,
        [FromServices] EventsAddressedMessageClient events,
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
            var buildTestClient = context.RequestServices.GetService<MafBuildTestEnvironmentClient>();
            var sourceControlClient = context.RequestServices.GetService<MafSourceControlOwnerClient>();
            var registrationOwner = context.RequestServices.GetService<RuntimeRegistrationOwner>();
            var actionGrants = context.RequestServices.GetService<ExecutableActionGrantOwnerStore>();
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
            RuntimeRegistration? rubberduckOwnerRegistration = null;
            var hasRubberduckOwner = false;
            if (plan.Workflow.Definition.Steps.Any(step =>
                    step.PlatformGate == WorkflowPlatformGate.RubberDuck) &&
                registrationOwner is not null && actionGrants is not null)
            {
                try
                {
                    rubberduckOwnerRegistration = await registrationOwner.ReadCurrentForSessionAsync(
                        context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
                    CastingGenerationExecutionContract.RequireCurrentRegistration(
                        rubberduckOwnerRegistration,
                        projectId,
                        runId,
                        sessionId,
                        decision.State.Fence,
                        decision.SelectionHash,
                        context.RequestServices.GetRequiredService<TimeProvider>());
                    hasRubberduckOwner =
                        rubberduckOwnerRegistration.Binding.WorkflowStepId is null &&
                        rubberduckOwnerRegistration.Binding.AgentId == RuntimeActionContract.CoordinatorRootStepId;
                }
                catch (Exception failure) when (
                    failure is CoordinationException or RuntimeAuthorizationException)
                {
                    rubberduckOwnerRegistration = null;
                }
            }
            // Bind the accepted snapshot, not a model-supplied executor name or an edited project catalog.
            var bound = MafExecutionWorkflowBindings.RequireBound(
                plan,
                buildTestClient is not null,
                sourceControlClient is not null,
                sourceControlClient is not null,
                hasIndependentReviewOwner: true,
                hasRubberduckOwner: hasRubberduckOwner);
            if (request.BuildTestWorkspaces is { } workspaces)
            {
                if (workspaces.Count > plan.Workflow.Definition.Steps.Length ||
                    workspaces.Keys.Any(stepId => !plan.Workflow.Definition.Steps.Any(
                        step => step.Id == stepId && MafBuildTestCommandContract.IsExecutable(step))))
                    throw new CoordinationException(
                        "maf_execution_dispatch_invalid", StatusCodes.Status400BadRequest);
                foreach (var locator in workspaces.Values)
                    SourceControlEndpoints.ValidateBuildTestWorkspaceLocator(locator);
            }
            if (request.OpenPullRequestWorkspaces is { } pullRequestWorkspaces)
            {
                if (pullRequestWorkspaces.Count > plan.Workflow.Definition.Steps.Length ||
                    pullRequestWorkspaces.Any(item =>
                        !plan.Workflow.Definition.Steps.Any(step =>
                            step.Id == item.Key &&
                            step.Mode == WorkflowStepMode.Platform &&
                            step.PlatformGate == WorkflowPlatformGate.OpenPullRequest) ||
                        !MafOpenPullRequestContract.IsValidWorkspace(item.Value)))
                    throw new CoordinationException(
                        "source_control_pull_request_request_invalid", StatusCodes.Status400BadRequest);
            }
            if (request.MergeInputs is { } mergeInputs &&
                (mergeInputs.Count > plan.Workflow.Definition.Steps.Length ||
                 mergeInputs.Any(item =>
                     !plan.Workflow.Definition.Steps.Any(step =>
                         step.Id == item.Key &&
                         step.Mode == WorkflowStepMode.Platform &&
                         step.PlatformGate == WorkflowPlatformGate.Merge) ||
                     !MafMergeContract.IsValidInput(item.Value))))
                throw new CoordinationException(
                    "source_control_merge_request_invalid", StatusCodes.Status400BadRequest);
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
                    !MafIndependentReviewContract.AllowsDecisionVersionAdvance(
                        checkpoint.State, decision, association.DecisionStateVersion)))
                throw new CoordinationException(
                    "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);
            if (checkpoint is not null)
                foreach (var intent in checkpoint.State.OpenPullRequestIntents.Values)
                {
                    if (request.OpenPullRequestWorkspaces?.TryGetValue(intent.StepId, out var locator) == true &&
                        locator != intent.Workspace)
                        throw new CoordinationException(
                            "source_control_run_binding_changed", StatusCodes.Status409Conflict);
                    var currentPin = await context.RequestServices.GetRequiredService<SourceControlOwnerStore>()
                        .ReadRepositoryPinAsync(actor, identity, selection, cancellationToken)
                        .ConfigureAwait(false);
                    RequireOpenPullRequestDispatchSource(
                        intent,
                        checkpoint.State,
                        actor,
                        identity,
                        selection.Authorization.TenantId,
                        decision.StateVersion,
                        decision.State.Fence,
                        decision.SelectionHash,
                        currentPin);
                }
            if (checkpoint is not null)
                foreach (var intent in checkpoint.State.MergeIntents.Values)
                {
                    if (request.MergeInputs?.TryGetValue(intent.StepId, out var mergeInput) == true &&
                        (mergeInput.Method != intent.Method ||
                         mergeInput.PullRequestNumber is { } suppliedNumber &&
                             suppliedNumber != intent.PullRequestNumber ||
                         mergeInput.ExpectedHeadSha is { } suppliedHead &&
                             !string.Equals(
                                 suppliedHead, intent.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase)))
                        throw new CoordinationException(
                            "source_control_run_binding_changed", StatusCodes.Status409Conflict);
                    var currentPin = await context.RequestServices.GetRequiredService<SourceControlOwnerStore>()
                        .ReadRepositoryPinAsync(actor, identity, selection, cancellationToken)
                        .ConfigureAwait(false);
                    MafMergeContract.RequireCurrentSource(
                        intent, actor, identity, selection.Authorization.TenantId,
                        decision.StateVersion, decision.State, decision.State.Fence,
                        decision.SelectionHash, currentPin);
                }
            if (checkpoint is not null)
                foreach (var intent in checkpoint.State.BuildTestIntents.Values)
                {
                    var authorizationReference = CreateBuildTestDispatchAuthorizationReference(
                        intent, checkpoint.State.Progress, identity, decision);
                    if (request.BuildTestWorkspaces?.TryGetValue(intent.StepId, out var locator) == true)
                        SourceControlEndpoints.RequireSameBuildTestWorkspace(intent.EffectiveWorkspaceSource, locator);
                    if (intent.EffectiveWorkspaceSource is { } source)
                        await SourceControlEndpoints.RequireCurrentBuildTestInputAsync(
                            context, source, authorizationReference, options, projects, decisions,
                            context.RequestServices.GetRequiredService<SourceControlOwnerStore>(), cancellationToken)
                            .ConfigureAwait(false);
                }

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
                // Serialize reconciliation with the owner decision and latest checkpoint; a stale
                // pre-I/O observation cannot append over an intervening dispatch or replan.
                await using var connection = await context.RequestServices
                    .GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    decision.StateVersion, decision.State.Fence, plan.Plan.Id, cancellationToken)
                    .ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var current = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                if (!SameCheckpointInfo(current?.Info, checkpoint?.Info))
                    throw new CoordinationException(
                        "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
                var next = (checkpoint?.State ??
                    new MafExecutionCheckpoint(0, plan.Plan.Id, decision.StateVersion, reconciled)) with
                {
                    Revision = checkpoint is null ? 1 : checked(checkpoint.State.Revision + 1),
                    DecisionStateVersion = decision.StateVersion,
                    Progress = reconciled
                };
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, Guid.NewGuid().ToString("N"),
                    checkpoint?.Info, next, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var maxChildren = CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot);
            var maxConcurrentChildren =
                CoordinatorWorkflowCatalog.ReadMaxConcurrentChildren(selection.Selection.Snapshot);
            var maxWallTimeSeconds =
                CoordinatorWorkflowCatalog.ReadMaxWallTimeSeconds(selection.Selection.Snapshot);
            _ =
                CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection.Selection.Snapshot);
            // Run and agent spend budgets are pinned in the accepted selection and do not change for
            // the life of the run. Project budgets are dropped here because they can change at any
            // time; they are read from Projects again before each dispatch.
            var pinnedSpendBudgets = CoordinatorWorkflowCatalog.ReadSpendBudgets(selection.Selection.Snapshot)
                .Where(budget => budget.Scope != SpendBudgetScopes.Project).ToImmutableArray();
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

            var environment = context.RequestServices.GetService<RuntimeEnvironmentContextClient>();
            var egress = context.RequestServices.GetService<RuntimeEgressVerificationHttpClient>();
            var childSpawnAuthorization = context.RequestServices
                .GetRequiredService<MafChildSpawnAuthorizationOwner>();
            var runtimeSourceOwner = context.RequestServices.GetService<RuntimeUsageSourceOwner>();
            var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
            bool HasBuildTestExecutor(WorkflowStepDefinition step) =>
                buildTestClient is not null && MafBuildTestCommandContract.IsExecutable(step) &&
                (request.BuildTestEnvironmentId is not null ||
                 checkpoint.State.BuildTestIntents.ContainsKey(step.Id));
            bool HasPlatformExecutor(WorkflowStepDefinition step) =>
                (sourceControlClient is not null &&
                    step.PlatformGate is WorkflowPlatformGate.OpenPullRequest or WorkflowPlatformGate.Merge) ||
                step.PlatformGate == WorkflowPlatformGate.IndependentReview ||
                step.PlatformGate == WorkflowPlatformGate.RubberDuck && hasRubberduckOwner ||
                HasBuildTestExecutor(step);
            var hostClient = context.RequestServices.GetRequiredService<IHttpClientFactory>()
                .CreateClient("maf-execution-host");
            var dispatched = ImmutableArray.CreateBuilder<string>();
            var unavailable = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            (string OwnerIntentId, string ApprovalRequestId)? pendingMergeApproval = null;
            (string EvidenceHash, string ApprovalRequestId)? pendingReviewApproval = null;
            var responseDecisionStateVersion = decision.StateVersion;

            async Task RevalidateDispatchAsync(CancellationToken token)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                var current = await decisions.ReadCurrentAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                var dispatchStillCurrent =
                    current.StateVersion == decision.StateVersion &&
                    current.State.Fence == decision.State.Fence &&
                    current.State.CanDispatch;
                var exactPendingMergeApproval =
                    pendingMergeApproval is { } pending &&
                    current.StateVersion == checked(decision.StateVersion + 1) &&
                    current.State.Fence == decision.State.Fence &&
                    current.State.PendingGate is { } gate &&
                    gate.RequestId == pending.ApprovalRequestId &&
                    gate.SubjectId == pending.OwnerIntentId;
                var exactPendingReviewApproval =
                    pendingReviewApproval is { } review &&
                    current.StateVersion == checked(decision.StateVersion + 1) &&
                    current.State.Fence == decision.State.Fence &&
                    current.State.PendingGate is { } reviewGate &&
                    reviewGate.RequestId == review.ApprovalRequestId &&
                    reviewGate.SubjectId == review.EvidenceHash;
                if ((!dispatchStillCurrent && !exactPendingMergeApproval && !exactPendingReviewApproval) ||
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

            async Task<RuntimeEgressVerificationReceipt> VerifyEgressAsync(CancellationToken token)
            {
                if (registrationOwner is null)
                    throw new CoordinationException(
                        "maf_execution_egress_unavailable", StatusCodes.Status503ServiceUnavailable);
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                try
                {
                    // Map both the current registration read and remote verification into stable endpoint failures.
                    var registration = await registrationOwner.ReadCurrentForSessionAsync(
                        context, projectId, runId, sessionId, token).ConfigureAwait(false);
                    return await VerifyEgressForRegistrationAsync(registration, sessionId, token)
                        .ConfigureAwait(false);
                }
                catch (RuntimeAuthorizationException failure)
                {
                    throw MafExecutionEgressGuard.MapVerificationFailure(failure);
                }
            }

            async Task<RuntimeEgressVerificationReceipt> VerifyEgressForRegistrationAsync(
                RuntimeRegistration registration, string targetSessionId, CancellationToken token)
            {
                if (registrationOwner is null || environment is null || egress is null)
                    throw new CoordinationException(
                        "maf_execution_egress_unavailable", StatusCodes.Status503ServiceUnavailable);
                RuntimeActorAuthorization? runtimeActor = null;
                try
                {
                    // Actor expiry is an expected authorization outcome and must use the mapped response contract.
                    runtimeActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                    var binding = registration.Binding;
                    var readiness = await environment.ReadReadinessAsync(
                        runtimeActor, projectId, runId, targetSessionId,
                        binding.EnvironmentId, binding.ProfileId, token)
                        .ConfigureAwait(false);
                    var request = MafExecutionEgressGuard.RequireReadiness(
                        registration, readiness, projectId, runId, targetSessionId,
                        selection.Selection.ContextRevision, decision.SelectionHash, decision.State.Fence,
                        timeProvider.GetUtcNow());
                    var receipt = await egress.VerifyAsync(request, runtimeActor, token).ConfigureAwait(false);
                    // Re-read the owner after remote I/O: an old receipt cannot authorize a replaced registration.
                    var current = await registrationOwner.ReadCurrentForSessionAsync(
                        context, projectId, runId, targetSessionId, token).ConfigureAwait(false);
                    if (current != registration)
                        throw new CoordinationException(
                            "maf_execution_egress_stale", StatusCodes.Status409Conflict);
                    await RevalidateDispatchAsync(token).ConfigureAwait(false);
                    return receipt;
                }
                catch (RuntimeAuthorizationException failure)
                {
                    // Readiness, actor, and Environment failures are expected fail-closed outcomes, not server faults.
                    throw MafExecutionEgressGuard.MapVerificationFailure(failure);
                }
                finally
                {
                    runtimeActor?.Bearer.Invalidate();
                }
            }

            CoordinationException MapChildSpawnAuthorizationFailure(RuntimeAuthorizationException failure)
            {
                // Preserve final denials and stale authority as 403/409; unavailable policy evidence is retryable.
                var statusCode = failure.Code switch
                {
                    "maf_child_spawn_not_authorized" => StatusCodes.Status403Forbidden,
                    "maf_child_spawn_policy_unavailable" or
                    "maf_child_spawn_decision_unavailable" or
                    "maf_child_spawn_authorization_unavailable" or
                    "maf_child_spawn_authorization_missing" or
                    "maf_child_spawn_policy_receipt_unavailable" =>
                        StatusCodes.Status503ServiceUnavailable,
                    // Unknown authorization failures indicate a stale or conflicting intent, never an implicit allow.
                    _ => StatusCodes.Status409Conflict
                };
                return new CoordinationException(failure.Code, statusCode, failure);
            }

            async Task RequireChildSpawnAllowedAsync(
                MafChildSpawnAuthorizationIntent intent, CancellationToken token)
            {
                try
                {
                    // The owner evaluates the pinned Policy provider and persists allow evidence for this exact intent.
                    var evidence = await childSpawnAuthorization.AuthorizeAsync(
                        context, intent, token).ConfigureAwait(false);
                    // Confirm the owner returned evidence for the exact deterministic intent before spawning.
                    if (evidence.IntentId != intent.IntentId ||
                        !string.Equals(
                            evidence.IntentHash,
                            MafChildSpawnAuthorizationContract.ComputeIntentHash(intent),
                            StringComparison.Ordinal) ||
                        evidence.Outcome != PolicyEvaluationOutcome.Allow ||
                        evidence.ReasonCode != PolicyEvaluationReasonCode.Allowed)
                        throw new CoordinationException(
                            "maf_child_spawn_authorization_invalid", StatusCodes.Status409Conflict);
                }
                // Convert owner-specific authorization failures into the endpoint's stable response contract.
                catch (RuntimeAuthorizationException failure)
                {
                    throw MapChildSpawnAuthorizationFailure(failure);
                }
            }

            async Task RequireChildSpawnConsumedAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                MafChildSpawnAuthorizationIntent intent,
                CancellationToken token)
            {
                try
                {
                    // Consumption shares the child/outbox/checkpoint transaction; a failure rolls back all four writes.
                    var consumed = await childSpawnAuthorization.CheckAndConsumeWithinTransactionAsync(
                        context, connection, transaction, intent, token).ConfigureAwait(false);
                    // Match either supported intent variant to the same immutable allow and consumption row.
                    if (consumed.Evidence.IntentId != intent.IntentId ||
                        !string.Equals(
                            consumed.Evidence.IntentHash,
                            MafChildSpawnAuthorizationContract.ComputeIntentHash(intent),
                            StringComparison.Ordinal) ||
                        consumed.Evidence.Outcome != PolicyEvaluationOutcome.Allow ||
                        consumed.Evidence.ReasonCode != PolicyEvaluationReasonCode.Allowed ||
                        consumed.Evidence.ConsumedAt is null)
                        throw new CoordinationException(
                            "maf_child_spawn_authorization_invalid", StatusCodes.Status409Conflict);
                    // Either first consumption or exact replay is valid because both refer to the committed spawn.
                }
                // A missing, stale, conflicting or revoked receipt must abort the enclosing spawn transaction.
                catch (RuntimeAuthorizationException failure)
                {
                    throw MapChildSpawnAuthorizationFailure(failure);
                }
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
                    // The child can have its own Environment; native admission pins its own current registration.
                    var receipt = intent.EgressReceipt;
                    if (receipt is null ||
                        registration.Binding.TenantId != receipt.Fence.Owner.TenantId ||
                        registration.Binding.ContextRevision != receipt.ContextRevision)
                        throw new CoordinationException(
                            "maf_execution_egress_stale", StatusCodes.Status409Conflict);
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
                // Stop sending new work after the run's wall-time limit.
                if (IsWallTimeLimitReached())
                    return null;
                // The prompt must be exactly the prompt recorded in the dispatch intent.
                if (RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)) != intent.PromptHash)
                    throw new CoordinationException(
                        "maf_execution_dispatch_intent_conflict", StatusCodes.Status409Conflict);
                // If the intent already has a stored result, return it instead of sending again.
                var pending = await ReadCurrentDispatchIntentAsync(intent, token).ConfigureAwait(false);
                if (!pending.ShouldSend)
                    return pending.Snapshot.State.Results[intent.AssociationId];
                MafExecutionEgressGuard.RequireSameReceipt(
                    intent.EgressReceipt, await VerifyEgressAsync(token).ConfigureAwait(false));
                // A null registration means the child runtime is not registered or not ready yet.
                // The dispatch is not sent now and is retried later.
                var registration = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (registration is null)
                    return null;
                var readinessEndpoint = RuntimeHostEndpoint(
                    registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness");
                // Recheck the decision state and registration before calling the host, because both
                // can change between awaits.
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var current = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (current is null)
                    return null;
                if (current != registration)
                    throw new CoordinationException(
                        "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                _ = await VerifyEgressForRegistrationAsync(
                    registration, intent.ChildSessionId, token).ConfigureAwait(false);
                RuntimeHostReadinessReceipt? readiness;
                // Ask the runtime host whether it is ready. The short-lived actor token is
                // invalidated right after the call.
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

                // The host call can take time, so check the registration and decision state again.
                current = await ReadDispatchRegistrationAsync(intent, token).ConfigureAwait(false);
                if (current is null)
                    return null;
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                if (current != registration)
                    throw new CoordinationException(
                        "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                // Build the message for the host. Its message ID is the dispatch ID used for the
                // dispatch row and for any spend budget reservation.
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
                    // Read the intent again just before preparation; another request may have
                    // completed it in the meantime.
                    var currentIntent = await ReadCurrentDispatchIntentAsync(intent, token).ConfigureAwait(false);
                    if (!currentIntent.ShouldSend)
                        return currentIntent.Snapshot.State.Results[intent.AssociationId];
                    MafExecutionEgressGuard.RequireSameReceipt(
                        intent.EgressReceipt, await VerifyEgressAsync(token).ConfigureAwait(false));
                    // Native dispatches require the usage source owner to record admission and cost.
                    if (runtimeSourceOwner is null)
                        throw new CoordinationException(
                            "maf_execution_native_source_unavailable", StatusCodes.Status503ServiceUnavailable);
                    // Read the current project budgets for each dispatch, so a lowered project budget
                    // applies to the next dispatch without restarting the run.
                    var projectSpendBudgets = await projects.ReadCurrentProjectSpendBudgetsAsync(
                        context, projectId, runId, token).ConfigureAwait(false);
                    // Prepare the native turn in one transaction: owner authority, fence, native
                    // limits, credit snapshot, and spend budget admission, then the dispatch row. The
                    // owner check delegate runs before and after that work. Any denial or failure
                    // throws here, so the message below is never sent. An empty project budget list
                    // is passed as null, which means the project has no project budgets.
                    await runtimeSourceOwner.PrepareNativeTurnAsync(
                        context, registration, identity, currentIntent.Snapshot, intent, message,
                        (connection, transaction, currentToken) =>
                            decisions.RequireCurrentDispatchStateInTransactionAsync(
                                connection, transaction, actor, identity, selection, selectionContext,
                                decision.StateVersion, decision.State.Fence, plan.Plan.Id, currentToken), token,
                        new SpendBudgetAdmissionContext(pinnedSpendBudgets,
                            projectSpendBudgets.Budgets.IsDefaultOrEmpty ? null : projectSpendBudgets))
                        .ConfigureAwait(false);
                    // Preparation committed, so the dispatch is admitted. Send it to the runtime host.
                    MafExecutionEgressGuard.RequireSameReceipt(
                        intent.EgressReceipt, await VerifyEgressAsync(token).ConfigureAwait(false));
                    var sendingRegistration = await ReadDispatchRegistrationAsync(intent, token)
                        .ConfigureAwait(false);
                    if (sendingRegistration != registration)
                        throw new CoordinationException(
                            "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                    _ = await VerifyEgressForRegistrationAsync(
                        registration, intent.ChildSessionId, token).ConfigureAwait(false);
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
                // Accept only a single non-empty agent text reply for this native session, within the
                // size limit and without control characters other than line breaks and tabs.
                if (response.Kind != "message" || response.MessageId == Guid.Empty ||
                    response.ContextId != expectedContext || response.Role != "agent" ||
                    response.Parts is not [{ Kind: "text", Text: { Length: > 0 } answer }] ||
                    answer.Length > AddressedMessageValidation.MaximumTextLength ||
                    answer.Any(character => char.IsControl(character) &&
                        character is not ('\r' or '\n' or '\t')))
                    throw new CoordinationException(
                        "maf_execution_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
                // The answer is returned only after the native turn's usage is accounted.
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
                var pullRequestResults = plan.Workflow.Definition.Steps
                    .Where(step =>
                        dependencySteps.Contains(step.Id) &&
                        step.PlatformGate == WorkflowPlatformGate.OpenPullRequest)
                    .Select(step => state.OpenPullRequestReceipts.TryGetValue(step.Id, out var receipt)
                        ? (step.Id, Receipt: receipt)
                        : throw new CoordinationException(
                            "maf_execution_dependency_result_unavailable", StatusCodes.Status409Conflict))
                    .ToArray();
                var mergeResults = plan.Workflow.Definition.Steps
                    .Where(step =>
                        dependencySteps.Contains(step.Id) &&
                        step.PlatformGate == WorkflowPlatformGate.Merge)
                    .Select(step => state.MergeReceipts.TryGetValue(step.Id, out var receipt)
                        ? (step.Id, Receipt: receipt)
                        : throw new CoordinationException(
                            "maf_execution_dependency_result_unavailable", StatusCodes.Status409Conflict))
                    .ToArray();

                if (requiredResults.Count == 0 &&
                    platformResults.Length == 0 &&
                    pullRequestResults.Length == 0 &&
                    mergeResults.Length == 0)
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
                foreach (var (stepId, receipt) in pullRequestResults)
                    prompt.Append("\n\n[").Append(stepId).Append("]\n")
                        .Append(MafOpenPullRequestContract.ResultSummary(receipt));
                foreach (var (stepId, receipt) in mergeResults)
                    prompt.Append("\n\n[").Append(stepId).Append("]\n")
                        .Append(MafMergeContract.ResultSummary(receipt));
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
                    RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)))
                {
                    EgressReceipt = intent.EgressReceipt
                };
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
                MafExecutionEgressGuard.RequireSameReceipt(
                    intent.EgressReceipt, await VerifyEgressAsync(token).ConfigureAwait(false));
                var currentChild = await ReadDispatchRegistrationAsync(intent, token)
                    .ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_child_registration_stale", StatusCodes.Status409Conflict);
                _ = await VerifyEgressForRegistrationAsync(
                    currentChild, intent.ChildSessionId, token).ConfigureAwait(false);
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
                if (latest.Info.SessionId != expectedCheckpoint.Info.SessionId ||
                    latest.Info.CheckpointId != expectedCheckpoint.Info.CheckpointId ||
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
                await RequireCurrentBuildTestAsync(token).ConfigureAwait(false);
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
                if (operation.Status == SandboxBuildTestOperationStatus.Completed &&
                    intent.EffectiveWorkspaceSource is not null)
                {
                    var hydrated = await execution.HydrateBuildTestIntentAsync(intent, token).ConfigureAwait(false);
                    var sourceControl = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                    var acknowledgment = await SourceControlEndpoints.AdmitBuildTestOutputAsync(
                        context, hydrated, operation, options, projects, decisions, store, sourceControl, events,
                        async readToken =>
                        {
                            var captureActor = await CreateRuntimeActorAsync(readToken).ConfigureAwait(false);
                            try
                            {
                                return await buildTestClient.ReadWorkspaceCaptureAsync(
                                    captureActor, intent, operation.WorkspaceCapture!, readToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                captureActor.Bearer.Invalidate();
                            }
                        },
                        RequireCurrentBuildTestAsync, token).ConfigureAwait(false);
                    await RequireCurrentBuildTestAsync(token).ConfigureAwait(false);
                    var acknowledgmentActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                    try
                    {
                        var acknowledged = await buildTestClient.AcknowledgeWorkspaceCaptureAsync(
                            acknowledgmentActor, intent, acknowledgment, token).ConfigureAwait(false);
                        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(operation.WorkspaceCapture),
                                JsonSerializer.SerializeToElement(acknowledged.WorkspaceCapture)))
                            throw new CoordinationException("maf_execution_workspace_capture_acknowledgment_conflict",
                                StatusCodes.Status409Conflict);
                        operation = acknowledged;
                    }
                    finally
                    {
                        acknowledgmentActor.Bearer.Invalidate();
                    }
                }
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
                if (operation.WorkspaceCaptureAcknowledgment is { } admittedEntry &&
                    intent.EffectiveWorkspaceSource is { } workspaceSource &&
                    operation.WorkspaceCapture is { } captureReference)
                {
                    var sourceControl = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                    var (capture, _) = await sourceControl.ReadPrerequisiteOutputCaptureInTransactionAsync(
                        connection, transaction, actor, identity, workspaceSource.TenantId,
                        workspaceSource.AcceptedSelectionHash, intent.ExecutionFence,
                        admittedEntry.Capture.CaptureId, token).ConfigureAwait(false);
                    if (capture is null ||
                        SourceControlEndpoints.ReadBuildTestAcknowledgment(capture, captureReference, workspaceSource) !=
                            admittedEntry)
                        throw new CoordinationException("maf_execution_workspace_capture_acknowledgment_conflict",
                            StatusCodes.Status409Conflict);
                }
                var next = PrepareBuildTestCompletionCheckpoint(latest, intent, operation);
                checkpoint = next is null
                    ? latest
                    : await execution.AppendInTransactionAsync(
                        connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, token)
                        .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;

                async Task RequireCurrentBuildTestAsync(CancellationToken currentToken)
                {
                    await RevalidateDispatchAsync(currentToken).ConfigureAwait(false);
                    var current = await execution.ReadLatestAsync(currentToken).ConfigureAwait(false) ??
                        throw new CoordinationException("maf_execution_checkpoint_unavailable",
                            StatusCodes.Status503ServiceUnavailable);
                    if (current.State.WorkPlanId != intent.WorkPlanId ||
                        current.State.DecisionStateVersion != intent.DecisionStateVersion ||
                        !current.State.BuildTestIntents.TryGetValue(intent.StepId, out var retained) ||
                        !MafExecutionCheckpointContract.BuildTestIntentsMatch(intent, retained))
                        throw new CoordinationException("maf_execution_build_test_intent_conflict",
                            StatusCodes.Status409Conflict);
                }
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
                if (request.BuildTestWorkspaces?.ContainsKey(step.Id) == true &&
                    preparation.ExecutionOptions.CollectorMode != SandboxBuildTestLimits.WorkspaceCollectorMode)
                    throw new CoordinationException(
                        "maf_execution_build_test_workspace_profile_unavailable", StatusCodes.Status409Conflict);
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
                    if (request.BuildTestWorkspaces?.TryGetValue(step.Id, out var retainedLocator) == true)
                        SourceControlEndpoints.RequireSameBuildTestWorkspace(
                            retained.EffectiveWorkspaceSource, retainedLocator);
                    if (retained.EffectiveWorkspaceSource is { } retainedSource)
                        await context.RequestServices.GetRequiredService<SourceControlOwnerStore>()
                            .RequireBuildTestInputInTransactionAsync(
                                connection, transaction, actor, identity, selection, decision.StateVersion,
                                decision.State.Fence, retainedSource, token).ConfigureAwait(false);
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }
                var checkpointId = Guid.NewGuid().ToString("N");
                var intent = new MafExecutionBuildTestIntent(
                    Guid.NewGuid(), identity, checkpointId, checked(latest.State.Revision + 1),
                    plan.Plan.Id, step.Id, decision.StateVersion, decision.State.Fence, decision.SelectionHash,
                    step.BuildTestCommand, preparation.ExecutionOptions, preparation.ExpectedBinding);
                if (request.BuildTestWorkspaces?.TryGetValue(step.Id, out var locator) == true)
                {
                    var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                    var workspaceManager = context.RequestServices.GetService<GitWorkspaceManager>()
                        ?? throw new CoordinationException(
                            "source_control_workspace_unconfigured", StatusCodes.Status503ServiceUnavailable);
                    var input = await SourceControlEndpoints.PrepareBuildTestInputAsync(
                        context, actor, intent.CheckpointReference(), intent.OperationId, locator,
                        step.BuildTestCommand.WorkspaceSource, options, projects, decisions, sourceOwner,
                        context.RequestServices.GetRequiredService<ISourceControlAdapter>(),
                        workspaceManager, token).ConfigureAwait(false);
                    intent = intent.AttachWorkspaceInput(input.Source, input.Seed);
                    await RevalidateDispatchAsync(token).ConfigureAwait(false);
                    await sourceOwner.RequireBuildTestInputInTransactionAsync(
                        connection, transaction, actor, identity, selection, decision.StateVersion,
                        decision.State.Fence, input.Source, token).ConfigureAwait(false);
                }
                var next = PrepareBuildTestCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, checkpointId, latest.Info, next, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return intent;
            }

            async Task RevalidateMergeDispatchAsync(CancellationToken token)
            {
                // A stale coordinator snapshot is a Source Control binding failure at this owner boundary.
                try
                {
                    await RevalidateDispatchAsync(token).ConfigureAwait(false);
                }
                catch (CoordinationException failure) when (failure.Code == "maf_execution_dispatch_stale")
                {
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict, failure);
                }
            }

            async Task RequireCurrentMergeAsync(
                MafExecutionMergeIntent intent, long? ownerApprovalStateVersion, CancellationToken token)
            {
                // Re-read the decision and pin after approval; the original checkpoint may predate the gate answer.
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                var currentDecision = await decisions.ReadCurrentAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                var pin = await sourceOwner.ReadRepositoryPinAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                MafMergeContract.RequireCurrentSource(
                    intent, actor, identity, selection.Authorization.TenantId,
                    currentDecision.StateVersion, currentDecision.State, currentDecision.State.Fence,
                    currentDecision.SelectionHash, pin, ownerApprovalStateVersion);
                var latest = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.WorkPlanId != intent.WorkPlanId ||
                    latest.State.DecisionStateVersion != currentDecision.StateVersion ||
                    !latest.State.MergeIntents.TryGetValue(intent.StepId, out var retained) ||
                    !MafExecutionCheckpointContract.MergeIntentsMatch(intent, retained) ||
                    latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
                        MafExecutionTaskStatus.Running)
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict);
            }

            async Task RequireMergeDecisionInTransactionAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                CancellationToken token)
            {
                // Keep the decision version check in the same transaction as the checkpoint lock.
                try
                {
                    await decisions.RequireCurrentDispatchStateInTransactionAsync(
                        connection, transaction, actor, identity, selection, selectionContext,
                        decision.StateVersion, decision.State.Fence, plan.Plan.Id, token)
                        .ConfigureAwait(false);
                }
                catch (CoordinationException failure) when (failure.Code == "maf_execution_dispatch_stale")
                {
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict, failure);
                }
            }

            var mergeExecutor = sourceControlClient is null
                ? null
                : new MafMergeExecutor(
                    execution,
                    sourceControlClient,
                    dataSource,
                    CreateRuntimeActorAsync,
                    RequireCurrentMergeAsync,
                    RequireMergeDecisionInTransactionAsync,
                    IsWallTimeLimitReached,
                    () => wallTimeLimitReached = true,
                    snapshot => checkpoint = snapshot,
                    (ownerIntentId, approvalRequestId) =>
                    {
                        pendingMergeApproval = (ownerIntentId, approvalRequestId);
                        responseDecisionStateVersion = checked(decision.StateVersion + 1);
                    });

            async Task<MafExecutionMergeIntent> PrepareMergeAsync(
                WorkflowStepDefinition step, CancellationToken token)
            {
                // Persist one exact owner identity before the first merge-intent request.
                if (sourceControlClient is null)
                    throw new CoordinationException(
                        "source_control_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (step.Mode != WorkflowStepMode.Platform ||
                    step.PlatformGate != WorkflowPlatformGate.Merge ||
                    request.MergeInputs?.GetValueOrDefault(step.Id) is not { } input ||
                    !MafMergeContract.IsValidInput(input))
                    throw new CoordinationException(
                        "source_control_merge_request_invalid", StatusCodes.Status400BadRequest);

                await RevalidateMergeDispatchAsync(token).ConfigureAwait(false);
                var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                var pin = await sourceOwner.ReadRepositoryPinAsync(
                    actor, identity, selection, token).ConfigureAwait(false);

                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await RequireMergeDecisionInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.MergeIntents.TryGetValue(step.Id, out var retained))
                {
                    MafMergeContract.RequireCurrentSource(
                        retained, actor, identity, selection.Authorization.TenantId,
                        decision.StateVersion, decision.State, decision.State.Fence,
                        decision.SelectionHash, pin);
                    if (retained.Method != input.Method ||
                        input.PullRequestNumber is { } suppliedNumber &&
                            suppliedNumber != retained.PullRequestNumber ||
                        input.ExpectedHeadSha is { } suppliedHead &&
                            !string.Equals(
                                suppliedHead, retained.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
                        throw new CoordinationException(
                            "source_control_run_binding_changed", StatusCodes.Status409Conflict);
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }

                var (pullRequestNumber, expectedHeadSha) = MafMergeContract.ResolvePullRequest(
                    plan.Workflow.Definition, step, latest.State, input);
                var checkpointId = Guid.NewGuid().ToString("N");
                var intent = MafMergeContract.CreateIntent(
                    Guid.NewGuid(), identity, checkpointId, checked(latest.State.Revision + 1),
                    plan.Plan.Id, plan.Workflow.Definition.Id, plan.Workflow.Definition.Revision,
                    step.Id, decision.StateVersion, decision.State.Fence, decision.SelectionHash,
                    pin, pullRequestNumber, expectedHeadSha, input.Method);
                var next = PrepareMergeCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_merge_intent_conflict", StatusCodes.Status409Conflict);
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, checkpointId, latest.Info, next, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return intent;
            }

            async Task<MafMergeExecutionOutcome> ExecuteMergeAsync(
                MafExecutionMergeIntent intent, CancellationToken token)
            {
                if (mergeExecutor is null)
                    throw new CoordinationException(
                        "source_control_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                return await mergeExecutor.ExecuteAsync(intent, token).ConfigureAwait(false);
            }

            async Task RequireCurrentOpenPullRequestAsync(
                MafExecutionOpenPullRequestIntent intent, CancellationToken token)
            {
                await RevalidateOpenPullRequestDispatchAsync(token).ConfigureAwait(false);
                var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                var pin = await sourceOwner.ReadRepositoryPinAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                MafOpenPullRequestContract.RequireCurrentSource(
                    intent, actor, identity, selection.Authorization.TenantId,
                    decision.StateVersion, decision.State.Fence, decision.SelectionHash, pin);
                var latest = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.WorkPlanId != intent.WorkPlanId ||
                    latest.State.DecisionStateVersion != intent.DecisionStateVersion ||
                    !latest.State.OpenPullRequestIntents.TryGetValue(intent.StepId, out var retained) ||
                    !MafExecutionCheckpointContract.OpenPullRequestIntentsMatch(intent, retained) ||
                    latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
                        MafExecutionTaskStatus.Running)
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict);
            }

            async Task RevalidateOpenPullRequestDispatchAsync(CancellationToken token)
            {
                try
                {
                    await RevalidateDispatchAsync(token).ConfigureAwait(false);
                }
                catch (CoordinationException failure) when (failure.Code == "maf_execution_dispatch_stale")
                {
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict, failure);
                }
            }

            async Task RequireOpenPullRequestDecisionInTransactionAsync(
                NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
            {
                try
                {
                    await decisions.RequireCurrentDispatchStateInTransactionAsync(
                        connection, transaction, actor, identity, selection, selectionContext,
                        decision.StateVersion, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
                }
                catch (CoordinationException failure) when (failure.Code == "maf_execution_dispatch_stale")
                {
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict, failure);
                }
            }

            var openPullRequestExecutor = sourceControlClient is null
                ? null
                : new MafOpenPullRequestExecutor(
                    execution,
                    sourceControlClient,
                    context.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                    CreateRuntimeActorAsync,
                    RequireCurrentOpenPullRequestAsync,
                    RequireOpenPullRequestDecisionInTransactionAsync,
                    IsWallTimeLimitReached,
                    () => wallTimeLimitReached = true,
                    snapshot => checkpoint = snapshot);

            async Task<MafExecutionOpenPullRequestIntent> PrepareOpenPullRequestAsync(
                WorkflowStepDefinition step, CancellationToken token)
            {
                var locator = request.OpenPullRequestWorkspaces?.GetValueOrDefault(step.Id)
                    ?? throw new CoordinationException(
                        "source_control_workspace_request_invalid", StatusCodes.Status400BadRequest);
                if (sourceControlClient is null)
                    throw new CoordinationException(
                        "source_control_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (step.Mode != WorkflowStepMode.Platform ||
                    step.PlatformGate != WorkflowPlatformGate.OpenPullRequest ||
                    !MafOpenPullRequestContract.IsValidWorkspace(locator))
                    throw new CoordinationException(
                        "source_control_pull_request_request_invalid", StatusCodes.Status400BadRequest);

                await RevalidateOpenPullRequestDispatchAsync(token).ConfigureAwait(false);
                var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                var pin = await sourceOwner.ReadRepositoryPinAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                if (!string.Equals(locator.BaseBranch, pin.DefaultBranch, StringComparison.Ordinal))
                    throw new CoordinationException(
                        "source_control_run_binding_changed", StatusCodes.Status409Conflict);

                var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await RequireOpenPullRequestDecisionInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                await execution.AcquireExecutionLockInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (latest.State.OpenPullRequestIntents.TryGetValue(step.Id, out var retained))
                {
                    MafOpenPullRequestContract.RequireCurrentSource(
                        retained, actor, identity, selection.Authorization.TenantId,
                        decision.StateVersion, decision.State.Fence, decision.SelectionHash, pin);
                    if (retained.Workspace != locator)
                        throw new CoordinationException(
                            "source_control_run_binding_changed", StatusCodes.Status409Conflict);
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }

                var checkpointId = Guid.NewGuid().ToString("N");
                var completedWorkItems = latest.State.Progress.WorkItems.Values.Count(
                    status => status == MafExecutionTaskStatus.Succeeded);
                var intent = MafOpenPullRequestContract.CreateIntent(
                    Guid.NewGuid(), identity, checkpointId, checked(latest.State.Revision + 1),
                    plan.Plan.Id, step.Id, decision.StateVersion, decision.State.Fence,
                    decision.SelectionHash, pin, locator, completedWorkItems);
                var next = PrepareOpenPullRequestCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_open_pull_request_intent_conflict", StatusCodes.Status409Conflict);
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, checkpointId, latest.Info, next, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return intent;
            }

            async Task<bool> ExecuteOpenPullRequestAsync(
                MafExecutionOpenPullRequestIntent intent, CancellationToken token)
            {
                if (openPullRequestExecutor is null)
                    throw new CoordinationException(
                        "source_control_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                return await openPullRequestExecutor.ExecuteAsync(intent, token).ConfigureAwait(false);
            }

            async Task<CoordinatorDecisionCurrentState> ReadCurrentReviewAsync(CancellationToken token)
            {
                // Both the accepted selection and decision owner must still authorize this actor.
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                return await decisions.ReadCurrentAsync(actor, identity, selection, token).ConfigureAwait(false);
            }

            async Task RequireReviewEvidenceAsync(
                MafExecutionReviewIntent intent, MafExecutionCheckpoint state, CancellationToken token)
            {
                // Recompute the exact evidence from the accepted graph and append-only predecessor receipts.
                var step = plan.Workflow.Definition.Steps.SingleOrDefault(candidate =>
                    candidate.Id == intent.StepId && candidate.PlatformGate == WorkflowPlatformGate.IndependentReview);
                if (step is null || intent.Identity != identity || intent.ExecutionFence != decision.State.Fence ||
                    intent.AcceptedSelectionHash != decision.SelectionHash ||
                    intent.AuthorizedActorId != selection.Authorization.ActorId ||
                    intent.WorkPlanId != plan.Plan.Id)
                    throw new CoordinationException(
                        "maf_execution_review_binding_changed", StatusCodes.Status409Conflict);
                var current = MafIndependentReviewContract.CreateIntent(
                    intent.OperationId, identity, intent.CheckpointId, intent.CheckpointRevision,
                    plan, step, state, intent.DecisionStateVersion, intent.AuthorizedActorId,
                    intent.ExecutionFence,
                    intent.AcceptedSelectionHash);
                if (current != intent)
                    throw new CoordinationException(
                        "maf_execution_review_evidence_changed", StatusCodes.Status409Conflict);
                // BuildTest receipts bind a seed, not the live workspace; re-capture the exact source tree before every decision transition.
                foreach (var ancestorId in MafIndependentReviewContract.Ancestors(
                             plan.Workflow.Definition, step))
                {
                    if (!state.BuildTestIntents.TryGetValue(ancestorId, out var buildTest) ||
                        buildTest.EffectiveWorkspaceSource is not { } source)
                        continue;
                    var reference = CreateBuildTestDispatchAuthorizationReference(
                        buildTest, state.Progress, identity, decision);
                    var sourceOwner = context.RequestServices.GetRequiredService<SourceControlOwnerStore>();
                    await SourceControlEndpoints.RequireCurrentBuildTestInputAsync(
                        context, source, reference, options, projects, decisions, sourceOwner, token)
                        .ConfigureAwait(false);
                    var pin = await sourceOwner.ReadRepositoryPinAsync(
                        actor, identity, selection, token).ConfigureAwait(false);
                    var repository = SourceControlProjectConfigurationResolver.Resolve(
                        selection.Selection.Snapshot).Repository;
                    if (pin.PinId != source.SourceControlPinId ||
                        pin.Repository != repository ||
                        pin.ProviderBinding.Resource != source.SourceControlResource)
                        throw new CoordinationException(
                            "maf_execution_review_binding_changed", StatusCodes.Status409Conflict);
                    var workspaceManager = context.RequestServices.GetService<GitWorkspaceManager>()
                        ?? throw new CoordinationException(
                            "source_control_workspace_unavailable", StatusCodes.Status503ServiceUnavailable);
                    var captureRequest = new GitWorkspaceCaptureRequest(
                        pin.ProviderBinding, repository, source.WorkspaceId, source.BaseSha, source.BranchName);
                    var workspace = await workspaceManager.OpenExistingAsync(
                        captureRequest, token).ConfigureAwait(false);
                    if (workspace.WorkspaceIncarnationId != source.WorkspaceIncarnationId)
                        throw new CoordinationException(
                            "maf_execution_review_evidence_changed", StatusCodes.Status409Conflict);
                    await using var revisionLease = await workspaceManager.AcquireWorkspaceRevisionLeaseAsync(
                        identity.RunId, source.WorkspaceId, token).ConfigureAwait(false);
                    var capture = await workspaceManager.CaptureAsync(
                        captureRequest, workspace, token).ConfigureAwait(false);
                    var document = GitWorkspaceCapturePackage.Create(capture);
                    if (!MafIndependentReviewContract.MatchesBuildTestWorkspaceSource(source, document))
                        throw new CoordinationException(
                            "maf_execution_review_evidence_changed", StatusCodes.Status409Conflict);
                    await SourceControlEndpoints.RequireCurrentBuildTestInputAsync(
                        context, source, reference, options, projects, decisions, sourceOwner, token)
                        .ConfigureAwait(false);
                }
                // A PR workspace that advanced since review was requested cannot use the old answer.
                foreach (var ancestorId in MafIndependentReviewContract.Ancestors(
                             plan.Workflow.Definition, step))
                {
                    if (!state.OpenPullRequestIntents.TryGetValue(ancestorId, out var pullRequest) ||
                        sourceControlClient is null)
                        continue;
                    var currentActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                    try
                    {
                        var diff = await sourceControlClient.ReadWorkspaceDiffAsync(
                            currentActor, identity, pullRequest.Workspace, token).ConfigureAwait(false);
                        MafOpenPullRequestContract.RequireWorkspaceRevision(pullRequest, diff);
                    }
                    finally
                    {
                        currentActor.Bearer.Invalidate();
                    }

                    // The workspace revision alone does not prove that the provider's PR head is unchanged.
                    var pin = await context.RequestServices.GetRequiredService<SourceControlOwnerStore>()
                        .ReadRepositoryPinAsync(actor, identity, selection, token).ConfigureAwait(false);
                    var adapter = context.RequestServices.GetService<ISourceControlAdapter>();
                    var redemption = context.RequestServices.GetService<SourceControlSecretRedemptionClient>();
                    if (pin.PinId != pullRequest.SourceControlPinId ||
                        pin.Repository != pullRequest.Repository ||
                        adapter?.ProviderId != pin.ProviderBinding.ProviderId ||
                        redemption is null)
                        throw new CoordinationException(
                            "maf_execution_review_binding_changed", StatusCodes.Status409Conflict);
                    var repository = SourceControlProjectConfigurationResolver.Resolve(
                        selection.Selection.Snapshot).Repository;
                    var providerPullRequest = await redemption.WithCredentialAsync(
                        context, pin, pin.ApiCredential,
                        async (credential, currentToken) =>
                        {
                            var operation = new SourceControlOperationContext(
                                pin.ProviderBinding, repository, credential);
                            await adapter.VerifyCurrentBindingAsync(
                                operation, SourceControlCapabilities.PullRequestRead, currentToken)
                                .ConfigureAwait(false);
                            return await adapter.ReadPullRequestAsync(
                                operation, state.OpenPullRequestReceipts[ancestorId].PullRequestNumber,
                                currentToken).ConfigureAwait(false);
                        }, token).ConfigureAwait(false);
                    if (!string.Equals(providerPullRequest.HeadSha,
                            state.OpenPullRequestReceipts[ancestorId].HeadSha,
                            StringComparison.OrdinalIgnoreCase) ||
                        providerPullRequest.Number != state.OpenPullRequestReceipts[ancestorId].PullRequestNumber)
                        throw new CoordinationException(
                            "maf_execution_review_evidence_changed", StatusCodes.Status409Conflict);
                }
            }

            async Task RequireCurrentRubberduckAsync(
                MafExecutionRubberduckIntent intent, CancellationToken token)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                var current = await decisions.ReadCurrentAsync(
                    actor, identity, selection, token).ConfigureAwait(false);
                var latest = await execution.ReadLatestAsync(token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (intent.Identity != identity ||
                    intent.AuthorizedActorId != selection.Authorization.ActorId ||
                    intent.ExecutionFence != decision.State.Fence ||
                    intent.AcceptedSelectionHash != decision.SelectionHash ||
                    intent.WorkPlanId != plan.Plan.Id ||
                    intent.WorkflowId != plan.Workflow.Definition.Id ||
                    intent.DefinitionRevision != plan.Workflow.Definition.Revision ||
                    current.State.Fence != intent.ExecutionFence ||
                    current.SelectionHash != intent.AcceptedSelectionHash ||
                    current.State.ConfirmedWorkPlan?.Plan.Id != intent.WorkPlanId ||
                    current.State.SelectedWorkflow?.Definition.Id != intent.WorkflowId ||
                    current.State.SelectedWorkflow.Definition.Revision != intent.DefinitionRevision ||
                    !current.State.CanDispatch ||
                    !MafIndependentReviewContract.AllowsDecisionVersionAdvance(
                        latest.State, current, intent.DecisionStateVersion))
                    throw new CoordinationException(
                        "maf_execution_rubberduck_binding_changed", StatusCodes.Status409Conflict);
                _ = await ReadCurrentRubberduckOwnerAsync(intent, token).ConfigureAwait(false);
            }

            async Task<RuntimeRegistration> ReadCurrentRubberduckOwnerAsync(
                MafExecutionRubberduckIntent? intent, CancellationToken token)
            {
                if (registrationOwner is null || rubberduckOwnerRegistration is null)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                var current = await registrationOwner.ReadCurrentForSessionAsync(
                    context, projectId, runId, sessionId, token).ConfigureAwait(false);
                CastingGenerationExecutionContract.RequireCurrentRegistration(
                    current, projectId, runId, sessionId, decision.State.Fence,
                    decision.SelectionHash, context.RequestServices.GetRequiredService<TimeProvider>());
                if (current.Binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
                    intent is not null &&
                    (current.RuntimeInstanceId != intent.OwnerRuntimeInstanceId ||
                     current.Revision != intent.OwnerRegistrationRevision) ||
                    current.RuntimeInstanceId != rubberduckOwnerRegistration.RuntimeInstanceId ||
                    current.Revision != rubberduckOwnerRegistration.Revision)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_owner_changed", StatusCodes.Status409Conflict);
                return current;
            }

            Task RequireRubberduckEvidenceAsync(
                MafExecutionRubberduckIntent intent, MafExecutionCheckpoint state, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var step = plan.Workflow.Definition.Steps.SingleOrDefault(candidate =>
                    candidate.Id == intent.StepId && candidate.PlatformGate == WorkflowPlatformGate.RubberDuck);
                if (step is null || rubberduckOwnerRegistration is null)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_binding_changed", StatusCodes.Status409Conflict);
                var current = MafRubberduckReviewContract.CreateIntent(
                    intent.OperationId,
                    identity,
                    intent.CheckpointId,
                    intent.CheckpointRevision,
                    plan,
                    step,
                    state,
                    intent.DecisionStateVersion,
                    intent.AuthorizedActorId,
                    intent.ExecutionFence,
                    intent.AcceptedSelectionHash,
                    rubberduckOwnerRegistration);
                if (current != intent)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_evidence_changed", StatusCodes.Status409Conflict);
                return Task.CompletedTask;
            }

            async Task<string> RunRubberduckOwnerAsync(
                MafExecutionRubberduckIntent intent, string prompt, CancellationToken token)
            {
                if (actionGrants is null)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                await RequireCurrentRubberduckAsync(intent, token).ConfigureAwait(false);
                var registration = await ReadCurrentRubberduckOwnerAsync(intent, token).ConfigureAwait(false);
                var promptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt));
                await actionGrants.AdmitCoordinatorRootModelTurnAsync(
                    context, registration, intent.OperationId, promptHash,
                    intent.DecisionStateVersion, token).ConfigureAwait(false);

                var time = context.RequestServices.GetRequiredService<TimeProvider>();
                var readinessActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                RuntimeHostReadinessReceipt? readiness;
                try
                {
                    readiness = await SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                        hostClient,
                        RuntimeHostEndpoint(registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness"),
                        readinessActor,
                        null,
                        allowNotReady: true,
                        token).ConfigureAwait(false);
                }
                finally
                {
                    readinessActor.Bearer.Invalidate();
                }
                if (readiness is null)
                    throw new CoordinationException(
                        "maf_execution_rubberduck_owner_unavailable", StatusCodes.Status503ServiceUnavailable);
                ValidateHostReadiness(readiness, registration, time);
                await RequireCurrentRubberduckAsync(intent, token).ConfigureAwait(false);

                var proof = new RuntimeHostSessionProof(
                    1,
                    registration,
                    readiness.SourceGrant.GrantId,
                    readiness.SourceGrant.Revision,
                    RuntimeCredentialPurpose.Observe);
                var message = new RuntimeA2AMessage(
                    "message",
                    intent.OperationId,
                    RuntimeContractValidation.NativeSessionId(registration.Binding),
                    "user",
                    [new("text", prompt)],
                    new(
                        proof,
                        AddressedMessageDeliveryMode.Immediate,
                        ContinueAfterCallerDisconnect: true,
                        ReadOnlyToolsOnly: true));
                var sendActor = await CreateRuntimeActorAsync(token).ConfigureAwait(false);
                RuntimeA2AResponse response;
                try
                {
                    response = await SendRuntimeHostMessageAsync(
                        hostClient, registration, readiness, sendActor, new(message), time, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    sendActor.Bearer.Invalidate();
                }
                await RequireCurrentRubberduckAsync(intent, token).ConfigureAwait(false);
                return MafRubberduckReviewContract.ReadOwnerResponse(
                    response, RuntimeContractValidation.NativeSessionId(registration.Binding));
            }

            async Task OpenReviewApprovalAsync(MafExecutionReviewIntent intent, CancellationToken token)
            {
                // The checkpoint already owns the operation; the decision owner owns exactly one request.
                var current = await ReadCurrentReviewAsync(token).ConfigureAwait(false);
                if (current.StateVersion != intent.DecisionStateVersion)
                    throw new CoordinationException(
                        "maf_execution_review_decision_conflict", StatusCodes.Status409Conflict);
                var transition = CoordinatorDecisionFlow.RequestApproval(
                    current.State, intent.ApprovalRequestId, intent.EvidenceHash,
                    selection.Authorization.ActorId, "Review the recorded evidence: " + intent.EvidenceSummary);
                if (!transition.IsSuccess)
                    throw new CoordinationException(
                        "maf_execution_review_decision_conflict", StatusCodes.Status409Conflict);
                await RequireReviewEvidenceAsync(
                    intent, (await execution.ReadLatestAsync(token).ConfigureAwait(false))!.State, token)
                    .ConfigureAwait(false);
                var saved = await decisions.PersistTransitionAsync(
                    actor, identity, selection, intent.DecisionStateVersion,
                    intent.ApprovalRequestId, intent.ApprovalRequestId, "approval.request",
                    CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                    {
                        intent.DecisionStateVersion, intent.ApprovalRequestId,
                        intent.EvidenceHash, intent.EvidenceSummary
                    }),
                    transition.State!, true, transition.Issues,
                    confirmedSelectionContext: null, candidateSelectionContext: null,
                    validatedTransitionValue: transition.Value, token).ConfigureAwait(false);
                if (!saved.TransitionAccepted || saved.State.PendingGate?.RequestId != intent.ApprovalRequestId)
                    throw new CoordinationException(
                        "maf_execution_review_decision_conflict", StatusCodes.Status409Conflict);
                pendingReviewApproval = (intent.EvidenceHash, intent.ApprovalRequestId);
                responseDecisionStateVersion = saved.StateVersion;
            }

            async Task RequireReviewDecisionInTransactionAsync(
                NpgsqlConnection connection, NpgsqlTransaction transaction, long version, CancellationToken token)
            {
                // Lock the owner's current approved decision before appending a review result.
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    version, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
            }

            var reviewExecutor = new MafIndependentReviewExecutor(
                execution, dataSource, ReadCurrentReviewAsync, OpenReviewApprovalAsync,
                RequireReviewDecisionInTransactionAsync, RequireReviewEvidenceAsync,
                selection.Authorization.ActorId, snapshot => checkpoint = snapshot,
                (intent, version) =>
                {
                    pendingReviewApproval = (intent.EvidenceHash, intent.ApprovalRequestId);
                    responseDecisionStateVersion = version;
                });

            async Task RequireRubberduckDecisionInTransactionAsync(
                NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
            {
                await decisions.RequireCurrentDispatchStateInTransactionAsync(
                    connection, transaction, actor, identity, selection, selectionContext,
                    decision.StateVersion, decision.State.Fence, plan.Plan.Id, token).ConfigureAwait(false);
            }

            var rubberduckExecutor = new MafRubberduckReviewExecutor(
                execution,
                dataSource,
                RunRubberduckOwnerAsync,
                RequireCurrentRubberduckAsync,
                RequireRubberduckDecisionInTransactionAsync,
                RequireRubberduckEvidenceAsync,
                snapshot => checkpoint = snapshot);

            foreach (var intent in checkpoint.State.RubberduckIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) is
                             MafExecutionTaskStatus.Succeeded or MafExecutionTaskStatus.Failed)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
            {
                if (await rubberduckExecutor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false) is null)
                    unavailable.Add(intent.StepId);
            }

            foreach (var intent in checkpoint.State.ReviewIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Succeeded))
                _ = await reviewExecutor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);

            async Task<MafExecutionReviewIntent> PrepareReviewAsync(
                WorkflowStepDefinition step, CancellationToken token)
            {
                // Write the immutable evidence and running state in the same checkpoint transaction.
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
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
                if (latest.State.ReviewIntents.TryGetValue(step.Id, out var retained))
                {
                    // Compare checkpoint evidence while holding the lock; external Source Control reads follow outside it.
                    var current = MafIndependentReviewContract.CreateIntent(
                        retained.OperationId, identity, retained.CheckpointId, retained.CheckpointRevision,
                        plan, step, latest.State, retained.DecisionStateVersion, retained.AuthorizedActorId,
                        retained.ExecutionFence,
                        retained.AcceptedSelectionHash);
                    if (retained != current)
                        throw new CoordinationException(
                            "maf_execution_review_evidence_changed", StatusCodes.Status409Conflict);
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }
                var checkpointId = Guid.NewGuid().ToString("N");
                var intent = MafIndependentReviewContract.CreateIntent(
                    Guid.NewGuid(), identity, checkpointId, checked(latest.State.Revision + 1),
                    plan, step, latest.State, decision.StateVersion, selection.Authorization.ActorId,
                    decision.State.Fence,
                    decision.SelectionHash);
                var next = PrepareReviewCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_review_intent_conflict", StatusCodes.Status409Conflict);
                checkpoint = await execution.AppendInTransactionAsync(
                    connection, transaction, checkpointId, latest.Info, next, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return intent;
            }

            async Task<MafExecutionRubberduckIntent> PrepareRubberduckAsync(
                WorkflowStepDefinition step, CancellationToken token)
            {
                await RevalidateDispatchAsync(token).ConfigureAwait(false);
                var owner = await ReadCurrentRubberduckOwnerAsync(null, token).ConfigureAwait(false);
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
                if (latest.State.RubberduckIntents.TryGetValue(step.Id, out var retained))
                {
                    var current = MafRubberduckReviewContract.CreateIntent(
                        retained.OperationId,
                        identity,
                        retained.CheckpointId,
                        retained.CheckpointRevision,
                        plan,
                        step,
                        latest.State,
                        retained.DecisionStateVersion,
                        retained.AuthorizedActorId,
                        retained.ExecutionFence,
                        retained.AcceptedSelectionHash,
                        owner);
                    if (retained != current ||
                        retained.OwnerRuntimeInstanceId != owner.RuntimeInstanceId ||
                        retained.OwnerRegistrationRevision != owner.Revision)
                        throw new CoordinationException(
                            "maf_execution_rubberduck_evidence_changed", StatusCodes.Status409Conflict);
                    checkpoint = latest;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return retained;
                }

                var checkpointId = Guid.NewGuid().ToString("N");
                var intent = MafRubberduckReviewContract.CreateIntent(
                    Guid.NewGuid(),
                    identity,
                    checkpointId,
                    checked(latest.State.Revision + 1),
                    plan,
                    step,
                    latest.State,
                    decision.StateVersion,
                    selection.Authorization.ActorId,
                    decision.State.Fence,
                    decision.SelectionHash,
                    owner);
                var next = PrepareRubberduckCheckpoint(latest, intent)
                    ?? throw new CoordinationException(
                        "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
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
            foreach (var intent in checkpoint.State.OpenPullRequestIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Running)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
                _ = await ExecuteOpenPullRequestAsync(intent, cancellationToken).ConfigureAwait(false);
            foreach (var intent in checkpoint.State.MergeIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Running)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
            {
                if (await ExecuteMergeAsync(intent, cancellationToken).ConfigureAwait(false) ==
                    MafMergeExecutionOutcome.AwaitingApproval)
                    break;
            }
            foreach (var intent in checkpoint.State.RubberduckIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Running)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
            {
                if (await rubberduckExecutor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false) is null)
                    unavailable.Add(intent.StepId);
            }
            foreach (var intent in checkpoint.State.ReviewIntents.Values
                         .Where(intent => checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) ==
                             MafExecutionTaskStatus.Running)
                         .OrderBy(intent => intent.StepId, StringComparer.Ordinal).ToArray())
                _ = await reviewExecutor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);

            var pendingDeliveries = pendingMergeApproval is null && pendingReviewApproval is null
                ? checkpoint.State.PendingDispatches.Values
                .OrderBy(intent => intent.AssociationId, StringComparer.Ordinal)
                .Select(intent =>
                {
                    var action = ResolveIntentAction(intent, checkpoint.State);
                    return (Intent: intent, Action: action,
                        Prompt: BuildPrompt(action, checkpoint.State, intent.AssociationId));
                })
                .ToArray()
                : Array.Empty<(MafExecutionDispatchIntent Intent, MafExecutionAction Action, string Prompt)>();
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
                    hasNonModelExecutor: HasPlatformExecutor,
                    checkpoint.State.FixedWorkAssociations);
                if (frontier.ReadyActions.IsDefaultOrEmpty)
                    break;

                var readyDispatches = new List<(
                    MafExecutionAction Action, MafExecutionDispatchIntent Intent, string Prompt)>();
                var mergeActions = new List<MafExecutionAction>();
                var reviewActions = new List<MafExecutionAction>();
                var rubberduckActions = new List<MafExecutionAction>();
                var platformAdvanced = false;
                foreach (var action in frontier.ReadyActions)
                {
                    MafExecutionWorkflowBindings.RequireBoundAction(bound, action);
                    if (IsWallTimeLimitReached())
                    {
                        wallTimeLimitReached = true;
                        break;
                    }
                    if (action.Step.PlatformGate == WorkflowPlatformGate.Merge)
                    {
                        mergeActions.Add(action);
                        continue;
                    }
                    if (action.Step.PlatformGate == WorkflowPlatformGate.IndependentReview)
                    {
                        reviewActions.Add(action);
                        continue;
                    }
                    if (action.Step.PlatformGate == WorkflowPlatformGate.RubberDuck)
                    {
                        rubberduckActions.Add(action);
                        continue;
                    }
                    if (action.Step.PlatformGate == WorkflowPlatformGate.OpenPullRequest)
                    {
                        var pullRequestIntent = await PrepareOpenPullRequestAsync(
                            action.Step, cancellationToken).ConfigureAwait(false);
                        platformAdvanced |= await ExecuteOpenPullRequestAsync(
                            pullRequestIntent, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (MafBuildTestCommandContract.IsExecutable(action.Step))
                    {
                        var commandIntent = await PrepareBuildTestAsync(action.Step, cancellationToken)
                            .ConfigureAwait(false);
                        platformAdvanced |= await ExecuteBuildTestAsync(commandIntent, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    string associationId;
                    string task;
                    string childSessionId;
                    ConfirmedWorkPlanItemAssociation? workPlanAssociation = null;
                    MafExecutionFixedWorkAssociation? fixedAssociation = null;
                    MafChildSpawnAuthorizationIntent spawnAuthorizationIntent;
                    if (action.Step.Mode == WorkflowStepMode.Fixed)
                    {
                        // Fixed steps have no WorkPlanItem; bind the deterministic association to the selected role.
                        var activationRevision = checked(checkpoint.State.FixedWorkAssociations.Values
                            .Where(existing =>
                                existing.WorkPlanId == plan.Plan.Id &&
                                string.Equals(existing.StepId, action.Step.Id, StringComparison.Ordinal))
                            .Select(existing => existing.ActivationRevision)
                            .DefaultIfEmpty(0)
                            .Max() + 1);
                        childSessionId = MafExecutionPlanner.CreateChildSessionId(
                            identity,
                            plan.Plan.Id,
                            MafExecutionPlanner.CreateFixedWorkAssociationId(
                                identity, plan.Plan.Id, action.Step.Id, activationRevision));
                        fixedAssociation = MafExecutionPlanner.CreateFixedWorkAssociation(
                            plan,
                            selectionContext,
                            identity,
                            action.Step,
                            decision.SelectionHash,
                            decision.State.Fence,
                            decision.StateVersion,
                            activationRevision,
                            childSessionId);
                        associationId = fixedAssociation.AssociationId;
                        task = fixedAssociation.Specification.Task;
                        spawnAuthorizationIntent = MafExecutionWorkflowBindings.CreateFixedChildSpawnIntent(
                            plan,
                            selectionContext,
                            identity,
                            action,
                            fixedAssociation,
                            decision.SelectionHash,
                            selection.Selection.ContextRevision,
                            decision.StateVersion,
                            decision.State.Fence);
                    }
                    else
                    {
                        if (action.Step.Mode != WorkflowStepMode.Open || action.WorkItem is not { } workItem)
                            throw new CoordinationException(
                                "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
                        associationId = workItem.Id;
                        task = workItem.Task;
                        workPlanAssociation = new ConfirmedWorkPlanItemAssociation(
                            workItem.Id, decision.StateVersion, decision.SelectionHash);
                        childSessionId = MafExecutionPlanner.CreateChildSessionId(
                            identity, plan.Plan.Id, associationId);
                        spawnAuthorizationIntent = MafExecutionWorkflowBindings.CreateOpenChildSpawnIntent(
                            plan, identity, action, selection.Selection.ContextRevision,
                            decision.StateVersion, decision.State.Fence);
                    }

                    var prompt = BuildPrompt(action, checkpoint.State, associationId, fixedAssociation);
                    await RequireChildSpawnAllowedAsync(
                        spawnAuthorizationIntent, cancellationToken).ConfigureAwait(false);
                    var egressReceipt = await VerifyEgressAsync(cancellationToken).ConfigureAwait(false);
                    var intent = new MafExecutionDispatchIntent(
                        associationId,
                        childSessionId,
                        spawnAuthorizationIntent.IntentId,
                        RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)))
                    {
                        EgressReceipt = egressReceipt
                    };
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
                    MafExecutionEgressGuard.RequireSameReceipt(
                        egressReceipt, await VerifyEgressAsync(cancellationToken).ConfigureAwait(false));
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
                            // The root owner lock serializes concurrent spawn callbacks. Recompute readiness
                            // from the latest checkpoint, not from the pre-I/O frontier (including conflict edges).
                            if (latest?.State.PendingDispatches.TryGetValue(associationId, out var existing) != true ||
                                existing != intent)
                                MafExecutionPlanner.RequireReadyDispatchAction(
                                    plan, latest?.State.Progress ?? MafExecutionProgress.Empty,
                                    latest?.State.FixedWorkAssociations, action);
                            await RequireChildSpawnConsumedAsync(
                                connection, transaction, spawnAuthorizationIntent, token).ConfigureAwait(false);
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

                if (IsWallTimeLimitReached())
                {
                    wallTimeLimitReached = true;
                    break;
                }
                if (readyDispatches.Count > 0)
                {
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
                foreach (var action in mergeActions)
                {
                    if (IsWallTimeLimitReached())
                    {
                        wallTimeLimitReached = true;
                        break;
                    }
                    var intent = await PrepareMergeAsync(action.Step, cancellationToken).ConfigureAwait(false);
                    var outcome = await ExecuteMergeAsync(intent, cancellationToken).ConfigureAwait(false);
                    platformAdvanced |= outcome == MafMergeExecutionOutcome.Completed;
                    if (outcome == MafMergeExecutionOutcome.AwaitingApproval)
                        break;
                }
                foreach (var action in rubberduckActions.OrderBy(action => action.Step.Order))
                {
                    if (pendingReviewApproval is not null || IsWallTimeLimitReached())
                    {
                        wallTimeLimitReached |= IsWallTimeLimitReached();
                        break;
                    }
                    var intent = await PrepareRubberduckAsync(action.Step, cancellationToken)
                        .ConfigureAwait(false);
                    var result = await rubberduckExecutor.ExecuteAsync(intent, cancellationToken)
                        .ConfigureAwait(false);
                    if (result is null)
                        unavailable.Add(action.Step.Id);
                    else
                        platformAdvanced = true;
                }
                foreach (var action in reviewActions)
                {
                    if (pendingMergeApproval is not null || IsWallTimeLimitReached())
                    {
                        wallTimeLimitReached |= IsWallTimeLimitReached();
                        break;
                    }
                    var intent = await PrepareReviewAsync(action.Step, cancellationToken).ConfigureAwait(false);
                    platformAdvanced |= await reviewExecutor.ExecuteAsync(
                        intent, cancellationToken).ConfigureAwait(false) is not null;
                    if (pendingReviewApproval is not null)
                        break;
                }
                if (pendingMergeApproval is not null || pendingReviewApproval is not null || wallTimeLimitReached)
                    break;
                if (readyDispatches.Count == 0)
                {
                    if (platformAdvanced)
                        continue;
                    break;
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
                hasNonModelExecutor: HasPlatformExecutor,
                checkpoint.State.FixedWorkAssociations);
            unavailable.UnionWith(finalFrontier.UnavailableExecutorStepIds);
            if (finalFrontier.IsComplete)
                await AppendCompletedPlanWitnessAsync(cancellationToken).ConfigureAwait(false);
            await RevalidateDispatchAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new MafExecutionDispatchResponse(
                plan.Plan.Id,
                responseDecisionStateVersion,
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

    /// <summary>
    /// Creates the authorization reference for checking a BuildTest intent against current decision state.
    /// </summary>
    /// <param name="intent">The persisted command intent.</param>
    /// <param name="progress">Current non-model step progress.</param>
    /// <param name="identity">The expected session identity.</param>
    /// <param name="decision">The current decision and selection binding.</param>
    /// <returns>The command's checkpoint reference updated to the current decision version.</returns>
    /// <exception cref="CoordinationException">Thrown when the command belongs to another session, fence, or selection, or a running command has a stale decision version.</exception>
    internal static SandboxBuildTestCheckpointReference CreateBuildTestDispatchAuthorizationReference(
        MafExecutionBuildTestIntent intent,
        MafExecutionProgress progress,
        SessionIdentity identity,
        CoordinatorDecisionCurrentState decision)
    {
        if (intent.Identity != identity || intent.ExecutionFence != decision.State.Fence ||
            intent.AcceptedSelectionHash != decision.SelectionHash ||
            progress.NonModelSteps.GetValueOrDefault(intent.StepId) == MafExecutionTaskStatus.Running &&
                intent.DecisionStateVersion != decision.StateVersion)
            throw new CoordinationException(
                "maf_execution_dispatch_stale", StatusCodes.Status409Conflict);

        return intent.CheckpointReference() with { DecisionStateVersion = decision.StateVersion };
    }

    /// <summary>
    /// Prepares a checkpoint that marks a model or fixed-work association running and records its pending dispatch.
    /// </summary>
    /// <param name="latest">The latest checkpoint, or <see langword="null"/> before the first checkpoint.</param>
    /// <param name="workPlanId">The accepted plan ID.</param>
    /// <param name="decisionStateVersion">The decision version authorizing the dispatch.</param>
    /// <param name="associationId">The work-item or fixed-work association ID.</param>
    /// <param name="fixedAssociation">The fixed-work binding, when this is a fixed action.</param>
    /// <param name="intent">The deterministic pending message intent, when dispatch is being recorded.</param>
    /// <returns>The next checkpoint state, or <see langword="null"/> when an identical or completed association is already present.</returns>
    /// <exception cref="CoordinationException">Thrown when plan, progress, fixed association, or retry intent conflicts with persisted state.</exception>
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

    /// <summary>Compares checkpoint identity across independently materialized database reads.</summary>
    internal static bool SameCheckpointInfo(CheckpointInfo? left, CheckpointInfo? right)
    {
        // Null represents the uninitialized state; only two absent snapshots are the same checkpoint.
        if (left is null || right is null)
            return left is null && right is null;
        // PostgreSQL rematerializes checkpoint objects per read, so compare their persisted identifiers.
        return string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
            string.Equals(left.CheckpointId, right.CheckpointId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds a new BuildTest intent and marks its platform step running in the next checkpoint revision.
    /// </summary>
    /// <param name="latest">The checkpoint being extended.</param>
    /// <param name="intent">The immutable accepted command intent.</param>
    /// <returns>The next state, or <see langword="null"/> for an exact idempotent replay.</returns>
    /// <exception cref="CoordinationException">Thrown when the command is bound to a stale plan, revision, decision, or concurrent platform step.</exception>
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

    /// <summary>Atomically records the review evidence and marks its platform step running.</summary>
    internal static MafExecutionCheckpoint? PrepareReviewCheckpoint(
        MafExecutionCheckpointSnapshot latest, MafExecutionReviewIntent intent)
    {
        ArgumentNullException.ThrowIfNull(latest);
        // A retry must reuse the original operation and cannot substitute a changed evidence identity.
        if (!MafIndependentReviewContract.IsValidIntent(intent))
            throw new CoordinationException(
                "maf_execution_review_evidence_invalid", StatusCodes.Status409Conflict);
        var state = latest.State;
        if (state.ReviewIntents.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != intent)
                throw new CoordinationException(
                    "maf_execution_review_intent_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (intent.CheckpointRevision != checked(state.Revision + 1) ||
            intent.WorkPlanId != state.WorkPlanId ||
            intent.Identity.SessionId != latest.Info.SessionId ||
            intent.DecisionStateVersion != state.DecisionStateVersion ||
            state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending ||
            state.Progress.NonModelSteps.Values.Any(status => status == MafExecutionTaskStatus.Running))
            throw new CoordinationException(
                "maf_execution_review_checkpoint_stale", StatusCodes.Status409Conflict);
        var next = state with
        {
            Revision = intent.CheckpointRevision,
            ReviewIntents = state.ReviewIntents.Add(intent.StepId, intent),
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(state, next);
        return next;
    }

    /// <summary>Atomically records an immutable RubberDuck intent and marks its gate running.</summary>
    internal static MafExecutionCheckpoint? PrepareRubberduckCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionRubberduckIntent intent)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        if (!MafRubberduckReviewContract.IsValidIntent(intent))
            throw new CoordinationException(
                "maf_execution_rubberduck_evidence_invalid", StatusCodes.Status409Conflict);
        var state = latest.State;
        if (state.RubberduckIntents.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != intent)
                throw new CoordinationException(
                    "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (intent.CheckpointRevision != checked(state.Revision + 1) ||
            intent.WorkPlanId != state.WorkPlanId ||
            intent.Identity.SessionId != latest.Info.SessionId ||
            intent.DecisionStateVersion != state.DecisionStateVersion ||
            state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending ||
            state.Progress.NonModelSteps.Values.Any(status => status == MafExecutionTaskStatus.Running))
            throw new CoordinationException(
                "maf_execution_rubberduck_checkpoint_stale", StatusCodes.Status409Conflict);
        var next = state with
        {
            Revision = intent.CheckpointRevision,
            RubberduckIntents = state.RubberduckIntents.Add(intent.StepId, intent),
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(state, next);
        return next;
    }

    /// <summary>Persists a retryable owner failure without releasing any dependent workflow step.</summary>
    internal static MafExecutionCheckpoint? PrepareRubberduckFailureCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionRubberduckIntent intent,
        string failureCode)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (!MafRubberduckReviewContract.IsValidFailureCode(failureCode) ||
            !latest.State.RubberduckIntents.TryGetValue(intent.StepId, out var retained) ||
            retained != intent ||
            latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Running ||
            latest.State.RubberduckReceipts.ContainsKey(intent.StepId))
            throw new CoordinationException(
                "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.RubberduckFailureCodes.GetValueOrDefault(intent.StepId) == failureCode)
            return null;
        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            RubberduckFailureCodes = latest.State.RubberduckFailureCodes.SetItem(intent.StepId, failureCode)
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    /// <summary>Records an authorized RubberDuck result and releases dependents only for PASS.</summary>
    internal static MafExecutionCheckpoint? PrepareRubberduckCompletionCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionRubberduckIntent intent,
        MafRubberduckTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var status = MafRubberduckReviewContract.ValidateReceipt(intent, receipt);
        if (!latest.State.RubberduckIntents.TryGetValue(intent.StepId, out var retained) ||
            retained != intent)
            throw new CoordinationException(
                "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.RubberduckReceipts.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != receipt ||
                latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != status)
                throw new CoordinationException(
                    "maf_execution_rubberduck_terminal_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Running)
            throw new CoordinationException(
                "maf_execution_rubberduck_checkpoint_stale", StatusCodes.Status409Conflict);
        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            RubberduckReceipts = latest.State.RubberduckReceipts.Add(intent.StepId, receipt),
            RubberduckFailureCodes = latest.State.RubberduckFailureCodes.Remove(intent.StepId),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(intent.StepId, status)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    /// <summary>Records a typed owner answer and blocks dependents if the human rejected it.</summary>
    internal static MafExecutionCheckpoint? PrepareReviewCompletionCheckpoint(
        MafExecutionCheckpointSnapshot latest, MafExecutionReviewIntent intent, MafReviewTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(latest);
        // A receipt belongs to exactly one checkpointed request and is immutable after append.
        var status = MafIndependentReviewContract.ValidateReceipt(intent, receipt);
        if (!latest.State.ReviewIntents.TryGetValue(intent.StepId, out var retained) || retained != intent)
            throw new CoordinationException(
                "maf_execution_review_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.ReviewReceipts.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != receipt ||
                latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != status)
                throw new CoordinationException(
                    "maf_execution_review_terminal_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Running)
            throw new CoordinationException(
                "maf_execution_review_checkpoint_stale", StatusCodes.Status409Conflict);
        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            ReviewReceipts = latest.State.ReviewReceipts.Add(intent.StepId, receipt),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(intent.StepId, status)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    /// <summary>Adds one immutable Merge request before its Source Control owner call.</summary>
    internal static MafExecutionCheckpoint? PrepareMergeCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionMergeIntent intent)
    {
        // The checkpoint becomes the retry authority before any Source Control owner request is sent.
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        var state = latest.State;
        if (!MafMergeContract.IsValidIntent(intent))
            throw new CoordinationException(
                "source_control_merge_request_invalid", StatusCodes.Status400BadRequest);
        if (state.MergeIntents.TryGetValue(intent.StepId, out var existing))
        {
            if (!MafExecutionCheckpointContract.MergeIntentsMatch(existing, intent))
                throw new CoordinationException(
                    "maf_execution_merge_intent_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (intent.CheckpointRevision != checked(state.Revision + 1) ||
            intent.WorkPlanId != state.WorkPlanId ||
            intent.Identity.SessionId != latest.Info.SessionId ||
            intent.DecisionStateVersion != state.DecisionStateVersion ||
            state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending ||
            state.Progress.NonModelSteps.Values.Any(status => status == MafExecutionTaskStatus.Running))
            throw new CoordinationException(
                "maf_execution_merge_checkpoint_stale", StatusCodes.Status409Conflict);

        var next = state with
        {
            Revision = intent.CheckpointRevision,
            MergeIntents = state.MergeIntents.Add(intent.StepId, intent),
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(state, next);
        return next;
    }

    /// <summary>Stores the owner's compact terminal proof and releases only the Merge gate's dependents.</summary>
    internal static MafExecutionCheckpoint? PrepareMergeCompletionCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionMergeIntent intent,
        MafMergeTerminalReceipt receipt)
    {
        // A receipt and Succeeded progress are one append-only transition, never separate writes.
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(receipt);
        _ = MafMergeContract.ValidateReceipt(intent, receipt);
        if (!latest.State.MergeIntents.TryGetValue(intent.StepId, out var retained) ||
            !MafExecutionCheckpointContract.MergeIntentsMatch(intent, retained))
            throw new CoordinationException(
                "maf_execution_merge_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.MergeReceipts.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != receipt ||
                latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
                    MafExecutionTaskStatus.Succeeded)
                throw new CoordinationException(
                    "maf_execution_merge_terminal_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
            MafExecutionTaskStatus.Running)
            throw new CoordinationException(
                "maf_execution_merge_checkpoint_stale", StatusCodes.Status409Conflict);

        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            MergeReceipts = latest.State.MergeReceipts.Add(intent.StepId, receipt),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Succeeded)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    internal static MafExecutionCheckpoint? PrepareOpenPullRequestCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionOpenPullRequestIntent intent)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        var state = latest.State;
        if (!MafOpenPullRequestContract.IsValidIntent(intent))
            throw new CoordinationException(
                "source_control_pull_request_request_invalid", StatusCodes.Status400BadRequest);
        if (state.OpenPullRequestIntents.TryGetValue(intent.StepId, out var existing))
        {
            if (!MafExecutionCheckpointContract.OpenPullRequestIntentsMatch(existing, intent))
                throw new CoordinationException(
                    "maf_execution_open_pull_request_intent_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (intent.CheckpointRevision != checked(state.Revision + 1) ||
            intent.WorkPlanId != state.WorkPlanId ||
            intent.Identity.SessionId != latest.Info.SessionId ||
            intent.DecisionStateVersion != state.DecisionStateVersion ||
            state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending ||
            state.Progress.NonModelSteps.Values.Any(status => status == MafExecutionTaskStatus.Running))
            throw new CoordinationException(
                "maf_execution_open_pull_request_checkpoint_stale", StatusCodes.Status409Conflict);

        var next = state with
        {
            Revision = intent.CheckpointRevision,
            OpenPullRequestIntents = state.OpenPullRequestIntents.Add(intent.StepId, intent),
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(state, next);
        return next;
    }

    /// <summary>Stores a compact owner-verified PR receipt and releases only that gate's dependents.</summary>
    internal static MafExecutionCheckpoint? PrepareOpenPullRequestCompletionCheckpoint(
        MafExecutionCheckpointSnapshot latest,
        MafExecutionOpenPullRequestIntent intent,
        MafOpenPullRequestTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(receipt);
        _ = MafOpenPullRequestContract.ValidateReceipt(intent, receipt);
        if (!latest.State.OpenPullRequestIntents.TryGetValue(intent.StepId, out var retained) ||
            !MafExecutionCheckpointContract.OpenPullRequestIntentsMatch(intent, retained))
            throw new CoordinationException(
                "maf_execution_open_pull_request_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.OpenPullRequestReceipts.TryGetValue(intent.StepId, out var previous))
        {
            if (previous != receipt ||
                latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
                    MafExecutionTaskStatus.Succeeded)
                throw new CoordinationException(
                    "maf_execution_open_pull_request_terminal_conflict", StatusCodes.Status409Conflict);
            return null;
        }
        if (latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) !=
            MafExecutionTaskStatus.Running)
            throw new CoordinationException(
                "maf_execution_open_pull_request_checkpoint_stale", StatusCodes.Status409Conflict);

        var next = latest.State with
        {
            Revision = checked(latest.State.Revision + 1),
            OpenPullRequestReceipts = latest.State.OpenPullRequestReceipts.Add(intent.StepId, receipt),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Succeeded)
            }
        };
        MafExecutionCheckpointContract.ValidateTransition(latest.State, next);
        return next;
    }

    /// <summary>
    /// Stores terminal BuildTest evidence and advances the platform step from running to its owner-derived status.
    /// </summary>
    /// <param name="latest">The checkpoint containing the running intent.</param>
    /// <param name="intent">The immutable intent whose owner operation completed.</param>
    /// <param name="operation">The operation snapshot returned by the environment owner.</param>
    /// <returns>The next checkpoint, or <see langword="null"/> while the operation is running or after an exact terminal replay.</returns>
    /// <exception cref="CoordinationException">Thrown when the intent or repeated terminal evidence conflicts with checkpoint state.</exception>
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

    /// <summary>
    /// Produces the bounded JSON summary of terminal BuildTest status and output evidence used in dependent prompts.
    /// </summary>
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
            preparedRepositoryPath = receipt.WorkspacePreparation?.RepositoryRelativePath,
            captureDocumentSha256 = receipt.WorkspaceCapture?.DocumentSha256,
            captureDocumentBytes = receipt.WorkspaceCapture?.DocumentByteLength,
            captureId = receipt.WorkspaceCaptureAcknowledgment?.Capture.CaptureId,
            eventPosition = receipt.WorkspaceCaptureAcknowledgment?.Position,
            outputCount = receipt.Outputs.Length,
            receipt.FailureCode
        });

    /// <summary>
    /// Checks whether a dispatch response exactly replays a result already stored for the same deterministic intent.
    /// </summary>
    /// <param name="state">The current checkpoint state.</param>
    /// <param name="intent">The dispatch intent being completed.</param>
    /// <param name="expectedIntent">The intent rebuilt from current checkpoint contents.</param>
    /// <param name="response">The returned child response.</param>
    /// <returns><see langword="true"/> when the identical response is already recorded and no pending intent remains.</returns>
    /// <exception cref="CoordinationException">Thrown when pending intent or result state conflicts with the replay.</exception>
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

    /// <summary>
    /// Decides whether a pending dispatch should be sent again or is already complete.
    /// </summary>
    /// <param name="state">The current checkpoint state.</param>
    /// <param name="intent">The intent the caller wants to send.</param>
    /// <param name="expectedIntent">The deterministic intent rebuilt from the current checkpoint.</param>
    /// <returns><see langword="true"/> only for a matching pending intent with running progress; <see langword="false"/> for a completed result.</returns>
    /// <exception cref="CoordinationException">Thrown when intent, result, and progress state conflict.</exception>
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

    /// <summary>
    /// Compares a persisted fixed-work association with a retry candidate across all pinned fields and outputs.
    /// </summary>
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

    /// <summary>
    /// Resolves a runtime-host route only from a valid HTTPS configure endpoint and a runtime API path.
    /// </summary>
    /// <param name="configureEndpoint">The registered host's HTTPS configure endpoint.</param>
    /// <param name="path">A runtime API path under the supported versioned prefix.</param>
    /// <returns>The validated absolute HTTPS endpoint.</returns>
    /// <exception cref="CoordinationException">Thrown when the base URI or path could change the trusted host destination.</exception>
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

    /// <summary>
    /// Sends a bounded, no-store request to a runtime host and validates its status, route, and JSON contract.
    /// </summary>
    /// <typeparam name="TResponse">The expected response contract.</typeparam>
    /// <param name="client">The HTTP client configured for runtime-host calls.</param>
    /// <param name="endpoint">The validated HTTPS runtime-host endpoint.</param>
    /// <param name="actor">The short-lived bearer credential and tenant selector.</param>
    /// <param name="body">The optional JSON request body; a non-null body uses POST.</param>
    /// <param name="allowNotReady">Whether the known not-ready response should return <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the remote request.</param>
    /// <returns>The response body, or <see langword="null"/> for an explicitly allowed not-ready result.</returns>
    /// <exception cref="CoordinationException">Thrown on redirects, cache violations, rejected statuses, malformed JSON, or uncertain transport outcomes.</exception>
    /// <remarks>Redirects are rejected and the original bearer is never forwarded to a different URI.</remarks>
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

    /// <summary>
    /// Validates readiness evidence and sends one addressed message to the registered runtime host.
    /// </summary>
    /// <returns>The host's non-null A2A response.</returns>
    /// <exception cref="CoordinationException">Thrown when readiness is stale or the runtime host request fails validation.</exception>
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

    /// <summary>
    /// Verifies that readiness and its observation grant are current for this runtime registration.
    /// </summary>
    /// <param name="receipt">The host readiness receipt.</param>
    /// <param name="registration">The active registration that must match the receipt.</param>
    /// <param name="timeProvider">The clock used to check grant and registration expiry.</param>
    /// <exception cref="CoordinationException">Thrown when startup phases, registration, image, fence, or observation grant are invalid or expired.</exception>
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

    /// <summary>
    /// Error response contract used to recognize the runtime host's explicit not-ready result.
    /// </summary>
    private sealed record RuntimeHostError(string Code);
}
