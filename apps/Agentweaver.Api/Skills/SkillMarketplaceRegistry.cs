using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Skills;

/// <summary>Administrator-managed, configuration-backed registry of trusted skill marketplaces.</summary>
public sealed class SkillMarketplaceOptions
{
    public List<SkillMarketplaceDefinition> Definitions { get; init; } = [];
}

public sealed class SkillMarketplaceDefinition
{
    public string Name { get; init; } = "";
    public string Repository { get; init; } = "";
    public string? Subpath { get; init; }
    public string? Branch { get; init; } = "main";
    public string? LayoutNote { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class SkillMarketplaceRegistry(IOptions<SkillMarketplaceOptions> options)
{
    private readonly IReadOnlyList<SkillMarketplaceDefinition> _definitions = options.Value.Definitions;

    public IReadOnlyList<SkillMarketplaceDefinition> ListEnabled() =>
        _definitions.Where(x => x.Enabled).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public SkillMarketplaceDefinition? FindEnabled(string name) =>
        ListEnabled().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string ToImportUrl(SkillMarketplaceDefinition definition)
    {
        var path = string.IsNullOrWhiteSpace(definition.Subpath) ? "" : $"/tree/{definition.Branch ?? "main"}/{definition.Subpath.Trim('/')}";
        return $"https://github.com/{definition.Repository.Trim('/')}{path}";
    }
}
