using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public static class WorkPlanValidator
{
    public static WorkflowValidationResult<WorkPlanSnapshot> ValidateAndSnapshot(
        WorkflowDefinitionSnapshot? workflow,
        WorkPlan? plan,
        WorkPlanRunSelectionContext? selectionContext)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (workflow is null)
        {
            Add(issues, WorkflowValidationCode.WorkPlanDefinitionMismatch, "workflow",
                "A validated workflow definition snapshot is required.");
        }
        if (plan is null)
        {
            Add(issues, WorkflowValidationCode.InvalidWorkPlanId, "plan",
                "A work plan is required.");
            return WorkflowValidationResult<WorkPlanSnapshot>.Failure(issues.ToImmutable());
        }

        if (!WorkflowValidationSupport.IsStableId(plan.Id))
            Add(issues, WorkflowValidationCode.InvalidWorkPlanId, "plan.id",
                "WorkPlan ID must be a stable, non-empty identifier.");
        if (workflow is not null &&
            (!string.Equals(plan.WorkflowId, workflow.Definition.Id, StringComparison.Ordinal) ||
             !string.Equals(plan.DefinitionRevision, workflow.Definition.Revision, StringComparison.Ordinal) ||
             !string.Equals(plan.CatalogVersion, workflow.Definition.CatalogVersion, StringComparison.Ordinal)))
            Add(issues, WorkflowValidationCode.WorkPlanDefinitionMismatch, "plan",
                "WorkPlan workflow, definition revision, and catalog version must match the validated snapshot.");

        if (plan.Items.IsDefault)
            Add(issues, WorkflowValidationCode.InvalidWorkPlanItems, "plan.items",
                "WorkPlan items must be an initialized collection.");

        var maximumPlanItems = workflow is null
            ? WorkflowDomainLimits.MaximumWorkItems
            : Math.Min(WorkflowDomainLimits.MaximumWorkItems, workflow.Definition.MaximumWorkItems);
        if (!plan.Items.IsDefault && plan.Items.Length > maximumPlanItems)
            Add(issues, WorkflowValidationCode.WorkPlanLimitExceeded, "plan.items",
                $"WorkPlan exceeds the limit of {maximumPlanItems} items.");
        var items = plan.Items.IsDefault
            ? ImmutableArray<WorkPlanItem>.Empty
            : plan.Items.Take(WorkflowDomainLimits.MaximumWorkItems).ToImmutableArray();

        var stepById = workflow?.Definition.Steps.ToDictionary(
            step => step.Id,
            StringComparer.Ordinal) ?? new Dictionary<string, WorkflowStepDefinition>(StringComparer.Ordinal);
        var itemById = new Dictionary<string, WorkPlanItem>(StringComparer.Ordinal);
        var itemIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var itemCountByStep = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependencyMap = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var hasWorkItems = items.Length > 0;
        var hasFixedWork = workflow is not null &&
                           workflow.Definition.Steps.Any(step =>
                               step.Mode == WorkflowStepMode.Fixed && step.FixedWork is not null);
        var hasBuildTestCommand = workflow is not null &&
                                 workflow.Definition.Steps.Any(step => step.BuildTestCommand is not null);
        var roleSelections = ValidateSelectionContext(
            selectionContext, hasWorkItems || hasFixedWork || hasBuildTestCommand, issues);
        if (workflow is not null && hasFixedWork)
            ValidateFixedWorkSelections(
                workflow.Definition, roleSelections, selectionContext, issues);
        if (workflow is not null && hasBuildTestCommand &&
            selectionContext?.IsolationProviderBinding is { } commandBinding)
        {
            foreach (var step in workflow.Definition.Steps.Where(step => step.BuildTestCommand is not null))
            {
                if (!commandBinding.NegotiatedCapabilities.Contains(SandboxCapabilities.BuildTestCommandPod) ||
                    !step.RequiredProviderCapabilities.All(commandBinding.NegotiatedCapabilities.Contains))
                    Add(issues, WorkflowValidationCode.RequiredCapabilityUnavailable,
                        $"definition.steps[{step.Id}].requiredProviderCapabilities",
                        $"The pinned provider lacks the command capability required by BuildTest step '{step.Id}'.");
            }
        }

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var path = $"plan.items[{index}]";
            if (item is null)
            {
                Add(issues, WorkflowValidationCode.InvalidWorkPlanItems, path,
                    "A WorkPlan item is required.");
                continue;
            }

            var hasValidId = WorkflowValidationSupport.IsStableId(item.Id);
            if (!hasValidId)
                Add(issues, WorkflowValidationCode.InvalidWorkItemId, path + ".id",
                    "Work item ID must be a stable, non-empty identifier.");
            else if (!itemById.TryAdd(item.Id, item))
                Add(issues, WorkflowValidationCode.DuplicateWorkItemId, path + ".id",
                    $"Work item ID '{item.Id}' is duplicated.");
            else
                itemIndexes.Add(item.Id, index);

            if (!WorkflowValidationSupport.IsStableId(item.WorkflowStepId))
            {
                Add(issues, WorkflowValidationCode.MissingWorkflowStepId, path + ".workflowStepId",
                    "Every proposed item must name a stable workflow step ID.");
            }
            else if (!stepById.TryGetValue(item.WorkflowStepId, out var step))
            {
                Add(issues, WorkflowValidationCode.UnknownWorkflowStep, path + ".workflowStepId",
                    $"Workflow step '{item.WorkflowStepId}' is not in the pinned catalog.");
            }
            else
            {
                if (step.Mode != WorkflowStepMode.Open)
                    Add(issues, WorkflowValidationCode.StepDoesNotAcceptProposals,
                        path + ".workflowStepId",
                        $"Step '{step.Id}' is {step.Mode}; model-proposed work belongs only to open steps.");
                itemCountByStep[step.Id] = itemCountByStep.GetValueOrDefault(step.Id) + 1;
                ValidateItemAgainstStep(item, step, path, roleSelections, selectionContext, issues);
            }

            ValidateWorkText(item, path, issues);
            if (hasValidId)
                ValidateDependencies(item, path, dependencyMap, issues);
            else if (item.DependsOn.IsDefault)
                Add(issues, WorkflowValidationCode.InvalidWorkItemDependency, path + ".dependsOn",
                    "Dependencies must be an initialized collection.");
            WorkflowDefinitionValidator.ValidateOutputPaths(
                item.DeclaredOutputs, path + ".declaredOutputs", issues);
        }

        if (workflow is not null)
            ValidateCardinality(workflow.Definition, itemCountByStep, issues);

        ValidateDependencyGraph(items, itemById, dependencyMap, stepById, issues);
        if (workflow is not null)
            ValidateFixedOutputConflicts(items, stepById, issues);

        if (issues.Count > 0)
            return WorkflowValidationResult<WorkPlanSnapshot>.Failure(issues.ToImmutable());

        var normalizedItems = SerializeOutputConflicts(items, itemIndexes, dependencyMap, stepById);
        var normalizedPlan = plan with { Items = normalizedItems };
        return WorkflowValidationResult<WorkPlanSnapshot>.Success(
            new WorkPlanSnapshot(
                workflow!,
                normalizedPlan,
                selectionContext?.IsolationProviderBinding));
    }

    private static Dictionary<string, RoleRunSelection> ValidateSelectionContext(
        WorkPlanRunSelectionContext? context,
        bool required,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var roleSelections = new Dictionary<string, RoleRunSelection>(StringComparer.Ordinal);
        if (context is null)
        {
            if (required)
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, "selectionContext",
                    "A pinned run-selection context is required for proposed work.");
            return roleSelections;
        }

        if (context.Roles.IsDefault)
        {
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, "selectionContext.roles",
                "Role selections must be an initialized collection.");
            return roleSelections;
        }
        if (context.Roles.Length > WorkflowDomainLimits.MaximumSteps)
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, "selectionContext.roles",
                $"The run selection may contain at most {WorkflowDomainLimits.MaximumSteps} roles.");

        for (var index = 0;
             index < Math.Min(context.Roles.Length, WorkflowDomainLimits.MaximumSteps);
             index++)
        {
            var role = context.Roles[index];
            var path = $"selectionContext.roles[{index}]";
            if (role is null || !WorkflowValidationSupport.IsStableId(role.RoleId))
            {
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, path,
                    "Each run selection must name a stable role ID.");
                continue;
            }
            if (!roleSelections.TryAdd(role.RoleId, role))
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, path + ".roleId",
                    $"Run selections duplicate role '{role.RoleId}'.");

            ValidateOpaqueReferences(role.EligibleAgentIds, path + ".eligibleAgentIds", issues);
            ValidateOpaqueReferences(
                role.ModelSelectionReferences, path + ".modelSelectionReferences", issues);
            ValidateEligibleIsolationChoices(
                role.EligibleIsolationChoices, path + ".eligibleIsolationChoices", issues);
        }

        if (required && context.IsolationProviderBinding is null)
        {
            Add(issues, WorkflowValidationCode.PinnedSandboxBindingRequired,
                "selectionContext.isolationProviderBinding",
                "Proposed work requires the run's immutable isolation provider binding.");
        }
        else if (context.IsolationProviderBinding is { } binding)
        {
            if (binding.Seam != ProviderSeam.Sandbox ||
                string.IsNullOrWhiteSpace(binding.ProviderId) ||
                binding.NegotiatedCapabilities is null ||
                binding.NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace))
                Add(issues, WorkflowValidationCode.PinnedSandboxBindingRequired,
                    "selectionContext.isolationProviderBinding",
                    "The isolation binding must be a valid pinned Sandbox provider binding.");
        }

        return roleSelections;
    }

    private static void ValidateItemAgainstStep(
        WorkPlanItem item,
        WorkflowStepDefinition step,
        string path,
        IReadOnlyDictionary<string, RoleRunSelection> roleSelections,
        WorkPlanRunSelectionContext? context,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var hasValidRole = WorkflowValidationSupport.IsStableId(item.RoleId);
        if (!hasValidRole ||
            !ContainsOrdinal(step.AllowedRoles, item.RoleId))
            Add(issues, WorkflowValidationCode.WorkPlanRoleNotAllowed, path + ".roleId",
                $"Role '{item.RoleId}' is not allowed in step '{step.Id}'.");
        if (!ContainsOrdinal(step.AllowedPhases, item.Phase))
            Add(issues, WorkflowValidationCode.WorkPlanPhaseNotAllowed, path + ".phase",
                $"Phase '{item.Phase}' is not allowed in step '{step.Id}'.");

        if (!WorkflowValidationSupport.IsOpaqueReference(item.AgentId))
        {
            Add(issues, WorkflowValidationCode.MissingAgent, path + ".agentId",
                "Every proposed work item must name an eligible agent.");
        }
        else if (!hasValidRole ||
                 !roleSelections.TryGetValue(item.RoleId, out var roleSelection) ||
                 !ContainsOrdinal(roleSelection.EligibleAgentIds, item.AgentId))
        {
            Add(issues, WorkflowValidationCode.AgentNotEligible, path + ".agentId",
                $"Agent '{item.AgentId}' is not eligible for role '{item.RoleId}' in this run.");
        }

        if (!WorkflowValidationSupport.IsOpaqueReference(item.ModelSelectionReference))
        {
            Add(issues, WorkflowValidationCode.MissingModelSelection,
                path + ".modelSelectionReference",
                "Every proposed work item must reference a pinned model selection.");
        }
        else if (!hasValidRole ||
                 !roleSelections.TryGetValue(item.RoleId, out var modelRoleSelection) ||
                 !ContainsOrdinal(modelRoleSelection.ModelSelectionReferences,
                     item.ModelSelectionReference))
        {
            Add(issues, WorkflowValidationCode.ModelSelectionNotEligible,
                path + ".modelSelectionReference",
                "Model selection is not eligible for the assigned role in the pinned run context.");
        }

        if (!WorkflowValidationSupport.IsOpaqueReference(item.IsolationProviderId))
            Add(issues, WorkflowValidationCode.PinnedProviderMismatch,
                path + ".isolationProviderId",
                "Every proposed work item must reference the pinned isolation provider.");
        else if (context?.IsolationProviderBinding is { } isolationBinding &&
                 !string.Equals(item.IsolationProviderId, isolationBinding.ProviderId,
                     StringComparison.Ordinal))
            Add(issues, WorkflowValidationCode.PinnedProviderMismatch,
                path + ".isolationProviderId",
                "WorkPlan isolation provider must match the immutable run binding.");

        if (!WorkflowValidationSupport.IsStableId(item.IsolationChoice))
            Add(issues, WorkflowValidationCode.MissingIsolationChoice,
                path + ".isolationChoice",
                "Every proposed work item must select an allowed isolation choice.");
        else
        {
            if (!ContainsOrdinal(step.AllowedIsolationChoices, item.IsolationChoice))
                Add(issues, WorkflowValidationCode.IsolationChoiceNotAllowed,
                    path + ".isolationChoice",
                    $"Isolation choice '{item.IsolationChoice}' is not allowed in step '{step.Id}'.");
            if (!hasValidRole ||
                !roleSelections.TryGetValue(item.RoleId, out var isolationRoleSelection) ||
                !ContainsOrdinal(
                    isolationRoleSelection.EligibleIsolationChoices, item.IsolationChoice))
                Add(issues, WorkflowValidationCode.IsolationChoiceNotAllowed,
                    path + ".isolationChoice",
                    $"Isolation choice '{item.IsolationChoice}' is not eligible for role '{item.RoleId}' in this run.");
        }

        var negotiatedCapabilities = context?.IsolationProviderBinding?.NegotiatedCapabilities;
        if (negotiatedCapabilities is not null &&
            !step.RequiredProviderCapabilities.All(negotiatedCapabilities.Contains))
            Add(issues, WorkflowValidationCode.RequiredCapabilityUnavailable,
                path + ".workflowStepId",
                $"The pinned provider lacks a capability required by step '{step.Id}'.");
    }

    private static void ValidateFixedWorkSelections(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, RoleRunSelection> roleSelections,
        WorkPlanRunSelectionContext? context,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (context is null)
            return;

        foreach (var step in definition.Steps.Where(
                     candidate => candidate.Mode == WorkflowStepMode.Fixed &&
                                  candidate.FixedWork is not null))
        {
            var fixedWork = step.FixedWork!;
            var path = $"definition.steps[{step.Id}].fixedWork";
            if (!roleSelections.TryGetValue(fixedWork.RoleId, out var roleSelection))
            {
                Add(issues, WorkflowValidationCode.InvalidSelectionContext,
                    "selectionContext.roles",
                    $"Run selection must include the prescribed fixed-work role '{fixedWork.RoleId}'.");
            }
            else if (!ContainsOrdinal(
                         roleSelection.EligibleIsolationChoices, fixedWork.IsolationChoice))
            {
                Add(issues, WorkflowValidationCode.IsolationChoiceNotAllowed,
                    path + ".isolationChoice",
                    $"Isolation choice '{fixedWork.IsolationChoice}' is not eligible for fixed-work role '{fixedWork.RoleId}' in this run.");
            }

            var negotiatedCapabilities = context.IsolationProviderBinding?.NegotiatedCapabilities;
            if (negotiatedCapabilities is not null &&
                !step.RequiredProviderCapabilities.All(negotiatedCapabilities.Contains))
                Add(issues, WorkflowValidationCode.RequiredCapabilityUnavailable,
                    path + ".requiredProviderCapabilities",
                    $"The pinned provider lacks a capability required by fixed-work step '{step.Id}'.");
        }
    }

    private static void ValidateWorkText(
        WorkPlanItem item,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(item.Title) ||
            item.Title.Length > WorkflowDomainLimits.MaximumWorkTextLength ||
            string.IsNullOrWhiteSpace(item.Task) ||
            item.Task.Length > WorkflowDomainLimits.MaximumWorkTextLength)
            Add(issues, WorkflowValidationCode.InvalidWorkItemText, path,
                "Work item title and task must be non-empty and within the text limit.");
    }

    private static void ValidateDependencies(
        WorkPlanItem item,
        string path,
        IDictionary<string, ImmutableArray<string>> dependencyMap,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (item.DependsOn.IsDefault)
        {
            Add(issues, WorkflowValidationCode.InvalidWorkItemDependency, path + ".dependsOn",
                "Dependencies must be an initialized collection.");
            dependencyMap[item.Id] = [];
            return;
        }
        if (item.DependsOn.Length > WorkflowDomainLimits.MaximumWorkItems)
            Add(issues, WorkflowValidationCode.WorkPlanLimitExceeded, path + ".dependsOn",
                $"A work item may have at most {WorkflowDomainLimits.MaximumWorkItems} dependencies.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = ImmutableArray.CreateBuilder<string>();
        foreach (var dependency in item.DependsOn.Take(WorkflowDomainLimits.MaximumWorkItems))
        {
            if (!WorkflowValidationSupport.IsStableId(dependency))
            {
                Add(issues, WorkflowValidationCode.InvalidWorkItemDependency,
                    path + ".dependsOn",
                    "Each dependency must be a stable work item ID.");
                continue;
            }
            if (!seen.Add(dependency))
            {
                Add(issues, WorkflowValidationCode.DuplicateWorkItemDependency,
                    path + ".dependsOn",
                    $"Dependency '{dependency}' is duplicated.");
                continue;
            }
            if (string.Equals(item.Id, dependency, StringComparison.Ordinal))
            {
                Add(issues, WorkflowValidationCode.WorkItemDependencyCycle,
                    path + ".dependsOn",
                    "A work item cannot depend on itself.");
                continue;
            }
            dependencies.Add(dependency);
        }

        dependencyMap[item.Id] = dependencies.ToImmutable();
    }

    private static void ValidateCardinality(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, int> itemCountByStep,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        foreach (var step in definition.Steps.Where(step => step.Mode == WorkflowStepMode.Open))
        {
            var count = itemCountByStep.GetValueOrDefault(step.Id);
            if (count < step.Cardinality.Minimum || count > step.Cardinality.Maximum)
                Add(issues, WorkflowValidationCode.WorkItemCardinalityViolation,
                    $"plan.steps[{step.Id}]",
                    $"Step '{step.Id}' accepts {step.Cardinality.Minimum}–{step.Cardinality.Maximum} items; received {count}.");
        }
    }

    private static void ValidateDependencyGraph(
        ImmutableArray<WorkPlanItem> items,
        IReadOnlyDictionary<string, WorkPlanItem> itemById,
        IReadOnlyDictionary<string, ImmutableArray<string>> dependencyMap,
        IReadOnlyDictionary<string, WorkflowStepDefinition> stepById,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var graphIsValid = true;
        foreach (var item in items)
        {
            if (item is null || !WorkflowValidationSupport.IsStableId(item.Id) ||
                !itemById.ContainsKey(item.Id) ||
                !dependencyMap.TryGetValue(item.Id, out var dependencies))
                continue;

            foreach (var dependency in dependencies)
            {
                if (!itemById.TryGetValue(dependency, out var prerequisite))
                {
                    graphIsValid = false;
                    Add(issues, WorkflowValidationCode.MissingWorkItemDependency,
                        $"plan.items[{item.Id}].dependsOn",
                        $"Dependency '{dependency}' is not part of the WorkPlan.");
                    continue;
                }
                if (!WorkflowValidationSupport.IsStableId(item.WorkflowStepId) ||
                    !WorkflowValidationSupport.IsStableId(prerequisite.WorkflowStepId) ||
                    !stepById.TryGetValue(item.WorkflowStepId, out var itemStep) ||
                    !stepById.TryGetValue(prerequisite.WorkflowStepId, out var prerequisiteStep))
                    continue;
                if (itemStep.Order < prerequisiteStep.Order)
                    Add(issues, WorkflowValidationCode.DependencyStepOrderViolation,
                        $"plan.items[{item.Id}].dependsOn",
                        $"Work item in step '{itemStep.Id}' cannot depend on later step '{prerequisiteStep.Id}'.");
            }
        }

        if (!graphIsValid)
            return;

        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        var cycleFound = false;
        foreach (var item in itemById.Values)
            Visit(item);
        if (cycleFound)
            return;

        foreach (var item in itemById.Values)
        {
            if (!WorkflowValidationSupport.IsStableId(item.WorkflowStepId) ||
                !stepById.TryGetValue(item.WorkflowStepId, out var step))
                continue;
            foreach (var ancestorStepId in GetAncestorSteps(step, stepById))
            {
                foreach (var prerequisite in itemById.Values.Where(
                             candidate => candidate.WorkflowStepId == ancestorStepId))
                {
                    if (!WorkflowValidationSupport.Reaches(
                            item.Id, prerequisite.Id, dependencyMap, itemById))
                        Add(issues, WorkflowValidationCode.MissingJoinDependency,
                            $"plan.items[{item.Id}].dependsOn",
                            $"Step '{step.Id}' must join all planned work from predecessor step '{ancestorStepId}'.");
                }
            }
        }

        void Visit(WorkPlanItem item)
        {
            if (cycleFound)
                return;
            if (state.TryGetValue(item.Id, out var existing))
            {
                if (existing == 1)
                {
                    cycleFound = true;
                    Add(issues, WorkflowValidationCode.WorkItemDependencyCycle,
                        $"plan.items[{item.Id}].dependsOn",
                        "WorkPlan dependencies contain a cycle.");
                }
                return;
            }

            state[item.Id] = 1;
            if (dependencyMap.TryGetValue(item.Id, out var dependencies))
            {
                foreach (var dependency in dependencies)
                {
                    if (itemById.TryGetValue(dependency, out var prerequisite))
                        Visit(prerequisite);
                }
            }
            state[item.Id] = 2;
        }
    }

    private static IEnumerable<string> GetAncestorSteps(
        WorkflowStepDefinition step,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(step.DependsOn);
        while (pending.TryPop(out var dependency))
        {
            if (!visited.Add(dependency) || !steps.TryGetValue(dependency, out var ancestor))
                continue;
            yield return ancestor.Id;
            foreach (var parent in ancestor.DependsOn)
                pending.Push(parent);
        }
    }

    private static void ValidateFixedOutputConflicts(
        ImmutableArray<WorkPlanItem> items,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var stepDependencies = steps.ToDictionary(
            step => step.Key,
            step => step.Value.DependsOn,
            StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null ||
                item.DeclaredOutputs.IsDefault ||
                !WorkflowValidationSupport.IsStableId(item.WorkflowStepId) ||
                !steps.TryGetValue(item.WorkflowStepId, out var itemStep))
                continue;
            foreach (var fixedStep in steps.Values.Where(
                         step => step.Mode == WorkflowStepMode.Fixed && step.FixedWork is not null ||
                             step.BuildTestCommand is not null))
            {
                var conflicts = item.DeclaredOutputs.Any(output =>
                    WorkflowValidationSupport.PrescribedOutputs(fixedStep).Any(fixedOutput =>
                        WorkflowValidationSupport.OutputPathsMatch(output, fixedOutput)));
                if (!conflicts)
                    continue;

                var ordered = WorkflowValidationSupport.Reaches(
                                  itemStep.Id, fixedStep.Id, stepDependencies) ||
                              WorkflowValidationSupport.Reaches(
                                  fixedStep.Id, itemStep.Id, stepDependencies);
                if (!ordered)
                    Add(issues, WorkflowValidationCode.OutputConflictWithFixedWork,
                        $"plan.items[{item.Id}].declaredOutputs",
                        $"Outputs overlap prescribed work in step '{fixedStep.Id}' without a catalog dependency ordering.");
            }
        }
    }

    private static ImmutableArray<WorkPlanItem> SerializeOutputConflicts(
        ImmutableArray<WorkPlanItem> items,
        IReadOnlyDictionary<string, int> indexes,
        Dictionary<string, ImmutableArray<string>> dependencyMap,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps)
    {
        var itemById = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var seen = new List<(string Output, string ItemId)>();
        foreach (var item in items
                     .OrderBy(item => steps[item.WorkflowStepId].Order)
                     .ThenBy(item => indexes[item.Id]))
        {
            foreach (var output in item.DeclaredOutputs)
            {
                var owners = seen
                    .Where(entry => WorkflowValidationSupport.OutputPathsMatch(entry.Output, output))
                    .Select(entry => entry.ItemId)
                    .Distinct(StringComparer.Ordinal)
                    .Reverse()
                    .ToArray();
                foreach (var owner in owners)
                {
                    if (WorkflowValidationSupport.Reaches(item.Id, owner, dependencyMap, itemById) ||
                        WorkflowValidationSupport.Reaches(owner, item.Id, dependencyMap, itemById))
                        continue;
                    dependencyMap[item.Id] = WorkflowValidationSupport.AddDistinct(
                        dependencyMap[item.Id], owner);
                }
            }
            seen.AddRange(item.DeclaredOutputs.Select(output => (output, item.Id)));
        }

        return [.. items.Select(item => item with { DependsOn = dependencyMap[item.Id] })];
    }

    private static bool ContainsOrdinal(ImmutableArray<string> values, string? value) =>
        !values.IsDefault && values.Contains(value!, StringComparer.Ordinal);

    private static void ValidateOpaqueReferences(
        ImmutableArray<string> references,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (references.IsDefault)
        {
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, path,
                "The selection collection must be initialized.");
            return;
        }
        if (references.Length > WorkflowDomainLimits.MaximumEligibilityValues)
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, path,
                $"The selection may contain at most {WorkflowDomainLimits.MaximumEligibilityValues} references.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0;
             index < Math.Min(references.Length, WorkflowDomainLimits.MaximumEligibilityValues);
             index++)
        {
            var reference = references[index];
            if (!WorkflowValidationSupport.IsOpaqueReference(reference))
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, $"{path}[{index}]",
                    "Selection references must be non-empty opaque identifiers.");
            else if (!seen.Add(reference))
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, $"{path}[{index}]",
                    $"Selection reference '{reference}' is duplicated.");
        }
    }

    private static void ValidateEligibleIsolationChoices(
        ImmutableArray<string> choices,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (choices.IsDefault)
        {
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, path,
                "Eligible isolation choices must be an initialized collection.");
            return;
        }
        if (choices.Length > WorkflowDomainLimits.MaximumEligibilityValues)
            Add(issues, WorkflowValidationCode.InvalidSelectionContext, path,
                $"The selection may contain at most {WorkflowDomainLimits.MaximumEligibilityValues} isolation choices.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0;
             index < Math.Min(choices.Length, WorkflowDomainLimits.MaximumEligibilityValues);
             index++)
        {
            var choice = choices[index];
            if (!WorkflowValidationSupport.IsStableId(choice))
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, $"{path}[{index}]",
                    "Eligible isolation choices must be stable identifiers.");
            else if (!seen.Add(choice))
                Add(issues, WorkflowValidationCode.InvalidSelectionContext, $"{path}[{index}]",
                    $"Isolation choice '{choice}' is duplicated.");
        }
    }

    private static void Add(
        ImmutableArray<WorkflowValidationIssue>.Builder issues,
        WorkflowValidationCode code,
        string path,
        string message) =>
        issues.Add(new WorkflowValidationIssue(code, path, message));
}
