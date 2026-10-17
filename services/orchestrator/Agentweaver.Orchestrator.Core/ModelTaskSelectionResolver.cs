using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Applies task, agent, and role precedence to accepted run model routes.</summary>
public static class ModelTaskSelectionResolver
{
    /// <summary>
    /// Resolves the full most-specific accepted model selection for one task and assignment.
    /// Agent-specific routes precede role routes; unscoped task routes are the final fallback.
    /// </summary>
    public static ModelTaskSelectionReference? ResolveSelection(
        ModelTaskKind taskKind,
        string roleId,
        string agentId,
        ImmutableArray<ModelTaskSelectionReference> selections)
    {
        if (!Enum.IsDefined(taskKind) ||
            !WorkflowValidationSupport.IsStableId(roleId) ||
            !WorkflowValidationSupport.IsStableId(agentId) ||
            selections.IsDefault)
            return null;

        var matching = selections
            .Where(selection =>
                selection is not null &&
                selection.TaskKind == taskKind &&
                (selection.RoleId is null ||
                 string.Equals(selection.RoleId, roleId, StringComparison.Ordinal)) &&
                (selection.AgentId is null ||
                 string.Equals(selection.AgentId, agentId, StringComparison.Ordinal)))
            .ToArray();
        if (matching.Length == 0 || matching.Any(selection => !IsValid(selection)))
            return null;

        var mostSpecific = matching.Max(Specificity);
        var preferred = matching.Where(selection => Specificity(selection) == mostSpecific).ToArray();
        return preferred.Length == 1 &&
               IsValid(preferred[0])
            ? preferred[0]
            : null;
    }

    /// <summary>Resolves only the model reference when callers do not need its accepted effort.</summary>
    public static string? ResolveReference(
        ModelTaskKind taskKind,
        string roleId,
        string agentId,
        ImmutableArray<ModelTaskSelectionReference> selections) =>
        ResolveSelection(taskKind, roleId, agentId, selections)?.ModelSelectionReference;

    /// <summary>Checks the supported runtime reasoning-effort values accepted by task routes.</summary>
    public static bool IsValidReasoningEffort(string? reasoningEffort) =>
        reasoningEffort is "low" or "medium" or "high" or "xhigh";

    private static bool IsValid(ModelTaskSelectionReference selection) =>
        Enum.IsDefined(selection.TaskKind) &&
        (selection.RoleId is null || WorkflowValidationSupport.IsStableId(selection.RoleId)) &&
        (selection.AgentId is null || WorkflowValidationSupport.IsStableId(selection.AgentId)) &&
        WorkflowValidationSupport.IsOpaqueReference(selection.ModelSelectionReference) &&
        IsValidReasoningEffort(selection.ReasoningEffort);

    private static int Specificity(ModelTaskSelectionReference selection) =>
        (selection.RoleId is null ? 0 : 1) + (selection.AgentId is null ? 0 : 2);
}
