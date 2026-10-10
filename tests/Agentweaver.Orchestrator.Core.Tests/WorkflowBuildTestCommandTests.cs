using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowBuildTestCommandTests
{
    [Fact]
    public void SnapshotsTypedCommandWithoutInterpretingArgumentsOrChoosingExecutionOptions()
    {
        var command = Command() with { Arguments = ["test", "a b", "--", "$(literal)"] };
        var snapshot = Snapshot(command);

        Assert.Equal(command, snapshot.Definition.Steps[0].BuildTestCommand);
        Assert.Equal(["test", "a b", "--", "$(literal)"], snapshot.Definition.Steps[0].BuildTestCommand!.Arguments.ToArray());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("executable")]
    [InlineData("traversal")]
    [InlineData("workdir")]
    [InlineData("subdirectory")]
    [InlineData("controls")]
    [InlineData("defaultargs")]
    [InlineData("argbytes")]
    [InlineData("output")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    public void RejectsUnboundedOrMalformedTypedCommands(string fault)
    {
        var command = Command();
        command = fault switch
        {
            "profile" => command with { ExecutionProfileReference = " " },
            "executable" => command with { ExecutableReference = "dotnet" },
            "traversal" => command with { ExecutableReference = "/usr/../dotnet" },
            "workdir" => command with { WorkingDirectory = "../outside" },
            "subdirectory" => command with { WorkingDirectory = "src" },
            "controls" => command with { Arguments = ["test\n"] },
            "defaultargs" => command with { Arguments = default },
            "argbytes" => command with { Arguments = [.. Enumerable.Repeat(new string('x', 4096), 9)] },
            "output" => command with { Outputs = [command.Outputs[0] with { RelativePath = "../result" }] },
            "duplicate" => command with { Outputs = [command.Outputs[0], command.Outputs[0]] },
            "oversize" => command with { Outputs = [command.Outputs[0] with { MaximumBytes = 67_108_865 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(Definition(command));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == WorkflowValidationCode.InvalidBuildTestCommand);
    }

    [Fact]
    public void RejectsTypedCommandOnAnUnrelatedPlatformGate()
    {
        var definition = Definition(Command());
        definition = definition with
        {
            Steps = [definition.Steps[0] with { PlatformGate = WorkflowPlatformGate.Merge }]
        };
        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);
        Assert.Contains(result.Issues, issue => issue.Code == WorkflowValidationCode.InvalidBuildTestCommand);
    }

    [Theory]
    [InlineData("args")]
    [InlineData("profile")]
    [InlineData("outputs")]
    [InlineData("required")]
    public void AnyExecutionScopeChangeRequiresConfirmation(string change)
    {
        var command = Command();
        var next = change switch
        {
            "args" => command with { Arguments = ["--no-restore", "test"] },
            "profile" => command with { ExecutionProfileReference = "profile-2" },
            "outputs" => command with { Outputs = [command.Outputs[0] with { MaximumBytes = 2048 }] },
            "required" => command with { Outputs = [command.Outputs[0] with { Required = false }] },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var diff = WorkflowScopeDiffer.Compare(Snapshot(command), Snapshot(next));

        Assert.True(diff.RequiresConfirmation);
        Assert.Equal(["build-test"], diff.ChangedStepIds.ToArray());
    }

    [Fact]
    public void EquivalentImmutableCollectionsDoNotRequireAnotherConfirmation()
    {
        var command = Command();
        var equivalent = command with
        {
            Arguments = command.Arguments.ToImmutableArray(),
            Outputs = command.Outputs.Select(output => output with { }).ToImmutableArray()
        };
        Assert.False(WorkflowScopeDiffer.Compare(Snapshot(command), Snapshot(equivalent)).RequiresConfirmation);
    }

    [Fact]
    public void TypedBuildTestRequiresAPinnedSandboxEvenWithoutModelWork()
    {
        var workflow = Snapshot(Command());
        var plan = new WorkPlan("plan-1", workflow.Definition.Id, workflow.Definition.Revision,
            workflow.Definition.CatalogVersion, []);
        var invalid = WorkPlanValidator.ValidateAndSnapshot(workflow, plan, new([], null));
        var valid = WorkPlanValidator.ValidateAndSnapshot(
            workflow, plan, new([], WorkflowTestData.PinnedSandboxBinding(
                "sandbox.test", SandboxCapabilities.BuildTestCommandPod)));

        Assert.Contains(invalid.Issues, issue => issue.Code == WorkflowValidationCode.PinnedSandboxBindingRequired);
        Assert.True(valid.IsValid);
    }

    [Fact]
    public void TypedBuildTestCannotUseAProviderWithoutTheCommandCapability()
    {
        var workflow = Snapshot(Command());
        var plan = new WorkPlan("plan-1", workflow.Definition.Id, workflow.Definition.Revision,
            workflow.Definition.CatalogVersion, []);

        var invalid = WorkPlanValidator.ValidateAndSnapshot(
            workflow, plan, new([], WorkflowTestData.PinnedSandboxBinding()));

        Assert.Contains(invalid.Issues, issue => issue.Code == WorkflowValidationCode.RequiredCapabilityUnavailable);
    }

    [Fact]
    public void PrescribedCommandOutputsSerializeOverlappingFixedAndPlatformSteps()
    {
        var definition = WorkflowTestData.Definition(WorkflowDefinitionOrigin.Generated,
            WorkflowTestData.Fixed("setup", 0, [], ["artifacts/results.xml"]),
            WorkflowTestData.Platform("build-test", 1, [], WorkflowPlatformGate.BuildTest) with
            {
                BuildTestCommand = Command()
            });

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.True(result.IsValid);
        Assert.Equal(["setup"], result.Value!.Definition.Steps[1].DependsOn.ToArray());
    }

    [Fact]
    public void UnorderedModelAndCommandOutputCollisionsRejectThePlan()
    {
        var workflow = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.Generated,
                WorkflowTestData.Open("implement", 0, []),
                WorkflowTestData.Platform("build-test", 1, [], WorkflowPlatformGate.BuildTest) with
                {
                    BuildTestCommand = Command()
                }));
        var result = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(
                WorkflowTestData.Item("item-1", outputs: ["artifacts/results.xml"])),
            WorkflowTestData.SelectionContext());

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.OutputConflictWithFixedWork);
    }

    private static WorkflowBuildTestCommand Command() =>
        new("profile-1", "/usr/bin/dotnet", ["test", "--no-restore"], ".",
            [new("results", "artifacts/results.xml", true, 1024)]);

    private static WorkflowDefinition Definition(WorkflowBuildTestCommand command) =>
        WorkflowTestData.Definition(WorkflowDefinitionOrigin.Generated,
            WorkflowTestData.Platform("build-test", 0, [], WorkflowPlatformGate.BuildTest) with
            {
                BuildTestCommand = command
            });

    private static WorkflowDefinitionSnapshot Snapshot(WorkflowBuildTestCommand command)
    {
        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(Definition(command));
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }
}
