using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SourceControlProjectConfigurationResolverTests
{
    [Fact]
    public void ReadsOnlyExactRepositoryAndSecretReferencesFromAcceptedSnapshot()
    {
        using var document = JsonDocument.Parse("""
            {
              "projectId": "project-1",
              "runId": "run-1",
              "projectConfiguration": {
                "modelSelection": { "reference": "model-1" },
                "sourceControl": {
                  "repository": { "owner": "octo", "name": "repo" },
                  "apiSecretReference": { "id": "github-api", "version": "v3" },
                  "checkoutSecretReference": { "id": "github-checkout", "version": "v2" },
                  "webhookSecretReference": { "id": "github-webhook", "version": "v1" }
                }
              }
            }
            """);

        var settings = SourceControlProjectConfigurationResolver.Resolve(document.RootElement);

        Assert.Equal("octo/repo", settings.Repository.FullName);
        Assert.Equal("github-api", settings.ApiSecretReference!.Id);
        Assert.Equal("v3", settings.ApiSecretReference!.Version);
        Assert.Equal("github-checkout", settings.CheckoutSecretReference!.Id);
        Assert.Equal("v2", settings.CheckoutSecretReference.Version);
        Assert.Equal("github-webhook", settings.WebhookSecretReference!.Id);
        Assert.Equal("v1", settings.WebhookSecretReference.Version);
    }

    [Theory]
    [InlineData("""{"projectConfiguration":{"modelSelection":{"reference":"model-1"}}}""")]
    [InlineData("""{"projectConfiguration":{"sourceControl":null}}""")]
    [InlineData("""{"providers":[]}""")]
    public void MissingLegacySourceControlSettingsAreUnavailableWithoutFallback(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);

        var exception = Assert.Throws<SourceControlProjectConfigurationException>(() =>
            SourceControlProjectConfigurationResolver.Resolve(document.RootElement));

        Assert.Equal(SourceControlProjectConfigurationFailure.Missing, exception.Failure);
    }

    [Theory]
    [InlineData("""{"projectConfiguration":{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"apiSecretReference":{"id":"secret","version":"v1"},"token":"never"}}}""")]
    [InlineData("""{"projectConfiguration":{"sourceControl":{"repository":{"owner":"octo"},"apiSecretReference":{"id":"secret","version":"v1"}}}}""")]
    [InlineData("""{"projectConfiguration":{"sourceControl":"octo/repo"}}""")]
    [InlineData("""{"projectConfiguration":{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","identityRepositorySelectionCode":"never"}}}""")]
    [InlineData("""{"projectConfiguration":{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","selectionCode":"never"}}}""")]
    public void RejectsMalformedOrUnknownSourceControlSettings(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);

        var exception = Assert.Throws<SourceControlProjectConfigurationException>(() =>
            SourceControlProjectConfigurationResolver.Resolve(document.RootElement));

        Assert.Equal(SourceControlProjectConfigurationFailure.Invalid, exception.Failure);
    }

    [Fact]
    public void OptionalSecretReferencesAndDerivedRepositoryNameAreOmittedFromJson()
    {
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        var json = JsonSerializer.Serialize(settings, options);

        Assert.Contains("\"apiSecretReference\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("checkoutSecretReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("webhookSecretReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fullName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NegotiatedPinIsBoundToExactAcceptedSnapshotRepositoryAndPurposeReferences()
    {
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"),
            new SecretRef("github-checkout", "v2"),
            new SecretRef("github-webhook", "v1"));
        var snapshot = AcceptedSnapshot(settings);
        var acceptedRun = AcceptedRun(snapshot);
        var (catalog, resolver) = CreateCatalogAndResolver();
        var negotiation = new SourceControlRepositoryNegotiation(
            settings.Repository,
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repo:123", 9),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    SourceControlCapabilities.RepositoryRead,
                    SourceControlCapabilities.Merge)),
            123,
            "main",
            IsPrivate: true);

        var pin = SourceControlProjectConfigurationResolver.PinNegotiatedRepository(
            snapshot,
            catalog,
            resolver,
            acceptedRun,
            "pin-1",
            negotiation,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

        Assert.Equal(acceptedRun, pin.AcceptedRun);
        Assert.Equal(settings.Repository, pin.Repository);
        Assert.Equal("github-api", pin.ApiCredential!.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Api, pin.ApiCredential.Purpose);
        Assert.Equal("github-checkout", pin.CheckoutCredential!.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Checkout, pin.CheckoutCredential.Purpose);
        Assert.Equal("github-webhook", pin.WebhookCredential!.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Webhook, pin.WebhookCredential.Purpose);
    }

    [Fact]
    public void NegotiatedPinRejectsSnapshotBindingOrPhysicalRepositoryMismatch()
    {
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"),
            new SecretRef("github-checkout", "v2"));
        var snapshot = AcceptedSnapshot(settings);
        var acceptedRun = AcceptedRun(snapshot, new string('B', 64));
        var (catalog, resolver) = CreateCatalogAndResolver();
        var negotiation = Negotiation(new SourceControlRepositoryIdentity("octo", "repo"));

        var stale = Assert.Throws<SourceControlProjectConfigurationException>(() =>
            SourceControlProjectConfigurationResolver.PinNegotiatedRepository(
                snapshot, catalog, resolver, acceptedRun, "pin-1", negotiation,
                DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Equal(SourceControlProjectConfigurationFailure.Invalid, stale.Failure);

        acceptedRun = AcceptedRun(snapshot);
        var mismatchedRepository = Assert.Throws<SourceControlProjectConfigurationException>(() =>
            SourceControlProjectConfigurationResolver.PinNegotiatedRepository(
                snapshot,
                catalog,
                resolver,
                acceptedRun,
                "pin-1",
                Negotiation(new SourceControlRepositoryIdentity("octo", "another")),
                DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Equal(SourceControlProjectConfigurationFailure.Invalid, mismatchedRepository.Failure);
    }

    [Fact]
    public void NegotiatedGitHubAppPinUsesExactIdentityConnectionAndNoApiOrCheckoutSecret()
    {
        var selectionCode = new string('a', 64);
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            apiSecretReference: null,
            webhookSecretReference: new SecretRef("github-webhook", "v1"),
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "github-connection-1");
        var snapshot = AcceptedSnapshot(settings, includeIssueWrite: true);
        var acceptedRun = AcceptedRun(snapshot);
        var (catalog, resolver) = CreateCatalogAndResolver(includeIssueWrite: true);
        var negotiation = Negotiation(settings.Repository, includeIssueWrite: true);
        var selectionHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(selectionCode)));
        var mismatchedBinding = new SourceControlGitHubAppBinding(
            "another-connection",
            4,
            12345,
            new string('A', 64),
            selectionHash);

        var mismatch = Assert.Throws<SourceControlProjectConfigurationException>(() =>
            SourceControlProjectConfigurationResolver.PinNegotiatedGitHubAppRepository(
                snapshot,
                catalog,
                resolver,
                acceptedRun,
                "pin-1",
                selectionCode,
                negotiation,
                mismatchedBinding,
                DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Equal(SourceControlProjectConfigurationFailure.Invalid, mismatch.Failure);

        var binding = new SourceControlGitHubAppBinding(
            "github-connection-1",
            4,
            12345,
            new string('A', 64),
            selectionHash,
            issueWriteGranted: true);
        var pin = SourceControlProjectConfigurationResolver.PinNegotiatedGitHubAppRepository(
            snapshot,
            catalog,
            resolver,
            acceptedRun,
            "pin-1",
            selectionCode,
            negotiation,
            binding,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

        Assert.Equal(acceptedRun, pin.AcceptedRun);
        Assert.Equal(settings.Repository, pin.Repository);
        Assert.Null(pin.ApiCredential);
        Assert.Null(pin.CheckoutCredential);
        Assert.Equal(binding, pin.GitHubAppBinding);
        Assert.Equal("github-connection-1", pin.GitHubAppBinding!.IdentityConnectionId);
        Assert.Equal(12345, pin.GitHubAppBinding.InstallationId);
        Assert.Equal(new string('a', 64), pin.GitHubAppBinding.PermissionDigest);
        Assert.Equal(selectionHash, pin.GitHubAppBinding.IdentityRepositorySelectionHash);
        Assert.Equal("github-webhook", pin.WebhookCredential!.Secret.Id);
        Assert.Contains(
            SourceControlCapabilities.IssueWrite,
            pin.ProviderBinding.NegotiatedCapabilities);
    }

    [Fact]
    public void PrivateRepositoryCannotBePinnedWithoutCheckoutReference()
    {
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"));
        var snapshot = AcceptedSnapshot(settings);
        var (catalog, resolver) = CreateCatalogAndResolver();

        Assert.Throws<ArgumentException>(() =>
            SourceControlProjectConfigurationResolver.PinNegotiatedRepository(
                snapshot,
                catalog,
                resolver,
                AcceptedRun(snapshot),
                "pin-1",
                Negotiation(settings.Repository),
                DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
    }

    private static JsonElement AcceptedSnapshot(
        SourceControlProjectSettings settings,
        bool includeIssueWrite = false)
    {
        var capabilities = new List<string>
        {
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge
        };
        if (includeIssueWrite)
            capabilities.Add(SourceControlCapabilities.IssueWrite);
        var requiredCapabilities = new List<string>
        {
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge
        };
        if (includeIssueWrite)
            requiredCapabilities.Add(SourceControlCapabilities.IssueWrite);
        var sourceSelection = new EffectiveProviderSelection(
            ProviderCardinality.Exclusive,
            ProviderSeam.SourceControl,
            [
                new EffectiveProviderCandidate(
                    ProviderSeam.SourceControl,
                    "github",
                    "1.0.0",
                    1,
                    "options-r1",
                    ProviderHostingPattern.InProcess,
                    capabilities.ToImmutableArray(),
                    requiredCapabilities.ToImmutableArray())
            ]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.SerializeToElement(new
        {
            ProjectId = "project-1",
            RunId = "run-1",
            ProjectRevision = 1,
            ProjectConfigurationRevision = 2,
            PlatformRuntimeRevision = 3,
            ContextRevision = "context-4",
            Providers = new[] { sourceSelection },
            ProjectConfiguration = new
            {
                ModelSelection = new { Reference = "model-1" },
                SourceControl = settings
            }
        }, options);
    }

    private static SourceControlAcceptedRunBinding AcceptedRun(
        JsonElement snapshot,
        string? acceptedSelectionHash = null) =>
        new(
            "https://issuer.test",
            "actor-1",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            acceptedSelectionHash ?? Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(snapshot.GetRawText()))),
            1,
            2,
            3,
            "context-4",
            5);

    private static SourceControlRepositoryNegotiation Negotiation(
        SourceControlRepositoryIdentity repository,
        bool includeIssueWrite = false)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        capabilities.Add(SourceControlCapabilities.RepositoryRead);
        capabilities.Add(SourceControlCapabilities.Merge);
        if (includeIssueWrite)
            capabilities.Add(SourceControlCapabilities.IssueWrite);
        return new SourceControlRepositoryNegotiation(
            repository,
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repo:123", 9),
                capabilities.ToImmutable()),
            123,
            "main",
            IsPrivate: true);
    }

    private static (ProviderCatalog Catalog, ProviderResolver Resolver) CreateCatalogAndResolver(
        bool includeIssueWrite = false)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        capabilities.Add(SourceControlCapabilities.RepositoryRead);
        capabilities.Add(SourceControlCapabilities.Merge);
        if (includeIssueWrite)
            capabilities.Add(SourceControlCapabilities.IssueWrite);
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.SourceControl,
                "github",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.InProcess,
                capabilities.ToImmutable()),
            true,
            "options-r1",
            1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")],
            []).Value!;
        return (catalog, new ProviderResolver(catalog));
    }
}
