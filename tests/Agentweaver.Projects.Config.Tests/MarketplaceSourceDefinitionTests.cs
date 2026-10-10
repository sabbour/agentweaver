using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class MarketplaceSourceDefinitionTests
{
    [Fact]
    public void Normalize_produces_a_canonical_public_repository_and_safe_relative_location()
    {
        var definition = MarketplaceSourceDefinition.Normalize(
            name: null,
            repository: "https://github.com/Contoso/Skills.git",
            requestedRef: "release/1.0",
            subpath: "/skills/catalog/");

        Assert.Equal("skills", definition.Name);
        Assert.Equal("contoso/skills", definition.Repository);
        Assert.Equal("release/1.0", definition.RequestedRef);
        Assert.Equal("skills/catalog", definition.Subpath);
    }

    [Theory]
    [InlineData("http://github.com/contoso/skills")]
    [InlineData("https://github.com.evil.example/contoso/skills")]
    [InlineData("https://user@github.com/contoso/skills")]
    [InlineData("https://github.com/contoso/skills?ref=main")]
    [InlineData("contoso/../skills")]
    [InlineData("contoso/skills/tree/main")]
    public void Normalize_rejects_untrusted_or_ambiguous_repository_inputs(string repository)
    {
        var exception = Assert.Throws<MarketplaceSourceException>(() =>
            MarketplaceSourceDefinition.Normalize(null, repository, "main", null));

        Assert.Equal("invalid_marketplace_request", exception.Code);
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("refs/heads/.hidden")]
    [InlineData("release/main?download=1")]
    [InlineData("release//main")]
    [InlineData("release/branch.lock")]
    public void Normalize_rejects_invalid_git_refs(string requestedRef)
    {
        var exception = Assert.Throws<MarketplaceSourceException>(() =>
            MarketplaceSourceDefinition.Normalize(
                "skills", "contoso/skills", requestedRef, "skills"));

        Assert.Equal("invalid_marketplace_request", exception.Code);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("skills\\private")]
    [InlineData("skills//nested")]
    [InlineData("skills/./nested")]
    public void Normalize_rejects_unsafe_source_subpaths(string subpath)
    {
        var exception = Assert.Throws<MarketplaceSourceException>(() =>
            MarketplaceSourceDefinition.Normalize(
                "skills", "contoso/skills", "main", subpath));

        Assert.Equal("invalid_marketplace_request", exception.Code);
    }
}
