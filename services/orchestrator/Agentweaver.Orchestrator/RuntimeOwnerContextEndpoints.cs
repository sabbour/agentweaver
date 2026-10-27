using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Resolves the current accepted runtime owner context for a child session and its selected work.
/// It verifies both model-work items and fixed-work associations against owner state before returning runtime configuration.
/// </summary>
public static partial class CoordinationEndpoints
{
    internal const string CoordinatorRuntimeAgentId = RuntimeActionContract.CoordinatorRootStepId;

    /// <summary>
    /// HTTP endpoint handler that reads the current runtime owner context.
    /// </summary>
    private static Task<IResult> ReadRuntimeOwnerContextAsync(
        string projectId, string runId, string sessionId, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts, PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await ReadRuntimeOwnerContextCoreAsync(
            projectId, runId, sessionId, context, options, projects, store, decisions,
            runSelectionContexts, checkpoints, cancellationToken, allowCoordinatorRoot: true)
            .ConfigureAwait(false)), cancellationToken);

    /// <summary>
    /// Builds a runtime owner context from the accepted run selection, root decision, child association, and run admission.
    /// </summary>
    /// <param name="allowTerminalChild">When true, permits observation of terminal fixed-work children without allowing dispatch.</param>
    /// <returns>The selected agent/model, authority revisions, and runtime budget limits.</returns>
    /// <exception cref="CoordinationException">Thrown when owner state, selection, plan, child association, or model binding is stale or invalid.</exception>
    /// <remarks>The method rereads selection, owner, and decision state before returning to detect changes during context construction.</remarks>
    internal static async Task<RuntimeOwnerContext> ReadRuntimeOwnerContextCoreAsync(
        string projectId, string runId, string sessionId, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts, PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken,
        bool allowTerminalChild = false,
        bool allowCoordinatorRoot = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var budgetLimits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection.Selection.Snapshot);
        var identity = new SessionIdentity(projectId, runId, sessionId);
        var acceptedSelectionHash = CoordinationOwnerStore.HashSelection(selection.Selection);
        var owner = allowCoordinatorRoot
            ? await store.ReadRuntimeOwnerStateForRegistrationAsync(
                actor, identity, selection.Authorization.TenantId, acceptedSelectionHash, cancellationToken)
                .ConfigureAwait(false)
            : await (allowTerminalChild
                ? store.ReadRuntimeOwnerStateForObservationAsync(actor, identity, cancellationToken)
                : store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken)).ConfigureAwait(false);
        if (owner.IsCoordinatorRoot)
            return await ReadCoordinatorRootRuntimeOwnerContextAsync(
                projectId, runId, sessionId, context, actor, identity, projects, store, decisions,
                selection, budgetLimits, owner, acceptedSelectionHash, cancellationToken).ConfigureAwait(false);

        var root = new SessionIdentity(projectId, runId, owner.RootSessionId);
        var selectionContext = await runSelectionContexts.ReadAsync(
                selection.Selection, owner.ExecutionFence, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        var decision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        if (decision.State.Fence != owner.ExecutionFence ||
            decision.SelectionHash != owner.AcceptedSelectionHash ||
            (!allowTerminalChild && !decision.State.CanDispatch) ||
            decision.State.ConfirmedWorkPlan is not { } confirmedPlan)
            throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        var validatedPlan = WorkPlanValidator.ValidateAndSnapshot(
            confirmedPlan.Workflow, confirmedPlan.Plan, selectionContext);
        if (!validatedPlan.IsValid || validatedPlan.Value is null)
            throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
        var plan = validatedPlan.Value;
        string agentId;
        string roleId;
        string modelSelectionReference;
        string workflowStepId;
        ModelTaskSelectionReference modelTaskSelection;
        MafExecutionCheckpointStore? execution = null;
        MafExecutionFixedWorkAssociation? fixedAssociation = null;
        MafExecutionCheckpointSnapshot? fixedCheckpoint = null;
        // Derive the route from accepted task and assignment data; a work item cannot override it.
        if (owner.WorkPlanItemId is { } workPlanItemId)
        {
            var workPlanItem = plan.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, workPlanItemId, StringComparison.Ordinal));
            if (workPlanItem is null || string.IsNullOrWhiteSpace(workPlanItem.AgentId) ||
                string.IsNullOrWhiteSpace(workPlanItem.ModelSelectionReference))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            agentId = workPlanItem.AgentId;
            roleId = workPlanItem.RoleId;
            workflowStepId = workPlanItem.WorkflowStepId;
            var step = plan.Workflow.Definition.Steps.FirstOrDefault(item =>
                string.Equals(item.Id, workflowStepId, StringComparison.Ordinal));
            if (step is null)
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            modelTaskSelection = ModelTaskSelectionResolver.ResolveSelection(
                step.ModelTaskKind ?? ModelTaskKind.Straightforward,
                roleId,
                agentId,
                selectionContext.ModelTaskSelections)
                ?? throw new CoordinationException(
                    "runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            if (!string.Equals(modelTaskSelection.ModelSelectionReference,
                    workPlanItem.ModelSelectionReference, StringComparison.Ordinal))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            modelSelectionReference = modelTaskSelection.ModelSelectionReference;
        }
        else
        {
            execution = new MafExecutionCheckpointStore(
                checkpoints, CreateExecutionCheckpointBinding(root, actor, owner.ExecutionFence,
                    MafExecutionCheckpointStore.CurrentSdkVersion,
                    PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot)));
            fixedAssociation = await (allowTerminalChild
                ? execution.ReadFixedWorkAssociationForObservationAsync(
                    sessionId, plan.Plan.Id, cancellationToken)
                : execution.ReadFixedWorkAssociationAsync(
                    sessionId, plan.Plan.Id, cancellationToken)).ConfigureAwait(false);
            fixedCheckpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (fixedAssociation is null ||
                fixedCheckpoint is null ||
                !fixedCheckpoint.State.FixedWorkAssociations.TryGetValue(
                    fixedAssociation.AssociationId, out var persistedAssociation) ||
                !FixedAssociationsMatch(persistedAssociation, fixedAssociation) ||
                !MatchesFixedRuntimeAssociation(fixedAssociation, plan, selectionContext, root,
                    sessionId, owner.AcceptedSelectionHash, owner.ExecutionFence,
                    fixedCheckpoint.State, decision))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            agentId = fixedAssociation.AgentId;
            workflowStepId = fixedAssociation.StepId;
            var step = plan.Workflow.Definition.Steps.FirstOrDefault(item =>
                string.Equals(item.Id, workflowStepId, StringComparison.Ordinal));
            if (step?.FixedWork is null)
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            roleId = step.FixedWork.RoleId;
            modelTaskSelection = ModelTaskSelectionResolver.ResolveSelection(
                step.ModelTaskKind ?? ModelTaskKind.Straightforward,
                roleId,
                agentId,
                selectionContext.ModelTaskSelections)
                ?? throw new CoordinationException(
                    "runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            if (!string.Equals(modelTaskSelection.ModelSelectionReference,
                    fixedAssociation.ModelSelectionReference, StringComparison.Ordinal))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            modelSelectionReference = modelTaskSelection.ModelSelectionReference;
        }
        var modelSelection = ReadModelSelection(
            selection.Selection.Snapshot, modelTaskSelection);
        var runAdmission = await store.ReadRunAdmissionAsync(actor, selection, cancellationToken)
            .ConfigureAwait(false);
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
        var currentOwner = await (allowTerminalChild
            ? store.ReadRuntimeOwnerStateForObservationAsync(actor, identity, cancellationToken)
            : store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken)).ConfigureAwait(false);
        if (currentOwner != owner)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        var currentDecision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        if (currentDecision.StateVersion != decision.StateVersion ||
            currentDecision.SelectionHash != decision.SelectionHash ||
            currentDecision.State.Fence != owner.ExecutionFence ||
            (!allowTerminalChild && !currentDecision.State.CanDispatch) ||
            currentDecision.State.ConfirmedWorkPlan is not { } currentConfirmedPlan)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        if (owner.WorkPlanItemId is { } currentWorkPlanItemId)
        {
            var currentWorkPlanItem = currentConfirmedPlan.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, currentWorkPlanItemId, StringComparison.Ordinal));
            if (currentWorkPlanItem is null ||
                !string.Equals(currentWorkPlanItem.RoleId, roleId, StringComparison.Ordinal) ||
                !string.Equals(currentWorkPlanItem.WorkflowStepId, workflowStepId, StringComparison.Ordinal) ||
                !string.Equals(currentWorkPlanItem.AgentId, agentId, StringComparison.Ordinal) ||
                !string.Equals(currentWorkPlanItem.ModelSelectionReference, modelSelectionReference,
                    StringComparison.Ordinal))
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        }
        else
        {
            var currentPlan = WorkPlanValidator.ValidateAndSnapshot(
                currentConfirmedPlan.Workflow, currentConfirmedPlan.Plan, selectionContext);
            if (!currentPlan.IsValid || currentPlan.Value is null)
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            var currentAssociation = await (allowTerminalChild
                ? execution!.ReadFixedWorkAssociationForObservationAsync(
                    sessionId, currentPlan.Value.Plan.Id, cancellationToken)
                : execution!.ReadFixedWorkAssociationAsync(
                    sessionId, currentPlan.Value.Plan.Id, cancellationToken)).ConfigureAwait(false);
            var currentCheckpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (currentAssociation is null ||
                currentCheckpoint is null ||
                !currentCheckpoint.State.FixedWorkAssociations.TryGetValue(
                    currentAssociation.AssociationId, out var currentPersistedAssociation) ||
                !FixedAssociationsMatch(currentPersistedAssociation, currentAssociation) ||
                !MatchesFixedRuntimeAssociation(currentAssociation, currentPlan.Value, selectionContext,
                    root, sessionId, currentDecision.SelectionHash, owner.ExecutionFence,
                    currentCheckpoint.State, currentDecision) ||
                !MatchesFixedRuntimeAssociation(fixedAssociation!, currentPlan.Value, selectionContext,
                    root, sessionId, currentDecision.SelectionHash, owner.ExecutionFence,
                    currentCheckpoint.State, currentDecision))
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            var currentFixedOwner = await (allowTerminalChild
                ? store.ReadRuntimeOwnerStateForObservationAsync(actor, identity, cancellationToken)
                : store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken)).ConfigureAwait(false);
            if (currentFixedOwner != owner)
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            var latestDecision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
                .ConfigureAwait(false);
            if (latestDecision.StateVersion != decision.StateVersion ||
                latestDecision.SelectionHash != decision.SelectionHash ||
            latestDecision.State.Fence != owner.ExecutionFence ||
            (!allowTerminalChild && !latestDecision.State.CanDispatch))
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        }
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
        return new RuntimeOwnerContext(
            1, actor.Issuer, actor.Subject, owner.TenantId, projectId, runId, sessionId,
            agentId, modelSelectionReference, owner.RuntimeTurnId,
            selection.Selection.ProjectRevision, selection.Selection.ProjectConfigurationRevision,
            selection.Selection.PlatformRuntimeRevision, selection.Selection.ContextRevision,
            owner.AcceptedSelectionHash.ToLowerInvariant(), owner.ExecutionFence, owner.LogicalTurnOrdinal,
            owner.StateVersion, decision.StateVersion)
        {
            WorkflowStepId = workflowStepId,
            ReasoningEffort = modelTaskSelection.ReasoningEffort,
            ModelCredentialReference = modelSelection.CredentialReference,
            ModelSourceMode = modelSelection.SourceMode,
            ModelBindingPin = runAdmission?.ModelBindingPin is { } runPin &&
                              string.Equals(runPin.ModelSelectionReference, modelSelectionReference,
                                  StringComparison.Ordinal) &&
                              string.Equals(runPin.ReasoningEffort, modelTaskSelection.ReasoningEffort,
                                  StringComparison.Ordinal)
                ? runPin
                : modelSelection.ModelBindingPin,
            ModelConnectionId = modelSelection.ConnectionId,
            ModelConnectionScope = modelSelection.ConnectionScope,
            MaxModelTurns = budgetLimits.MaxModelTurns,
            MaxToolCalls = budgetLimits.MaxToolCalls,
            MaxPromptTokens = budgetLimits.MaxPromptTokens,
            MaxRevisionAttempts = budgetLimits.MaxRevisionAttempts,
            CopilotSoftCreditLimit = budgetLimits.CopilotSoftCreditLimit,
            CopilotHardCreditLimit = budgetLimits.CopilotHardCreditLimit
        };
    }

    private static async Task<RuntimeOwnerContext> ReadCoordinatorRootRuntimeOwnerContextAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CoordinatorDecisionOwnerStore decisions,
        AuthorizedRunSelection selection,
        (int? MaxModelTurns, int? MaxToolCalls, int? MaxPromptTokens, int? MaxRevisionAttempts,
            decimal? CopilotSoftCreditLimit, decimal? CopilotHardCreditLimit) budgetLimits,
        SessionRuntimeOwnerState owner,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        var root = new SessionIdentity(projectId, runId, owner.RootSessionId);
        if (!owner.IsCoordinatorRoot || owner.RootSessionId != sessionId ||
            owner.TenantId != selection.Authorization.TenantId ||
            owner.AcceptedSelectionHash != acceptedSelectionHash)
            throw new CoordinationException(
                "runtime_owner_context_unavailable", StatusCodes.Status409Conflict);

        var decision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        RequireCoordinatorRootDecision(decision, owner);

        RuntimeAcceptedModelSelection modelSelection;
        try
        {
            // The coordinator root uses the model accepted for the run, not an assigned roster agent.
            modelSelection = RuntimeAcceptedModelSelection.Read(selection.Selection.Snapshot);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }

        var runAdmission = await store.ReadRunAdmissionAsync(actor, selection, cancellationToken)
            .ConfigureAwait(false);
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
        var currentOwner = await store.ReadRuntimeOwnerStateForRegistrationAsync(
            actor, identity, selection.Authorization.TenantId, acceptedSelectionHash, cancellationToken)
            .ConfigureAwait(false);
        var currentDecision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        if (currentOwner != owner || currentDecision.StateVersion != decision.StateVersion ||
            currentDecision.SelectionHash != decision.SelectionHash)
            throw new CoordinationException(
                "runtime_owner_context_stale", StatusCodes.Status409Conflict);
        RequireCoordinatorRootDecision(currentDecision, currentOwner);
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

        // Reuse admission's immutable model pin only for the same model reference and reasoning effort.
        var modelBindingPin = runAdmission is { } admission &&
            admission.ModelBindingPin.ModelSelectionReference == modelSelection.Reference &&
            admission.ModelBindingPin.ReasoningEffort == modelSelection.ReasoningEffort
                ? admission.ModelBindingPin
                : modelSelection.ModelBindingPin;
        return new RuntimeOwnerContext(
            1, actor.Issuer, actor.Subject, owner.TenantId, projectId, runId, sessionId,
            CoordinatorRuntimeAgentId, modelSelection.Reference, owner.RuntimeTurnId,
            selection.Selection.ProjectRevision, selection.Selection.ProjectConfigurationRevision,
            selection.Selection.PlatformRuntimeRevision, selection.Selection.ContextRevision,
            owner.AcceptedSelectionHash.ToLowerInvariant(), owner.ExecutionFence, owner.LogicalTurnOrdinal,
            owner.StateVersion, decision.StateVersion)
        {
            ModelCredentialReference = modelSelection.CredentialReference,
            ModelSourceMode = modelSelection.SourceMode,
            ReasoningEffort = modelSelection.ReasoningEffort,
            ModelBindingPin = modelBindingPin,
            ModelConnectionId = modelSelection.ConnectionId,
            ModelConnectionScope = modelSelection.ConnectionScope,
            MaxModelTurns = budgetLimits.MaxModelTurns,
            MaxToolCalls = budgetLimits.MaxToolCalls,
            MaxPromptTokens = budgetLimits.MaxPromptTokens,
            MaxRevisionAttempts = budgetLimits.MaxRevisionAttempts,
            CopilotSoftCreditLimit = budgetLimits.CopilotSoftCreditLimit,
            CopilotHardCreditLimit = budgetLimits.CopilotHardCreditLimit
        };
    }

    private static void RequireCoordinatorRootDecision(
        CoordinatorDecisionCurrentState decision,
        SessionRuntimeOwnerState owner)
    {
        if (decision.State.Fence != owner.ExecutionFence ||
            decision.SelectionHash != owner.AcceptedSelectionHash ||
            decision.State.ConfirmedWorkPlan is not null ||
            decision.State.CandidateWorkPlan is not null ||
            decision.State.PendingGate is not null)
            throw new CoordinationException(
                "runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Rebuilds the expected fixed-work association from current selection data and compares it to the saved binding.
    /// </summary>
    internal static bool MatchesFixedRuntimeAssociation(
        MafExecutionFixedWorkAssociation association,
        WorkPlanSnapshot snapshot,
        WorkPlanRunSelectionContext selectionContext,
        SessionIdentity parent,
        string childSessionId,
        string acceptedSelectionHash,
        long executionFence,
        MafExecutionCheckpoint checkpoint,
        CoordinatorDecisionCurrentState decision)
    {
        // Require checkpointed review history only after the decision version advances; the same version needs no review proof.
        if (association.AcceptedSelectionHash != acceptedSelectionHash ||
            association.ExecutionFence != executionFence ||
            !MafIndependentReviewContract.AllowsDecisionVersionAdvance(
                checkpoint, decision, association.DecisionStateVersion))
            return false;
        var step = snapshot.Workflow.Definition.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, association.StepId, StringComparison.Ordinal));
        if (step?.FixedWork is null)
            return false;

        MafExecutionFixedWorkAssociation expected;
        try
        {
            expected = MafExecutionPlanner.CreateFixedWorkAssociation(
                snapshot, selectionContext, parent, step, acceptedSelectionHash,
                executionFence, association.DecisionStateVersion, association.ActivationRevision, childSessionId);
        }
        catch (CoordinationException exception) when (exception.Code is
            "maf_fixed_work_unavailable" or "maf_fixed_work_selection_unavailable" or
            "maf_fixed_work_association_conflict")
        {
            return false;
        }

        return FixedAssociationsMatch(expected, association);
    }

    /// <summary>Reads the exact accepted model and effort selected for this workflow task.</summary>
    /// <exception cref="CoordinationException">Thrown when the accepted model route is missing or invalid.</exception>
    private static RuntimeAcceptedModelSelection ReadModelSelection(
        JsonElement snapshot, ModelTaskSelectionReference expectedSelection)
    {
        try
        {
            var accepted = RuntimeAcceptedModelSelection.Read(
                snapshot, expectedSelection.ModelSelectionReference, expectedSelection.ReasoningEffort);
            return accepted;
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }
}
