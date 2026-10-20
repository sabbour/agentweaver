using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class SkillMarketplaceCatalogTests
{
    [Fact]
    public void Missing_section_uses_the_built_in_definitions_sorted_by_name()
    {
        var catalog = SkillMarketplaceCatalog.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(
            new[] { "Azure Skills", "GitHub Awesome Copilot" },
            catalog.Definitions.Select(item => item.Definition.Name).ToArray());
        var awesome = catalog.Definitions[1];
        Assert.Equal("github/awesome-copilot", awesome.Definition.Repository);
        Assert.Equal("main", awesome.Definition.RequestedRef);
        Assert.Equal("skills", awesome.Definition.Subpath);
        Assert.NotNull(awesome.LayoutNote);
    }

    [Fact]
    public void Configured_section_replaces_defaults_normalizes_and_skips_disabled_entries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Name"] = "Team skills",
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Repository"] = "https://github.com/Contoso/Skills.git",
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Subpath"] = "/skills/",
                ["ProjectsConfig:SkillMarketplaces:Definitions:1:Name"] = "Hidden",
                ["ProjectsConfig:SkillMarketplaces:Definitions:1:Repository"] = "contoso/hidden",
                ["ProjectsConfig:SkillMarketplaces:Definitions:1:Enabled"] = "false",
            })
            .Build();

        var definition = Assert.Single(SkillMarketplaceCatalog.FromConfiguration(configuration).Definitions);

        Assert.Equal("Team skills", definition.Definition.Name);
        Assert.Equal("contoso/skills", definition.Definition.Repository);
        Assert.Equal("main", definition.Definition.RequestedRef);
        Assert.Equal("skills", definition.Definition.Subpath);
        Assert.Null(definition.LayoutNote);
    }

    [Fact]
    public void Empty_configured_section_offers_no_definitions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Name"] = "Off",
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Repository"] = "contoso/off",
                ["ProjectsConfig:SkillMarketplaces:Definitions:0:Enabled"] = "false",
            })
            .Build();

        Assert.Empty(SkillMarketplaceCatalog.FromConfiguration(configuration).Definitions);
    }

    [Theory]
    [InlineData(null, "contoso/skills")]
    [InlineData("Bad", "https://gitlab.com/contoso/skills")]
    [InlineData("Bad", "contoso/skills", "../escape")]
    public void Invalid_definitions_fail_at_startup(string? name, string repository, string? subpath = null)
    {
        Assert.Throws<InvalidOperationException>(() => new SkillMarketplaceCatalog(
        [
            new SkillMarketplaceDefinitionSettings { Name = name, Repository = repository, Subpath = subpath },
        ]));
    }

    [Fact]
    public void Duplicate_names_fail_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new SkillMarketplaceCatalog(
        [
            new SkillMarketplaceDefinitionSettings { Name = "Skills", Repository = "contoso/one" },
            new SkillMarketplaceDefinitionSettings { Name = "skills", Repository = "contoso/two" },
        ]));
    }

    [Fact]
    public void Describe_links_only_an_active_source_with_the_same_location()
    {
        var catalog = new SkillMarketplaceCatalog(SkillMarketplaceCatalog.DefaultDefinitions);
        var match = Source("GitHub/Awesome-Copilot", "main", "skills", ProjectMarketplaceSourceState.Active);
        var removed = Source("microsoft/skills", "main", ".github/plugins/azure-sdk-dotnet/skills", ProjectMarketplaceSourceState.Removed);
        var otherRef = Source("microsoft/skills", "release", ".github/plugins/azure-sdk-dotnet/skills", ProjectMarketplaceSourceState.Active);

        var views = catalog.Describe([removed, otherRef, match]);

        Assert.Null(views[0].SourceId);
        Assert.Equal(match.SourceId, views[1].SourceId);
    }

    private static ProjectMarketplaceSourceRecord Source(
        string repository, string requestedRef, string? subpath, ProjectMarketplaceSourceState state) => new()
    {
        ProjectId = "project",
        SourceId = Guid.NewGuid(),
        Name = repository,
        NormalizedName = repository.ToUpperInvariant(),
        Repository = repository,
        RequestedRef = requestedRef,
        Subpath = subpath,
        Revision = 1,
        State = state,
        CreatedByActorId = "actor",
        UpdatedByActorId = "actor",
    };
}
