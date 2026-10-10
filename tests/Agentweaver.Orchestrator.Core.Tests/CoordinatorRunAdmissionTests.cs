using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CoordinatorRunAdmissionTests
{
    [Fact]
    public void ExplicitSoftCreditLimitAlsoRequiresPricingWithoutInventingAHardLimit()
    {
        var selection = JsonSerializer.SerializeToElement(new
        {
            runLimits = new { copilotSoftCreditLimit = 0m },
            projectConfiguration = new { },
            modelSelection = new
            {
                reference = "accepted-reference", sourceMode = "hostedCopilot",
                connectionId = "11111111-1111-1111-1111-111111111111"
            }
        });
        Assert.True(CoordinationOwnerStore.RequiresRunAdmission(selection));
        Assert.Null(CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection).CopilotHardCreditLimit);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"runLimits":{}}""")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":null}}""")]
    public void LegacyUncappedSelectionsDoNotInventPricingAdmission(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(CoordinationOwnerStore.RequiresRunAdmission(document.RootElement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void ExplicitHardCreditLimitRequiresRealRunAdmission(decimal hard)
    {
        Assert.True(CoordinationOwnerStore.RequiresRunAdmission(Selection("project", "run", hard)));
    }

    [Theory]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10},"modelSelection":{"reference":"model"}}""")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10},"modelSelection":{"reference":"model","sourceMode":"byok"}}""")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10},"modelSelection":{"reference":"model","sourceMode":"hostedCopilot"}}""")]
    [InlineData("""{"runLimits":{"copilotSoftCreditLimit":10},"modelSelection":{"reference":"model","sourceMode":"unknown"}}""")]
    public void CreditLimitsCannotUseUnknownByokOrUnboundHostedPricing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequiresRunAdmission(document.RootElement));
        Assert.Equal("runtime_cost_budget_model_unavailable", failure.Code);
        Assert.Equal(409, failure.StatusCode);
    }

    internal static JsonElement Selection(string projectId, string runId, decimal hard = 10) =>
        JsonSerializer.SerializeToElement(new
        {
            projectId, runId, projectRevision = 1, projectConfigurationRevision = 1,
            platformRuntimeRevision = 1, contextRevision = "context-v1",
            projectConfiguration = new
            {
                blueprintWorkflowReferences = Array.Empty<object>(),
                agentCharters = Array.Empty<object>(), casting = Array.Empty<object>()
            },
            modelSelection = new
            {
                reference = "accepted-reference", sourceMode = "hostedCopilot",
                connectionId = "11111111-1111-1111-1111-111111111111"
            },
            runLimits = new { maxChildren = 100, maxConcurrentChildren = 32, copilotHardCreditLimit = hard }
        });

    internal static RuntimeRunAdmissionReceipt Receipt(AuthorizedRunSelection selection, decimal observed = 0)
    {
        var scope = selection.Selection;
        var resolver = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = new("concrete-model", ModelSourceMode.HostedCopilot)
            });
        var card = new CostRateCard("card", "1", SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000,
            ImmutableDictionary<string, decimal>.Empty.Add("concrete-model", 2.5m));
        var binding = new CostBinding(SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost",
            "1.0.0", 1, "options-v1", "resource", 1,
            ImmutableHashSet.Create("cost.usage.price", "cost.work.quote"), card);
        var amounts = observed == 0 ? ImmutableArray<UsageAmountTotal>.Empty
            : [new(SdkMeterSources.CopilotNanoAiu, "AIC", observed, 1, 0)];
        var agents = observed == 0 ? ImmutableArray<UsageAgentTotals>.Empty
            : [new("agent", 1, null, null, null, null, null, null, true, amounts)];
        return new(1, selection.Authorization.TenantId, scope.ProjectId, scope.RunId,
            RuntimeRunAdmissionContract.SelectionHash(scope.Snapshot),
            RuntimeAcceptedModelSelection.Read(scope.Snapshot),
            resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null),
            new(selection.Authorization.TenantId, scope.ProjectId, scope.RunId, observed == 0 ? 0 : 1, true, agents, amounts));
    }
}
