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
    private readonly Assembly _asm;
    private readonly string _resourcePrefix;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string? Fid(string? id) => CatalogIdentifier.ToResourceStem(id);

    public CatalogReader()
        : this(typeof(CatalogReader).Assembly, "Agentweaver.Squad.Catalog.Resources")
    {
    }

    /// <summary>Creates a reader for an embedded catalog resource namespace.</summary>
    public CatalogReader(Assembly assembly, string resourcePrefix)
    {
        _asm = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _resourcePrefix = string.IsNullOrWhiteSpace(resourcePrefix)
            ? throw new ArgumentException("A resource prefix is required.", nameof(resourcePrefix))
            : resourcePrefix;
    }

    private string? ReadResourceText(string resourceName)
    {
        using var stream = _asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<TeamTemplate> LoadTemplates()
    {
        var manifestText = ReadResourceText($"{_resourcePrefix}.catalog.manifest.json");
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
        var stem = Fid(id);
        if (stem is null) return null;
        var text = ReadResourceText($"{_resourcePrefix}.groupings.{stem}.json");
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
        => LoadRoleResult(id).Role;

    /// <summary>Loads a role without allowing malformed embedded JSON to escape the catalog boundary.</summary>
    public CatalogRoleLoadResult LoadRoleResult(string id)
    {
        var stem = Fid(id);
        if (stem is null) return new CatalogRoleLoadResult(id, null, "invalid_id");
        var source = $"{stem}.json";
        var text = ReadResourceText($"{_resourcePrefix}.roles.{source}");
        if (text is null) return new CatalogRoleLoadResult(source, null, "missing");

        RoleDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RoleDto>(text, _json);
        }
        catch (JsonException)
        {
            return new CatalogRoleLoadResult(source, null, "malformed_json");
        }
        if (dto is null) return new CatalogRoleLoadResult(source, null, "missing_id");
        if (!CatalogIdentifier.IsSafe(dto.Id) ||
            !string.Equals(dto.Id, id, StringComparison.Ordinal))
            return new CatalogRoleLoadResult(source, null, "invalid_id");

        return new CatalogRoleLoadResult(source, new Role(
            dto.Id!,
            dto.Title ?? id,
            dto.Summary ?? string.Empty,
            dto.DefaultModel ?? string.Empty,
            dto.Capabilities ?? [],
            dto.Responsibilities ?? [],
            dto.Boundaries ?? []), null);
    }

    /// <summary>
    /// Loads every castable role in the catalog. Reserved, platform-owned orchestration roles
    /// (Scribe, Work Monitor, Rai, Coordinator -- see <see cref="ReservedRoles"/>) exist as catalog
    /// entries only so their built-in charters can be compiled by <c>CharterCompiler</c>; they are
    /// excluded here so they are never offered as a castable/domain role to a blueprint or workflow
    /// generator, or listed as a manual-casting option.
    /// </summary>
    public IReadOnlyList<Role> LoadAllRoles()
    {
        var prefix = $"{_resourcePrefix}.roles.";
        var roleNames = _asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));

        var byId = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in roleNames)
        {
            var id = resourceName[prefix.Length..^".json".Length].Replace('_', '-');
            if (!CatalogIdentifier.IsSafe(id)) continue;
            if (ReservedRoles.IsReserved(id)) continue;
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
        => LoadAllBlueprintLoadResults()
            .Where(result => result.Blueprint is not null)
            .Select(result => result.Blueprint!)
            .ToList();

    /// <summary>
    /// Loads every embedded blueprint with its resource-scoped parse result. Consumers that expose
    /// catalog diagnostics use this rather than silently dropping malformed assets.
    /// </summary>
    public IReadOnlyList<CatalogBlueprintLoadResult> LoadAllBlueprintLoadResults()
    {
        var prefix = $"{_resourcePrefix}.blueprints.";
        var names = _asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        var result = new List<CatalogBlueprintLoadResult>();
        foreach (var resourceName in names)
        {
            var text = ReadResourceText(resourceName);
            result.Add(ParseBlueprint(text, resourceName[prefix.Length..]));
        }
        return result;
    }

    /// <summary>Loads a single predefined blueprint by id, or null when none is embedded.</summary>
    public Blueprint? LoadBlueprint(string id)
    {
        var stem = Fid(id);
        if (stem is null) return null;
        var text = ReadResourceText($"{_resourcePrefix}.blueprints.{stem}.json");
        return ParseBlueprint(text, $"{stem}.json").Blueprint;
    }

    private static CatalogBlueprintLoadResult ParseBlueprint(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CatalogBlueprintLoadResult(source, null, "missing blueprint resource content");
        if (text.Length > 262_144)
            return new CatalogBlueprintLoadResult(source, null, "blueprint resource exceeds the 262144 character limit");

        BlueprintDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BlueprintDto>(text, _json);
        }
        catch (JsonException)
        {
            return new CatalogBlueprintLoadResult(source, null, "malformed blueprint JSON");
        }
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
            return new CatalogBlueprintLoadResult(source, null, "blueprint id is missing");

        // Prefer the explicit workflows array; fall back to wrapping the legacy single workflow string.
        IReadOnlyList<string> workflows = dto.Workflows is { Count: > 0 }
            ? dto.Workflows
            : dto.Workflow is not null
                ? (IReadOnlyList<string>)[dto.Workflow]
                : (IReadOnlyList<string>)["default"];

        return new CatalogBlueprintLoadResult(source, new Blueprint(
            dto.Id!,
            dto.Name ?? dto.Id!,
            dto.Description ?? string.Empty,
            dto.Roster ?? [],
            workflows,
            dto.ReviewPolicy ?? "default",
            dto.SandboxProfile ?? "default")
        {
            SkillBindings = (dto.SkillBindings ?? [])
                .Where(binding => !string.IsNullOrWhiteSpace(binding.RoleId))
                .Select(binding => new BlueprintSkillBinding(
                    binding.RoleId!,
                    binding.Skills?.Where(skill => !string.IsNullOrWhiteSpace(skill)).ToArray() ?? []))
                .ToArray(),
        }, null);
    }

    public string? LoadCharterTemplate(string roleId)
    {
        var stem = Fid(roleId);
        return stem is null ? null : ReadResourceText($"{_resourcePrefix}.charters.{stem}.md");
    }

    /// <summary>
    /// Loads a built-in MAF agent template (<c>.github/agents/{name}.agent.md</c> content)
    /// by agent name. Returns <c>null</c> if no embedded template exists for the agent.
    /// </summary>
    public string? LoadMafAgentTemplate(string agentName)
        => ReadResourceText($"{_resourcePrefix}.agents.{agentName.ToLowerInvariant()}.agent.md");

    public string? LoadRaiPolicyTemplate()
    {
        var resourceName = $"{_resourcePrefix}.agents.rai_policy.md";
        using var stream = _asm.GetManifestResourceStream(resourceName);
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
        var prefix = $"{_resourcePrefix}.workflows.";
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
        [property: JsonPropertyName("sandbox_profile")] string? SandboxProfile,
        [property: JsonPropertyName("skill_bindings")] IReadOnlyList<BlueprintSkillBindingDto>? SkillBindings);

    private sealed record BlueprintSkillBindingDto(
        [property: JsonPropertyName("role_id")] string? RoleId,
        [property: JsonPropertyName("skills")] IReadOnlyList<string>? Skills);
}

/// <summary>A blueprint resource and its source-scoped loading outcome.</summary>
public sealed record CatalogBlueprintLoadResult(string Source, Blueprint? Blueprint, string? Error);

/// <summary>A role resource and its sanitized loading outcome.</summary>
public sealed record CatalogRoleLoadResult(string Source, Role? Role, string? ErrorCode);
