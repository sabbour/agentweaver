using Agentweaver.Abstractions;
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

        var choices = ProjectsConfigService.BuildModelChoices(project, platform);

        Assert.Equal("unavailable", choices.State);
        Assert.Equal("model_capability_catalog_unavailable", choices.UnavailableReason);
        Assert.Equal("verifiedAtRunStart", choices.RuntimeEligibility);
        Assert.Empty(choices.Choices);
        Assert.Empty(choices.ReasoningEfforts);
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
            new VersionedPlatformRuntimeDefaults(0, null, null, null));

        Assert.Equal("unavailable", choices.State);
        Assert.Equal("model_capability_catalog_unavailable", choices.UnavailableReason);
        Assert.Empty(choices.Choices);
        Assert.Empty(choices.SavedReferences);
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
