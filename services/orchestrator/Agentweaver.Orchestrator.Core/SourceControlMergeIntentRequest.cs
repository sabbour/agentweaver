using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public sealed class SourceControlMergeIntentRequest
{
    private SourceControlMergeIntentRequest(
        string intentId,
        string approvalRequestId,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlRepositoryPin repositoryPin,
        long sourceStateVersion,
        string workflowId,
        string definitionRevision,
        string workPlanId,
        string assemblyRequestId,
        string workflowStepId,
        SourceControlPullRequest pullRequest,
        SourceControlMergeMethod method,
        DateTimeOffset createdAt)
    {
        IntentId = intentId;
        ApprovalRequestId = approvalRequestId;
        AcceptedRun = acceptedRun;
        RepositoryPin = repositoryPin;
        SourceStateVersion = sourceStateVersion;
        WorkflowId = workflowId;
        DefinitionRevision = definitionRevision;
        WorkPlanId = workPlanId;
        AssemblyRequestId = assemblyRequestId;
        WorkflowStepId = workflowStepId;
        PullRequest = pullRequest;
        Method = method;
        CreatedAt = createdAt;
    }

    public string IntentId { get; }
    public string ApprovalRequestId { get; }
    public SourceControlAcceptedRunBinding AcceptedRun { get; }
    public SourceControlRepositoryPin RepositoryPin { get; }
    public long SourceStateVersion { get; }
    public string WorkflowId { get; }
    public string DefinitionRevision { get; }
    public string WorkPlanId { get; }
    public string AssemblyRequestId { get; }
    public string WorkflowStepId { get; }
    public SourceControlPullRequest PullRequest { get; }
    public SourceControlMergeMethod Method { get; }
    public DateTimeOffset CreatedAt { get; }

    public static SourceControlMergeIntentRequest Create(
        string intentId,
        string approvalRequestId,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlRepositoryPin repositoryPin,
        long sourceStateVersion,
        CoordinatorDecisionState currentDecisionState,
        CoordinatorAssemblyRequestSnapshot assemblyRequest,
        SourceControlPullRequest pullRequest,
        SourceControlMergeMethod method,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(repositoryPin);
        ArgumentNullException.ThrowIfNull(currentDecisionState);
        ArgumentNullException.ThrowIfNull(assemblyRequest);
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (!WorkflowValidationSupport.IsStableId(intentId) ||
            !WorkflowValidationSupport.IsStableId(approvalRequestId))
            throw new ArgumentException("Merge intent and approval request IDs must be stable identifiers.");
        if (sourceStateVersion < 1)
            throw new ArgumentOutOfRangeException(
                nameof(sourceStateVersion), "Source decision version must be positive.");
        if (repositoryPin.AcceptedRun != acceptedRun ||
            currentDecisionState.Fence != acceptedRun.Fence ||
            currentDecisionState.SelectedWorkflow is null ||
            currentDecisionState.ConfirmedWorkPlan is null ||
            currentDecisionState.PendingGate is not null)
            throw new InvalidOperationException(
                "A merge request must use the current accepted run, workflow, plan, and gate state.");

        var request = assemblyRequest.Request;
        var workflow = currentDecisionState.SelectedWorkflow.Definition;
        var plan = currentDecisionState.ConfirmedWorkPlan.Plan;
        if (request.Gate != WorkflowPlatformGate.Merge ||
            assemblyRequest.Step.Mode != WorkflowStepMode.Platform ||
            assemblyRequest.Step.PlatformGate != WorkflowPlatformGate.Merge ||
            request.WorkflowId != workflow.Id ||
            request.DefinitionRevision != workflow.Revision ||
            request.WorkPlanId != plan.Id ||
            request.WorkflowStepId != assemblyRequest.Step.Id ||
            !workflow.Steps.Any(step =>
                step.Id == request.WorkflowStepId &&
                step.Mode == WorkflowStepMode.Platform &&
                step.PlatformGate == WorkflowPlatformGate.Merge))
            throw new InvalidOperationException(
                "A merge request must target the exact accepted workflow's platform Merge step.");

        if (!repositoryPin.ProviderBinding.NegotiatedCapabilities.Contains(SourceControlCapabilities.Merge))
            throw new InvalidOperationException(
                "The pinned SourceControl provider did not negotiate merge capability.");
        if (pullRequest.Number <= 0 ||
            !string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) ||
            pullRequest.Merged ||
            string.IsNullOrWhiteSpace(pullRequest.HeadBranch) ||
            string.IsNullOrWhiteSpace(pullRequest.BaseBranch) ||
            !IsGitObjectId(pullRequest.HeadSha) ||
            !IsGitObjectId(pullRequest.BaseSha) ||
            !Enum.IsDefined(method))
            throw new ArgumentException(
                "A merge request requires an open pull request with exact branch and commit identities.",
                nameof(pullRequest));
        if (createdAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Intent creation time must be UTC.", nameof(createdAt));

        return new SourceControlMergeIntentRequest(
            intentId,
            approvalRequestId,
            acceptedRun,
            repositoryPin,
            sourceStateVersion,
            workflow.Id,
            workflow.Revision,
            plan.Id,
            request.RequestId,
            request.WorkflowStepId,
            pullRequest,
            method,
            createdAt);
    }

    public SourceControlMergeIntent Approve(
        CoordinatorDecisionState currentDecisionState,
        CoordinatorGateDecisionReceipt approvalReceipt)
    {
        ArgumentNullException.ThrowIfNull(currentDecisionState);
        ArgumentNullException.ThrowIfNull(approvalReceipt);
        if (approvalReceipt.RequestId != ApprovalRequestId ||
            approvalReceipt.SubjectId != IntentId ||
            currentDecisionState.Fence != AcceptedRun.Fence)
            throw new InvalidOperationException(
                "Merge intent approval must be the exact current typed receipt for this intent.");

        var assembly = CoordinatorDecisionFlow.RequestAssembly(
            currentDecisionState,
            new CoordinatorAssemblyRequest(
                AssemblyRequestId,
                WorkflowId,
                DefinitionRevision,
                WorkPlanId,
                WorkflowStepId,
                WorkflowPlatformGate.Merge));
        if (!assembly.IsSuccess || assembly.Value is null)
            throw new InvalidOperationException(
                "The accepted merge workflow step is no longer legal in the current decision state.");
        return SourceControlMergeIntent.Create(
            IntentId,
            AcceptedRun,
            RepositoryPin,
            currentDecisionState,
            assembly.Value,
            approvalReceipt,
            PullRequest,
            Method,
            CreatedAt);
    }

    private static bool IsGitObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
