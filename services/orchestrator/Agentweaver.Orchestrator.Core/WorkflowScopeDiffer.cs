using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public static class WorkflowScopeDiffer
{
    public static WorkflowDefinitionScopeDiff Compare(
        WorkflowDefinitionSnapshot previous,
        WorkflowDefinitionSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        var oldSteps = previous.Definition.Steps.ToDictionary(
            step => step.Id, StringComparer.Ordinal);
        var newSteps = next.Definition.Steps.ToDictionary(
            step => step.Id, StringComparer.Ordinal);

        return new WorkflowDefinitionScopeDiff(
            [.. newSteps.Keys.Except(oldSteps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. oldSteps.Keys.Except(newSteps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. oldSteps.Keys.Intersect(newSteps.Keys, StringComparer.Ordinal)
                .Where(id => !Equivalent(oldSteps[id], newSteps[id]))
                .Order(StringComparer.Ordinal)],
            !string.Equals(previous.Definition.Id, next.Definition.Id, StringComparison.Ordinal),
            previous.Definition.MaximumWorkItems != next.Definition.MaximumWorkItems);
    }

    public static WorkPlanScopeDiff Compare(WorkPlanSnapshot previous, WorkPlanSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        var oldItems = previous.Plan.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var newItems = next.Plan.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return new WorkPlanScopeDiff(
            [.. newItems.Keys.Except(oldItems.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. oldItems.Keys.Except(newItems.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. oldItems.Keys.Intersect(newItems.Keys, StringComparer.Ordinal)
                .Where(id => !Equivalent(oldItems[id], newItems[id]))
                .Order(StringComparer.Ordinal)],
            WorkflowScopeDiffer.Compare(previous.Workflow, next.Workflow).RequiresConfirmation,
            !SameProviderBinding(previous.IsolationProviderBinding, next.IsolationProviderBinding));
    }

    private static bool SameProviderBinding(
        Agentweaver.Abstractions.PinnedProviderBinding? left,
        Agentweaver.Abstractions.PinnedProviderBinding? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Seam == right.Seam &&
              string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
              left.AdapterVersion == right.AdapterVersion &&
              left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
              string.Equals(left.OptionsRevision, right.OptionsRevision, StringComparison.Ordinal) &&
              left.Resource == right.Resource &&
              left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities);

    private static bool Equivalent(WorkflowStepDefinition left, WorkflowStepDefinition right) =>
        left.Mode == right.Mode &&
        left.Order == right.Order &&
        string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal) &&
        left.Cardinality == right.Cardinality &&
        SameSet(left.DependsOn, right.DependsOn) &&
        SameSet(left.AllowedRoles, right.AllowedRoles) &&
        SameSet(left.AllowedPhases, right.AllowedPhases) &&
        SameSet(left.AllowedIsolationChoices, right.AllowedIsolationChoices) &&
        SameSet(left.RequiredProviderCapabilities, right.RequiredProviderCapabilities) &&
        left.PlatformGate == right.PlatformGate &&
        Equivalent(left.FixedWork, right.FixedWork) &&
        Equivalent(left.BuildTestCommand, right.BuildTestCommand);

    private static bool Equivalent(WorkflowBuildTestCommand? left, WorkflowBuildTestCommand? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.ExecutionProfileReference == right.ExecutionProfileReference &&
              left.ExecutableReference == right.ExecutableReference &&
              left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
              left.WorkingDirectory == right.WorkingDirectory &&
              left.Outputs.SequenceEqual(right.Outputs);

    private static bool Equivalent(FixedWorkSpecification? left, FixedWorkSpecification? right) =>
        left is null
            ? right is null
            : right is not null &&
              string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
              string.Equals(left.Task, right.Task, StringComparison.Ordinal) &&
              string.Equals(left.RoleId, right.RoleId, StringComparison.Ordinal) &&
              string.Equals(left.Phase, right.Phase, StringComparison.Ordinal) &&
              string.Equals(left.IsolationChoice, right.IsolationChoice, StringComparison.Ordinal) &&
              SameSet(left.DeclaredOutputs, right.DeclaredOutputs);

    private static bool Equivalent(WorkPlanItem left, WorkPlanItem right) =>
        string.Equals(left.WorkflowStepId, right.WorkflowStepId, StringComparison.Ordinal) &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Task, right.Task, StringComparison.Ordinal) &&
        string.Equals(left.RoleId, right.RoleId, StringComparison.Ordinal) &&
        string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal) &&
        string.Equals(left.Phase, right.Phase, StringComparison.Ordinal) &&
        string.Equals(left.ModelSelectionReference, right.ModelSelectionReference, StringComparison.Ordinal) &&
        string.Equals(left.IsolationProviderId, right.IsolationProviderId, StringComparison.Ordinal) &&
        string.Equals(left.IsolationChoice, right.IsolationChoice, StringComparison.Ordinal) &&
        SameSet(left.DependsOn, right.DependsOn) &&
        SameSet(left.DeclaredOutputs, right.DeclaredOutputs);

    private static bool SameSet(ImmutableArray<string> left, ImmutableArray<string> right) =>
        !left.IsDefault && !right.IsDefault &&
        left.Length == right.Length &&
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
}
