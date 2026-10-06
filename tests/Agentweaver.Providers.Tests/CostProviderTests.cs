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
}
