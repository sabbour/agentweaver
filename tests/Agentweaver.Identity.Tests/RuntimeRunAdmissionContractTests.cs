using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeRunAdmissionContractTests
{
    [Fact]
    public void AdmissionBindsTheRealSelectionReferenceConnectionAndConcretePricedModelWithoutSdkFacts()
    {
        var selection = Selection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Equal("accepted-reference", receipt.ModelSelection.Reference);
        Assert.Equal("concrete-model", receipt.ModelBindingPin.ModelId);
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

    [Fact]
    public void AdmissionPreservesTheAcceptedReasoningEffortInTheModelPin()
    {
        var selection = Selection(reasoningEffort: "low");
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection);

        Assert.Equal("low", receipt.ModelSelection.ReasoningEffort);
        Assert.Equal("low", receipt.ModelBindingPin.ReasoningEffort);
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
        var receipt = Receipt(selection);
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

    [Theory]
    [InlineData("reasoning", "gpt-6.1-sol")]
    [InlineData("coding", "gpt-6-luna")]
    public void AdmissionAcceptsBothApprovedHostedModelBindingsWithPresentRateEntries(
        string modelReference, string modelId)
    {
        var multiplier = modelReference == "reasoning" ? 1m : 3m;
        var selection = Selection(modelReference);
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var receipt = Receipt(selection, modelId, multiplier);

        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant", "project", "run", selection));
        Assert.Equal(modelId, receipt.ModelBindingPin.ModelId);
        Assert.Equal(multiplier, receipt.CostBinding!.RateCard.ModelMultipliers[modelId]);
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
    public void ChangedOrUnpricedAdmissionCannotBecomeFreeRootAcceptance(string fault)
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
            "unpriced" => receipt with { Quote = new(null, null, CostDisposition.Unpriced, null, "unavailable") },
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

    private static RuntimeRunAdmissionReceipt Receipt(
        JsonElement selection,
        string modelId = "concrete-model",
        decimal multiplier = 2.5m)
    {
        var modelReference = selection.GetProperty("modelSelection").GetProperty("reference").GetString()!;
        var resolver = ModelResolver(modelReference, modelId);
        var card = new CostRateCard("card", "1", SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000,
            ImmutableDictionary<string, decimal>.Empty.Add(modelId, multiplier));
        var binding = new CostBinding(SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost",
            "1.0.0", 1, "options-v1", "resource", 1,
            ImmutableHashSet.Create("cost.usage.price", "cost.work.quote"), card);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        return new(1, "tenant", "project", "run", RuntimeRunAdmissionContract.SelectionHash(selection),
            model, resolver.Pin(modelReference, ModelSourceMode.HostedCopilot, model.ReasoningEffort),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null),
            new("tenant", "project", "run", 0, true, [], []));
    }

    private static RuntimeModelBindingsResolver ModelResolver(
        string modelReference = "accepted-reference", string modelId = "concrete-model") =>
        new("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                [modelReference] = new(modelId, ModelSourceMode.HostedCopilot)
            });
}
