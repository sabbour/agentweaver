using System.Text.Json;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CoordinatorWorkflowCatalogTests
{
    [Fact]
    public void DefaultWorkflowHasBoundedImplementationBeforeBuildTestGate()
    {
        using var snapshot = JsonDocument.Parse(
            """{"projectConfiguration":{"blueprintWorkflowReferences":[]}}""");
        var selection = new EffectiveRunSelection(
            "project-1", "run-1", 1, 1, 1, "context-v1", snapshot.RootElement.Clone());

        var catalog = CoordinatorWorkflowCatalog.ForSelection(selection);
        var definition = WorkflowDefinitionValidator.ValidateAndSnapshot(catalog.DefaultWorkflow);

        Assert.True(definition.IsValid, string.Join("; ", definition.Issues.Select(issue => issue.Message)));
        Assert.Collection(
            catalog.DefaultWorkflow.Steps,
            implementation =>
            {
                Assert.Equal("implementation", implementation.Id);
                Assert.Equal(WorkflowStepMode.Open, implementation.Mode);
                Assert.Equal(new WorkflowCardinality(0, 8), implementation.Cardinality);
            },
            buildTest =>
            {
                Assert.Equal("build-test", buildTest.Id);
                Assert.Equal(WorkflowStepMode.Platform, buildTest.Mode);
                Assert.Equal(WorkflowPlatformGate.BuildTest, buildTest.PlatformGate);
                Assert.Equal(new[] { "implementation" }, buildTest.DependsOn.ToArray());
            });
    }

    [Theory]
    [InlineData(60)]
    [InlineData(86400)]
    public void WallTimeLimitUsesResolvedRunLimitBounds(int seconds)
    {
        using var selection = JsonDocument.Parse(
            JsonSerializer.Serialize(new { runLimits = new { maxWallTimeSeconds = seconds } }));

        Assert.Equal(seconds, CoordinatorWorkflowCatalog.ReadMaxWallTimeSeconds(selection.RootElement));
    }

    [Theory]
    [InlineData(59)]
    [InlineData(86401)]
    public void WallTimeLimitRejectsValuesOutsideResolvedRunLimitBounds(int seconds)
    {
        using var selection = JsonDocument.Parse(
            JsonSerializer.Serialize(new { runLimits = new { maxWallTimeSeconds = seconds } }));

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.ReadMaxWallTimeSeconds(selection.RootElement));

        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
    }

    [Fact]
    public void WallTimeLimitIsReachedAtItsInclusiveDeadline()
    {
        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var deadline = startedAt.AddSeconds(60);

        Assert.False(CoordinatorWorkflowCatalog.IsWallTimeLimitReached(
            startedAt, 60, deadline.AddTicks(-1)));
        Assert.True(CoordinatorWorkflowCatalog.IsWallTimeLimitReached(startedAt, 60, deadline));
        Assert.True(CoordinatorWorkflowCatalog.IsWallTimeLimitReached(
            startedAt, 60, deadline.AddTicks(1)));
    }

    [Fact]
    public void RuntimeNumericRunPoliciesPreserveConfiguredValuesAndOmission()
    {
        using var configured = JsonDocument.Parse(
            """{"runLimits":{"maxModelTurns":12,"maxToolCalls":100,"maxPromptTokens":20000}}""");
        var limits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(configured.RootElement);

        Assert.Equal(12, limits.MaxModelTurns);
        Assert.Equal(100, limits.MaxToolCalls);
        Assert.Equal(20000, limits.MaxPromptTokens);

        using var omitted = JsonDocument.Parse("""{"runLimits":{}}""");
        var unconfigured = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(omitted.RootElement);

        Assert.Null(unconfigured.MaxModelTurns);
        Assert.Null(unconfigured.MaxToolCalls);
        Assert.Null(unconfigured.MaxPromptTokens);
    }

    [Theory]
    [InlineData("maxModelTurns", 0)]
    [InlineData("maxModelTurns", 1001)]
    [InlineData("maxToolCalls", 0)]
    [InlineData("maxToolCalls", 10001)]
    [InlineData("maxPromptTokens", 1023)]
    [InlineData("maxPromptTokens", 200001)]
    public void RuntimeNumericRunPoliciesRejectValuesOutsideProjectsBounds(
        string propertyName,
        int value)
    {
        using var selection = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            runLimits = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [propertyName] = value
            }
        }));

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection.RootElement));

        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
    }
}
