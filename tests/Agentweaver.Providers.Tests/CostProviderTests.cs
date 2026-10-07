using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class CostProviderTests
{
    private static readonly Version Adapter = new(1, 0, 0);
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "cost.usage.price");

    private static ProviderRegistration Entry(
        string id, ProviderSeam seam = ProviderSeam.Cost, bool enabled = true, string revision = "options-v1") =>
        new(new ProviderDescriptor(seam, id, Adapter, 1, ProviderHostingPattern.InProcess, Capabilities),
            enabled, revision, 1);

    private static ProviderCatalog Catalog(
        ProviderRegistration[] entries,
        ProviderMeterSourceSelection[] sources)
    {
        var result = ProviderCatalog.Create(entries, [], [], meterSourceSelections: sources);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private static CostProviderResolutionRequest Request(string meterSource = "copilot.nano_aiu") =>
        new(meterSource, Adapter, 1, Capabilities);

    [Fact]
    public void ResolvesEnabledProviderIndependentlyForEachMeterSource()
    {
        var catalog = Catalog(
            [Entry("copilot-cost"), Entry("hosted-model-cost")],
            [new("copilot.nano_aiu", "copilot-cost"), new("hosted.tokens", "hosted-model-cost")]);
        var resolver = new ProviderResolver(catalog);

        var copilot = resolver.ResolveCost(Request());
        var hosted = resolver.ResolveCost(Request("hosted.tokens"));

        Assert.True(copilot.IsSuccess, copilot.Error?.Message);
        Assert.True(hosted.IsSuccess, hosted.Error?.Message);
        Assert.Equal("copilot-cost", copilot.Value!.Candidate.ProviderId);
        Assert.Equal("hosted-model-cost", hosted.Value!.Candidate.ProviderId);
        Assert.Equal("copilot.nano_aiu", copilot.Value.MeterSource);
        Assert.Equal("hosted.tokens", hosted.Value.MeterSource);
        Assert.True(catalog.TryGetMeterSource("copilot.nano_aiu", out var selected));
        Assert.Equal("copilot-cost", selected);
    }

    [Fact]
    public void ValidatesMeterSourceSelectionsWithoutChangingOtherCatalogCardinalities()
    {
        Assert.Equal(ProviderCardinality.KeyedByMeterSource,
            ProviderSeams.Cardinality(ProviderSeam.Cost));
        Assert.Equal(ProviderErrorCode.DuplicateSelection,
            ProviderCatalog.Create([Entry("cost")], [], [], meterSourceSelections:
                [new("copilot.nano_aiu", "cost"), new("copilot.nano_aiu", "cost")]).Error!.Code);
        Assert.Equal(ProviderErrorCode.ProviderSeamMismatch,
            ProviderCatalog.Create([Entry("not-cost", ProviderSeam.Storage)], [], [],
                meterSourceSelections: [new("copilot.nano_aiu", "not-cost")]).Error!.Code);
        Assert.Equal(ProviderErrorCode.ProviderDisabled,
            ProviderCatalog.Create([Entry("disabled", enabled: false)], [], [],
                meterSourceSelections: [new("copilot.nano_aiu", "disabled")]).Error!.Code);

        var resolver = new ProviderResolver(Catalog([Entry("cost")], []));
        var generic = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Cost, null, Adapter, 1, Capabilities));
        Assert.Equal(ProviderErrorCode.UnsupportedCardinality, generic.Error!.Code);
        Assert.Equal(ProviderErrorCode.MissingDefault, resolver.ResolveCost(Request()).Error!.Code);
    }

    [Fact]
    public void RequiresExactAdapterSchemaAndAdvertisedCapabilities()
    {
        var resolver = new ProviderResolver(Catalog(
            [Entry("copilot-cost")], [new("copilot.nano_aiu", "copilot-cost")]));
        Assert.Equal(ProviderErrorCode.AdapterVersionMismatch,
            resolver.ResolveCost(Request() with { RequiredAdapterVersion = new Version(2, 0, 0) }).Error!.Code);
        Assert.Equal(ProviderErrorCode.OptionsSchemaMismatch,
            resolver.ResolveCost(Request() with { RequiredOptionsSchemaVersion = 2 }).Error!.Code);
        Assert.Equal(ProviderErrorCode.CapabilityUnavailable,
            resolver.ResolveCost(Request() with
            {
                RequiredCapabilities = ImmutableHashSet.Create(StringComparer.Ordinal, "cost.unavailable"),
            }).Error!.Code);
        Assert.Equal("options-v1", resolver.ResolveCost(Request()).Value!.Candidate.OptionsRevision);
    }

    [Fact]
    public void RejectsInvalidRequestsAndAbsentMeterSources()
    {
        var catalog = Catalog(
            [Entry("copilot-cost")], [new("copilot.nano_aiu", "copilot-cost")]);
        var resolver = new ProviderResolver(catalog);
        Assert.Equal(ProviderErrorCode.MissingDefault,
            resolver.ResolveCost(Request("removed.source")).Error!.Code);
        Assert.Equal(ProviderErrorCode.InvalidConfiguration,
            resolver.ResolveCost(Request(" ")).Error!.Code);
        Assert.Equal(ProviderErrorCode.InvalidConfiguration,
            resolver.ResolveCost(Request() with { RequiredOptionsSchemaVersion = 0 }).Error!.Code);
    }

    [Fact]
    public void PinsCostThroughTheExistingNegotiationPath()
    {
        var resolver = new ProviderResolver(Catalog(
            [Entry("copilot-cost")], [new("copilot.nano_aiu", "copilot-cost")]));
        var resolution = resolver.ResolveCost(Request()).Value!;
        var negotiation = new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.Cost, "copilot-cost", "meter-resource", 7),
            Capabilities);

        var pinned = resolver.PinCost("run-1", resolution, "meter-resource", negotiation);

        Assert.True(pinned.IsSuccess, pinned.Error?.Message);
        Assert.Equal("copilot.nano_aiu", pinned.Value!.MeterSource);
        Assert.Equal("run-1", pinned.Value.ProviderBinding.RunId);
        Assert.Equal(7, pinned.Value.ProviderBinding.Resource.Generation);
        Assert.Equal(Capabilities, pinned.Value.ProviderBinding.NegotiatedCapabilities);
    }

    [Fact]
    public void RefusesToPinCandidateResolvedBeforeOptionsChanged()
    {
        var original = new ProviderResolver(Catalog(
            [Entry("copilot-cost")], [new("copilot.nano_aiu", "copilot-cost")]));
        var resolution = original.ResolveCost(Request()).Value!;
        var changed = new ProviderResolver(Catalog(
            [Entry("copilot-cost", revision: "options-v2")],
            [new("copilot.nano_aiu", "copilot-cost")]));

        var pinned = changed.PinCost(
            "run-1", resolution, "meter-resource",
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.Cost, "copilot-cost", "meter-resource", 1),
                Capabilities));

        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch, pinned.Error!.Code);
    }

    [Fact]
    public void VerificationRejectsMissingOrChangedPinnedProviderIdentity()
    {
        var resolver = new ProviderResolver(Catalog(
            [Entry("copilot-cost")], [new("copilot.nano_aiu", "copilot-cost")]));

        Assert.True(resolver.VerifyCost(Request(), Binding("copilot-cost")).IsSuccess);
        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch,
            resolver.VerifyCost(Request(), Binding("different-cost-provider")).Error!.Code);
        Assert.Equal(ProviderErrorCode.ProviderNotFound,
            resolver.VerifyCost(Request("removed.source"),
                Binding("copilot-cost", source: "removed.source")).Error!.Code);
    }

    private static CostBinding Binding(string providerId, string source = "copilot.nano_aiu") =>
        new(
            source, providerId, Adapter.ToString(), 1, "options-v1", "meter-resource", 7,
            Capabilities,
            new CostRateCard("copilot-rate", "v1", source, "AIC", 1_000_000_000m,
                ImmutableDictionary<string, decimal>.Empty));
}
