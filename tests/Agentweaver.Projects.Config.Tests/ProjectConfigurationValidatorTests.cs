using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectConfigurationValidatorTests
{
    private static PlatformRuntimeDefaults PlatformDefaults() => new()
    {
        EgressBaseline = [Fqdn("API.Example.com.")],
        RunLimits = new CopilotRunLimits
        {
            MaxModelTurns = 12,
            MaxToolCalls = 100,
            MaxChildren = 4,
            MaxConcurrentChildren = 2,
            MaxWallTimeSeconds = 3600,
            MaxPromptTokens = 20000,
        },
    };

    private static NetworkEgressRule Fqdn(string host, int port = 443) =>
        new(NetworkEgressPurpose.ModelEndpoint, NetworkEgressDestinationKind.Fqdn,
            host, port, EgressProtocol.Tcp);

    [Fact]
    public void NormalizesEgressAndAllowsOnlySubsetWithRequiredDestinations()
    {
        var baseline = ProjectConfigurationValidator.Validate(PlatformDefaults()).EgressBaseline;
        Assert.Equal("api.example.com", Assert.Single(baseline).Destination);

        var narrowed = ProjectConfigurationValidator.ResolveEgress(
            baseline,
            [Fqdn("api.example.com")],
            [Fqdn("API.EXAMPLE.COM.")]);
        Assert.Equal(baseline.ToArray(), narrowed.ToArray());

        var widened = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveEgress(
                baseline,
                [Fqdn("other.example.com")],
                []));
        Assert.Equal(StatusCodes.Status400BadRequest, widened.StatusCode);

        var missingRequired = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveEgress(
                baseline,
                [],
                [Fqdn("api.example.com")]));
        Assert.Equal(StatusCodes.Status400BadRequest, missingRequired.StatusCode);
    }

    [Fact]
    public void ReadsLegacyRunSelectionEgressWithoutBroadeningItsEffectiveDestinations()
    {
        const string legacySnapshot = """
            {
              "projectId": "project",
              "runId": "run",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "legacy-context",
              "modelSelection": { "reference": "model" },
              "providers": [],
              "egressAllowlist": [
                { "host": "API.Example.com.", "port": 443, "protocol": "tcp" }
              ],
              "runLimits": {},
              "projectConfiguration": {
                "egressNarrowing": [
                  { "host": "API.Example.com.", "port": 443, "protocol": "tcp" }
                ]
              }
            }
            """;

        var selection = ProjectsConfigService.DeserializeRunSelection(legacySnapshot);
        var effectiveRule = Assert.Single(selection.EgressAllowlist);

        Assert.Equal("api.example.com", effectiveRule.Destination);
        Assert.Equal(NetworkEgressPurpose.PublicHttps, effectiveRule.Purpose);
        Assert.Equal(NetworkEgressDestinationKind.Fqdn, effectiveRule.DestinationKind);
        Assert.Equal(selection.EgressAllowlist, selection.EgressBaseline);
        Assert.Equal(selection.EgressAllowlist, selection.ProjectEgressNarrowing);
        Assert.Equal(selection.EgressAllowlist, selection.RequiredEgress);
        Assert.Equal(effectiveRule, Assert.Single(selection.ProjectConfiguration.EgressNarrowing!.Value));
    }

    [Fact]
    public void ProjectRunLimitsCannotExceedPlatformLimits()
    {
        var platform = ProjectConfigurationValidator.Validate(PlatformDefaults());
        var narrowed = ProjectConfigurationValidator.ResolveLimits(platform, new CopilotRunLimitOverrides
        {
            MaxModelTurns = 6,
            MaxChildren = 0,
        });
        Assert.Equal(6, narrowed.MaxModelTurns);
        Assert.Equal(0, narrowed.MaxChildren);
        Assert.Equal(100, narrowed.MaxToolCalls);

        var widening = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveLimits(platform, new CopilotRunLimitOverrides
            {
                MaxModelTurns = 13,
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, widening.StatusCode);
    }

    [Fact]
    public void RejectsProviderOverridesForNonOverridableCardinalities()
    {
        var error = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                ProviderOverrides = [new ProjectProviderOverride(ProviderSeam.Policy, "policy")],
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void RejectsNullConfigurationEntriesAsClientErrors()
    {
        var providerError = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                ProviderOverrides = ImmutableArray.CreateRange(new ProjectProviderOverride[] { null! }),
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, providerError.StatusCode);

        var charterError = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                AgentCharters = ImmutableArray.CreateRange(new ProjectAgentCharter[] { null! }),
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, charterError.StatusCode);
    }

    [Fact]
    public void LoadsProviderCatalogSnapshotFromCatalogOwnerConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Id"] = "sandbox-platform",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdapterVersion"] = "1.0.0",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsSchemaVersion"] = "1",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Hosting"] = "KubernetesController",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdvertisedCapabilities:0"] = "container.create",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Enabled"] = "true",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsRevision"] = "options-v1",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:ProviderId"] = "sandbox-platform",
            })
            .Build();

        var catalog = ProviderCatalogConfiguration.Load(configuration);
        var result = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")));
        Assert.True(result.IsSuccess);
        Assert.Equal("options-v1", result.Value!.Candidate!.OptionsRevision);
    }
}
