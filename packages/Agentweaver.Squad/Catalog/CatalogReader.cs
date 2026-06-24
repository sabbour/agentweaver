using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Squad.Model;

namespace Agentweaver.Squad.Catalog;

/// <summary>
/// Reads the embedded scenario catalog: team templates, role archetypes, charter templates, and
/// blueprints. Resource names map directory separators to dots; hyphenated ids map to underscored
/// file names. The role set is fixed at build time: blueprints may roster only roles that exist in
/// the catalog (blueprints never mint roles).
/// </summary>
public sealed class CatalogReader
{
    private static readonly Assembly _asm = typeof(CatalogReader).Assembly;
    private const string ResourcePrefix = "Agentweaver.Squad.Catalog.Resources";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string Fid(string id) => id.Replace('-', '_');

    private string? ReadResourceText(string resourceName)
    {
        using var stream = _asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<TeamTemplate> LoadTemplates()
    {
        var manifestText = ReadResourceText($"{ResourcePrefix}.catalog.manifest.json");
        if (manifestText is null) return [];

        var manifest = JsonSerializer.Deserialize<CatalogManifestDto>(manifestText, _json);
        if (manifest?.Templates is null) return [];

        var result = new List<TeamTemplate>();
        foreach (var id in manifest.Templates)
        {
            var template = LoadTemplate(id);
            if (template is not null) result.Add(template);
        }
        return result;
    }

    public TeamTemplate? LoadTemplate(string id)
    {
        var text = ReadResourceText($"{ResourcePrefix}.groupings.{Fid(id)}.json");
        if (text is null) return null;

        var dto = JsonSerializer.Deserialize<TemplateDto>(text, _json);
        if (dto is null) return null;

        var roles = new List<Role>();
        foreach (var roleId in dto.Roles ?? [])
        {
            var role = LoadRole(roleId);
            if (role is not null) roles.Add(role);
        }

        return new TeamTemplate(dto.Id ?? id, dto.Title ?? id, dto.Description ?? string.Empty, roles);
    }

    public Role? LoadRole(string id)
    {
        var text = ReadResourceText($"{ResourcePrefix}.roles.{Fid(id)}.json");
        if (text is null) return null;

        var dto = JsonSerializer.Deserialize<RoleDto>(text, _json);
        if (dto is null) return null;

        return new Role(
            dto.Id ?? id,
            dto.Title ?? id,
            dto.Summary ?? string.Empty,
            dto.DefaultModel ?? string.Empty,
            dto.Capabilities ?? [],
            dto.Responsibilities ?? [],
            dto.Boundaries ?? []);
    }

    public IReadOnlyList<Role> LoadAllRoles()
    {
        var prefix = $"{ResourcePrefix}.roles.";
        var roleNames = _asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));

        var byId = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in roleNames)
        {
            var id = resourceName[prefix.Length..^".json".Length].Replace('_', '-');
            var role = LoadRole(id);
            if (role is not null) byId[role.Id] = role;
        }

        return byId.Values.ToList();
    }

    /// <summary>
    /// Returns whether a role with the given id is in the catalog. Used to enforce the blueprint role
    /// constraint: blueprints may roster only catalog roles.
    /// </summary>
    public bool HasRole(string id) => LoadRole(id) is not null;

    // -----------------------------------------------------------------------
    // Blueprints
    // -----------------------------------------------------------------------

    /// <summary>Loads all predefined blueprints embedded under <c>Catalog/Resources/blueprints</c>.</summary>
    public IReadOnlyList<Blueprint> LoadAllBlueprints()
    {
        var prefix = $"{ResourcePrefix}.blueprints.";
        var names = _asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        var result = new List<Blueprint>();
        foreach (var resourceName in names)
        {
            var text = ReadResourceText(resourceName);
            var blueprint = ParseBlueprint(text);
            if (blueprint is not null) result.Add(blueprint);
        }
        return result;
    }

    /// <summary>Loads a single predefined blueprint by id, or null when none is embedded.</summary>
    public Blueprint? LoadBlueprint(string id)
    {
        var text = ReadResourceText($"{ResourcePrefix}.blueprints.{Fid(id)}.json");
        return ParseBlueprint(text);
    }

    private static Blueprint? ParseBlueprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dto = JsonSerializer.Deserialize<BlueprintDto>(text, _json);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) return null;

        // Prefer the explicit workflows array; fall back to wrapping the legacy single workflow string.
        IReadOnlyList<string> workflows = dto.Workflows is { Count: > 0 }
            ? dto.Workflows
            : dto.Workflow is not null
                ? (IReadOnlyList<string>)[dto.Workflow]
                : (IReadOnlyList<string>)["default"];

        return new Blueprint(
            dto.Id!,
            dto.Name ?? dto.Id!,
            dto.Description ?? string.Empty,
            dto.Roster ?? [],
            workflows,
            dto.ReviewPolicy ?? "default",
            dto.SandboxProfile ?? "default");
    }

    public string? LoadCharterTemplate(string roleId)
        => ReadResourceText($"{ResourcePrefix}.charters.{Fid(roleId)}.md");

    /// <summary>
    /// Loads a built-in MAF agent template (<c>.github/agents/{name}.agent.md</c> content)
    /// by agent name. Returns <c>null</c> if no embedded template exists for the agent.
    /// </summary>
    public string? LoadMafAgentTemplate(string agentName)
        => ReadResourceText($"{ResourcePrefix}.agents.{agentName.ToLowerInvariant()}.agent.md");

    public string? LoadRaiPolicyTemplate()
    {
        var resourceName = $"{typeof(CatalogReader).Assembly.GetName().Name}.Catalog.Resources.agents.rai_policy.md";
        using var stream = typeof(CatalogReader).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // -----------------------------------------------------------------------
    // Workflow library
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads all predefined workflow YAML documents embedded under
    /// <c>Catalog/Resources/workflows</c>. Returns a list of (yaml, source) pairs
    /// where <c>source</c> is the short resource file name, sorted by name for
    /// deterministic conflict resolution in the <c>WorkflowRegistry</c>.
    /// </summary>
    public IReadOnlyList<(string Yaml, string Source)> LoadAllWorkflowYamls()
    {
        var prefix = $"{ResourcePrefix}.workflows.";
        var names = _asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) &&
                        (n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                         n.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.Ordinal);

        var result = new List<(string, string)>();
        foreach (var name in names)
        {
            var text = ReadResourceText(name);
            if (text is not null)
                result.Add((text, name[prefix.Length..]));
        }
        return result;
    }

    private sealed record CatalogManifestDto(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("templates")] IReadOnlyList<string>? Templates);

    private sealed record TemplateDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles);

    private sealed record RoleDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("default_model")] string? DefaultModel,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities,
        [property: JsonPropertyName("responsibilities")] IReadOnlyList<string>? Responsibilities,
        [property: JsonPropertyName("boundaries")] IReadOnlyList<string>? Boundaries);

    private sealed record BlueprintDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("roster")] IReadOnlyList<string>? Roster,
        [property: JsonPropertyName("workflow")] string? Workflow,
        [property: JsonPropertyName("workflows")] IReadOnlyList<string>? Workflows,
        [property: JsonPropertyName("review_policy")] string? ReviewPolicy,
        [property: JsonPropertyName("sandbox_profile")] string? SandboxProfile);
}
