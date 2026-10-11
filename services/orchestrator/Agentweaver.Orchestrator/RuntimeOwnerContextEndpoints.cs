using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    private static Task<IResult> ReadRuntimeOwnerContextAsync(
        string projectId, string runId, string sessionId, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts, PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await ReadRuntimeOwnerContextCoreAsync(
            projectId, runId, sessionId, context, options, projects, store, decisions,
            runSelectionContexts, checkpoints, cancellationToken).ConfigureAwait(false)), cancellationToken);

    internal static async Task<RuntimeOwnerContext> ReadRuntimeOwnerContextCoreAsync(
        string projectId, string runId, string sessionId, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts, PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var budgetLimits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection.Selection.Snapshot);
        var identity = new SessionIdentity(projectId, runId, sessionId);
        var owner = await store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken)
            .ConfigureAwait(false);
        var root = new SessionIdentity(projectId, runId, owner.RootSessionId);
        var selectionContext = await runSelectionContexts.ReadAsync(
                selection.Selection, owner.ExecutionFence, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        var decision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        if (decision.State.Fence != owner.ExecutionFence ||
            decision.SelectionHash != owner.AcceptedSelectionHash ||
            !decision.State.CanDispatch || decision.State.ConfirmedWorkPlan is not { } confirmedPlan)
            throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        var validatedPlan = WorkPlanValidator.ValidateAndSnapshot(
            confirmedPlan.Workflow, confirmedPlan.Plan, selectionContext);
        if (!validatedPlan.IsValid || validatedPlan.Value is null)
            throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
        var plan = validatedPlan.Value;
        string agentId;
        string modelSelectionReference;
        string workflowStepId;
        MafExecutionCheckpointStore? execution = null;
        MafExecutionFixedWorkAssociation? fixedAssociation = null;
        if (owner.WorkPlanItemId is { } workPlanItemId)
        {
            var workPlanItem = plan.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, workPlanItemId, StringComparison.Ordinal));
            if (workPlanItem is null || string.IsNullOrWhiteSpace(workPlanItem.AgentId) ||
                string.IsNullOrWhiteSpace(workPlanItem.ModelSelectionReference))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            agentId = workPlanItem.AgentId;
            modelSelectionReference = workPlanItem.ModelSelectionReference;
            workflowStepId = workPlanItem.WorkflowStepId;
        }
        else
        {
            execution = new MafExecutionCheckpointStore(
                checkpoints, CreateExecutionCheckpointBinding(root, actor, owner.ExecutionFence,
                    MafExecutionCheckpointStore.CurrentSdkVersion,
                    PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot)));
            fixedAssociation = await execution.ReadFixedWorkAssociationAsync(
                sessionId, plan.Plan.Id, cancellationToken).ConfigureAwait(false);
            if (fixedAssociation is null ||
                !MatchesFixedRuntimeAssociation(fixedAssociation, plan, selectionContext, root,
                    sessionId, owner.AcceptedSelectionHash, owner.ExecutionFence, decision.StateVersion))
                throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
            agentId = fixedAssociation.AgentId;
            modelSelectionReference = fixedAssociation.ModelSelectionReference;
            workflowStepId = fixedAssociation.StepId;
        }
        var modelSelection = ReadModelSelection(
            selection.Selection.Snapshot, modelSelectionReference);
        var runAdmission = await store.ReadRunAdmissionAsync(actor, selection, cancellationToken)
            .ConfigureAwait(false);
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
        var currentOwner = await store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken)
            .ConfigureAwait(false);
        if (currentOwner != owner)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        var currentDecision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
            .ConfigureAwait(false);
        if (currentDecision.StateVersion != decision.StateVersion ||
            currentDecision.SelectionHash != decision.SelectionHash ||
            currentDecision.State.Fence != owner.ExecutionFence || !currentDecision.State.CanDispatch ||
            currentDecision.State.ConfirmedWorkPlan is not { } currentConfirmedPlan)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        if (owner.WorkPlanItemId is { } currentWorkPlanItemId)
        {
            var currentWorkPlanItem = currentConfirmedPlan.Plan.Items.FirstOrDefault(item =>
                string.Equals(item.Id, currentWorkPlanItemId, StringComparison.Ordinal));
            if (currentWorkPlanItem is null ||
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
            var currentAssociation = await execution!.ReadFixedWorkAssociationAsync(
                sessionId, currentPlan.Value.Plan.Id, cancellationToken).ConfigureAwait(false);
            if (currentAssociation is null ||
                !MatchesFixedRuntimeAssociation(currentAssociation, currentPlan.Value, selectionContext,
                    root, sessionId, currentDecision.SelectionHash, owner.ExecutionFence,
                    currentDecision.StateVersion) ||
                !MatchesFixedRuntimeAssociation(fixedAssociation!, currentPlan.Value, selectionContext,
                    root, sessionId, currentDecision.SelectionHash, owner.ExecutionFence,
                    currentDecision.StateVersion))
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            if (await store.ReadRuntimeOwnerStateAsync(actor, identity, cancellationToken).ConfigureAwait(false) != owner)
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            var latestDecision = await decisions.ReadCurrentAsync(actor, root, selection, cancellationToken)
                .ConfigureAwait(false);
            if (latestDecision.StateVersion != decision.StateVersion ||
                latestDecision.SelectionHash != decision.SelectionHash ||
                latestDecision.State.Fence != owner.ExecutionFence || !latestDecision.State.CanDispatch)
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
            ModelCredentialReference = modelSelection.CredentialReference,
            ModelSourceMode = modelSelection.SourceMode,
            ModelBindingPin = runAdmission?.ModelBindingPin ?? modelSelection.ModelBindingPin,
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

    internal static bool MatchesFixedRuntimeAssociation(
        MafExecutionFixedWorkAssociation association,
        WorkPlanSnapshot snapshot,
        WorkPlanRunSelectionContext selectionContext,
        SessionIdentity parent,
        string childSessionId,
        string acceptedSelectionHash,
        long executionFence,
        long decisionStateVersion)
    {
        if (association.AcceptedSelectionHash != acceptedSelectionHash ||
            association.ExecutionFence != executionFence ||
            association.DecisionStateVersion != decisionStateVersion)
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
                executionFence, decisionStateVersion, association.ActivationRevision, childSessionId);
        }
        catch (CoordinationException exception) when (exception.Code is
            "maf_fixed_work_unavailable" or "maf_fixed_work_selection_unavailable" or
            "maf_fixed_work_association_conflict")
        {
            return false;
        }

        return FixedAssociationsMatch(expected, association);
    }

    private static RuntimeAcceptedModelSelection ReadModelSelection(
        JsonElement snapshot, string expectedModelReference)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("modelSelection", out var modelSelection) ||
            modelSelection.ValueKind != JsonValueKind.Object ||
            !modelSelection.TryGetProperty("reference", out var reference) ||
            reference.ValueKind != JsonValueKind.String ||
            !string.Equals(reference.GetString(), expectedModelReference, StringComparison.Ordinal))
            throw new CoordinationException(
                "runtime_owner_model_selection_unavailable", StatusCodes.Status409Conflict);

        try
        {
            var accepted = RuntimeAcceptedModelSelection.Read(snapshot);
            return accepted;
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }
}
