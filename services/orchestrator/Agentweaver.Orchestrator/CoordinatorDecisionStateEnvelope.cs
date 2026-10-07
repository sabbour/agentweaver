using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record CoordinatorDecisionStateEnvelope(
    int Version,
    string Issuer,
    string Subject,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string AcceptedSelectionHash,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    long Fence,
    CoordinatorOutcomeSpecification? OutcomeSpecification,
    bool OutcomeConfirmed,
    int NextClarifyingQuestionIndex,
    WorkflowDefinition? SelectedWorkflow,
    bool WorkflowConfirmed,
    WorkPlan? ConfirmedWorkPlan,
    WorkPlanSelectionContextEnvelope? ConfirmedSelectionContext,
    WorkPlan? CandidateWorkPlan,
    WorkPlanSelectionContextEnvelope? CandidateSelectionContext,
    WorkPlanScopeDiff? LastScopeDiff,
    CoordinatorGateRequest? PendingGate,
    ImmutableArray<CoordinatorGateDecisionReceipt> DecisionReceipts)
{
    public const int CurrentVersion = 1;

    public static CoordinatorDecisionStateEnvelope Capture(
        CoordinatorDecisionState state,
        CoordinatorDecisionBinding binding,
        WorkPlanRunSelectionContext? confirmedSelectionContext = null,
        WorkPlanRunSelectionContext? candidateSelectionContext = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new CoordinatorDecisionStateEnvelope(
            CurrentVersion,
            binding.Issuer,
            binding.Subject,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.RootSessionId,
            binding.AcceptedSelectionHash,
            binding.ProjectRevision,
            binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision,
            binding.ContextRevision,
            state.Fence,
            state.OutcomeSpec?.Specification,
            state.OutcomeConfirmed,
            state.NextClarifyingQuestionIndex,
            state.SelectedWorkflow?.Definition,
            state.WorkflowConfirmed,
            state.ConfirmedWorkPlan?.Plan,
            WorkPlanSelectionContextEnvelope.Capture(
                state.ConfirmedWorkPlan, confirmedSelectionContext),
            state.CandidateWorkPlan?.Plan,
            WorkPlanSelectionContextEnvelope.Capture(
                state.CandidateWorkPlan, candidateSelectionContext),
            state.LastScopeDiff,
            state.PendingGate,
            state.DecisionReceipts);
    }

    public CoordinatorDecisionStateEnvelopeRestoreResult Restore(
        CoordinatorDecisionBinding expectedBinding,
        Func<PinnedProviderBindingEnvelope, PinnedProviderBinding?>? pinnedBindingResolver = null)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        try
        {
            return RestoreCore(expectedBinding, pinnedBindingResolver);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(
                "decisionState",
                "The persisted decision envelope could not be safely restored.");
        }
    }

    private CoordinatorDecisionStateEnvelopeRestoreResult RestoreCore(
        CoordinatorDecisionBinding expectedBinding,
        Func<PinnedProviderBindingEnvelope, PinnedProviderBinding?>? pinnedBindingResolver)
    {
        if (Version != CurrentVersion ||
            !string.Equals(Issuer, expectedBinding.Issuer, StringComparison.Ordinal) ||
            !string.Equals(Subject, expectedBinding.Subject, StringComparison.Ordinal) ||
            !string.Equals(TenantId, expectedBinding.TenantId, StringComparison.Ordinal) ||
            !string.Equals(ProjectId, expectedBinding.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(RunId, expectedBinding.RunId, StringComparison.Ordinal) ||
            !string.Equals(RootSessionId, expectedBinding.RootSessionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(AcceptedSelectionHash) ||
            AcceptedSelectionHash.Length != 64 ||
            !AcceptedSelectionHash.All(Uri.IsHexDigit) ||
            !string.Equals(
                AcceptedSelectionHash, expectedBinding.AcceptedSelectionHash, StringComparison.Ordinal) ||
            ProjectRevision != expectedBinding.ProjectRevision ||
            ProjectConfigurationRevision != expectedBinding.ProjectConfigurationRevision ||
            PlatformRuntimeRevision != expectedBinding.PlatformRuntimeRevision ||
            !string.Equals(ContextRevision, expectedBinding.ContextRevision, StringComparison.Ordinal) ||
            Fence != expectedBinding.Fence)
            return Failure(
                "decisionState.binding",
                "The persisted decision envelope does not match the current actor, tenant, root, selection, or fence.");

        var outcome = OutcomeSpecification is null
            ? null
            : CoordinatorOutcomeSpecValidator.ValidateAndSnapshot(OutcomeSpecification);
        if (outcome is { IsValid: false })
            return new CoordinatorDecisionStateEnvelopeRestoreResult(null, outcome.Issues);

        var workflow = SelectedWorkflow is null
            ? null
            : WorkflowDefinitionValidator.ValidateAndSnapshot(SelectedWorkflow);
        if (workflow is { IsValid: false })
            return new CoordinatorDecisionStateEnvelopeRestoreResult(null, workflow.Issues);

        var confirmed = RestorePlan(
            ConfirmedWorkPlan,
            ConfirmedSelectionContext,
            workflow?.Value,
            expectedBinding.RunId,
            pinnedBindingResolver,
            "decisionState.confirmedWorkPlan");
        if (!confirmed.IsValid)
            return new CoordinatorDecisionStateEnvelopeRestoreResult(null, confirmed.Issues);

        var candidate = RestorePlan(
            CandidateWorkPlan,
            CandidateSelectionContext,
            workflow?.Value,
            expectedBinding.RunId,
            pinnedBindingResolver,
            "decisionState.candidateWorkPlan");
        if (!candidate.IsValid)
            return new CoordinatorDecisionStateEnvelopeRestoreResult(null, candidate.Issues);

        var scopeDiff = confirmed.Snapshot is not null && candidate.Snapshot is not null
            ? WorkflowScopeDiffer.Compare(confirmed.Snapshot, candidate.Snapshot)
            : LastScopeDiff;
        var restored = CoordinatorDecisionState.Restore(
            Fence,
            outcome?.Value,
            OutcomeConfirmed,
            NextClarifyingQuestionIndex,
            workflow?.Value,
            WorkflowConfirmed,
            confirmed.Snapshot,
            candidate.Snapshot,
            scopeDiff,
            PendingGate,
            DecisionReceipts);
        return new CoordinatorDecisionStateEnvelopeRestoreResult(
            restored.Value,
            restored.Issues);
    }

    private static PlanRestoreResult RestorePlan(
        WorkPlan? plan,
        WorkPlanSelectionContextEnvelope? selectionContext,
        WorkflowDefinitionSnapshot? workflow,
        string runId,
        Func<PinnedProviderBindingEnvelope, PinnedProviderBinding?>? pinnedBindingResolver,
        string path)
    {
        if (plan is null)
            return new PlanRestoreResult(null, []);
        if (workflow is null)
            return PlanRestoreResult.Failure(
                path, "A selected workflow is required to restore a work plan.");

        var context = selectionContext?.Restore(
            runId, pinnedBindingResolver, path + ".selectionContext");
        if (context is { IsValid: false })
            return new PlanRestoreResult(null, context.Issues);

        var validated = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            plan,
            context?.Value);
        if (!validated.IsValid)
            return PlanRestoreResult.Failure(WorkflowPlanIssuePrefix.Apply(validated, path));
        return new PlanRestoreResult(validated.Value, []);
    }

    private static CoordinatorDecisionStateEnvelopeRestoreResult Failure(string path, string message) =>
        new(null,
        [
            new WorkflowValidationIssue(
                WorkflowValidationCode.InvalidDecisionGate,
                path,
                message)
        ]);

    private static class WorkflowPlanIssuePrefix
    {
        public static ImmutableArray<WorkflowValidationIssue> Apply(
            WorkflowValidationResult<WorkPlanSnapshot> result,
            string prefix) =>
            result.Issues.Select(issue => issue with
            {
                Path = issue.Path.Length == 0 ? prefix : $"{prefix}.{issue.Path}"
            }).ToImmutableArray();
    }

    private sealed record PlanRestoreResult(
        WorkPlanSnapshot? Snapshot,
        ImmutableArray<WorkflowValidationIssue> Issues)
    {
        public bool IsValid => Issues.IsEmpty;

        public static PlanRestoreResult Failure(string path, string message) =>
            Failure([
                new WorkflowValidationIssue(
                    WorkflowValidationCode.InvalidSelectionContext,
                    path,
                    message)
            ]);

        public static PlanRestoreResult Failure(
            ImmutableArray<WorkflowValidationIssue> issues) =>
            new(null, issues);
    }
}

internal sealed record CoordinatorDecisionBinding(
    string Issuer,
    string Subject,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string AcceptedSelectionHash,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    long Fence)
{
    public static CoordinatorDecisionBinding Create(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        long fence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        if (fence < 1 ||
            actor.Issuer != selection.Authorization.Issuer ||
            actor.Subject != selection.Authorization.ActorId ||
            string.IsNullOrWhiteSpace(selection.Authorization.TenantId) ||
            selection.Selection.ProjectId != identity.ProjectId ||
            selection.Selection.RunId != identity.RunId ||
            selection.Selection.ProjectRevision < 1 ||
            selection.Selection.ProjectConfigurationRevision < 1 ||
            selection.Selection.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(selection.Selection.ContextRevision))
            throw new CoordinationException(
                "coordinator_selection_invalid", StatusCodes.Status409Conflict);

        return new CoordinatorDecisionBinding(
            actor.Issuer,
            actor.Subject,
            selection.Authorization.TenantId,
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(selection.Selection.Snapshot.GetRawText()))),
            selection.Selection.ProjectRevision,
            selection.Selection.ProjectConfigurationRevision,
            selection.Selection.PlatformRuntimeRevision,
            selection.Selection.ContextRevision,
            fence);
    }
}

internal sealed record CoordinatorDecisionStateEnvelopeRestoreResult(
    CoordinatorDecisionState? State,
    ImmutableArray<WorkflowValidationIssue> Issues)
{
    public bool IsValid => State is not null && Issues.IsEmpty;
}

internal sealed record WorkPlanSelectionContextEnvelope(
    ImmutableArray<RoleRunSelection> Roles,
    PinnedProviderBindingEnvelope? IsolationProviderBinding)
{
    public static WorkPlanSelectionContextEnvelope? Capture(
        WorkPlanSnapshot? plan,
        WorkPlanRunSelectionContext? context)
    {
        if (plan is null || context is null)
            return null;
        return new WorkPlanSelectionContextEnvelope(
            context.Roles,
            PinnedProviderBindingEnvelope.Capture(context.IsolationProviderBinding));
    }

    public SelectionContextRestoreResult Restore(
        string runId,
        Func<PinnedProviderBindingEnvelope, PinnedProviderBinding?>? pinnedBindingResolver,
        string path)
    {
        if (Roles.IsDefault)
            return new SelectionContextRestoreResult(null,
            [
                new WorkflowValidationIssue(
                    WorkflowValidationCode.InvalidSelectionContext,
                    path + ".roles",
                    "Persisted selection roles must be initialized.")
            ]);

        PinnedProviderBinding? binding = null;
        if (IsolationProviderBinding is not null)
        {
            if (pinnedBindingResolver is null)
                return SelectionContextRestoreResult.Failure(
                    path + ".isolationProviderBinding",
                    "Restoring a plan requires the exact pinned binding from trusted provider persistence.");
            binding = pinnedBindingResolver(IsolationProviderBinding);
            if (binding is null ||
                !IsolationProviderBinding.Matches(binding, runId))
                return SelectionContextRestoreResult.Failure(
                    path + ".isolationProviderBinding",
                    "Trusted provider persistence returned a missing or incompatible pinned binding.");
        }

        return new SelectionContextRestoreResult(
            new WorkPlanRunSelectionContext(Roles, binding),
            []);
    }
}

internal sealed record SelectionContextRestoreResult(
    WorkPlanRunSelectionContext? Value,
    ImmutableArray<WorkflowValidationIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;

    public static SelectionContextRestoreResult Failure(string path, string message) =>
        new(null,
        [
            new WorkflowValidationIssue(
                WorkflowValidationCode.PinnedSandboxBindingRequired,
                path,
                message)
        ]);
}

internal sealed record PinnedProviderBindingEnvelope(
    string RunId,
    ProviderSeam Seam,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    ProviderHostingPattern Hosting,
    string ResourceId,
    long ResourceGeneration,
    ImmutableHashSet<string> NegotiatedCapabilities)
{
    public static PinnedProviderBindingEnvelope? Capture(PinnedProviderBinding? binding) =>
        binding is null
            ? null
            : new PinnedProviderBindingEnvelope(
                binding.RunId,
                binding.Seam,
                binding.ProviderId,
                binding.AdapterVersion.ToString(),
                binding.OptionsSchemaVersion,
                binding.OptionsRevision,
                binding.Hosting,
                binding.Resource.ResourceId,
                binding.Resource.Generation,
                binding.NegotiatedCapabilities);

    public bool Matches(PinnedProviderBinding binding, string runId) =>
        RunId == runId &&
        binding.RunId == RunId &&
        binding.Seam == Seam &&
        binding.ProviderId == ProviderId &&
        binding.AdapterVersion.ToString() == AdapterVersion &&
        binding.OptionsSchemaVersion == OptionsSchemaVersion &&
        binding.OptionsRevision == OptionsRevision &&
        binding.Hosting == Hosting &&
        binding.Resource.ResourceId == ResourceId &&
        binding.Resource.Generation == ResourceGeneration &&
        binding.NegotiatedCapabilities.SetEquals(NegotiatedCapabilities);
}
