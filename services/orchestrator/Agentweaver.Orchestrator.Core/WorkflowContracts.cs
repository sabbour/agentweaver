using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public enum WorkflowDefinitionOrigin
{
    BuiltIn,
    Generated
}

public enum WorkflowStepMode
{
    Fixed,
    Open,
    Platform
}

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

public sealed record WorkflowCardinality(int Minimum, int Maximum);

public sealed record FixedWorkSpecification(
    string Title,
    string Task,
    string RoleId,
    string Phase,
    string IsolationChoice,
    ImmutableArray<string> DeclaredOutputs);

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
    WorkflowPlatformGate? PlatformGate);

public sealed record WorkflowDefinition(
    string Id,
    string Revision,
    string CatalogVersion,
    WorkflowDefinitionOrigin Origin,
    int MaximumWorkItems,
    ImmutableArray<WorkflowStepDefinition> Steps);

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

public sealed record WorkPlan(
    string Id,
    string WorkflowId,
    string DefinitionRevision,
    string CatalogVersion,
    ImmutableArray<WorkPlanItem> Items);

public sealed record RoleRunSelection(
    string RoleId,
    ImmutableArray<string> EligibleAgentIds,
    ImmutableArray<string> ModelSelectionReferences,
    ImmutableArray<string> EligibleIsolationChoices = default);

public sealed record WorkPlanRunSelectionContext(
    ImmutableArray<RoleRunSelection> Roles,
    PinnedProviderBinding? IsolationProviderBinding);

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
    OutputLimitExceeded
}

public sealed record WorkflowValidationIssue(
    WorkflowValidationCode Code,
    string Path,
    string Message);

public sealed class WorkflowValidationResult<T> where T : class
{
    private WorkflowValidationResult(T? value, ImmutableArray<WorkflowValidationIssue> issues) =>
        (Value, Issues) = (value, issues);

    public T? Value { get; }
    public ImmutableArray<WorkflowValidationIssue> Issues { get; }
    public bool IsValid => Value is not null && Issues.IsEmpty;

    internal static WorkflowValidationResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), []);

    internal static WorkflowValidationResult<T> Failure(
        ImmutableArray<WorkflowValidationIssue> issues)
    {
        if (issues.IsEmpty)
            throw new ArgumentException("A failed validation result requires at least one issue.", nameof(issues));
        return new WorkflowValidationResult<T>(null, issues);
    }
}

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
}

public sealed class WorkflowDefinitionSnapshot
{
    internal WorkflowDefinitionSnapshot(WorkflowDefinition definition) => Definition = definition;

    public WorkflowDefinition Definition { get; }
    public bool RequiresFirstUseConfirmation =>
        Definition.Origin == WorkflowDefinitionOrigin.Generated;
}

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

public sealed record WorkflowDefinitionScopeDiff(
    ImmutableArray<string> AddedStepIds,
    ImmutableArray<string> RemovedStepIds,
    ImmutableArray<string> ChangedStepIds,
    bool WorkflowIdentityChanged,
    bool WorkPlanLimitChanged)
{
    public bool RequiresConfirmation =>
        WorkflowIdentityChanged || WorkPlanLimitChanged ||
        !AddedStepIds.IsEmpty || !RemovedStepIds.IsEmpty || !ChangedStepIds.IsEmpty;
}

public sealed record WorkPlanScopeDiff(
    ImmutableArray<string> AddedWorkItemIds,
    ImmutableArray<string> RemovedWorkItemIds,
    ImmutableArray<string> ChangedWorkItemIds,
    bool WorkflowDefinitionChanged,
    bool IsolationProviderBindingChanged)
{
    public bool RequiresConfirmation =>
        WorkflowDefinitionChanged || IsolationProviderBindingChanged ||
        !AddedWorkItemIds.IsEmpty || !RemovedWorkItemIds.IsEmpty || !ChangedWorkItemIds.IsEmpty;
}
