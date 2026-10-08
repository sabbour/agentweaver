using System.Collections.Immutable;
using System.Text.Json;
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
    public void ExplicitModelModesBelongToProjectOrPlatformSettingsAndLegacyModeIsNotInferred()
    {
        var key = new SecretRef("selected-project-key", "version-1");
        var project = new ProjectConfiguration
        {
            ModelSelection = new("project-byok", key, ModelSourceMode.Byok)
        };
        Assert.Equal(ModelSourceMode.Byok,
            ProjectConfigurationValidator.Validate(project).ModelSelection!.SourceMode);
        var platform = PlatformDefaults() with
        {
            ModelSelection = new("platform-copilot", null, ModelSourceMode.HostedCopilot, Guid.NewGuid())
        };
        Assert.Equal(ModelSourceMode.HostedCopilot,
            ProjectConfigurationValidator.Validate(platform).ModelSelection!.SourceMode);
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(project with
        {
            ModelSelection = project.ModelSelection! with { SourceMode = (ModelSourceMode)123 }
        }));
        var legacy = new ModelSelectionSettings("legacy-model", key);
        Assert.Null(legacy.SourceMode);
        Assert.Null(legacy.ConnectionId);
        Assert.DoesNotContain("sourceMode", JsonSerializer.Serialize(legacy, new JsonSerializerOptions(
            JsonSerializerDefaults.Web)));
        Assert.DoesNotContain("connectionId", JsonSerializer.Serialize(legacy, new JsonSerializerOptions(
            JsonSerializerDefaults.Web)));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(platform with
        {
            ModelSelection = platform.ModelSelection! with { CredentialReference = key }
        }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(platform with
        {
            ModelSelection = platform.ModelSelection! with { ConnectionId = null }
        }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(project with
        {
            ModelSelection = project.ModelSelection! with { ConnectionId = Guid.NewGuid() }
        }));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProjectConfiguration>(
                """{"personalModelSelection":{"reference":"personal-byok"}}"""));
    }

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
    public void ValidatesOptionalDefaultWorkflowIdentifier()
    {
        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            DefaultWorkflowId = "workflow.default",
        });
        Assert.Equal("workflow.default", configuration.DefaultWorkflowId);

        var error = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                DefaultWorkflowId = "not a stable identifier",
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void SourceControlSettingsAreOptionalAndPersistOnlyExactSecretReferences()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = ProjectConfigurationValidator.Validate(new ProjectConfiguration());
        var legacyJson = JsonSerializer.Serialize(legacy, options);
        Assert.DoesNotContain("\"sourceControl\"", legacyJson, StringComparison.Ordinal);

        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"),
            new SecretRef("github-checkout", "v2"),
            new SecretRef("github-webhook", "v1"));
        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            SourceControl = settings,
        });
        var json = JsonSerializer.Serialize(configuration, options);

        Assert.Equal(settings, configuration.SourceControl);
        Assert.Contains("\"repository\":{\"owner\":\"octo\",\"name\":\"repo\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"apiSecretReference\":{\"id\":\"github-api\",\"version\":\"v3\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"checkoutSecretReference\":{\"id\":\"github-checkout\",\"version\":\"v2\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"webhookSecretReference\":{\"id\":\"github-webhook\",\"version\":\"v1\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"authMode\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"identityConnectionId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHubAppSettingsUseStableIdentityConnectionWithoutPersistedApiOrCheckoutSecrets()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            apiSecretReference: null,
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn_123");

        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            SourceControl = settings,
        });
        var json = JsonSerializer.Serialize(configuration, options);
        var restored = JsonSerializer.Deserialize<ProjectConfiguration>(json, options)!.SourceControl!;

        Assert.Equal(SourceControlAuthMode.GitHubApp, restored.AuthMode);
        Assert.Equal("app-conn_123", restored.IdentityConnectionId);
        Assert.Null(restored.ApiSecretReference);
        Assert.Null(restored.CheckoutSecretReference);
        Assert.Contains("\"authMode\":\"githubApp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"identityConnectionId\":\"app-conn_123\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("identityRepositorySelectionCode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apiSecretReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("checkoutSecretReference", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","selectionCode":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""")]
    [InlineData("""{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","identityRepositorySelectionCode":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""")]
    public void RejectsTransientSelectionCodeFromProjectConfiguration(string configuration)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProjectConfiguration>(configuration, options));
    }

    [Fact]
    public void LegacySettingsRoundTripWithoutAddingAuthModeOrConnectionToAcceptedJson()
    {
        const string legacy = """
            {"repository":{"owner":"octo","name":"repo"},
             "apiSecretReference":{"id":"github-api","version":"v3"}}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var settings = JsonSerializer.Deserialize<SourceControlProjectSettings>(legacy, options)!;
        var serialized = JsonSerializer.Serialize(settings, options);

        Assert.Null(settings.AuthMode);
        Assert.Null(settings.IdentityConnectionId);
        Assert.Equal(
            """{"repository":{"owner":"octo","name":"repo"},"apiSecretReference":{"id":"github-api","version":"v3"}}""",
            serialized);
    }

    [Fact]
    public void RejectsMismatchedOrUnknownSourceControlAuthBindings()
    {
        var repository = new SourceControlRepositoryIdentity("octo", "repo");
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            apiSecretReference: null,
            authMode: SourceControlAuthMode.GitHubApp));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            new SecretRef("github-api", "v3"),
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn"));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            null,
            checkoutSecretReference: new SecretRef("github-checkout", "v2"),
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn"));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            new SecretRef("github-api", "v3"),
            identityConnectionId: "app-conn"));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SourceControlProjectSettings>(
                """{"repository":{"owner":"octo","name":"repo"},"apiSecretReference":{"id":"api","version":"v1"},"authMode":"unexpected"}""",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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
