using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectModelChoicesTests
{
    [Fact]
    public void SavedReferencesNeverBecomeEligibleChoicesOrEfforts()
    {
        var platform = new VersionedPlatformRuntimeDefaults(
            7,
            new PlatformRuntimeDefaults
            {
                ModelSelection = new ModelSelectionSettings("platform-model", SourceMode: ModelSourceMode.HostedCopilot),
                ModelTaskDefaults = [],
                ModelRoleDefaults = [new("reviewer", new ModelSelectionOverride("shared-model"))],
            },
            "platform-admin",
            DateTimeOffset.UnixEpoch);
        var project = new ProjectConfiguration
        {
            ModelSelection = new ModelSelectionSettings(
                "project-model", new SecretRef("private-key", "exact-version"), ModelSourceMode.Byok),
            ModelAgentOverrides = [new("agent", new ModelSelectionOverride("shared-model"))],
        };

        var choices = ProjectsConfigService.BuildModelChoices(project, platform, ModelBindingCapabilities.Unavailable);

        Assert.Equal("unavailable", choices.State);
        Assert.Equal("model_capability_catalog_unavailable", choices.UnavailableReason);
        Assert.Equal("verifiedAtRunStart", choices.RuntimeEligibility);
        Assert.Empty(choices.Choices);
        Assert.Null(choices.BindingsRevision);
        Assert.Equal("high", choices.DefaultReasoningEffort);
        Assert.Collection(
            choices.SavedReferences,
            item =>
            {
                Assert.Equal("platform-model", item.Reference);
                Assert.Equal(["platformDefault"], item.Sources.ToArray());
            },
            item =>
            {
                Assert.Equal("project-model", item.Reference);
                Assert.Equal(["projectSaved"], item.Sources.ToArray());
            },
            item =>
            {
                Assert.Equal("shared-model", item.Reference);
                Assert.Equal(["platformRole", "projectSaved"], item.Sources.ToArray());
            });
    }

    [Fact]
    public void ReportsUnavailableWithoutPlatformDefaults()
    {
        var choices = ProjectsConfigService.BuildModelChoices(
            new ProjectConfiguration(),
            new VersionedPlatformRuntimeDefaults(0, null, null, null),
            ModelBindingCapabilities.Unavailable);

        Assert.Equal("unavailable", choices.State);
        Assert.Equal("model_capability_catalog_unavailable", choices.UnavailableReason);
        Assert.Empty(choices.Choices);
        Assert.Empty(choices.SavedReferences);
    }

    /// <summary>Builds the read-only binding copy exactly as the deployment profile supplies it.</summary>
    internal static ModelBindingCapabilities Bindings(params (string Key, string Value)[] values) =>
        ModelBindingCapabilities.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build());

    internal static ModelBindingCapabilities SampleBindings() => Bindings(
        ("AgentHost:ModelBindingsRevision", "models-r7"),
        ("AgentHost:ModelBindings:hosted-reasoning:SourceMode", "HostedCopilot"),
        ("AgentHost:ModelBindings:hosted-reasoning:Enabled", "true"),
        ("AgentHost:ModelBindings:byok-coder:SourceMode", "Byok"),
        ("AgentHost:ModelBindings:byok-coder:Enabled", "true"),
        ("AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:0", "xhigh"),
        ("AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:1", "low"),
        ("AgentHost:ModelBindings:retired:SourceMode", "Byok"),
        ("AgentHost:ModelBindings:retired:Enabled", "false"),
        ("AgentHost:ModelBindings:retired:SupportedReasoningEfforts:0", "high"));

    [Fact]
    public void AvailableBindingsListOnlyEnabledReferencesWithTheirReasoningSource()
    {
        var project = new ProjectConfiguration
        {
            ModelSelection = new ModelSelectionSettings(
                "retired", new SecretRef("private-key", "exact-version"), ModelSourceMode.Byok),
            ModelTaskOverrides = [new(ModelTaskKind.Planning, new ModelSelectionOverride("saved-only"))],
        };

        var choices = ProjectsConfigService.BuildModelChoices(
            project, new VersionedPlatformRuntimeDefaults(0, null, null, null), SampleBindings());

        Assert.Equal("available", choices.State);
        Assert.Null(choices.UnavailableReason);
        Assert.Equal("verifiedAtRunStart", choices.RuntimeEligibility);
        Assert.Equal("models-r7", choices.BindingsRevision);
        Assert.Collection(
            choices.Choices,
            item =>
            {
                Assert.Equal("byok-coder", item.Reference);
                Assert.Equal("byok", item.SourceMode);
                Assert.Equal("modelBinding", item.ReasoningEffortSource);
                Assert.Equal(["low", "xhigh"], item.ReasoningEfforts.ToArray());
            },
            item =>
            {
                Assert.Equal("hosted-reasoning", item.Reference);
                Assert.Equal("hostedCopilot", item.SourceMode);
                Assert.Equal("verifiedAtRunStart", item.ReasoningEffortSource);
                Assert.Empty(item.ReasoningEfforts);
            });
        // A disabled binding and a merely saved reference stay out of the choices but remain visible as saved.
        Assert.DoesNotContain(choices.Choices, item => item.Reference is "retired" or "saved-only");
        Assert.Equal(["retired", "saved-only"], choices.SavedReferences.Select(item => item.Reference).ToArray());
    }

    [Fact]
    public void ChoicesResponseCarriesOnlyEligibilityFields()
    {
        var choices = ProjectsConfigService.BuildModelChoices(
            new ProjectConfiguration
            {
                ModelSelection = new ModelSelectionSettings(
                    "byok-coder", new SecretRef("private-key", "exact-version"), ModelSourceMode.Byok),
            },
            new VersionedPlatformRuntimeDefaults(0, null, null, null),
            SampleBindings());
        var json = JsonSerializer.SerializeToNode(choices, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? node)
        {
            if (node is JsonObject item)
            {
                foreach (var (name, value) in item)
                {
                    names.Add(name);
                    Collect(value);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var value in array)
                    Collect(value);
            }
        }
        Collect(json);

        // Any provider URL, header, model ID, or credential field added later must be reviewed against this list.
        Assert.Equal(
            [
                "bindingsRevision", "choices", "defaultReasoningEffort", "reasoningEffortSource", "reasoningEfforts",
                "reference", "runtimeEligibility", "savedReferences", "sourceMode", "sources", "state",
                "unavailableReason",
            ],
            names.ToArray());
        var text = json.ToJsonString();
        Assert.DoesNotContain("private-key", text);
        Assert.DoesNotContain("exact-version", text);
        Assert.DoesNotContain("https://", text);
    }

    [Fact]
    public void AcceptsEveryBindingAgentHostAcceptsButListsOnlyRunnableChoices()
    {
        // AgentHost accepts hosted bindings that list efforts (it ignores them) and BYOK bindings without efforts
        // (no run can start with them). The copy must load both without failing service startup.
        var bindings = Bindings(
            ("AgentHost:ModelBindingsRevision", "models-r8"),
            ("AgentHost:ModelBindings:hosted-listed:SourceMode", "HostedCopilot"),
            ("AgentHost:ModelBindings:hosted-listed:Enabled", "true"),
            ("AgentHost:ModelBindings:hosted-listed:SupportedReasoningEfforts:0", "high"),
            ("AgentHost:ModelBindings:byok-unlisted:SourceMode", "Byok"),
            ("AgentHost:ModelBindings:byok-unlisted:Enabled", "true"),
            ("AgentHost:ModelBindings:byok-off:SourceMode", "Byok"),
            ("AgentHost:ModelBindings:byok-off:Enabled", "false"));

        var choices = ProjectsConfigService.BuildModelChoices(
            new ProjectConfiguration(), new VersionedPlatformRuntimeDefaults(0, null, null, null), bindings);

        var only = Assert.Single(choices.Choices);
        Assert.Equal("hosted-listed", only.Reference);
        Assert.Equal("verifiedAtRunStart", only.ReasoningEffortSource);
        Assert.Empty(only.ReasoningEfforts);
    }

    [Fact]
    public void AbsentBindingCopyIsUnavailable()
    {
        var bindings = Bindings();

        Assert.False(bindings.IsAvailable);
        Assert.Same(ModelBindingCapabilities.Unavailable, bindings);
    }

    public static TheoryData<string, string, string?> MalformedBindings => new()
    {
        { "revision only", "AgentHost:ModelBindings:hosted-reasoning:SourceMode", null },
        { "bindings only", "AgentHost:ModelBindingsRevision", null },
        { "bad revision", "AgentHost:ModelBindingsRevision", "bad revision" },
        { "model id", "AgentHost:ModelBindings:hosted-reasoning:ModelId", "gpt-6.1-sol" },
        { "provider url", "AgentHost:ModelBindings:byok-coder:Provider:BaseUrl", "https://models.example/" },
        { "provider header", "AgentHost:ModelBindings:byok-coder:Provider:Headers:api-key", "secret" },
        { "credential", "AgentHost:ModelBindings:byok-coder:CredentialReference:Id", "private-key" },
        { "source mode", "AgentHost:ModelBindings:hosted-reasoning:SourceMode", "Hosted" },
        { "missing enabled", "AgentHost:ModelBindings:hosted-reasoning:Enabled", null },
        { "enabled text", "AgentHost:ModelBindings:hosted-reasoning:Enabled", "yes" },
        { "unknown effort", "AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:0", "max" },
        { "duplicate effort", "AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:0", "low" },
        { "index gap", "AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:3", "medium" },
        { "scalar efforts", "AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts", "low" },
        { "bad reference", "AgentHost:ModelBindings:bad reference:SourceMode", "HostedCopilot" },
    };

    [Theory]
    [MemberData(nameof(MalformedBindings))]
    public void MalformedBindingCopyFailsClearly(string scenario, string key, string? value)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AgentHost:ModelBindingsRevision"] = "models-r7",
            ["AgentHost:ModelBindings:hosted-reasoning:SourceMode"] = "HostedCopilot",
            ["AgentHost:ModelBindings:hosted-reasoning:Enabled"] = "true",
            ["AgentHost:ModelBindings:byok-coder:SourceMode"] = "Byok",
            ["AgentHost:ModelBindings:byok-coder:Enabled"] = "true",
            ["AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:0"] = "xhigh",
            ["AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:1"] = "low",
        };
        switch (scenario)
        {
            case "revision only":
                foreach (var binding in values.Keys.Where(item => item.StartsWith("AgentHost:ModelBindings:")).ToArray())
                    values.Remove(binding);
                break;
            case "bindings only":
                values.Remove(key);
                break;
            default:
                if (value is null)
                    values.Remove(key);
                else
                    values[key] = value;
                break;
        }
        if (scenario == "scalar efforts")
        {
            values.Remove("AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:0");
            values.Remove("AgentHost:ModelBindings:byok-coder:SupportedReasoningEfforts:1");
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelBindingCapabilities.FromConfiguration(configuration));
        Assert.StartsWith("Projects & Config model-binding configuration is invalid", error.Message);
    }

    [Fact]
    public void RejectsAnyNonEmptySubmittedProviderOverride()
    {
        var stored = new ProjectConfiguration
        {
            ProviderOverrides = [new ProjectProviderOverride(ProviderSeam.Sandbox, "sandbox-project")],
            OrderedProviderOverrides =
            [
                new ProjectOrderedProviderOverride(ProviderSeam.Guardrails, ["first", "second"]),
            ],
        };

        var unchanged = Assert.Throws<ProjectConfigException>(() =>
            ProjectsConfigService.RejectSubmittedProviderOverrides(stored));
        Assert.Equal("project_provider_override_unsupported", unchanged.Code);
        var orderedOnly = Assert.Throws<ProjectConfigException>(() =>
            ProjectsConfigService.RejectSubmittedProviderOverrides(stored with { ProviderOverrides = [] }));
        Assert.Equal("project_provider_override_unsupported", orderedOnly.Code);

        var submitted = new ProjectConfiguration { ProviderOverrides = [], OrderedProviderOverrides = [] };
        ProjectsConfigService.RejectSubmittedProviderOverrides(submitted);
        var kept = ProjectsConfigService.KeepStoredProviderOverrides(stored, submitted);
        Assert.Equal(stored.ProviderOverrides, kept.ProviderOverrides);
        Assert.Equal(stored.OrderedProviderOverrides, kept.OrderedProviderOverrides);
    }
}
