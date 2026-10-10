using System.Text.Json;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CoordinatorRuntimeBudgetTests
{
    [Fact]
    public void ReadsOnlyAcceptedRunLimitsAndPreservesMissingValuesAndExplicitZero()
    {
        var missing = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(
            JsonSerializer.SerializeToElement(new { runLimits = new { } }));
        Assert.Equal((null, null, null, null, null, null), missing);
        var accepted = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(
            JsonSerializer.SerializeToElement(new
            {
                runLimits = new
                {
                    maxModelTurns = 5, maxToolCalls = 10, maxPromptTokens = 1024,
                    maxRevisionAttempts = 0, copilotSoftCreditLimit = 0m, copilotHardCreditLimit = 0m
                },
                projectConfiguration = new { runLimits = new { copilotHardCreditLimit = 999m } }
            }));
        Assert.Equal((5, 10, 1024, 0, 0m, 0m), accepted);
    }

    [Theory]
    [InlineData("""{"runLimits":null}""")]
    [InlineData("""{"runLimits":{"maxModelTurns":0}}""")]
    [InlineData("""{"runLimits":{"maxToolCalls":10001}}""")]
    [InlineData("""{"runLimits":{"maxPromptTokens":1023}}""")]
    [InlineData("""{"runLimits":{"maxRevisionAttempts":-1}}""")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":-1}}""")]
    [InlineData("""{"runLimits":{"copilotSoftCreditLimit":2,"copilotHardCreditLimit":1}}""")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":"1"}}""")]
    [InlineData("""{"runLimits":{"maxRevisionAttempts":true}}""")]
    public void RejectsInvalidAcceptedLimitsThroughTheOwnerErrorContract(string json)
    {
        using var document = JsonDocument.Parse(json);
        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(document.RootElement));
        Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
        Assert.Equal(502, failure.StatusCode);
    }
}
