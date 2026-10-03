using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class CompositeProviderTests
{
    private static readonly Version Version = new(0, 1, 0);
    private static ImmutableHashSet<string> Caps(params string[] values) =>
        values.ToImmutableHashSet(StringComparer.Ordinal);

    private static ProviderRegistration Entry(string id, ProviderSeam seam, bool enabled = true,
        int schema = 1, params string[] capabilities) =>
        new(new ProviderDescriptor(seam, id, Version, schema, ProviderHostingPattern.InProcess,
            Caps(capabilities)), enabled, $"revision-{id}", schema);

    private static ProviderResult<ProviderCatalog> Create(
        ProviderRegistration[] entries, ProviderOrderedSelection[]? ordered = null,
        ProviderOverridePermission[]? permitted = null, ProviderLayerSelection[]? layers = null) =>
        ProviderCatalog.Create(entries, [], permitted ?? [], ordered, layers);

    private static ProviderCatalog Catalog(
        ProviderRegistration[] entries, ProviderOrderedSelection[]? ordered = null,
        ProviderOverridePermission[]? permitted = null, ProviderLayerSelection[]? layers = null) =>
        Assert.IsType<ProviderCatalog>(Create(entries, ordered, permitted, layers).Value);

    private static OrderedProviderResolutionRequest Ordered(ProviderSeam seam,
        ImmutableArray<string>? project = null, Version? version = null,
        int schema = 1, params string[] required) =>
        new(seam, project, version ?? Version, schema, Caps(required));

    private static NetworkPolicyResolutionRequest Network(params string[] required) =>
        new(Version, 1, Caps(required), Caps());

    private static ProviderPinInput Input(ProviderSeam seam, string id, int generation = 7,
        params string[] capabilities) =>
        new($"resource-{id}", new(new ProviderResourceRef(seam, id, $"resource-{id}", generation),
            Caps(capabilities)));

    private static void Fails<T>(ProviderResult<T> result, ProviderErrorCode code) where T : class
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(code, Assert.IsType<ProviderError>(result.Error).Code);
    }

    [Theory]
    [InlineData(ProviderSeam.Guardrails)]
    [InlineData(ProviderSeam.Telemetry)]
    public void OrderedSetAllowsOnlyPermittedProjectEditsAndPinsExactOrder(ProviderSeam seam)
    {
        var original = new ProviderResolver(Catalog(
            [Entry("fixed-a", seam, capabilities: ["check"]), Entry("fixed-b", seam, capabilities: ["check"]),
                Entry("optional", seam, capabilities: ["check"]), Entry("added", seam, capabilities: ["check"])],
            [new(seam, ["fixed-a", "optional", "fixed-b"])],
            [new(seam, "optional"), new(seam, "added")]));
        var project = ImmutableArray.Create("added", "fixed-a", "fixed-b", "optional");
        var resolved = original.ResolveOrdered(Ordered(seam, project, required: ["check"]));
        Assert.Equal(project, resolved.Value!.Candidates.Select(c => c.ProviderId));
        var pinned = original.PinOrdered("run-1", resolved.Value,
            [Input(seam, "added", capabilities: ["check"]), Input(seam, "fixed-a", capabilities: ["check"]),
                Input(seam, "fixed-b", capabilities: ["check"]), Input(seam, "optional", capabilities: ["check"])]);
        Assert.Equal(project, pinned.Value!.Bindings.Select(b => b.ProviderId));
        Assert.Equal("revision-added", pinned.Value.Bindings[0].OptionsRevision);
        Assert.Equal(7, pinned.Value.Bindings[0].Resource.Generation);
        var changed = new ProviderResolver(Catalog([Entry("replacement", seam)],
            [new(seam, ["replacement"])]));
        Assert.Equal("replacement", changed.ResolveOrdered(Ordered(seam)).Value!.Candidates[0].ProviderId);
        Assert.Equal(project, pinned.Value.Bindings.Select(b => b.ProviderId));
        Assert.Equal(new[] { "fixed-a", "fixed-b" },
            original.ResolveOrdered(Ordered(seam, ["fixed-a", "fixed-b"])).Value!.Candidates.Select(c => c.ProviderId));
        Fails(original.ResolveOrdered(Ordered(seam, ["fixed-b", "fixed-a"])),
            ProviderErrorCode.OverrideNotPermitted);
        Fails(original.ResolveOrdered(Ordered(seam, ["optional", "fixed-b"])),
            ProviderErrorCode.OverrideNotPermitted);
    }

    [Fact]
    public void OrderedCatalogAndResolutionRejectInvalidInputs()
    {
        var seam = ProviderSeam.Guardrails;
        var entries = new[] { Entry("one", seam, capabilities: ["required"]),
            Entry("two", seam, enabled: false), Entry("foreign", ProviderSeam.Policy) };
        Fails(Create(entries, [new(seam, ["one", "one"])]), ProviderErrorCode.DuplicateSelection);
        Fails(Create(entries, [new(seam, ["unknown"])]), ProviderErrorCode.ProviderNotFound);
        Fails(Create(entries, [new(seam, ["foreign"])]), ProviderErrorCode.ProviderSeamMismatch);
        Fails(Create(entries, [new(seam, ["two"])]), ProviderErrorCode.ProviderDisabled);
        Fails(Create(entries, [new(ProviderSeam.Sandbox, ["one"])]),
            ProviderErrorCode.InvalidConfiguration);
        Fails(ProviderCatalog.Create(entries, [new(seam, "one")], []),
            ProviderErrorCode.InvalidConfiguration);
        var resolver = new ProviderResolver(Catalog(entries, [new(seam, ["one"])],
            [new(seam, "two")]));
        Fails(resolver.ResolveOrdered(Ordered(ProviderSeam.Sandbox)), ProviderErrorCode.InvalidConfiguration);
        Fails(resolver.ResolveOrdered(Ordered(seam, ["one", "one"])), ProviderErrorCode.DuplicateSelection);
        Fails(resolver.ResolveOrdered(Ordered(seam, ["one", "two"])), ProviderErrorCode.ProviderDisabled);
        Fails(resolver.ResolveOrdered(Ordered(seam, ["one", "foreign"])),
            ProviderErrorCode.OverrideNotPermitted);
        Fails(resolver.ResolveOrdered(Ordered(seam, version: new Version(2, 0))),
            ProviderErrorCode.AdapterVersionMismatch);
        Fails(resolver.ResolveOrdered(Ordered(seam, schema: 2)), ProviderErrorCode.OptionsSchemaMismatch);
        Fails(resolver.ResolveOrdered(Ordered(seam, required: ["missing"])),
            ProviderErrorCode.CapabilityUnavailable);
        Fails(new ProviderResolver(Catalog(entries)).ResolveOrdered(Ordered(seam)),
            ProviderErrorCode.MissingDefault);
        Fails(resolver.ResolveOrdered(Ordered(seam, default(ImmutableArray<string>))),
            ProviderErrorCode.InvalidConfiguration);
        Fails(new ProviderResolver(Catalog(entries, [new(seam, [])]))
            .ResolveOrdered(Ordered(seam, required: ["required"])), ProviderErrorCode.CapabilityUnavailable);
    }

    [Fact]
    public void OrderedPinRejectsMissingSwappedOrInvalidResources()
    {
        var seam = ProviderSeam.Telemetry;
        var resolver = new ProviderResolver(Catalog(
            [Entry("first", seam, capabilities: ["export"]), Entry("second", seam, capabilities: ["export"])],
            [new(seam, ["first", "second"])]));
        var result = resolver.ResolveOrdered(Ordered(seam, required: ["export"])).Value!;
        Fails(resolver.PinOrdered("run", result, [Input(seam, "first", capabilities: ["export"])]),
            ProviderErrorCode.InvalidNegotiation);
        Fails(resolver.PinOrdered("run", result,
            [Input(seam, "second", capabilities: ["export"]), Input(seam, "first", capabilities: ["export"])]),
            ProviderErrorCode.ResourceMismatch);
        Fails(resolver.PinOrdered("run", result,
            [Input(seam, "first", capabilities: ["export"]), Input(seam, "second", 0, "export")]),
            ProviderErrorCode.InvalidNegotiation);
        Fails(resolver.PinOrdered("run", result,
            [Input(seam, "first", capabilities: ["export"]), Input(seam, "second")]),
            ProviderErrorCode.CapabilityUnavailable);
    }

    [Fact]
    public void NetworkLayersRequireL3L4AndPlatformCompatibleSelection()
    {
        var entries = new[] { Entry("l3", ProviderSeam.NetworkPolicy, capabilities: ["cidr"]),
            Entry("l7", ProviderSeam.NetworkPolicy, capabilities: ["http"]),
            Entry("wrong", ProviderSeam.Sandbox), Entry("off", ProviderSeam.NetworkPolicy, false) };
        Fails(Create(entries, layers: [new(NetworkPolicyLayer.L7, "l7")]),
            ProviderErrorCode.MissingDefault);
        Fails(Create(entries, layers: [new(NetworkPolicyLayer.L3L4, "l3"),
            new(NetworkPolicyLayer.L3L4, "l7")]), ProviderErrorCode.DuplicateSelection);
        Fails(Create(entries, layers: [new(NetworkPolicyLayer.L3L4, "wrong")]),
            ProviderErrorCode.ProviderSeamMismatch);
        Fails(Create(entries, layers: [new(NetworkPolicyLayer.L3L4, "off")]),
            ProviderErrorCode.ProviderDisabled);
        Fails(Create(entries, layers: [new(NetworkPolicyLayer.L3L4, "unknown")]),
            ProviderErrorCode.ProviderNotFound);
        Fails(Create(entries, permitted: [new(ProviderSeam.NetworkPolicy, "l3")]),
            ProviderErrorCode.InvalidConfiguration);
        var resolver = new ProviderResolver(Catalog(entries, layers: [
            new(NetworkPolicyLayer.L3L4, "l3"), new(NetworkPolicyLayer.L7, "l7")]));
        var resolved = resolver.ResolveNetworkPolicy(Network("cidr"));
        Assert.Equal([NetworkPolicyLayer.L3L4, NetworkPolicyLayer.L7],
            resolved.Value!.Layers.Select(l => l.Layer));
        Assert.Equal(["l3", "l7"], resolved.Value.Layers.Select(l => l.Candidate.ProviderId));
        Fails(resolver.ResolveNetworkPolicy(new(Version, 1, Caps("missing"), Caps())),
            ProviderErrorCode.CapabilityUnavailable);
        Fails(resolver.ResolveNetworkPolicy(new(Version, 1, Caps(), Caps("missing"))),
            ProviderErrorCode.CapabilityUnavailable);
        Fails(resolver.ResolveNetworkPolicy(new(new Version(2, 0), 1, Caps(), Caps())),
            ProviderErrorCode.AdapterVersionMismatch);
        Fails(resolver.ResolveNetworkPolicy(new(Version, 2, Caps(), Caps())),
            ProviderErrorCode.OptionsSchemaMismatch);
        var l3Only = new ProviderResolver(Catalog(entries,
            layers: [new(NetworkPolicyLayer.L3L4, "l3")]));
        Assert.Single(l3Only.ResolveNetworkPolicy(Network()).Value!.Layers);
        Fails(l3Only.ResolveNetworkPolicy(new(Version, 1, Caps(), Caps("http"))),
            ProviderErrorCode.CapabilityUnavailable);
        Fails(new ProviderResolver(Catalog(entries)).ResolveNetworkPolicy(Network()),
            ProviderErrorCode.MissingDefault);
    }

    [Fact]
    public void NetworkPinRequiresConfirmedIntentGenerationAndMatchingLayerResources()
    {
        var seam = ProviderSeam.NetworkPolicy;
        var original = new ProviderResolver(Catalog(
            [Entry("l3", seam), Entry("l7", seam)],
            layers: [new(NetworkPolicyLayer.L3L4, "l3"), new(NetworkPolicyLayer.L7, "l7")]));
        var resolved = original.ResolveNetworkPolicy(Network()).Value!;
        var inputs = new[] { Input(seam, "l3"), Input(seam, "l7") };
        Fails(original.PinNetworkPolicy("run", resolved, inputs, 5, 4),
            ProviderErrorCode.InvalidNegotiation);
        Fails(original.PinNetworkPolicy("run", resolved, inputs, 0, 0),
            ProviderErrorCode.InvalidNegotiation);
        Fails(original.PinNetworkPolicy("run", resolved, [inputs[0]], 5, 5),
            ProviderErrorCode.InvalidNegotiation);
        Fails(original.PinNetworkPolicy("run", resolved, [inputs[1], inputs[0]], 5, 5),
            ProviderErrorCode.ResourceMismatch);
        Fails(original.PinNetworkPolicy("run", resolved, [inputs[0], Input(seam, "l7", 0)], 5, 5),
            ProviderErrorCode.InvalidNegotiation);
        var pinned = original.PinNetworkPolicy("run", resolved, inputs, 5, 5).Value!;
        Assert.Equal(5, pinned.AppliedIntentGeneration);
        Assert.Equal([NetworkPolicyLayer.L3L4, NetworkPolicyLayer.L7],
            pinned.Layers.Select(l => l.Layer));
        var updated = new ProviderResolver(Catalog([Entry("replacement", seam)],
            layers: [new(NetworkPolicyLayer.L3L4, "replacement")]));
        Assert.Equal("replacement",
            updated.ResolveNetworkPolicy(Network()).Value!.Layers[0].Candidate.ProviderId);
        Assert.Equal("l3", pinned.Layers[0].Binding.ProviderId);
        Assert.Equal("revision-l3", pinned.Layers[0].Binding.OptionsRevision);
        Assert.Equal(7, pinned.Layers[0].Binding.Resource.Generation);
    }
}
