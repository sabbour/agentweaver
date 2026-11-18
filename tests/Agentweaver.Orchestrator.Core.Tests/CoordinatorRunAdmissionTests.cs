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

    [Theory]
    [InlineData("""{"hardLimit":5}""", false)]
    [InlineData("""{"softLimit":5}""", true)]
    [InlineData("""{"hardLimit":5,"unit":"AIC"}""", true)]
    public void ByokUsesAHardUsdRunBudgetInsteadOfCopilotCreditAdmission(string budgetOverride, bool rejected)
    {
        var budget = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"budgetId":"byok-run-usd","revision":1,"scope":"run","meterSource":"byok.tokens","unit":"USD",
             "softLimit":null,"hardLimit":null,"reservePerDispatch":0.5}
            """)!;
        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(budgetOverride)!)
            budget[key] = value;
        var selection = JsonSerializer.SerializeToElement(new
        {
            runLimits = new { copilotHardCreditLimit = 10m, spendBudgets = new[] { budget } },
            modelSelection = new
            {
                reference = "azure-gpt", sourceMode = "byok",
                credentialReference = new { id = "byok-azure-openai-key", version = "0123456789abcdef0123456789abcdef" }
            }
        });

        if (!rejected)
        {
            Assert.False(CoordinationOwnerStore.RequiresRunAdmission(selection));
            return;
        }
        Assert.Equal("runtime_cost_budget_model_unavailable", Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequiresRunAdmission(selection)).Code);
    }

    [Theory]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10},"modelSelection":{"reference":"azure-gpt","sourceMode":"byok","credentialReference":{"id":"byok-azure-openai-key","version":"0123456789abcdef0123456789abcdef"}}}""", "runtime_cost_budget_model_unavailable")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10,"spendBudgets":[{"budgetId":"byok","revision":1,"scope":"run","meterSource":"byok.tokens","unit":"USD","softLimit":null,"hardLimit":null,"reservePerDispatch":1}]},"modelSelection":{"reference":"azure-gpt","sourceMode":"byok","credentialReference":{"id":"byok-azure-openai-key","version":"0123456789abcdef0123456789abcdef"}}}""", "projects_run_selection_contract_invalid")]
    [InlineData("""{"runLimits":{"copilotHardCreditLimit":10,"spendBudgets":[{"budgetId":"byok","revision":1,"scope":"run","meterSource":"byok.tokens","unit":"EUR","softLimit":null,"hardLimit":5,"reservePerDispatch":1}]},"modelSelection":{"reference":"azure-gpt","sourceMode":"byok","credentialReference":{"id":"byok-azure-openai-key","version":"0123456789abcdef0123456789abcdef"}}}""", "runtime_cost_budget_model_unavailable")]
    public void ByokCreditLimitCannotPassWithoutAUsableHardUsdBudget(string json, string expectedCode)
    {
        using var document = JsonDocument.Parse(json);
        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequiresRunAdmission(document.RootElement));
        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public void RevisionBackedByokAdmissionStillRequiresAValidHardUsdBudget()
    {
        var provider = ByokProviderBinding("primary-provider");
        var pin = RuntimeModelBindingsResolver.Pin(provider, "high");
        var missingBudget = JsonSerializer.SerializeToElement(new
        {
            runLimits = new { copilotHardCreditLimit = 10m, spendBudgets = Array.Empty<object>() },
            projectConfiguration = new { },
            modelSelection = ByokModel(provider, pin)
        });

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequiresRunAdmission(missingBudget));
        Assert.Equal("runtime_cost_budget_model_unavailable", failure.Code);

        var fundedBudget = JsonSerializer.SerializeToElement(new
        {
            runLimits = new
            {
                copilotHardCreditLimit = 10m,
                spendBudgets = new[]
                {
                    new
                    {
                        budgetId = "byok-run-usd",
                        revision = 1,
                        scope = "run",
                        meterSource = SdkMeterSources.ByokTokens,
                        unit = "USD",
                        softLimit = (decimal?)null,
                        hardLimit = 5m,
                        reservePerDispatch = 0.5m
                    }
                }
            },
            projectConfiguration = new { },
            modelSelection = ByokModel(provider, pin)
        });

        Assert.True(CoordinationOwnerStore.RequiresRunAdmission(fundedBudget));
    }

    [Fact]
    public void RevisionBackedTaskOverrideRequiresAdmissionAndUsesItsOwnVerifiedCard()
    {
        var primaryProvider = ByokProviderBinding("primary-provider");
        var taskProvider = ByokProviderBinding("task-provider");
        var primaryPin = RuntimeModelBindingsResolver.Pin(primaryProvider, "high");
        var taskPin = RuntimeModelBindingsResolver.Pin(taskProvider, "high");
        var selection = JsonSerializer.SerializeToElement(new
        {
            projectId = "project",
            runId = "run",
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            projectConfiguration = new { },
            modelSelection = ByokModel(primaryProvider, primaryPin),
            modelSelections = new[]
            {
                new { selection = ByokModel(taskProvider, taskPin), connectionScope = "project" }
            }
        });
        Assert.True(CoordinationOwnerStore.RequiresRunAdmission(selection));

        var primaryCost = ByokCostBinding(primaryProvider, primaryPin);
        var taskCost = ByokCostBinding(taskProvider, taskPin);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        var receipt = new RuntimeRunAdmissionReceipt(
            1,
            "tenant",
            "project",
            "run",
            RuntimeRunAdmissionContract.SelectionHash(selection),
            model,
            primaryPin,
            primaryCost,
            new(null, "USD", CostDisposition.Unpriced, primaryCost.RateCard,
                "byok-token-quotes-unsupported"),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardContentHash = primaryProvider.RateCardPin!.ContentHash,
            RateCardAdmissions =
            [
                new(primaryPin, primaryProvider, primaryCost),
                new(taskPin, taskProvider, taskCost)
            ]
        };
        var request = new RuntimeRunAdmissionRequest(
            1, RuntimeRunAdmissionContract.SelectionHash(selection));
        Assert.Equal(0m, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));

        var delivered = CoordinationEndpoints.FindRateCardAdmission(
            receipt, taskProvider.ProviderId, "high", required: true);
        Assert.Equal(taskPin, delivered!.ModelBindingPin);
        Assert.Equal(taskProvider.ProviderId, delivered.ProviderBindingRevision.ProviderId);
        Assert.Equal(taskCost, delivered.CostBinding);
    }

    [Fact]
    public void ByokDollarBudgetDoesNotRequireAHostedCopilotQuoteForItsTaskOverride()
    {
        var taskProvider = ByokProviderBinding("task-provider");
        var taskPin = RuntimeModelBindingsResolver.Pin(taskProvider, "high");
        var taskCost = ByokCostBinding(taskProvider, taskPin);
        var selection = JsonSerializer.SerializeToElement(new
        {
            projectId = "project",
            runId = "run",
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            projectConfiguration = new { },
            modelSelection = new
            {
                reference = "hosted-primary",
                sourceMode = "hostedCopilot",
                connectionId = "11111111-1111-1111-1111-111111111111"
            },
            modelSelections = new[]
            {
                new { selection = ByokModel(taskProvider, taskPin), connectionScope = "project" }
            },
            runLimits = new
            {
                copilotHardCreditLimit = (decimal?)null,
                spendBudgets = new[]
                {
                    new
                    {
                        budgetId = "byok-run-usd",
                        revision = 1,
                        scope = "run",
                        meterSource = SdkMeterSources.ByokTokens,
                        unit = "USD",
                        softLimit = (decimal?)null,
                        hardLimit = 5m,
                        reservePerDispatch = 0.5m
                    }
                }
            }
        });
        Assert.True(CoordinationOwnerStore.RequiresRunAdmission(selection));

        var model = RuntimeAcceptedModelSelection.Read(selection);
        var hostedResolver = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["hosted-primary"] = new("gpt-6.1-sol", ModelSourceMode.HostedCopilot)
            });
        var hostedPin = hostedResolver.Pin(model.Reference, ModelSourceMode.HostedCopilot);
        var request = new RuntimeRunAdmissionRequest(
            1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = new RuntimeRunAdmissionReceipt(
            1,
            "tenant",
            "project",
            "run",
            request.AcceptedSelectionHash,
            model,
            hostedPin,
            null,
            new(null, "AIC", CostDisposition.Unpriced, null, "model-rate-unavailable"),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardAdmissions = [new(taskPin, taskProvider, taskCost)]
        };

        Assert.Equal(0m, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        var delivered = CoordinationEndpoints.FindRateCardAdmission(
            receipt, taskProvider.ProviderId, "high", required: true);
        Assert.Equal(taskProvider.ProviderId, delivered!.ProviderBindingRevision.ProviderId);
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
                ["accepted-reference"] = new("gpt-6.1-sol", ModelSourceMode.HostedCopilot)
            });
        var card = new CostRateCard("card", "2026-10-17", SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000,
            ImmutableDictionary<string, decimal>.Empty)
        {
            ProviderSourceUrl = "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing",
            SnapshotVersion = "2026-10-17",
            RetrievedOn = new DateOnly(2026, 10, 17),
            TokenRateCurrency = "USD",
            TokensPerRateUnit = 1_000_000m,
            CreditsPerCurrencyUnit = 100m,
            LongContextThresholdTokens = 272_000,
            ContentHash = new string('a', 64),
            ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("gpt-6.1-sol", new(
                    new(2m, 0.1m, 2.5m, 10m),
                    new(4m, 0.2m, 5m, 15m)))
        };
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
            new(selection.Authorization.TenantId, scope.ProjectId, scope.RunId, observed == 0 ? 0 : 1, true, agents, amounts))
        {
            RateCardContentHash = card.ContentHash
        };
    }

    internal static RuntimeRunAdmissionReceipt UnpricedReceipt(AuthorizedRunSelection selection)
    {
        var receipt = Receipt(selection);
        // Keep this a valid explicit-unpriced contract, not a malformed priced receipt.
        return receipt with
        {
            CostBinding = null,
            Quote = new(null, "AIC", CostDisposition.Unpriced, null, "model-rate-unavailable"),
            RateCardContentHash = null
        };
    }

    internal static RuntimeRunAdmissionReceipt UnpricedUsageReceipt(AuthorizedRunSelection selection)
    {
        var receipt = Receipt(selection);
        // Preserve the ledger event while representing its unknown amount as an unpriced event.
        var amount = new UsageAmountTotal(SdkMeterSources.CopilotNanoAiu, "AIC", 0m, 0, 1);
        var agent = new UsageAgentTotals(
            "agent", 1, null, null, null, null, null, null, false, [amount]);
        return receipt with
        {
            CopilotTotals = new(
                selection.Authorization.TenantId,
                selection.Selection.ProjectId,
                selection.Selection.RunId,
                1,
                false,
                [agent],
                [amount])
        };
    }

    private static ModelProviderBindingRevision ByokProviderBinding(string providerId)
    {
        var card = new CostRateCard(
            "manual-provider-card", "v1", SdkMeterSources.ByokTokens, "USD",
            1_000_000m, ImmutableDictionary<string, decimal>.Empty);
        var provenance = new ManualRuntimeRateCardReadProvenance(
            "https://pricing.example/rate-card", new DateOnly(2026, 1, 1));
        var rateCardPin = RuntimeRateCardReadContract.CreatePin(
            new(RuntimeRateCardReadContract.CurrentSchemaVersion, card, provenance));
        var identity = new ModelCredentialIdentity(
            Guid.NewGuid(), ModelProviderBindingScope.Project, "project", providerId);
        return new(
            ModelProviderBindingRevision.CurrentContractVersion,
            providerId,
            ModelProviderBindingScope.Project,
            "project",
            "Customer provider",
            "azure",
            "https://provider.example",
            "deployment-1",
            "responses",
            null,
            ["high"],
            null,
            3,
            "credential-v1",
            rateCardPin.RateCardId,
            rateCardPin.RateCardVersion,
            new string(providerId == "primary-provider" ? 'd' : 'e', 64))
        {
            CredentialIdentity = identity,
            CredentialReference = new(providerId + "-credential", "credential-v1"),
            RateCardPin = rateCardPin
        };
    }

    private static object ByokModel(
        ModelProviderBindingRevision provider, RuntimeModelBindingPin pin) =>
        new
        {
            reference = provider.ProviderId,
            sourceMode = "byok",
            credentialReference = new
            {
                id = provider.CredentialReference!.Id,
                version = provider.CredentialReference.Version
            },
            credentialIdentity = provider.CredentialIdentity,
            reasoningEffort = "high",
            modelBindingPin = pin,
            providerBindingRevision = provider
        };

    private static CostBinding ByokCostBinding(
        ModelProviderBindingRevision provider, RuntimeModelBindingPin pin) =>
        new(
            SdkMeterSources.ByokTokens,
            "events.runtime-rate-card",
            "1.0.0",
            1,
            provider.ConfigurationHash,
            provider.RateCardPin!.RateCardId,
            provider.ProviderRevision,
            ImmutableHashSet.Create(StringComparer.Ordinal, "cost.usage.price"),
            new(
                provider.RateCardPin.RateCardId,
                provider.RateCardPin.RateCardVersion,
                SdkMeterSources.ByokTokens,
                "USD",
                1_000_000m,
                ImmutableDictionary<string, decimal>.Empty)
            )
        {
            RuntimeRateCardPin = provider.RateCardPin,
            RuntimeByokIdentity = RuntimeByokCostBindingIdentity.From(pin, provider)
        };
}
