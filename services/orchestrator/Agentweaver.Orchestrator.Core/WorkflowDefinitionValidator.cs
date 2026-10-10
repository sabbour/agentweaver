using System.Collections.Immutable;
using System.Text;

namespace Agentweaver.Orchestrator.Core;

public static class WorkflowDefinitionValidator
{
    public static bool IsValidOutputPath(string? path) =>
        WorkflowValidationSupport.IsValidOutputPath(path);

    public static WorkflowValidationResult<WorkflowDefinitionSnapshot> ValidateAndSnapshot(
        WorkflowDefinition? definition)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (definition is null)
        {
            Add(issues, WorkflowValidationCode.InvalidDefinitionId, "definition",
                "A workflow definition is required.");
            return WorkflowValidationResult<WorkflowDefinitionSnapshot>.Failure(issues.ToImmutable());
        }

        if (!WorkflowValidationSupport.IsStableId(definition.Id))
            Add(issues, WorkflowValidationCode.InvalidDefinitionId, "definition.id",
                "Definition ID must be a stable, non-empty identifier.");
        if (!WorkflowValidationSupport.IsOpaqueReference(definition.Revision))
            Add(issues, WorkflowValidationCode.InvalidDefinitionRevision, "definition.revision",
                "Definition revision must be a non-empty opaque reference.");
        if (!WorkflowValidationSupport.IsOpaqueReference(definition.CatalogVersion))
            Add(issues, WorkflowValidationCode.InvalidCatalogVersion, "definition.catalogVersion",
                "Catalog version must be a non-empty opaque reference.");
        if (!Enum.IsDefined(definition.Origin))
            Add(issues, WorkflowValidationCode.InvalidDefinitionOrigin, "definition.origin",
                "Definition origin is not supported.");
        if (definition.MaximumWorkItems is < 1 or > WorkflowDomainLimits.MaximumWorkItems)
            Add(issues, WorkflowValidationCode.WorkPlanLimitExceeded, "definition.maximumWorkItems",
                $"Maximum work items must be between 1 and {WorkflowDomainLimits.MaximumWorkItems}.");
        if (definition.Steps.IsDefaultOrEmpty ||
            definition.Steps.Length > WorkflowDomainLimits.MaximumSteps)
            Add(issues, WorkflowValidationCode.InvalidStepCount, "definition.steps",
                $"A catalog must contain between 1 and {WorkflowDomainLimits.MaximumSteps} steps.");

        var steps = definition.Steps.IsDefault
            ? ImmutableArray<WorkflowStepDefinition>.Empty
            : definition.Steps.Take(WorkflowDomainLimits.MaximumSteps).ToImmutableArray();
        var stepById = new Dictionary<string, WorkflowStepDefinition>(StringComparer.Ordinal);
        var orders = new HashSet<int>();

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var path = $"definition.steps[{index}]";
            if (step is null)
            {
                Add(issues, WorkflowValidationCode.InvalidStepId, path,
                    "A workflow step is required.");
                continue;
            }

            if (!WorkflowValidationSupport.IsStableId(step.Id))
                Add(issues, WorkflowValidationCode.InvalidStepId, path + ".id",
                    "Step ID must be a stable, non-empty identifier.");
            else if (!stepById.TryAdd(step.Id, step))
                Add(issues, WorkflowValidationCode.DuplicateStepId, path + ".id",
                    $"Step ID '{step.Id}' is duplicated.");

            if (string.IsNullOrWhiteSpace(step.Purpose) ||
                step.Purpose.Length > WorkflowDomainLimits.MaximumWorkTextLength)
                Add(issues, WorkflowValidationCode.InvalidStepPurpose, path + ".purpose",
                    "Step purpose must be non-empty and within the text limit.");
            if (step.Order < 0)
                Add(issues, WorkflowValidationCode.InvalidStepOrder, path + ".order",
                    "Step order must be zero or greater.");
            else if (!orders.Add(step.Order))
                Add(issues, WorkflowValidationCode.DuplicateStepOrder, path + ".order",
                    $"Step order '{step.Order}' is duplicated.");
            if (!Enum.IsDefined(step.Mode))
                Add(issues, WorkflowValidationCode.InvalidStepMode, path + ".mode",
                    "Step mode must be fixed, open, or platform.");

            ValidateCardinality(step, path, issues);
            ValidateStringIds(step.AllowedRoles, path + ".allowedRoles",
                WorkflowValidationCode.InvalidStepRole, WorkflowValidationCode.DuplicateStepRole,
                issues);
            ValidateStringIds(step.AllowedPhases, path + ".allowedPhases",
                WorkflowValidationCode.InvalidStepPhase, WorkflowValidationCode.DuplicateStepPhase,
                issues);
            ValidateStringIds(step.AllowedIsolationChoices, path + ".allowedIsolationChoices",
                WorkflowValidationCode.InvalidIsolationChoice,
                WorkflowValidationCode.DuplicateIsolationChoice, issues);
            ValidateStringIds(step.RequiredProviderCapabilities, path + ".requiredProviderCapabilities",
                WorkflowValidationCode.InvalidProviderCapability,
                WorkflowValidationCode.DuplicateProviderCapability, issues);
            ValidateStringIds(step.DependsOn, path + ".dependsOn",
                WorkflowValidationCode.InvalidStepDependency,
                WorkflowValidationCode.DuplicateStepDependency, issues);
            ValidateMode(step, path, issues);
            ValidateBuildTestCommand(step, path, issues);
        }

        var minimumOpenWorkItems = steps
            .Where(step => step is not null &&
                           step.Mode == WorkflowStepMode.Open &&
                           step.Cardinality is not null)
            .Sum(step => (long)Math.Max(step.Cardinality!.Minimum, 0));
        if (definition.MaximumWorkItems is >= 1 and <= WorkflowDomainLimits.MaximumWorkItems &&
            minimumOpenWorkItems > definition.MaximumWorkItems)
            Add(issues, WorkflowValidationCode.WorkPlanLimitExceeded,
                "definition.maximumWorkItems",
                $"Open-step minimums require {minimumOpenWorkItems} work items, exceeding the workflow limit of {definition.MaximumWorkItems}.");

        if (stepById.Count == steps.Length)
            ValidateStepGraph(stepById, issues);
        if (issues.Count > 0)
            return WorkflowValidationResult<WorkflowDefinitionSnapshot>.Failure(issues.ToImmutable());

        var normalizedSteps = NormalizeFixedOutputConflicts(steps);
        var snapshotDefinition = definition with { Steps = normalizedSteps };
        return WorkflowValidationResult<WorkflowDefinitionSnapshot>.Success(
            new WorkflowDefinitionSnapshot(snapshotDefinition));
    }

    private static void ValidateCardinality(
        WorkflowStepDefinition step,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (step.Cardinality is null ||
            step.Cardinality.Minimum < 0 ||
            step.Cardinality.Maximum < 1 ||
            step.Cardinality.Maximum < step.Cardinality.Minimum ||
            step.Cardinality.Maximum > WorkflowDomainLimits.MaximumItemsPerStep)
        {
            Add(issues, WorkflowValidationCode.InvalidCardinality, path + ".cardinality",
                $"Cardinality must be bounded from 0 through {WorkflowDomainLimits.MaximumItemsPerStep}.");
        }
    }

    private static void ValidateMode(
        WorkflowStepDefinition step,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (step.Cardinality is null ||
            step.AllowedRoles.IsDefault ||
            step.AllowedPhases.IsDefault ||
            step.AllowedIsolationChoices.IsDefault ||
            step.RequiredProviderCapabilities.IsDefault ||
            step.DependsOn.IsDefault)
            return;

        switch (step.Mode)
        {
            case WorkflowStepMode.Fixed:
                if (step.Cardinality.Minimum != 1 || step.Cardinality.Maximum != 1 ||
                    step.AllowedRoles.Length != 1 || step.AllowedPhases.Length != 1 ||
                    step.AllowedIsolationChoices.Length != 1 ||
                    step.FixedWork is null || step.PlatformGate is not null)
                {
                    Add(issues, WorkflowValidationCode.InvalidFixedWork, path,
                        "A fixed step prescribes exactly one role, phase, isolation choice, and work item.");
                    return;
                }

                ValidateFixedWork(step, path, issues);
                break;
            case WorkflowStepMode.Open:
                if (step.AllowedRoles.IsDefaultOrEmpty ||
                    step.AllowedPhases.IsDefaultOrEmpty ||
                    step.AllowedIsolationChoices.IsDefaultOrEmpty ||
                    step.FixedWork is not null || step.PlatformGate is not null)
                    Add(issues, WorkflowValidationCode.InvalidStepMode, path,
                        "An open step needs eligible roles, phases, isolation choices, and no fixed work or gate.");
                break;
            case WorkflowStepMode.Platform:
                if (step.PlatformGate is not { } gate || !Enum.IsDefined(gate) ||
                    step.AllowedRoles.Length != 0 || step.AllowedPhases.Length != 0 ||
                    step.AllowedIsolationChoices.Length != 0 ||
                    step.RequiredProviderCapabilities.Length != 0 ||
                    step.FixedWork is not null ||
                    step.Cardinality.Maximum != 1 ||
                    step.Cardinality.Minimum is < 0 or > 1)
                    Add(issues, WorkflowValidationCode.InvalidPlatformGate, path,
                        "A platform step must name one platform-owned gate and cannot declare agent eligibility or fixed work.");
                break;
        }
    }

    private static void ValidateFixedWork(
        WorkflowStepDefinition step,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        var work = step.FixedWork!;
        if (string.IsNullOrWhiteSpace(work.Title) ||
            work.Title.Length > WorkflowDomainLimits.MaximumWorkTextLength ||
            string.IsNullOrWhiteSpace(work.Task) ||
            work.Task.Length > WorkflowDomainLimits.MaximumWorkTextLength ||
            !string.Equals(work.RoleId, step.AllowedRoles[0], StringComparison.Ordinal) ||
            !string.Equals(work.Phase, step.AllowedPhases[0], StringComparison.Ordinal) ||
            !string.Equals(work.IsolationChoice, step.AllowedIsolationChoices[0], StringComparison.Ordinal))
            Add(issues, WorkflowValidationCode.InvalidFixedWork, path + ".fixedWork",
                "Prescribed fixed work must have bounded text and match its sole eligible role, phase, and isolation choice.");

        ValidateOutputPaths(work.DeclaredOutputs, path + ".fixedWork.declaredOutputs", issues);
    }

    private static void ValidateBuildTestCommand(
        WorkflowStepDefinition step,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (step.BuildTestCommand is not { } command)
            return;
        if (step.Mode != WorkflowStepMode.Platform ||
            step.PlatformGate != WorkflowPlatformGate.BuildTest ||
            !IsValidBuildTestCommand(command))
            Add(issues, WorkflowValidationCode.InvalidBuildTestCommand, path + ".buildTestCommand",
                "BuildTest requires a bounded typed command, execution profile, workspace paths, and output obligations.");
    }

    public static bool IsValidBuildTestCommand(WorkflowBuildTestCommand? command)
    {
        if (command is null ||
            !WorkflowValidationSupport.IsOpaqueReference(command.ExecutionProfileReference) ||
            command.ExecutableReference is not { Length: > 1 and <= 512 } executable ||
            executable[0] != '/' || executable.Contains('\\') || executable.Contains(':') ||
            executable.Any(char.IsControl) ||
            executable.Split('/').Skip(1).Any(segment => segment.Length == 0 || segment is "." or "..") ||
            command.Arguments.IsDefault || command.Arguments.Length > 256 ||
            command.Arguments.Any(argument => argument is null || argument.Length > 4096 ||
                argument.Any(char.IsControl)) ||
            command.Arguments.Sum(argument => (long)Encoding.UTF8.GetByteCount(argument)) > 32768 ||
            command.WorkingDirectory != "." ||
            command.Outputs.IsDefault || command.Outputs.Length > 32 ||
            command.Outputs.Any(output => output is null ||
                !WorkflowValidationSupport.IsStableId(output.Name) ||
                output.RelativePath is not { Length: <= 512 } ||
                !WorkflowValidationSupport.IsValidOutputPath(output.RelativePath) ||
                output.MaximumBytes is < 1 or > 67_108_864) ||
            command.Outputs.Select(output => output.Name).Distinct(StringComparer.Ordinal).Count() !=
                command.Outputs.Length ||
            command.Outputs.Select(output => output.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
                command.Outputs.Length)
            return false;
        return true;
    }

    private static void ValidateStepGraph(
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        foreach (var step in steps.Values)
        {
            if (step.DependsOn.IsDefault)
                continue;
            foreach (var dependency in step.DependsOn)
            {
                if (string.Equals(step.Id, dependency, StringComparison.Ordinal))
                    Add(issues, WorkflowValidationCode.StepDependencyCycle,
                        $"definition.steps[{step.Id}].dependsOn",
                        $"Step '{step.Id}' cannot depend on itself.");
                else if (!WorkflowValidationSupport.IsStableId(dependency) ||
                         !steps.ContainsKey(dependency))
                    Add(issues, WorkflowValidationCode.MissingStepDependency,
                        $"definition.steps[{step.Id}].dependsOn",
                        $"Step '{step.Id}' depends on missing step '{dependency}'.");
            }
        }

        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        var cycleFound = false;
        foreach (var step in steps.Values)
            Visit(step);

        if (!cycleFound)
        {
            foreach (var step in steps.Values)
            {
                foreach (var dependency in step.DependsOn)
                {
                    if (steps.TryGetValue(dependency, out var earlier) &&
                        earlier.Order >= step.Order)
                        Add(issues, WorkflowValidationCode.StepOrderViolation,
                            $"definition.steps[{step.Id}].dependsOn",
                            $"Dependency step '{dependency}' must precede '{step.Id}' in catalog order.");
                }
            }
        }

        void Visit(WorkflowStepDefinition step)
        {
            if (!steps.ContainsKey(step.Id) || cycleFound)
                return;
            if (state.TryGetValue(step.Id, out var existing))
            {
                if (existing == 1)
                {
                    cycleFound = true;
                    Add(issues, WorkflowValidationCode.StepDependencyCycle,
                        $"definition.steps[{step.Id}].dependsOn",
                        "Workflow step dependencies contain a cycle.");
                }
                return;
            }

            state[step.Id] = 1;
            foreach (var dependency in step.DependsOn.IsDefault
                         ? ImmutableArray<string>.Empty
                         : step.DependsOn)
            {
                if (WorkflowValidationSupport.IsStableId(dependency) &&
                    steps.TryGetValue(dependency, out var target))
                    Visit(target);
            }
            state[step.Id] = 2;
        }
    }

    private static ImmutableArray<WorkflowStepDefinition> NormalizeFixedOutputConflicts(
        ImmutableArray<WorkflowStepDefinition> steps)
    {
        var dependencies = steps.ToDictionary(
            step => step.Id,
            step => step.DependsOn,
            StringComparer.Ordinal);
        var seen = new List<(string Output, string StepId)>();

        foreach (var step in steps.OrderBy(item => item.Order))
        {
            foreach (var output in WorkflowValidationSupport.PrescribedOutputs(step))
            {
                var owners = seen
                    .Where(entry => WorkflowValidationSupport.OutputPathsMatch(entry.Output, output))
                    .Select(entry => entry.StepId)
                    .Distinct(StringComparer.Ordinal)
                    .Reverse();
                foreach (var owner in owners)
                {
                    if (WorkflowValidationSupport.Reaches(step.Id, owner, dependencies))
                        continue;
                    dependencies[step.Id] = WorkflowValidationSupport.AddDistinct(
                        dependencies[step.Id], owner);
                }
            }

            seen.AddRange(WorkflowValidationSupport.PrescribedOutputs(step).Select(output => (output, step.Id)));
        }

        return [.. steps.Select(step => step with { DependsOn = dependencies[step.Id] })];
    }

    internal static bool ValidateOutputPaths(
        ImmutableArray<string> outputs,
        string path,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (outputs.IsDefault)
        {
            Add(issues, WorkflowValidationCode.InvalidOutputPath, path,
                "Declared outputs must be an initialized collection.");
            return false;
        }
        if (outputs.Length > WorkflowDomainLimits.MaximumOutputsPerItem)
            Add(issues, WorkflowValidationCode.OutputLimitExceeded, path,
                $"A work item may declare at most {WorkflowDomainLimits.MaximumOutputsPerItem} output paths.");

        var valid = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0;
             index < Math.Min(outputs.Length, WorkflowDomainLimits.MaximumOutputsPerItem);
             index++)
        {
            var output = outputs[index];
            if (!WorkflowValidationSupport.IsValidOutputPath(output))
            {
                Add(issues, WorkflowValidationCode.InvalidOutputPath, $"{path}[{index}]",
                    "Output paths must be canonical, repository-relative file paths.");
                valid = false;
                continue;
            }
            if (!seen.Add(output))
            {
                Add(issues, WorkflowValidationCode.DuplicateOutputPath, $"{path}[{index}]",
                    $"Output path '{output}' is duplicated.");
                valid = false;
            }
        }
        return valid;
    }

    private static void ValidateStringIds(
        ImmutableArray<string> values,
        string path,
        WorkflowValidationCode invalidCode,
        WorkflowValidationCode duplicateCode,
        ImmutableArray<WorkflowValidationIssue>.Builder issues)
    {
        if (values.IsDefault)
        {
            Add(issues, invalidCode, path, "The collection must be initialized.");
            return;
        }
        if (values.Length > WorkflowDomainLimits.MaximumEligibilityValues)
            Add(issues, invalidCode, path,
                $"The collection may contain at most {WorkflowDomainLimits.MaximumEligibilityValues} values.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0;
             index < Math.Min(values.Length, WorkflowDomainLimits.MaximumEligibilityValues);
             index++)
        {
            if (!WorkflowValidationSupport.IsStableId(values[index]))
                Add(issues, invalidCode, $"{path}[{index}]",
                    "Value must be a stable, non-empty identifier.");
            else if (!seen.Add(values[index]))
                Add(issues, duplicateCode, $"{path}[{index}]",
                    $"Value '{values[index]}' is duplicated.");
        }
    }

    private static void Add(
        ImmutableArray<WorkflowValidationIssue>.Builder issues,
        WorkflowValidationCode code,
        string path,
        string message) =>
        issues.Add(new WorkflowValidationIssue(code, path, message));
}
