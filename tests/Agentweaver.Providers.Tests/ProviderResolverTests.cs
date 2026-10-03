using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class ProviderResolverTests
{
    private static readonly Version Adapter = new(0, 1, 0);

    private static ImmutableHashSet<string> Caps(params string[] values) =>
        values.ToImmutableHashSet(StringComparer.Ordinal);

    private static ProviderRegistration Entry(string id, ProviderSeam seam = ProviderSeam.Sandbox,
        bool enabled = true, string revision = "options-v1", int schema = 1,
        params string[] capabilities) =>
        new(new ProviderDescriptor(seam, id, Adapter, schema, ProviderHostingPattern.KubernetesController,
            Caps(capabilities)), enabled, revision, schema);

    private static ProviderCatalog Catalog(
        ProviderRegistration[]? entries = null, ProviderSelection[]? defaults = null,
        ProviderOverridePermission[]? allowed = null) =>
        Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            entries ?? [Entry("primary"), Entry("alternate")],
            defaults ?? [new(ProviderSeam.Sandbox, "primary")],
            allowed ?? [new(ProviderSeam.Sandbox, "alternate")]).Value);

    private static ProviderResolutionRequest Request(ProviderSeam seam = ProviderSeam.Sandbox,
        string? projectOverride = null, Version? adapter = null, int schema = 1,
        params string[] required) =>
        new(seam, projectOverride, adapter ?? Adapter, schema, Caps(required));

    private static void Fails<T>(ProviderResult<T> result, ProviderErrorCode code) where T : class
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(code, Assert.IsType<ProviderError>(result.Error).Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error!.Message));
    }

    [Fact]
    public void MapsAllFifteenApprovedSeamsAndCardinalities()
    {
        Assert.Equal(15, Enum.GetValues<ProviderSeam>().Length);
        Assert.Equal(6, Enum.GetValues<ProviderSeam>()
            .Count(s => ProviderSeams.Cardinality(s) == ProviderCardinality.Exclusive));
        Assert.Equal(4, Enum.GetValues<ProviderSeam>()
            .Count(s => ProviderSeams.Cardinality(s) == ProviderCardinality.PlatformSingleton));
        Assert.Equal(2, Enum.GetValues<ProviderSeam>()
            .Count(s => ProviderSeams.Cardinality(s) == ProviderCardinality.OrderedComposite));
        Assert.Equal(ProviderCardinality.Layered, ProviderSeams.Cardinality(ProviderSeam.NetworkPolicy));
        Assert.Equal(ProviderCardinality.KeyedByMeterSource, ProviderSeams.Cardinality(ProviderSeam.Cost));
        Assert.Equal(ProviderCardinality.PerApplication, ProviderSeams.Cardinality(ProviderSeam.ApplicationHosting));
    }

    [Fact]
    public void SelectsPlatformDefaultThenPermittedProjectOverride()
    {
        var resolver = new ProviderResolver(Catalog());
        Assert.Equal("primary", resolver.Resolve(Request()).Value!.Candidate!.ProviderId);
        Assert.Equal("alternate", resolver.Resolve(Request(projectOverride: "alternate")).Value!.Candidate!.ProviderId);
    }

    [Fact]
    public void RejectsUnauthorizedOrSingletonOverride()
    {
        var resolver = new ProviderResolver(Catalog(allowed: []));
        Fails(resolver.Resolve(Request(projectOverride: "alternate")), ProviderErrorCode.OverrideNotPermitted);
        var policy = new ProviderResolver(Catalog(
            entries: [Entry("policy", ProviderSeam.Policy)],
            defaults: [new(ProviderSeam.Policy, "policy")], allowed: []));
        Assert.Equal("policy", policy.Resolve(Request(ProviderSeam.Policy)).Value!.Candidate!.ProviderId);
        Fails(policy.Resolve(Request(ProviderSeam.Policy, "policy")), ProviderErrorCode.OverrideNotPermitted);
        var objects = new ProviderResolver(Catalog(
            entries: [Entry("blob", ProviderSeam.ObjectStore)],
            defaults: [new(ProviderSeam.ObjectStore, "blob")], allowed: []));
        Assert.Equal("blob", objects.Resolve(Request(ProviderSeam.ObjectStore)).Value!.Candidate!.ProviderId);
        Fails(objects.Resolve(Request(ProviderSeam.ObjectStore, "blob")), ProviderErrorCode.OverrideNotPermitted);
    }

    [Fact]
    public void RejectsDisabledMissingOrMismatchedProvider()
    {
        var disabled = new ProviderResolver(Catalog(
            entries: [Entry("disabled", enabled: false)],
            defaults: [new(ProviderSeam.Sandbox, "disabled")], allowed: []));
        Fails(disabled.Resolve(Request()), ProviderErrorCode.ProviderDisabled);
        Fails(new ProviderResolver(Catalog(defaults: [])).Resolve(Request()), ProviderErrorCode.MissingDefault);
        Fails(ProviderCatalog.Create([Entry("storage", ProviderSeam.Storage)],
            [new(ProviderSeam.Sandbox, "storage")], []), ProviderErrorCode.ProviderSeamMismatch);
        Fails(ProviderCatalog.Create([Entry("primary")],
            [new(ProviderSeam.Sandbox, "unknown")], []), ProviderErrorCode.ProviderNotFound);
    }

    [Fact]
    public void RejectsDuplicateDescriptorsAndInvalidOptionsRegistration()
    {
        Fails(ProviderCatalog.Create([Entry("same"), Entry("same", ProviderSeam.Storage)], [], []),
            ProviderErrorCode.DuplicateProviderId);
        Fails(ProviderCatalog.Create([Entry("primary")],
            [new(ProviderSeam.Sandbox, "primary"), new(ProviderSeam.Sandbox, "primary")], []),
            ProviderErrorCode.DuplicateDefault);
        var inconsistent = Entry("primary") with { OptionsSchemaVersion = 2 };
        Fails(ProviderCatalog.Create([inconsistent], [], []), ProviderErrorCode.OptionsSchemaMismatch);
    }

    [Fact]
    public void RequiresExactAdapterAndOptionsSchemaAndAdvertisedCapabilities()
    {
        var resolver = new ProviderResolver(Catalog(
            entries: [Entry("primary", capabilities: ["volume.attach"])], allowed: []));
        Fails(resolver.Resolve(Request(adapter: new Version(0, 2, 0))),
            ProviderErrorCode.AdapterVersionMismatch);
        Fails(resolver.Resolve(Request(schema: 2)), ProviderErrorCode.OptionsSchemaMismatch);
        Fails(resolver.Resolve(Request(required: ["snapshot.capture"])), ProviderErrorCode.CapabilityUnavailable);
        Assert.True(resolver.Resolve(Request(required: ["volume.attach"])).IsSuccess);
    }

    [Theory]
    [InlineData(ProviderSeam.Guardrails)]
    [InlineData(ProviderSeam.Telemetry)]
    [InlineData(ProviderSeam.NetworkPolicy)]
    [InlineData(ProviderSeam.Cost)]
    [InlineData(ProviderSeam.ApplicationHosting)]
    public void ExplicitlyRejectsUnsupportedResolutionCardinalities(ProviderSeam seam)
    {
        Fails(new ProviderResolver(Catalog()).Resolve(Request(seam)),
            ProviderErrorCode.UnsupportedCardinality);
    }

    [Fact]
    public void SnapshotNoneCannotClaimCapabilitiesAndIsNotPinnable()
    {
        var resolver = new ProviderResolver(Catalog(defaults: []));
        var none = resolver.Resolve(Request(ProviderSeam.Snapshots));
        Assert.True(none.IsSuccess);
        Assert.True(none.Value!.IsNone);
        Assert.Null(none.Value.Candidate);
        var pinnedNone = resolver.PinSnapshotNone("run-1", none.Value);
        Assert.True(pinnedNone.IsSuccess);
        Assert.Equal(ProviderSeam.Snapshots, pinnedNone.Value!.Seam);
        Assert.Empty(pinnedNone.Value.NegotiatedCapabilities);
        Fails(resolver.PinSnapshotNone("run-1",
            new ProviderResolver(Catalog()).Resolve(Request()).Value!),
            ProviderErrorCode.InvalidConfiguration);
        Fails(resolver.Resolve(Request(ProviderSeam.Snapshots, required: [ProviderCapabilities.SnapshotCapture])),
            ProviderErrorCode.CapabilityUnavailable);
    }

    [Fact]
    public void NegotiationChecksRequiredSubsetAndProviderAndResourceIdentity()
    {
        var resolver = new ProviderResolver(Catalog(entries: [
            Entry("primary", capabilities: ["volume.attach", "snapshot.capture"])], allowed: []));
        var candidate = resolver.Resolve(Request(required: ["volume.attach"])).Value!.Candidate!;
        ResourceNegotiation Negotiated(string provider, string id, params string[] caps) =>
            new(new ProviderResourceRef(ProviderSeam.Sandbox, provider, id, 7), Caps(caps));

        Fails(resolver.Pin("run-1", candidate, "env-1", Negotiated("alternate", "env-1", "volume.attach")),
            ProviderErrorCode.ResourceMismatch);
        Fails(resolver.Pin("run-1", candidate, "env-1", Negotiated("primary", "env-2", "volume.attach")),
            ProviderErrorCode.ResourceMismatch);
        Fails(resolver.Pin("run-1", candidate, "env-1", Negotiated("primary", "env-1", "snapshot.capture")),
            ProviderErrorCode.CapabilityUnavailable);
        Fails(resolver.Pin("run-1", candidate, "env-1", Negotiated("primary", "env-1", "volume.attach", "unknown")),
            ProviderErrorCode.InvalidNegotiation);

        var pin = resolver.Pin("run-1", candidate, "env-1", Negotiated("primary", "env-1", "volume.attach"));
        Assert.True(pin.IsSuccess);
        Assert.Equal(7, pin.Value!.Resource.Generation);
        Assert.Equal(Caps("volume.attach"), pin.Value.NegotiatedCapabilities);
        Assert.DoesNotContain("snapshot.capture", pin.Value.NegotiatedCapabilities);
        Assert.Equal("options-v1", pin.Value.OptionsRevision);
    }

    [Fact]
    public void PinnedBindingStaysStableAfterCatalogAndDefaultChange()
    {
        var original = new ProviderResolver(Catalog(entries: [Entry("primary", capabilities: ["required"])], allowed: []));
        var candidate = original.Resolve(Request(required: ["required"])).Value!.Candidate!;
        var binding = original.Pin("run-1", candidate, "env-1",
            new ResourceNegotiation(new ProviderResourceRef(ProviderSeam.Sandbox, "primary", "env-1", 1),
                Caps("required"))).Value!;
        var updated = new ProviderResolver(Catalog(
            entries: [Entry("alternate", revision: "options-v2")],
            defaults: [new(ProviderSeam.Sandbox, "alternate")], allowed: []));
        Assert.Equal("alternate", updated.Resolve(Request()).Value!.Candidate!.ProviderId);
        Assert.Equal("primary", binding.ProviderId);
        Assert.Equal("options-v1", binding.OptionsRevision);
        Assert.Equal("env-1", binding.Resource.ResourceId);
        Assert.Equal(Caps("required"), binding.NegotiatedCapabilities);
    }

    [Fact]
    public void PinnedSnapshotNoneSurvivesFutureSnapshotProviderDefault()
    {
        var before = new ProviderResolver(Catalog(defaults: []));
        var pinned = before.PinSnapshotNone("run-1",
            before.Resolve(Request(ProviderSeam.Snapshots)).Value!).Value!;
        var after = new ProviderResolver(Catalog(
            entries: [Entry("snapshots", ProviderSeam.Snapshots, capabilities: [
                ProviderCapabilities.SnapshotCapture])],
            defaults: [new(ProviderSeam.Snapshots, "snapshots")], allowed: []));
        Assert.Equal("snapshots", after.Resolve(Request(ProviderSeam.Snapshots)).Value!.Candidate!.ProviderId);
        Assert.Equal("run-1", pinned.RunId);
        Assert.Empty(pinned.NegotiatedCapabilities);
    }
}
