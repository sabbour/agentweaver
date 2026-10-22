using Microsoft.Extensions.Configuration;

namespace Agentweaver.Projects.Config;

/// <summary>One administrator-configured marketplace that users can add to a project as a source.</summary>
public sealed record SkillMarketplaceDefinitionSettings
{
    public string? Name { get; init; }
    public string? Repository { get; init; }
    public string? RequestedRef { get; init; }
    public string? Subpath { get; init; }
    public string? LayoutNote { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// A suggested marketplace. <see cref="SourceId"/> is the active project source with the same repository,
/// requested ref, and subpath, or null when the project hasn't added it.
/// </summary>
public sealed record SkillMarketplaceDefinitionView(
    string Name,
    string Repository,
    string RequestedRef,
    string? Subpath,
    string? LayoutNote,
    Guid? SourceId);

/// <summary>
/// Holds the enabled marketplace definitions from <c>ProjectsConfig:SkillMarketplaces:Definitions</c>.
/// Definitions are suggestions only. A user adds one through the source create route, which applies the
/// usual project authority, validation, and revision rules.
/// </summary>
public sealed class SkillMarketplaceCatalog
{
    /// <summary>The configuration section that replaces the built-in definitions when present.</summary>
    public const string SectionName = "ProjectsConfig:SkillMarketplaces:Definitions";

    /// <summary>The public marketplaces that Agentweaver 0.x offered by default.</summary>
    public static readonly IReadOnlyList<SkillMarketplaceDefinitionSettings> DefaultDefinitions =
    [
        new()
        {
            Name = "GitHub Awesome Copilot",
            Repository = "github/awesome-copilot",
            RequestedRef = "main",
            Subpath = "skills",
            LayoutNote = "Each skill is a folder under skills/ with a SKILL.md file.",
        },
        new()
        {
            Name = "Azure Skills",
            Repository = "microsoft/skills",
            RequestedRef = "main",
            Subpath = ".github/plugins/azure-sdk-dotnet/skills",
            LayoutNote = "Each skill is a folder with a SKILL.md file.",
        },
    ];

    /// <summary>Validates and sorts the enabled definitions by name.</summary>
    /// <exception cref="InvalidOperationException">A definition is invalid or two definitions share a name.</exception>
    public SkillMarketplaceCatalog(IEnumerable<SkillMarketplaceDefinitionSettings> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = new List<(ProjectMarketplaceSourceDefinition Definition, string? LayoutNote)>();
        // Skip disabled and null entries before validation. An administrator can then turn off a
        // definition, or turn off all of them, without deleting its other values.
        foreach (var settings in definitions.Where(definition => definition is { Enabled: true }))
        {
            // The source rules would derive a name from the repository. A definition requires an explicit
            // name because the UI shows it and the duplicate check below compares it.
            if (string.IsNullOrWhiteSpace(settings.Name))
                throw new InvalidOperationException("Each skill marketplace definition requires a name.");
            ProjectMarketplaceSourceDefinition definition;
            try
            {
                // Use the same normalization as the source create route. A suggestion then has the
                // repository, ref, and subpath that the route stores when a user adds it, and the
                // match in Describe works.
                definition = MarketplaceSourceDefinition.Normalize(
                    settings.Name, settings.Repository, settings.RequestedRef, settings.Subpath);
            }
            catch (MarketplaceSourceException exception)
            {
                // A request error becomes a startup error. A bad definition then stops the service
                // instead of returning a suggestion that the create route rejects.
                throw new InvalidOperationException(
                    $"Skill marketplace definition '{settings.Name}' is invalid: {exception.Message}", exception);
            }
            // Names are unique without case, the same as active source names in a project.
            if (normalized.Any(existing => string.Equals(
                    existing.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Skill marketplace definition '{definition.Name}' is configured more than once.");
            // A blank layout note is stored as null so clients can show the note only when it has text.
            normalized.Add((definition, string.IsNullOrWhiteSpace(settings.LayoutNote) ? null : settings.LayoutNote.Trim()));
        }
        // Sort once at startup so every response lists definitions in the same order.
        Definitions = normalized
            .OrderBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The enabled definitions, sorted by name.</summary>
    public IReadOnlyList<(ProjectMarketplaceSourceDefinition Definition, string? LayoutNote)> Definitions { get; }

    /// <summary>Uses the configured section when it exists; otherwise uses <see cref="DefaultDefinitions"/>.</summary>
    public static SkillMarketplaceCatalog FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        // A present section replaces the defaults instead of adding to them. An administrator then has
        // full control of the list. A section with only disabled items yields an empty list.
        return section.Exists()
            ? new SkillMarketplaceCatalog(section.Get<SkillMarketplaceDefinitionSettings[]>() ?? [])
            : new SkillMarketplaceCatalog(DefaultDefinitions);
    }

    /// <summary>Pairs each definition with the matching active project source, if the project added it.</summary>
    public IReadOnlyList<SkillMarketplaceDefinitionView> Describe(IEnumerable<ProjectMarketplaceSourceRecord> sources)
    {
        // Removed sources are tombstones. Ignore them so a definition shows as not added again after
        // the project removes its source.
        var active = sources.Where(source => source.State == ProjectMarketplaceSourceState.Active).ToArray();
        return Definitions
            .Select(item =>
            {
                // Match by location, not by name, because a user can rename a source after adding it.
                // GitHub repository names are case-insensitive. Git refs and paths are case-sensitive,
                // so a source on a different ref or subpath is a different source.
                var match = active.FirstOrDefault(source =>
                    string.Equals(source.Repository, item.Definition.Repository, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.RequestedRef, item.Definition.RequestedRef, StringComparison.Ordinal) &&
                    string.Equals(source.Subpath, item.Definition.Subpath, StringComparison.Ordinal));
                return new SkillMarketplaceDefinitionView(
                    item.Definition.Name,
                    item.Definition.Repository,
                    item.Definition.RequestedRef,
                    item.Definition.Subpath,
                    item.LayoutNote,
                    match?.SourceId);
            })
            .ToArray();
    }
}
