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
        CoordinatorRunSelectionContextStore runSelectionContexts, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await ReadRuntimeOwnerContextCoreAsync(
            projectId, runId, sessionId, context, options, projects, store, decisions,
            runSelectionContexts, cancellationToken).ConfigureAwait(false)), cancellationToken);

    internal static async Task<RuntimeOwnerContext> ReadRuntimeOwnerContextCoreAsync(
        string projectId, string runId, string sessionId, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore runSelectionContexts, CancellationToken cancellationToken)
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
        var workPlanItem = validatedPlan.Value.Plan.Items.FirstOrDefault(item =>
            string.Equals(item.Id, owner.WorkPlanItemId, StringComparison.Ordinal));
        if (workPlanItem is null || string.IsNullOrWhiteSpace(workPlanItem.AgentId) ||
            string.IsNullOrWhiteSpace(workPlanItem.ModelSelectionReference))
            throw new CoordinationException("runtime_owner_work_plan_unavailable", StatusCodes.Status409Conflict);
        var modelSelection = ReadModelSelection(
            selection.Selection.Snapshot, workPlanItem.ModelSelectionReference);
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
        var currentWorkPlanItem = currentDecision.State.ConfirmedWorkPlan?.Plan.Items.FirstOrDefault(item =>
            string.Equals(item.Id, owner.WorkPlanItemId, StringComparison.Ordinal));
        if (currentDecision.StateVersion != decision.StateVersion ||
            currentDecision.SelectionHash != decision.SelectionHash ||
            currentDecision.State.Fence != owner.ExecutionFence || !currentDecision.State.CanDispatch ||
            currentWorkPlanItem is null ||
            !string.Equals(currentWorkPlanItem.AgentId, workPlanItem.AgentId, StringComparison.Ordinal) ||
            !string.Equals(currentWorkPlanItem.ModelSelectionReference,
                workPlanItem.ModelSelectionReference, StringComparison.Ordinal))
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        await RequireUnchangedAuthorizedSelectionAsync(
            context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
        return new RuntimeOwnerContext(
            1, actor.Issuer, actor.Subject, owner.TenantId, projectId, runId, sessionId,
            workPlanItem.AgentId, workPlanItem.ModelSelectionReference, owner.RuntimeTurnId,
            selection.Selection.ProjectRevision, selection.Selection.ProjectConfigurationRevision,
            selection.Selection.PlatformRuntimeRevision, selection.Selection.ContextRevision,
            owner.AcceptedSelectionHash.ToLowerInvariant(), owner.ExecutionFence, owner.LogicalTurnOrdinal,
            owner.StateVersion, decision.StateVersion)
        {
            WorkflowStepId = workPlanItem.WorkflowStepId,
            ModelCredentialReference = modelSelection.CredentialReference,
            ModelSourceMode = modelSelection.SourceMode,
            ModelBindingPin = runAdmission?.ModelBindingPin,
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

    private static (SecretRef? CredentialReference, ModelSourceMode? SourceMode,
        Guid? ConnectionId, ProjectAuthorityResourceType? ConnectionScope) ReadModelSelection(
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
            return (accepted.CredentialReference, accepted.SourceMode, accepted.ConnectionId, accepted.ConnectionScope);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }
}
