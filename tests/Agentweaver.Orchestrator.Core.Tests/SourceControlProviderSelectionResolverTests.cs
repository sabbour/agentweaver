using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SourceControlProviderSelectionResolverTests
{
    private static readonly ImmutableHashSet<string> Advertised =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout,
            SourceControlCapabilities.PullRequestRead,
            SourceControlCapabilities.PullRequestWrite,
            SourceControlCapabilities.Merge);

    [Fact]
    public void ResolvesExactAcceptedCandidateAndPinsNegotiatedResource()
    {
        var required = ImmutableArray.Create(SourceControlCapabilities.RepositoryRead);
        var (catalog, resolver) = CreateCatalogAndResolver();
        var selection = SourceSelection("github", "1.0.0", "options-r4", required);

        var resolved = SourceControlProviderSelectionResolver.Resolve(
            Snapshot(selection), catalog, resolver);
        var negotiation = new ResourceNegotiation(
            new ProviderResourceRef(
                ProviderSeam.SourceControl, "github", "repo:123", 91),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                SourceControlCapabilities.RepositoryRead,
                SourceControlCapabilities.PullRequestWrite));

        var pinned = SourceControlProviderSelectionResolver.PinNegotiatedResource(
            resolver, resolved, "run-1", negotiation);

        Assert.Equal(ProviderSeam.SourceControl, pinned.Seam);
        Assert.Equal("github", pinned.ProviderId);
        Assert.Equal("options-r4", pinned.OptionsRevision);
        Assert.Equal("repo:123", pinned.Resource.ResourceId);
        Assert.Equal(91, pinned.Resource.Generation);
        Assert.Contains(SourceControlCapabilities.RepositoryRead, pinned.NegotiatedCapabilities);
    }

    [Fact]
    public void PinsExactRepositoryNegotiationWithAcceptedRunAndSecretReferences()
    {
        var required = ImmutableArray.Create(
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge);
        var (catalog, resolver) = CreateCatalogAndResolver();
        var selection = SourceControlProviderSelectionResolver.Resolve(
            Snapshot(SourceSelection("github", "1.0.0", "options-r4", required)),
            catalog,
            resolver);
        var acceptedRun = new SourceControlAcceptedRunBinding(
            "https://issuer.test",
            "actor-1",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            new string('A', 64),
            1,
            2,
            3,
            "context-4",
            5);
        var negotiation = new SourceControlRepositoryNegotiation(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repo:123", 91),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    SourceControlCapabilities.RepositoryRead,
                    SourceControlCapabilities.Merge)),
            123,
            "main",
            IsPrivate: true);

        var pin = SourceControlProviderSelectionResolver.PinNegotiatedRepository(
            resolver,
            selection,
            acceptedRun,
            "source-pin-1",
            negotiation,
            new SourceControlCredentialReference(
                new SecretRef("github-api", "version-7"),
                SourceControlSecretPurposes.Api),
            new SourceControlCredentialReference(
                new SecretRef("github-checkout", "version-3"),
                SourceControlSecretPurposes.Checkout),
            new SourceControlCredentialReference(
                new SecretRef("github-webhook", "version-2"),
                SourceControlSecretPurposes.Webhook),
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

        Assert.Equal(acceptedRun, pin.AcceptedRun);
        Assert.Equal(ProviderSeam.SourceControl, pin.ProviderBinding.Seam);
        Assert.Equal(acceptedRun.RunId, pin.ProviderBinding.RunId);
        Assert.Equal(91, pin.ProviderBinding.Resource.Generation);
        Assert.Equal(negotiation.Repository, pin.Repository);
        Assert.Equal(negotiation.ProviderRepositoryId, pin.ProviderRepositoryId);
        Assert.Equal("github-api", pin.ApiCredential!.Secret.Id);
        Assert.Equal("version-3", pin.CheckoutCredential!.Secret.Version);
        Assert.Equal("github-webhook", pin.WebhookCredential!.Secret.Id);
    }

    [Fact]
    public void DoesNotFallBackWhenAcceptedSelectionOmitsSourceControl()
    {
        var (catalog, resolver) = CreateCatalogAndResolver();
        var snapshot = Snapshot(
            new EffectiveProviderSelection(
                ProviderCardinality.Exclusive,
                ProviderSeam.Sandbox,
                [],
                MeterSource: null));

        var exception = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.Resolve(snapshot, catalog, resolver));

        Assert.Equal(SourceControlProviderSelectionFailure.Missing, exception.Failure);
    }

    [Fact]
    public void RejectsDuplicateOrNonExclusiveAcceptedSourceControlSelections()
    {
        var (catalog, resolver) = CreateCatalogAndResolver();
        var valid = SourceSelection("github", "1.0.0", "options-r4",
            [SourceControlCapabilities.RepositoryRead]);

        var duplicate = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.Resolve(
                Snapshot(valid, valid), catalog, resolver));
        Assert.Equal(SourceControlProviderSelectionFailure.Invalid, duplicate.Failure);

        var nonExclusive = new EffectiveProviderSelection(
            ProviderCardinality.OrderedComposite,
            ProviderSeam.SourceControl,
            [Candidate("github", "1.0.0", "options-r4",
                [SourceControlCapabilities.RepositoryRead])]);
        var cardinality = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.Resolve(
                Snapshot(nonExclusive), catalog, resolver));
        Assert.Equal(SourceControlProviderSelectionFailure.Invalid, cardinality.Failure);
    }

    [Fact]
    public void RejectsEffectiveMetadataThatNoLongerMatchesCatalog()
    {
        var (catalog, resolver) = CreateCatalogAndResolver();
        var selection = SourceSelection("github", "1.0.0", "stale-options",
            [SourceControlCapabilities.RepositoryRead]);

        var exception = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.Resolve(
                Snapshot(selection), catalog, resolver));

        Assert.Equal(SourceControlProviderSelectionFailure.Invalid, exception.Failure);
    }

    [Fact]
    public void RejectsNegotiatedResourceWithWrongProviderOrMissingRequiredCapability()
    {
        var required = ImmutableArray.Create(SourceControlCapabilities.RepositoryRead);
        var (catalog, resolver) = CreateCatalogAndResolver();
        var selection = SourceControlProviderSelectionResolver.Resolve(
            Snapshot(SourceSelection("github", "1.0.0", "options-r4", required)),
            catalog,
            resolver);
        var wrongProvider = new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.SourceControl, "other", "repo:123", 91),
            ImmutableHashSet.Create(StringComparer.Ordinal, SourceControlCapabilities.RepositoryRead));
        var wrong = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.PinNegotiatedResource(
                resolver, selection, "run-1", wrongProvider));
        Assert.Equal(SourceControlProviderSelectionFailure.Invalid, wrong.Failure);

        var missingCapability = new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repo:123", 91),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
        var unavailable = Assert.Throws<SourceControlProviderSelectionException>(() =>
            SourceControlProviderSelectionResolver.PinNegotiatedResource(
                resolver, selection, "run-1", missingCapability));
        Assert.Equal(SourceControlProviderSelectionFailure.Unavailable, unavailable.Failure);
    }

    private static (ProviderCatalog Catalog, ProviderResolver Resolver) CreateCatalogAndResolver()
    {
        var descriptor = new ProviderDescriptor(
            ProviderSeam.SourceControl,
            "github",
            new Version(1, 0, 0),
            1,
            ProviderHostingPattern.InProcess,
            Advertised);
        var registration = new ProviderRegistration(descriptor, true, "options-r4", 1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")],
            []).Value!;
        return (catalog, new ProviderResolver(catalog));
    }

    private static EffectiveProviderSelection SourceSelection(
        string providerId,
        string adapterVersion,
        string optionsRevision,
        ImmutableArray<string> requiredCapabilities) =>
        new(
            ProviderCardinality.Exclusive,
            ProviderSeam.SourceControl,
            [Candidate(providerId, adapterVersion, optionsRevision, requiredCapabilities)],
            MeterSource: null);

    private static EffectiveProviderCandidate Candidate(
        string providerId,
        string adapterVersion,
        string optionsRevision,
        ImmutableArray<string> requiredCapabilities) =>
        new(
            ProviderSeam.SourceControl,
            providerId,
            adapterVersion,
            1,
            optionsRevision,
            ProviderHostingPattern.InProcess,
            Advertised.Order(StringComparer.Ordinal).ToImmutableArray(),
            requiredCapabilities);

    private static JsonElement Snapshot(params EffectiveProviderSelection[] selections)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.SerializeToElement(new { providers = selections }, options);
    }
}
