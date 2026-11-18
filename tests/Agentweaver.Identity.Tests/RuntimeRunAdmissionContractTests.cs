using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeRunAdmissionContractTests
{
    [Fact]
    public void AdmissionBindsTheRealSelectionReferenceConnectionAndPublishedPricedModelWithoutSdkFacts()
    {
        var selection = Selection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Equal("accepted-reference", receipt.ModelSelection.Reference);
        Assert.Equal("gpt-6.1-sol", receipt.ModelBindingPin.ModelId);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), receipt.ModelSelection.ConnectionId);
        Assert.Equal(ProjectAuthorityResourceType.Platform, receipt.ModelSelection.ConnectionScope);
        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("ModelId", json);
        Assert.DoesNotContain("Sdk", json);
        Assert.DoesNotContain("Usage", json);
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            JsonSerializer.Deserialize<RuntimeRunAdmissionReceipt>(JsonSerializer.Serialize(receipt))!,
            request, "tenant", "project", "run", selection));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeRunAdmissionRequest>(
            $$"""{"ContractVersion":1,"AcceptedSelectionHash":"{{request.AcceptedSelectionHash}}","ModelId":"free-model"}"""));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    public void AdmissionPreservesTheAcceptedReasoningEffortInTheModelPin(string reasoningEffort)
    {
        var selection = Selection(reasoningEffort: reasoningEffort);
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);

        Assert.Equal(reasoningEffort, receipt.ModelSelection.ReasoningEffort);
        Assert.Equal(reasoningEffort, receipt.ModelBindingPin.ReasoningEffort);
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));

        var mismatched = receipt with
        {
            ModelBindingPin = receipt.ModelBindingPin with { ReasoningEffort = "high" }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(mismatched, request, "tenant", "project", "run", selection));
    }

    [Fact]
    public void AdmissionCannotReplaceAnAcceptedModelBindingPin()
    {
        var models = ModelResolver();
        var acceptedPin = models.Pin("accepted-reference", ModelSourceMode.HostedCopilot, "high");
        var selection = Selection(reasoningEffort: "high", modelBindingPin: acceptedPin);
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection, acceptedPin.ModelId);
        Assert.Equal(acceptedPin, receipt.ModelBindingPin);

        var changedPin = acceptedPin with
        {
            ConfigurationRevision = "model-bindings-v2",
            ConfigurationHash = new string('b', 64)
        };
        var replaced = receipt with
        {
            ModelBindingPin = changedPin
        };
        Assert.Equal("runtime_run_admission_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeRunAdmissionContract.ValidateReceipt(
                    replaced, request, "tenant", "project", "run", selection)).Code);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            models.Resolve("accepted-reference", ModelSourceMode.HostedCopilot,
                changedPin));
    }

    [Fact]
    public void AdmissionAcceptsExplicitUnpricedForAModelWithoutPublishedRates()
    {
        var selection = Selection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);
        receipt = receipt with
        {
            ModelBindingPin = receipt.ModelBindingPin with { ModelId = "unknown-model" },
            Quote = new(null, "AIC", CostDisposition.Unpriced, receipt.CostBinding!.RateCard,
                "model-rate-unavailable")
        };

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
    }

    [Theory]
    [InlineData("reasoning", "gpt-6.1-sol")]
    [InlineData("coding", "gpt-6-luna")]
    public void AdmissionAcceptsBothApprovedHostedModelBindingsWithPresentRateEntries(
        string modelReference, string modelId)
    {
        var selection = Selection(modelReference);
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection, modelId);

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Equal(modelId, receipt.ModelBindingPin.ModelId);
        Assert.Contains(modelId, receipt.CostBinding!.RateCard.ModelTokenRates.Keys);
        Assert.Equal(receipt.CostBinding.RateCard.ContentHash, receipt.RateCardContentHash);
    }

    [Fact]
    public void ByokAdmissionBindsTheExactProviderCredentialAndEventsRateCard()
    {
        var provider = ByokProviderBinding();
        var modelPin = RuntimeModelBindingsResolver.Pin(provider);
        var selection = ByokSelection(provider, modelPin);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        var binding = new CostBinding(
            SdkMeterSources.ByokTokens,
            "events.runtime-rate-card",
            "1.0.0",
            1,
            provider.ConfigurationHash,
            provider.RateCardPin!.RateCardId,
            provider.ProviderRevision,
            ImmutableHashSet.Create(StringComparer.Ordinal, "cost.usage.price"),
            RuntimeRateCard(provider))
        {
            RuntimeRateCardPin = provider.RateCardPin,
            RuntimeByokIdentity = RuntimeByokCostBindingIdentity.From(modelPin, provider)
        };
        var receipt = new RuntimeRunAdmissionReceipt(
            1,
            "tenant",
            "project",
            "run",
            RuntimeRunAdmissionContract.SelectionHash(selection),
            model,
            modelPin,
            binding,
            new(null, "USD", CostDisposition.Unpriced, binding.RateCard, "byok-token-quotes-unsupported"),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardContentHash = provider.RateCardPin.ContentHash,
            RateCardAdmissions = [new(modelPin, provider, binding)]
        };
        var request = new RuntimeRunAdmissionRequest(1, receipt.AcceptedSelectionHash);

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Equal(provider, model.ProviderBindingRevision);
        Assert.Equal("credential-v1", model.CredentialReference!.Version);

        var changedCard = receipt with
        {
            CostBinding = binding with
            {
                RuntimeRateCardPin = binding.RuntimeRateCardPin! with { ContentHash = new string('b', 64) }
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(changedCard, request, "tenant", "project", "run", selection));

        var changedCredential = receipt with
        {
            ModelSelection = model with
            {
                ProviderBindingRevision = provider with { CredentialVersion = "credential-v2" }
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(
                changedCredential, request, "tenant", "project", "run", selection));
    }

    [Fact]
    public void RevisionBackedTaskSelectionsRequireTheirOwnCardAdmission()
    {
        var primaryProvider = ByokProviderBinding();
        var primaryPin = RuntimeModelBindingsResolver.Pin(primaryProvider, "high");
        var taskCredentialIdentity = primaryProvider.CredentialIdentity! with
        {
            CredentialSlotId = Guid.NewGuid(),
            ProviderId = "task-provider",
        };
        var taskProvider = primaryProvider with
        {
            ProviderId = "task-provider",
            Deployment = "task-deployment",
            CredentialVersion = "credential-v2",
            CredentialIdentity = taskCredentialIdentity,
            CredentialReference = new("task-provider-key", "credential-v2"),
            ConfigurationHash = new string('e', 64),
        };
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
                new
                {
                    selection = ByokModel(taskProvider, taskPin),
                    connectionScope = "project",
                },
            },
        });
        var primaryBinding = ByokCostBinding(primaryProvider, primaryPin);
        var taskBinding = ByokCostBinding(taskProvider, taskPin);
        Assert.NotEqual(primaryBinding.RuntimeByokIdentity, taskBinding.RuntimeByokIdentity);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        var request = new RuntimeRunAdmissionRequest(
            1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = new RuntimeRunAdmissionReceipt(
            1,
            "tenant",
            "project",
            "run",
            request.AcceptedSelectionHash,
            model,
            primaryPin,
            primaryBinding,
            new(null, "USD", CostDisposition.Unpriced, primaryBinding.RateCard,
                "byok-token-quotes-unsupported"),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardContentHash = primaryProvider.RateCardPin!.ContentHash,
            RateCardAdmissions =
            [
                new(primaryPin, primaryProvider, primaryBinding),
                new(taskPin, taskProvider, taskBinding),
            ],
        };

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(
                receipt with { RateCardAdmissions = [receipt.RateCardAdmissions[0]] },
                request, "tenant", "project", "run", selection));
    }

    [Fact]
    public void ReadAllDeduplicatesThePrimaryProviderFromItsEffectiveTaskRoutes()
    {
        var provider = ByokProviderBinding();
        var pin = RuntimeModelBindingsResolver.Pin(provider, "high");
        var selection = JsonSerializer.SerializeToElement(new
        {
            projectId = "project",
            runId = "run",
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            projectConfiguration = new { },
            modelSelection = ByokModel(provider, pin),
            modelSelections = new[]
            {
                new { selection = ByokModel(provider, pin), connectionScope = "project" }
            }
        });

        var models = RuntimeAcceptedModelSelection.ReadAll(selection);

        Assert.Equal("provider", Assert.Single(models).Reference);
    }

    [Fact]
    public void RevisionBackedTaskAdmissionAllowsLegacyByokPrimarySelection()
    {
        var primaryProvider = ByokProviderBinding();
        var taskProvider = primaryProvider with
        {
            ProviderId = "task-provider",
            Deployment = "task-deployment",
            CredentialVersion = "credential-v2",
            CredentialIdentity = primaryProvider.CredentialIdentity! with { ProviderId = "task-provider" },
            CredentialReference = new("task-provider-key", "credential-v2"),
            ConfigurationHash = new string('e', 64),
        };
        var taskPin = RuntimeModelBindingsResolver.Pin(taskProvider, "high");
        var legacyPin = new RuntimeModelBindingPin(
            RuntimeModelBindingPin.CurrentContractVersion,
            "legacy-provider",
            "legacy-deployment",
            ModelSourceMode.Byok,
            "legacy-revision",
            new string('b', 64),
            "high")
        {
            ProviderType = "openai"
        };
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
                reference = "legacy-provider",
                sourceMode = "byok",
                credentialReference = new { id = "legacy-credential", version = "legacy-v1" },
                reasoningEffort = "high",
                modelBindingPin = legacyPin
            },
            modelSelections = new[]
            {
                new
                {
                    selection = ByokModel(taskProvider, taskPin),
                    connectionScope = "project",
                },
            },
        });
        var model = RuntimeAcceptedModelSelection.Read(selection);
        var taskBinding = ByokCostBinding(taskProvider, taskPin);
        var request = new RuntimeRunAdmissionRequest(
            1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = new RuntimeRunAdmissionReceipt(
            1,
            "tenant",
            "project",
            "run",
            request.AcceptedSelectionHash,
            model,
            legacyPin,
            null,
            new(null, "USD", CostDisposition.Unpriced, null, "byok-token-quotes-unsupported"),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardAdmissions = [new(taskPin, taskProvider, taskBinding)]
        };

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(
                receipt with
                {
                    Quote = receipt.Quote with { UnpricedReason = "model-rate-unavailable" }
                },
                request, "tenant", "project", "run", selection));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("hash")]
    [InlineData("connection")]
    [InlineData("reference")]
    [InlineData("model")]
    [InlineData("mode")]
    [InlineData("binding")]
    [InlineData("effort")]
    [InlineData("quote")]
    [InlineData("unit")]
    [InlineData("unpriced")]
    [InlineData("totals")]
    public void ChangedOrMalformedAdmissionCannotBecomeFreeRootAcceptance(string fault)
    {
        var selection = Selection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);
        receipt = fault switch
        {
            "scope" => receipt with { ProjectId = "foreign" },
            "hash" => receipt with { AcceptedSelectionHash = new string('a', 64) },
            "connection" => receipt with
            {
                ModelSelection = receipt.ModelSelection with { ConnectionId = Guid.NewGuid() }
            },
            "reference" => receipt with
            {
                ModelBindingPin = receipt.ModelBindingPin with { ModelSelectionReference = "concrete-model" }
            },
            "model" => receipt with { ModelBindingPin = receipt.ModelBindingPin with { ModelId = "unpriced-model" } },
            "mode" => receipt with { ModelBindingPin = receipt.ModelBindingPin with { SourceMode = ModelSourceMode.Byok } },
            "binding" => receipt with { CostBinding = null },
            "effort" => receipt with { ModelBindingPin = receipt.ModelBindingPin with { ReasoningEffort = "low" } },
            "quote" => receipt with { Quote = receipt.Quote with { Amount = null } },
            "unit" => receipt with { Quote = receipt.Quote with { Unit = "tokens" } },
            "unpriced" => receipt with { Quote = new(0, null, CostDisposition.Unpriced, null, "unavailable") },
            _ => receipt with { CopilotTotals = receipt.CopilotTotals with { IsFullyPriced = false } }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunAdmissionContract.ValidateReceipt(receipt, request, "tenant", "project", "run", selection));
    }

    [Theory]
    [InlineData("""{"modelSelection":{"reference":"accepted-reference","sourceMode":"unknown"}}""")]
    [InlineData("""{"modelSelection":{"reference":"accepted-reference","sourceMode":0}}""")]
    [InlineData("""{"modelSelection":{"reference":"accepted-reference","sourceMode":"byok","connectionId":"11111111-1111-1111-1111-111111111111"},"projectConfiguration":{}}""")]
    [InlineData("""{"modelSelection":{"reference":"accepted-reference","sourceMode":"hostedCopilot","connectionId":"11111111-1111-1111-1111-111111111111"},"projectConfiguration":{"modelSelection":{"reference":"different"}}}""")]
    public void SharedDecoderRejectsUnknownModesAndMismatchedConnectionAuthority(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeAcceptedModelSelection.Read(document.RootElement));
    }

    [Theory]
    [InlineData("""{"projectId":"project","runId":"run","projectRevision":"1"}""")]
    [InlineData("""{"projectId":"foreign","runId":"run"}""")]
    [InlineData("""{"projectId":"project","runId":"run","projectRevision":0}""")]
    public void InvalidSelectionScopeReturnsTheTypedFailure(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("runtime_run_admission_selection_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeRunAdmissionContract.ValidateSelectionScope(document.RootElement, "project", "run")).Code);
    }

    [Fact]
    public void ReadAllReturnsATypedFailureForNonObjectTaskSelections()
    {
        using var document = JsonDocument.Parse("""{"modelSelections":["invalid"]}""");

        Assert.Equal("runtime_accepted_model_selection_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeAcceptedModelSelection.ReadAll(document.RootElement)).Code);
    }

    private static JsonElement Selection(
        string modelReference = "accepted-reference",
        string reasoningEffort = "high",
        RuntimeModelBindingPin? modelBindingPin = null) =>
        JsonSerializer.SerializeToElement(new
    {
        projectId = "project", runId = "run", projectRevision = 1, projectConfigurationRevision = 1,
        platformRuntimeRevision = 1, contextRevision = "context-v1",
        projectConfiguration = new { },
        modelSelection = new
        {
            reference = modelReference, sourceMode = "hostedCopilot",
            connectionId = "11111111-1111-1111-1111-111111111111",
            reasoningEffort, modelBindingPin
        }
    });

    private static RuntimeRunAdmissionReceipt Receipt(JsonElement selection, string modelId = "gpt-6.1-sol")
    {
        var modelReference = selection.GetProperty("modelSelection").GetProperty("reference").GetString()!;
        var resolver = ModelResolver(modelReference, modelId);
        var card = PublishedCard();
        var binding = new CostBinding(SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost",
            "1.0.0", 1, "options-v1", "resource", 1,
            ImmutableHashSet.Create("cost.usage.price", "cost.work.quote"), card);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        return new RuntimeRunAdmissionReceipt(1, "tenant", "project", "run",
            RuntimeRunAdmissionContract.SelectionHash(selection),
            model, resolver.Pin(modelReference, ModelSourceMode.HostedCopilot, model.ReasoningEffort),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null),
            new("tenant", "project", "run", 0, true, [], []))
        {
            RateCardContentHash = card.ContentHash
        };
    }

    private static RuntimeModelBindingsResolver ModelResolver(
        string modelReference = "accepted-reference", string modelId = "concrete-model") =>
        new("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                [modelReference] = new(modelId, ModelSourceMode.HostedCopilot)
            });

    private static JsonElement ByokSelection(
        ModelProviderBindingRevision provider, RuntimeModelBindingPin modelPin) =>
        JsonSerializer.SerializeToElement(new
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
                reference = provider.ProviderId,
                sourceMode = "byok",
                credentialReference = new { id = "provider-credential", version = provider.CredentialVersion },
                credentialIdentity = provider.CredentialIdentity,
                reasoningEffort = "high",
                modelBindingPin = modelPin,
                providerBindingRevision = provider
            }
        });

    private static object ByokModel(
        ModelProviderBindingRevision provider, RuntimeModelBindingPin modelPin) =>
        new
        {
            reference = provider.ProviderId,
            sourceMode = "byok",
            credentialReference = new
            {
                id = provider.CredentialReference!.Id,
                version = provider.CredentialReference.Version,
            },
            credentialIdentity = provider.CredentialIdentity,
            reasoningEffort = "high",
            modelBindingPin = modelPin,
            providerBindingRevision = provider,
        };

    private static CostBinding ByokCostBinding(
        ModelProviderBindingRevision provider, RuntimeModelBindingPin modelPin)
    {
        var rateCardPin = provider.RateCardPin!;
        return new CostBinding(
            SdkMeterSources.ByokTokens,
            "events.runtime-rate-card",
            "1.0.0",
            1,
            provider.ConfigurationHash,
            rateCardPin.RateCardId,
            provider.ProviderRevision,
            ImmutableHashSet.Create(StringComparer.Ordinal, "cost.usage.price"),
            RuntimeRateCard(provider))
        {
            RuntimeRateCardPin = rateCardPin,
            RuntimeByokIdentity = RuntimeByokCostBindingIdentity.From(modelPin, provider),
        };
    }

    private static ModelProviderBindingRevision ByokProviderBinding()
    {
        var card = new CostRateCard(
            "manual-provider-card",
            "v1",
            SdkMeterSources.ByokTokens,
            "USD",
            1_000_000m,
            ImmutableDictionary<string, decimal>.Empty);
        var provenance = new ManualRuntimeRateCardReadProvenance(
            "https://pricing.example/rate-card", new DateOnly(2026, 1, 1));
        var rateCardPin = RuntimeRateCardReadContract.CreatePin(
            new(RuntimeRateCardReadContract.CurrentSchemaVersion, card, provenance));
        var identity = new ModelCredentialIdentity(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModelProviderBindingScope.Project,
            "project",
            "provider");
        return new(
            ModelProviderBindingRevision.CurrentContractVersion,
            "provider",
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
            new string('d', 64))
        {
            CredentialIdentity = identity,
            CredentialReference = new("provider-credential", "credential-v1"),
            RateCardPin = rateCardPin
        };
    }

    private static CostRateCard RuntimeRateCard(ModelProviderBindingRevision provider) =>
        new(
            provider.RateCardId!,
            provider.RateCardVersion!,
            SdkMeterSources.ByokTokens,
            "USD",
            1_000_000m,
            ImmutableDictionary<string, decimal>.Empty);

    /// <summary>Builds a test card with rate entries for both approved HostedCopilot models.</summary>
    private static CostRateCard PublishedCard() =>
        new("github-copilot-published", "2026-10-17",
            SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000m,
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
                .Add("gpt-6-luna", new(
                    new(0.1m, 0.01m, 0.125m, 0.5m),
                    new(0.2m, 0.02m, 0.25m, 0.75m)))
        };
}
