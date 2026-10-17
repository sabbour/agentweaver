using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Identifies built-in definitions and generated definitions that require first-use confirmation.</summary>
public enum WorkflowDefinitionOrigin
{
    BuiltIn,
    Generated
}

/// <summary>Determines whether work is prescribed, coordinator-proposed, or platform-owned.</summary>
public enum WorkflowStepMode
{
    Fixed,
    Open,
    Platform
}

/// <summary>Platform executor owned by a workflow gate; agents cannot substitute ordinary work for it.</summary>
public enum WorkflowPlatformGate
{
    BuildTest,
    Preview,
    ResponsibleAi,
    RubberDuck,
    IndependentReview,
    OpenPullRequest,
    Merge,
    Scribe,
    Publish
}

/// <summary>Inclusive lower and upper bounds for work accepted by one workflow step.</summary>
public sealed record WorkflowCardinality(int Minimum, int Maximum);

/// <summary>Prescribed task content and eligibility for a fixed workflow step.</summary>
public sealed record FixedWorkSpecification(
    string Title,
    string Task,
    string RoleId,
    string Phase,
    string IsolationChoice,
    ImmutableArray<string> DeclaredOutputs);

/// <summary>One bounded file obligation produced by a BuildTest command.</summary>
public sealed record WorkflowBuildTestOutput(
    string Name,
    string RelativePath,
    bool Required,
    long MaximumBytes);

/// <summary>Typed command and output contract for a platform-owned BuildTest step.</summary>
public sealed record WorkflowBuildTestCommand(
    string ExecutionProfileReference,
    string ExecutableReference,
    ImmutableArray<string> Arguments,
    string WorkingDirectory,
    ImmutableArray<WorkflowBuildTestOutput> Outputs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceSource? WorkspaceSource { get; init; }
}

/// <summary>Versioned workflow step contract, including dependencies, eligibility, and gate details.</summary>
public sealed record WorkflowStepDefinition(
    string Id,
    string Purpose,
    WorkflowStepMode Mode,
    int Order,
    WorkflowCardinality Cardinality,
    ImmutableArray<string> DependsOn,
    ImmutableArray<string> AllowedRoles,
    ImmutableArray<string> AllowedPhases,
    ImmutableArray<string> AllowedIsolationChoices,
    ImmutableArray<string> RequiredProviderCapabilities,
    FixedWorkSpecification? FixedWork,
    WorkflowPlatformGate? PlatformGate)
{
    /// <summary>
    /// Task category used to resolve this step's accepted model; omitted legacy values mean straightforward work.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelTaskKind? ModelTaskKind { get; init; }

    public WorkflowBuildTestCommand? BuildTestCommand { get; init; }
}

/// <summary>Untrusted workflow definition input before it is accepted as a validated snapshot.</summary>
public sealed record WorkflowDefinition(
    string Id,
    string Revision,
    string CatalogVersion,
    WorkflowDefinitionOrigin Origin,
    int MaximumWorkItems,
    ImmutableArray<WorkflowStepDefinition> Steps);

/// <summary>Proposed child work item; each item must map to an open step in the pinned workflow.</summary>
public sealed record WorkPlanItem(
    string Id,
    string WorkflowStepId,
    string Title,
    string Task,
    string RoleId,
    string AgentId,
    string Phase,
    string ModelSelectionReference,
    string IsolationProviderId,
    string IsolationChoice,
    ImmutableArray<string> DependsOn,
    ImmutableArray<string> DeclaredOutputs);

/// <summary>Coordinator-proposed plan carrying the exact workflow and catalog revisions it targets.</summary>
public sealed record WorkPlan(
    string Id,
    string WorkflowId,
    string DefinitionRevision,
    string CatalogVersion,
    ImmutableArray<WorkPlanItem> Items);

/// <summary>Run-pinned eligible agents, model selections, and isolation choices for a role.</summary>
public sealed record RoleRunSelection(
    string RoleId,
    ImmutableArray<string> EligibleAgentIds,
    ImmutableArray<string> ModelSelectionReferences,
    ImmutableArray<string> EligibleIsolationChoices = default);

/// <summary>Selections used to bind a proposed plan to the run's authorized roles and pinned Sandbox provider.</summary>
public sealed record WorkPlanRunSelectionContext(
    ImmutableArray<RoleRunSelection> Roles,
    PinnedProviderBinding? IsolationProviderBinding)
{
    /// <summary>Accepted task, role, and agent model routes carried with this run's immutable selection context.</summary>
    public ImmutableArray<ModelTaskSelectionReference> ModelTaskSelections { get; init; } = [];
}

/// <summary>Stable validation codes for workflow, coordinator, and WorkPlan contract failures.</summary>
public enum WorkflowValidationCode
{
    InvalidDefinitionId,
    InvalidDefinitionRevision,
    InvalidCatalogVersion,
    InvalidDefinitionOrigin,
    InvalidStepCount,
    InvalidStepId,
    DuplicateStepId,
    InvalidStepPurpose,
    InvalidStepOrder,
    DuplicateStepOrder,
    InvalidStepMode,
    InvalidModelTaskKind,
    InvalidCardinality,
    InvalidStepRole,
    DuplicateStepRole,
    InvalidStepPhase,
    DuplicateStepPhase,
    InvalidIsolationChoice,
    DuplicateIsolationChoice,
    InvalidProviderCapability,
    DuplicateProviderCapability,
    InvalidStepDependency,
    DuplicateStepDependency,
    MissingStepDependency,
    StepDependencyCycle,
    StepOrderViolation,
    InvalidFixedWork,
    InvalidPlatformGate,
    InvalidOutputPath,
    DuplicateOutputPath,
    InvalidWorkPlanId,
    InvalidWorkPlanItems,
    WorkPlanDefinitionMismatch,
    WorkPlanLimitExceeded,
    InvalidWorkItemId,
    DuplicateWorkItemId,
    MissingWorkflowStepId,
    UnknownWorkflowStep,
    StepDoesNotAcceptProposals,
    WorkItemCardinalityViolation,
    WorkPlanRoleNotAllowed,
    WorkPlanPhaseNotAllowed,
    MissingAgent,
    AgentNotEligible,
    MissingModelSelection,
    ModelSelectionNotEligible,
    MissingIsolationChoice,
    IsolationChoiceNotAllowed,
    InvalidSelectionContext,
    PinnedSandboxBindingRequired,
    PinnedProviderMismatch,
    RequiredCapabilityUnavailable,
    InvalidWorkItemText,
    InvalidWorkItemDependency,
    DuplicateWorkItemDependency,
    MissingWorkItemDependency,
    WorkItemDependencyCycle,
    DependencyStepOrderViolation,
    MissingJoinDependency,
    OutputConflictWithFixedWork,
    OutputLimitExceeded,
    InvalidOutcomeSpecification,
    InvalidDecisionGate,
    InvalidDecisionAnswer,
    InvalidDecisionRequestId,
    StaleDecisionRequest,
    UnauthorizedDecisionActor,
    InvalidDecisionFence,
    FreeformDecisionNotAllowed,
    OutcomeConfirmationRequired,
    WorkflowSelectionNotAuthorized,
    InvalidWorkflowSelection,
    WorkflowConfirmationRequired,
    WorkPlanConfirmationRequired,
    InvalidAssemblyRequest,
    CoordinatorTransitionBlocked,
    InvalidBuildTestCommand
}

/// <summary>A validation issue addressed to a contract path.</summary>
public sealed record WorkflowValidationIssue(
    WorkflowValidationCode Code,
    string Path,
    string Message);

/// <summary>Validation result that carries either one accepted value or one or more issues.</summary>
public sealed class WorkflowValidationResult<T> where T : class
{
    private WorkflowValidationResult(T? value, ImmutableArray<WorkflowValidationIssue> issues) =>
        (Value, Issues) = (value, issues);

    public T? Value { get; }
    public ImmutableArray<WorkflowValidationIssue> Issues { get; }
    public bool IsValid => Value is not null && Issues.IsEmpty;

    /// <summary>Creates a successful result; only the owning assembly can construct validator results.</summary>
    internal static WorkflowValidationResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), []);

    /// <summary>Creates a failed result and requires at least one issue to explain it.</summary>
    internal static WorkflowValidationResult<T> Failure(
        ImmutableArray<WorkflowValidationIssue> issues)
    {
        if (issues.IsEmpty)
            throw new ArgumentException("A failed validation result requires at least one issue.", nameof(issues));
        return new WorkflowValidationResult<T>(null, issues);
    }
}

/// <summary>Hard upper bounds applied by pure workflow and plan validators.</summary>
public static class WorkflowDomainLimits
{
    public const int MaximumSteps = 128;
    public const int MaximumWorkItems = 256;
    public const int MaximumItemsPerStep = 128;
    public const int MaximumEligibilityValues = 256;
    public const int MaximumOutputsPerItem = 64;
    public const int MaximumOutputPathLength = 1024;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumWorkTextLength = 16_384;
    public const int MaximumOutcomeQuestions = 32;
    public const int MaximumDecisionReceipts = 256;
}

/// <summary>Validated workflow definition; generated definitions indicate that first-use approval is required.</summary>
public sealed class WorkflowDefinitionSnapshot
{
    internal WorkflowDefinitionSnapshot(WorkflowDefinition definition) => Definition = definition;

    public WorkflowDefinition Definition { get; }
    /// <summary>Whether the generated definition must be confirmed before first use.</summary>
    public bool RequiresFirstUseConfirmation =>
        Definition.Origin == WorkflowDefinitionOrigin.Generated;
}

/// <summary>Validated WorkPlan and its workflow/provider snapshots, ready for coordinator decision flow.</summary>
public sealed class WorkPlanSnapshot
{
    internal WorkPlanSnapshot(
        WorkflowDefinitionSnapshot workflow,
        WorkPlan plan,
        PinnedProviderBinding? isolationProviderBinding) =>
        (Workflow, Plan, IsolationProviderBinding) =
            (workflow, plan, isolationProviderBinding);

    public WorkflowDefinitionSnapshot Workflow { get; }
    public WorkPlan Plan { get; }
    public PinnedProviderBinding? IsolationProviderBinding { get; }
}

/// <summary>Stable comparison of workflow-definition scope between two validated snapshots.</summary>
public sealed record WorkflowDefinitionScopeDiff(
    ImmutableArray<string> AddedStepIds,
    ImmutableArray<string> RemovedStepIds,
    ImmutableArray<string> ChangedStepIds,
    bool WorkflowIdentityChanged,
    bool WorkPlanLimitChanged)
{
    /// <summary>True when identity, limit, or any step's contract changed.</summary>
    public bool RequiresConfirmation =>
        WorkflowIdentityChanged || WorkPlanLimitChanged ||
        !AddedStepIds.IsEmpty || !RemovedStepIds.IsEmpty || !ChangedStepIds.IsEmpty;
}

/// <summary>Stable comparison of planned work and pinned isolation binding between snapshots.</summary>
public sealed record WorkPlanScopeDiff(
    ImmutableArray<string> AddedWorkItemIds,
    ImmutableArray<string> RemovedWorkItemIds,
    ImmutableArray<string> ChangedWorkItemIds,
    bool WorkflowDefinitionChanged,
    bool IsolationProviderBindingChanged)
{
    /// <summary>True when work, workflow definition, or provider binding changed.</summary>
    public bool RequiresConfirmation =>
        WorkflowDefinitionChanged || IsolationProviderBindingChanged ||
        !AddedWorkItemIds.IsEmpty || !RemovedWorkItemIds.IsEmpty || !ChangedWorkItemIds.IsEmpty;
}
