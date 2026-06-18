using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Squad.Model;

namespace Agentweaver.Squad.Catalog;

/// <summary>
/// Reads the embedded scenario catalog: team templates, role archetypes, and charter templates.
/// Resource names map directory separators to dots; hyphenated ids map to underscored file names.
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

        var result = new List<Role>();
        foreach (var resourceName in roleNames)
        {
            var id = resourceName[prefix.Length..^".json".Length].Replace('_', '-');
            var role = LoadRole(id);
            if (role is not null) result.Add(role);
        }
        return result;
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
}
