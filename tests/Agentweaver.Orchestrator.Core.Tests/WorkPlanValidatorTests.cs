using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkPlanValidatorTests
{
    [Fact]
    public void AcceptsStepBoundWorkAndSnapshotsOpaqueModelAndPinnedProviderSelections()
    {
        var workflow = WorkflowTestData.Snapshot();
        var context = WorkflowTestData.SelectionContext();
        var item = WorkflowTestData.Item("implementation");

        var result = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(item), context);

        Assert.True(result.IsValid);
        Assert.Equal("catalog-1", result.Value!.Workflow.Definition.CatalogVersion);
        Assert.Equal("model:provider/alpha",
            result.Value.Plan.Items[0].ModelSelectionReference);
        Assert.Same(context.IsolationProviderBinding, result.Value.IsolationProviderBinding);
    }

    [Fact]
    public void RejectsMissingStepsAndPlatformGateImpersonation()
    {
        var workflow = WorkflowTestData.Snapshot();
        var context = WorkflowTestData.SelectionContext();
        var missing = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work", stepId: "missing")),
            context);
        var platform = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work", stepId: "review")),
            context);
        var fixedStep = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work", stepId: "setup")),
            context);

        Assert.Contains(missing.Issues, issue =>
            issue.Code == WorkflowValidationCode.UnknownWorkflowStep);
        Assert.Contains(platform.Issues, issue =>
            issue.Code == WorkflowValidationCode.StepDoesNotAcceptProposals);
        Assert.Null(platform.Value);
        Assert.Contains(fixedStep.Issues, issue =>
            issue.Code == WorkflowValidationCode.StepDoesNotAcceptProposals);
        Assert.Null(fixedStep.Value);
    }

    [Fact]
    public void RequiresPinnedSelectionsAndCapabilitiesForPrescribedFixedWork()
    {
        var fixedStep = WorkflowTestData.Fixed("prepare", 0, [], ["docs/plan.md"]) with
        {
            RequiredProviderCapabilities = ["sandbox.fixed-work"]
        };
        var workflow = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, fixedStep));
        var plan = WorkflowTestData.Plan();
        var missingContext = WorkPlanValidator.ValidateAndSnapshot(workflow, plan, null);
        var missingRole = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            plan,
            new WorkPlanRunSelectionContext(
                [],
                WorkflowTestData.PinnedSandboxBinding(capabilities: ["sandbox.fixed-work"])));
        var ineligibleIsolation = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            plan,
            new WorkPlanRunSelectionContext(
                [new RoleRunSelection("planner", [], [], ["sandboxed"])],
                WorkflowTestData.PinnedSandboxBinding(capabilities: ["sandbox.fixed-work"])));
        var missingCapability = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            plan,
            new WorkPlanRunSelectionContext(
                [new RoleRunSelection("planner", [], [], ["worktree"])],
                WorkflowTestData.PinnedSandboxBinding(capabilities: ["sandbox.other"])));
        var accepted = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            plan,
            new WorkPlanRunSelectionContext(
                [new RoleRunSelection("planner", [], [], ["worktree"])],
                WorkflowTestData.PinnedSandboxBinding(capabilities: ["sandbox.fixed-work"])));

        Assert.Null(missingContext.Value);
        Assert.Contains(missingContext.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidSelectionContext);
        Assert.Null(missingRole.Value);
        Assert.Contains(missingRole.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidSelectionContext);
        Assert.Null(ineligibleIsolation.Value);
        Assert.Contains(ineligibleIsolation.Issues, issue =>
            issue.Code == WorkflowValidationCode.IsolationChoiceNotAllowed);
        Assert.Null(missingCapability.Value);
        Assert.Contains(missingCapability.Issues, issue =>
            issue.Code == WorkflowValidationCode.RequiredCapabilityUnavailable);
        Assert.True(accepted.IsValid);
    }

    [Fact]
    public void RejectsIneligibleRoleAgentPhaseModelAndIsolationChoice()
    {
        var item = WorkflowTestData.Item(
            "work",
            role: "unknown",
            agent: "unlisted-agent",
            phase: "deployment",
            modelSelection: "unlisted-model",
            isolationChoice: "unlisted-isolation");

        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(item),
            WorkflowTestData.SelectionContext());

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkPlanRoleNotAllowed);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.AgentNotEligible);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkPlanPhaseNotAllowed);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.ModelSelectionNotEligible);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.IsolationChoiceNotAllowed);
    }

    [Fact]
    public void RejectsIsolationChoiceNotEligibleForAssignedRole()
    {
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(WorkflowTestData.Item("work", isolationChoice: "worktree")),
            WorkflowTestData.SelectionContext(engineerIsolationChoices: ["sandboxed"]));

        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.IsolationChoiceNotAllowed &&
            issue.Path == "plan.items[0].isolationChoice" &&
            issue.Message.Contains("not eligible for role 'engineer'", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsRoleEligibleIsolationChoiceNotAllowedByWorkflowStep()
    {
        const string isolationChoice = "outside-step";
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(
                WorkflowTestData.Item("work", isolationChoice: isolationChoice)),
            WorkflowTestData.SelectionContext(
                engineerIsolationChoices: [isolationChoice]));

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.IsolationChoiceNotAllowed &&
            issue.Message.Contains("not allowed in step 'implement'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.IsolationChoiceNotAllowed &&
            issue.Message.Contains("not eligible for role", StringComparison.Ordinal));
    }

    [Fact]
    public void EnforcesStepCardinalityAndJoinsAllWorkFromPredecessorSteps()
    {
        var workflow = WorkflowTestData.Snapshot();
        var oneImplementation = WorkflowTestData.Item("implementation");
        var missingJoin = WorkflowTestData.Item("validation", stepId: "review-work");
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"]),
            WorkflowTestData.Open("implement", 1, ["setup"], minimum: 1, maximum: 4),
            WorkflowTestData.Open("review-work", 2, ["implement"], minimum: 0, maximum: 4),
            WorkflowTestData.Platform("review", 3, ["review-work"]));
        workflow = WorkflowTestData.Snapshot(definition);

        var missingCoverage = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(), WorkflowTestData.SelectionContext());
        var missingJoinResult = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(oneImplementation, missingJoin),
            WorkflowTestData.SelectionContext());

        Assert.Contains(missingCoverage.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkItemCardinalityViolation);
        Assert.Contains(missingJoinResult.Issues, issue =>
            issue.Code == WorkflowValidationCode.MissingJoinDependency);
    }

    [Fact]
    public void RejectsExceedingStepCardinalityAndTheGlobalWorkPlanLimit()
    {
        var oneItemStep = WorkflowTestData.Open(
            "implement", 0, [], minimum: 0, maximum: 1);
        var boundedWorkflow = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, oneItemStep));
        var tooManyForStep = WorkPlanValidator.ValidateAndSnapshot(
            boundedWorkflow,
            WorkflowTestData.Plan(
                WorkflowTestData.Item("first"),
                WorkflowTestData.Item("second")),
            WorkflowTestData.SelectionContext());

        var globallyBoundedWorkflow = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, [], minimum: 0, maximum: 128)) with
            {
                MaximumWorkItems = WorkflowDomainLimits.MaximumWorkItems
            });
        var beyondGlobalLimit = Enumerable.Range(
                0, WorkflowDomainLimits.MaximumWorkItems + 1)
            .Select(index => WorkflowTestData.Item($"work-{index}"))
            .ToArray();
        var tooManyForPlan = WorkPlanValidator.ValidateAndSnapshot(
            globallyBoundedWorkflow,
            WorkflowTestData.Plan(beyondGlobalLimit),
            WorkflowTestData.SelectionContext());

        Assert.Null(tooManyForStep.Value);
        Assert.Contains(tooManyForStep.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkItemCardinalityViolation);
        Assert.Null(tooManyForPlan.Value);
        Assert.Contains(tooManyForPlan.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkPlanLimitExceeded &&
            issue.Path == "plan.items");
    }

    [Fact]
    public void SerializesOverlappingSuffixAndFilenameOutputsDeterministically()
    {
        var first = WorkflowTestData.Item("first", outputs: ["foo.cs"]);
        var second = WorkflowTestData.Item("second", outputs: ["src/foo.cs"]);

        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(first, second),
            WorkflowTestData.SelectionContext());

        Assert.True(result.IsValid);
        Assert.Empty(result.Value!.Plan.Items[0].DependsOn);
        Assert.Equal(["first"], result.Value.Plan.Items[1].DependsOn.ToArray());
    }

    [Fact]
    public void PreservesAnExistingReverseOutputDependencyWithoutIntroducingACycle()
    {
        var first = WorkflowTestData.Item("first", dependsOn: ["second"], outputs: ["shared.cs"]);
        var second = WorkflowTestData.Item("second", outputs: ["src/shared.cs"]);

        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(first, second),
            WorkflowTestData.SelectionContext());

        Assert.True(result.IsValid);
        Assert.Equal(["second"], result.Value!.Plan.Items[0].DependsOn.ToArray());
        Assert.Empty(result.Value.Plan.Items[1].DependsOn);
    }

    [Fact]
    public void RejectsInvalidOutputPathAndDoesNotCreateSnapshotAfterRepeatedRejection()
    {
        var plan = WorkflowTestData.Plan(
            WorkflowTestData.Item("work", outputs: ["../../outside.cs"]));
        var workflow = WorkflowTestData.Snapshot();
        var context = WorkflowTestData.SelectionContext();
        WorkflowValidationResult<WorkPlanSnapshot>? rejected = null;

        for (var attempt = 0; attempt < 3; attempt++)
            rejected = WorkPlanValidator.ValidateAndSnapshot(workflow, plan, context);

        Assert.NotNull(rejected);
        Assert.False(rejected.IsValid);
        Assert.Null(rejected.Value);
        Assert.Contains(rejected.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidOutputPath);
    }

    [Fact]
    public void RejectsDuplicateAndExcessiveOutputPaths()
    {
        var workflow = WorkflowTestData.Snapshot();
        var context = WorkflowTestData.SelectionContext();
        var duplicateOutputs = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item(
                "duplicate",
                outputs: ["src/shared.cs", "src/shared.cs"])),
            context);
        var excessiveOutputs = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item(
                "excessive",
                outputs: Enumerable.Range(
                    0, WorkflowDomainLimits.MaximumOutputsPerItem + 1)
                    .Select(index => $"src/output-{index}.cs")
                    .ToArray())),
            context);

        Assert.Null(duplicateOutputs.Value);
        Assert.Contains(duplicateOutputs.Issues, issue =>
            issue.Code == WorkflowValidationCode.DuplicateOutputPath);
        Assert.Null(excessiveOutputs.Value);
        Assert.Contains(excessiveOutputs.Issues, issue =>
            issue.Code == WorkflowValidationCode.OutputLimitExceeded);
    }

    [Fact]
    public void RejectsMissingAndDuplicateWorkItemDependencies()
    {
        var missing = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(
                WorkflowTestData.Item("work", dependsOn: ["absent"])),
            WorkflowTestData.SelectionContext());
        var duplicate = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(
                WorkflowTestData.Item("work", dependsOn: ["other", "other"])),
            WorkflowTestData.SelectionContext());

        Assert.Null(missing.Value);
        Assert.Contains(missing.Issues, issue =>
            issue.Code == WorkflowValidationCode.MissingWorkItemDependency);
        Assert.Null(duplicate.Value);
        Assert.Contains(duplicate.Issues, issue =>
            issue.Code == WorkflowValidationCode.DuplicateWorkItemDependency);
    }

    [Fact]
    public void RejectsDependencyCyclesAndProviderPinMismatches()
    {
        var cyclic = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(
                WorkflowTestData.Item("first", dependsOn: ["second"]),
                WorkflowTestData.Item("second", dependsOn: ["first"])),
            WorkflowTestData.SelectionContext());
        var wrongProvider = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(),
            WorkflowTestData.Plan(WorkflowTestData.Item("work", providerId: "sandbox.other")),
            WorkflowTestData.SelectionContext());

        Assert.Contains(cyclic.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkItemDependencyCycle);
        Assert.Contains(wrongProvider.Issues, issue =>
            issue.Code == WorkflowValidationCode.PinnedProviderMismatch);
    }

    [Fact]
    public void RejectsMissingNegotiatedCapabilityAndUnorderedFixedOutputOverlap()
    {
        var workflow = WorkflowTestData.Snapshot();
        var noCapability = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work")),
            WorkflowTestData.SelectionContext(
                WorkflowTestData.PinnedSandboxBinding(capabilities: ["other.cap"])));

        var conflictingDefinition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("prepare", 0, [], ["src/work.cs"]),
            WorkflowTestData.Open("implement", 1, [], minimum: 1),
            WorkflowTestData.Platform("review", 2, ["implement"]));
        var fixedConflict = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(conflictingDefinition),
            WorkflowTestData.Plan(WorkflowTestData.Item("work", outputs: ["src/work.cs"])),
            WorkflowTestData.SelectionContext());

        Assert.Contains(noCapability.Issues, issue =>
            issue.Code == WorkflowValidationCode.RequiredCapabilityUnavailable);
        Assert.Contains(fixedConflict.Issues, issue =>
            issue.Code == WorkflowValidationCode.OutputConflictWithFixedWork);
    }
}
