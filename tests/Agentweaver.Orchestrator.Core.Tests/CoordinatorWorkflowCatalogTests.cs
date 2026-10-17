using System.Text.Json;
using Agentweaver.Abstractions;
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
                Assert.Equal(ModelTaskKind.CodeWriting, implementation.ModelTaskKind);
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
            """{"runLimits":{"maxModelTurns":12,"maxToolCalls":100,"maxPromptTokens":20000,"maxRevisionAttempts":0,"copilotSoftCreditLimit":4.25,"copilotHardCreditLimit":5}}""");
        var limits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(configured.RootElement);

        Assert.Equal(12, limits.MaxModelTurns);
        Assert.Equal(100, limits.MaxToolCalls);
        Assert.Equal(20000, limits.MaxPromptTokens);
        Assert.Equal(0, limits.MaxRevisionAttempts);
        Assert.Equal(4.25m, limits.CopilotSoftCreditLimit);
        Assert.Equal(5m, limits.CopilotHardCreditLimit);
        using var omitted = JsonDocument.Parse("""{"runLimits":{}}""");
        var unconfigured = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(omitted.RootElement);
        Assert.Null(unconfigured.MaxModelTurns);
        Assert.Null(unconfigured.MaxToolCalls);
        Assert.Null(unconfigured.MaxPromptTokens);
        Assert.Null(unconfigured.MaxRevisionAttempts);
        Assert.Null(unconfigured.CopilotSoftCreditLimit);
        Assert.Null(unconfigured.CopilotHardCreditLimit);
    }

    [Theory]
    [InlineData("maxModelTurns", 0)]
    [InlineData("maxModelTurns", 1001)]
    [InlineData("maxToolCalls", 0)]
    [InlineData("maxToolCalls", 10001)]
    [InlineData("maxPromptTokens", 1023)]
    [InlineData("maxPromptTokens", 200001)]
    [InlineData("maxRevisionAttempts", -1)]
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

    [Fact]
    public void RunSelectionContextCarriesAcceptedTaskRoutesAndAllEligibleRoleReferences()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [{ "agentId": "agent-1", "role": "engineer", "charter": "Implement work." }],
                "casting": [{ "agentId": "agent-1", "role": "engineer" }]
              },
              "modelSelection": { "reference": "model:provider/alpha" },
              "modelSelections": [
                { "selection": { "reference": "model:provider/alpha" }, "connectionScope": "project" },
                { "selection": { "reference": "model:provider/beta" }, "connectionScope": "project" }
              ],
              "modelTaskSelections": [
                { "taskKind": "straightforward", "roleId": null, "agentId": null, "modelSelectionReference": "model:provider/alpha" },
                { "taskKind": "codeWriting", "roleId": "engineer", "agentId": "agent-1", "modelSelectionReference": "model:provider/beta" }
              ]
            }
            """);

        var context = CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement);

        Assert.Equal(2, context.ModelTaskSelections.Length);
        Assert.Equal(ModelTaskKind.CodeWriting, context.ModelTaskSelections[1].TaskKind);
        Assert.Equal("model:provider/beta", context.ModelTaskSelections[1].ModelSelectionReference);
        Assert.Equal(
            ["model:provider/alpha", "model:provider/beta"],
            context.Roles.Single().ModelSelectionReferences.ToArray());
    }

    [Fact]
    public void RunSelectionContextPreservesDistinctEffortIdentitiesForOneModelReference()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [{ "agentId": "agent-1", "role": "engineer", "charter": "Implement work." }],
                "casting": [{ "agentId": "agent-1", "role": "engineer" }]
              },
              "modelSelection": { "reference": "model:provider/alpha", "reasoningEffort": "high" },
              "modelSelections": [
                { "selection": { "reference": "model:provider/alpha", "reasoningEffort": "high" }, "connectionScope": "project" },
                { "selection": { "reference": "model:provider/alpha", "reasoningEffort": "low" }, "connectionScope": "project" }
              ],
              "modelTaskSelections": [
                { "taskKind": "planning", "modelSelectionReference": "model:provider/alpha", "reasoningEffort": "low" },
                { "taskKind": "codeWriting", "modelSelectionReference": "model:provider/alpha", "reasoningEffort": "high" }
              ]
            }
            """);

        var context = CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement);
        var planning = ModelTaskSelectionResolver.ResolveSelection(
            ModelTaskKind.Planning, "engineer", "agent-1", context.ModelTaskSelections);
        var coding = ModelTaskSelectionResolver.ResolveSelection(
            ModelTaskKind.CodeWriting, "engineer", "agent-1", context.ModelTaskSelections);

        Assert.Equal(
            new ModelTaskSelectionReference(
                ModelTaskKind.Planning, null, null, "model:provider/alpha", "low"),
            planning);
        Assert.Equal(
            new ModelTaskSelectionReference(
                ModelTaskKind.CodeWriting, null, null, "model:provider/alpha", "high"),
            coding);
        Assert.Equal(["model:provider/alpha"], context.Roles.Single().ModelSelectionReferences.ToArray());
    }

    [Fact]
    public void RunSelectionContextAcceptsAnOverrideRouteMatrixLargerThanEligibilityLists()
    {
        var routes = Enum.GetValues<ModelTaskKind>()
            .SelectMany(taskKind =>
                new[]
                {
                    new
                    {
                        taskKind,
                        roleId = (string?)null,
                        agentId = (string?)null,
                        modelSelectionReference = "model:provider/alpha",
                        reasoningEffort = "high"
                    }
                }
                .Concat(Enumerable.Range(0, 16).Select(index => new
                {
                    taskKind,
                    roleId = (string?)$"role-{index}",
                    agentId = (string?)null,
                    modelSelectionReference = "model:provider/alpha",
                    reasoningEffort = "high"
                }))
                .Concat(Enumerable.Range(0, 16).Select(index => new
                {
                    taskKind,
                    roleId = (string?)$"role-{index}",
                    agentId = (string?)$"agent-{index}",
                    modelSelectionReference = "model:provider/alpha",
                    reasoningEffort = "high"
                })))
            .ToArray();
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            projectConfiguration = new
            {
                blueprintWorkflowReferences = Array.Empty<object>(),
                agentCharters = Array.Empty<object>(),
                casting = Array.Empty<object>()
            },
            modelSelection = new { reference = "model:provider/alpha" },
            modelTaskSelections = routes
        }));

        var context = CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement);

        Assert.Equal(264, context.ModelTaskSelections.Length);
    }

    [Fact]
    public void RunSelectionContextRejectsExactDuplicateModelSelectionIdentities()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              },
              "modelSelection": { "reference": "model:provider/alpha", "reasoningEffort": "high" },
              "modelSelections": [
                { "selection": { "reference": "model:provider/alpha", "reasoningEffort": "high" }, "connectionScope": "project" },
                { "selection": { "reference": "model:provider/alpha", "reasoningEffort": "high" }, "connectionScope": "project" }
              ]
            }
            """);

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement));

        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
    }

    [Fact]
    public void RunSelectionContextRejectsUnknownTaskRouteFields()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [{ "agentId": "agent-1", "role": "engineer", "charter": "Implement work." }],
                "casting": [{ "agentId": "agent-1", "role": "engineer" }]
              },
              "modelSelection": { "reference": "model:provider/alpha" },
              "modelTaskSelections": [
                { "taskKind": "straightforward", "modelSelectionReference": "model:provider/alpha", "unexpected": true }
              ]
            }
            """);

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement));

        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
    }

    [Fact]
    public void RunSelectionContextRejectsUnsupportedRouteReasoningEffort()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              },
              "modelSelection": { "reference": "model:provider/alpha", "reasoningEffort": "high" },
              "modelTaskSelections": [
                { "taskKind": "codeWriting", "modelSelectionReference": "model:provider/alpha", "reasoningEffort": "none" }
              ]
            }
            """);

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement));

        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
    }

    [Fact]
    public void LegacySingleModelSelectionAppliesToUnspecifiedTaskRoutes()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              },
              "modelSelection": { "reference": "model:provider/legacy" }
            }
            """);

        var context = CoordinatorWorkflowCatalog.CreateRunSelectionContext(snapshot.RootElement);

        Assert.Equal(
            "model:provider/legacy",
            ModelTaskSelectionResolver.ResolveReference(
                ModelTaskKind.CodeWriting, "engineer", "agent-1", context.ModelTaskSelections));
    }
}
