using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowDefinitionValidatorTests
{
    [Fact]
    public void AcceptsGeneratedDefinitionWithBroadExploratoryOpenStepAndSnapshotsVersion()
    {
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.Generated,
            WorkflowTestData.Open("exploration", 0, [], minimum: 0, maximum: 128));

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal("catalog-1", result.Value!.Definition.CatalogVersion);
        Assert.True(result.Value.RequiresFirstUseConfirmation);
    }

    [Fact]
    public void RejectsDuplicateAndUnstableStepIdsWithStructuredReasons()
    {
        var duplicate = WorkflowTestData.Open("duplicate", 0, [], minimum: 0);
        var invalid = duplicate with { Id = "not stable" };
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            duplicate,
            duplicate with { Order = 1 },
            invalid with { Order = 2 });

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.DuplicateStepId);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidStepId);
    }

    [Fact]
    public void RejectsCyclicStepsAndMissingDependencyReferences()
    {
        var first = WorkflowTestData.Open("first", 0, ["second"], minimum: 0);
        var second = WorkflowTestData.Open("second", 1, ["first"], minimum: 0);
        var cycle = WorkflowDefinitionValidator.ValidateAndSnapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, first, second));
        var missing = WorkflowDefinitionValidator.ValidateAndSnapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("first", 0, ["absent"], minimum: 0)));

        Assert.Contains(cycle.Issues, issue =>
            issue.Code == WorkflowValidationCode.StepDependencyCycle);
        Assert.Contains(missing.Issues, issue =>
            issue.Code == WorkflowValidationCode.MissingStepDependency);
    }

    [Fact]
    public void RejectsOutOfOrderDependenciesAndInvalidCardinality()
    {
        var later = WorkflowTestData.Open("later", 1, [], minimum: 0);
        var earlier = WorkflowTestData.Open("earlier", 0, ["later"], minimum: 0) with
        {
            Cardinality = new WorkflowCardinality(4, 2)
        };

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, later, earlier));

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.StepOrderViolation);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidCardinality);
    }

    [Fact]
    public void RejectsOpenStepMinimumsThatExceedTheWorkPlanLimit()
    {
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, [], minimum: 2, maximum: 4),
            WorkflowTestData.Open("validate", 1, [], minimum: 2, maximum: 4)) with
        {
            MaximumWorkItems = 3
        };

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.WorkPlanLimitExceeded &&
            issue.Path == "definition.maximumWorkItems");
    }

    [Fact]
    public void RejectsMalformedPlatformGateAndDoesNotSnapshotInvalidGeneratedDefinition()
    {
        var invalidGate = WorkflowTestData.Platform("build", 0, []) with
        {
            AllowedRoles = ["engineer"]
        };
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.Generated, invalidGate);

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidPlatformGate);
    }

    [Fact]
    public void FixedOutputOverlapsAddDeterministicStepOrdering()
    {
        var first = WorkflowTestData.Fixed("first", 0, [], ["foo.cs"]);
        var second = WorkflowTestData.Fixed("second", 1, [], ["src/foo.cs"]);

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(
            WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, first, second));

        Assert.True(result.IsValid);
        Assert.Equal(["first"], result.Value!.Definition.Steps[1].DependsOn.ToArray());
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/rooted.cs")]
    [InlineData(@"src\windows.cs")]
    [InlineData("C:/drive.cs")]
    public void RejectsNonRepositoryRelativeFixedOutputs(string output)
    {
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("fixed", 0, [], [output]));

        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowValidationCode.InvalidOutputPath);
    }
}
