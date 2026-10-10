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

    [Theory]
    [InlineData("scope")]
    [InlineData("hash")]
    [InlineData("connection")]
    [InlineData("reference")]
    [InlineData("model")]
    [InlineData("mode")]
    [InlineData("binding")]
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

    private static JsonElement Selection() => JsonSerializer.SerializeToElement(new
    {
        projectId = "project", runId = "run", projectRevision = 1, projectConfigurationRevision = 1,
        platformRuntimeRevision = 1, contextRevision = "context-v1",
        projectConfiguration = new { },
        modelSelection = new
        {
            reference = "accepted-reference", sourceMode = "hostedCopilot",
            connectionId = "11111111-1111-1111-1111-111111111111"
        }
    });

    private static RuntimeRunAdmissionReceipt Receipt(JsonElement selection)
    {
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
        return new(1, "tenant", "project", "run", RuntimeRunAdmissionContract.SelectionHash(selection),
            RuntimeAcceptedModelSelection.Read(selection), resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null),
            new("tenant", "project", "run", 0, true, [], []));
    }
}
