using Scaffolder.Squad.Catalog;

namespace Scaffolder.Tests.Casting;

/// <summary>
/// Tests for CatalogReader: verifies that the embedded role catalog loads correctly,
/// contains the expected templates and roles, and has no emojis in any content.
/// </summary>
public sealed class CatalogReaderTests
{
    private readonly CatalogReader _reader = new();

    [Fact]
    public void LoadTemplates_ReturnsExactlyFourTemplates()
    {
        var templates = _reader.LoadTemplates();
        Assert.Equal(4, templates.Count);
    }

    [Fact]
    public void LoadTemplates_ContainsTheFourRetainedTemplates()
    {
        var templates = _reader.LoadTemplates();
        var ids       = templates.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("product-feature-delivery", ids);
        Assert.Contains("quick-software-development", ids);
        Assert.Contains("content-authoring", ids);
        Assert.Contains("research", ids);
    }

    [Fact]
    public void LoadTemplates_EachTemplateHasRolesWithNonEmptyIdsAndTitles()
    {
        var templates = _reader.LoadTemplates();

        foreach (var template in templates)
        {
            Assert.NotEmpty(template.Roles);
            foreach (var role in template.Roles)
            {
                Assert.False(string.IsNullOrWhiteSpace(role.Id),    $"Role in {template.Id} has empty id.");
                Assert.False(string.IsNullOrWhiteSpace(role.Title), $"Role {role.Id} has empty title.");
            }
        }
    }

    [Fact]
    public void LoadRole_ReturnsRoleWithCapabilitiesResponsibilitiesAndBoundaries()
    {
        var templates = _reader.LoadTemplates();
        var firstRole = templates.SelectMany(g => g.Roles).First();

        var role = _reader.LoadRole(firstRole.Id);

        Assert.NotNull(role);
        Assert.NotEmpty(role.Capabilities);
        Assert.NotEmpty(role.Responsibilities);
        Assert.NotEmpty(role.Boundaries);
    }

    [Fact]
    public void LoadCharterTemplate_ReturnsNonEmptyMarkdownForEachRole()
    {
        var templates = _reader.LoadTemplates();

        foreach (var role in templates.SelectMany(g => g.Roles))
        {
            var template = _reader.LoadCharterTemplate(role.Id);
            Assert.False(string.IsNullOrWhiteSpace(template),
                $"Charter template for role {role.Id} is empty.");
        }
    }

    [Fact]
    public void NoCatalogContent_ContainsEmojis()
    {
        var templates = _reader.LoadTemplates();

        foreach (var template in templates)
        {
            AssertNoEmoji(template.Title, $"template {template.Id} title");
            AssertNoEmoji(template.Description, $"template {template.Id} description");

            foreach (var role in template.Roles)
            {
                AssertNoEmoji(role.Title,       $"role {role.Id} title");
                AssertNoEmoji(role.Summary, $"role {role.Id} summary");

                var fullRole = _reader.LoadRole(role.Id);
                if (fullRole is not null)
                {
                    AssertNoEmoji(string.Join(" ", fullRole.Capabilities),     $"role {role.Id} capabilities");
                    AssertNoEmoji(string.Join(" ", fullRole.Responsibilities), $"role {role.Id} responsibilities");
                    AssertNoEmoji(string.Join(" ", fullRole.Boundaries),       $"role {role.Id} boundaries");
                }

                var charterTemplate = _reader.LoadCharterTemplate(role.Id);
                AssertNoEmoji(charterTemplate, $"role {role.Id} charter template");
            }
        }
    }

    private static void AssertNoEmoji(string? text, string context)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var c in text)
        {
            // Emoji code points start at U+1F300; also catch Emoticons block.
            var cp = char.ConvertToUtf32(text, text.IndexOf(c));
            Assert.True(cp < 0x1F300 || cp > 0x1FAFF,
                $"Found emoji (U+{cp:X}) in {context}: \"{text}\"");
        }
    }
}

