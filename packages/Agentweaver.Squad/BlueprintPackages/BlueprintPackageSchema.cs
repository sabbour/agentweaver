using System.Text.Json;

namespace Agentweaver.Squad.BlueprintPackages;

/// <summary>
/// Draft 2020-12 schema for the on-wire manifest. The validator is authoritative for byte-oriented
/// checks (hashes, exact numbers, duplicate JSON names). The canonical-definition-path assertion is
/// declared in the schema's Agentweaver vocabulary and evaluated here because standard JSON Schema
/// cannot compare an entry's <c>path</c> to its sibling <c>kind</c> and <c>id</c> properties.
/// </summary>
public static class BlueprintPackageSchema
{
    public const string Id = "https://agentweaver.dev/schemas/blueprint-package-v1.json";
    public const string VocabularyId = "https://agentweaver.dev/vocab/blueprint-package-v1";
    public const string CanonicalDefinitionPathKeyword = "x-agentweaver-canonical-definition-path";
    private const string ResourceName = "Agentweaver.Squad.BlueprintPackages.Schemas.blueprint-package-v1.schema.json";

    /// <summary>The versioned schema embedded with the validator so callers cannot drift from its grammar.</summary>
    public static string Json { get; } = Load();

    /// <summary>Evaluates the Agentweaver-specific assertions declared by the distributed schema.</summary>
    public static void ValidateCustomKeywords(JsonElement manifest, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("definitions", out var definitions)
            || definitions.ValueKind != JsonValueKind.Array)
            return;

        foreach (var definition in definitions.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object
                || !definition.TryGetProperty("kind", out var kind)
                || !definition.TryGetProperty("id", out var id)
                || !definition.TryGetProperty("path", out var path)
                || kind.ValueKind != JsonValueKind.String
                || id.ValueKind != JsonValueKind.String
                || path.ValueKind != JsonValueKind.String)
                continue;

            var expected = kind.GetString() switch
            {
                "blueprint" => $"definitions/blueprints/{id.GetString()}.json",
                "role" => $"definitions/roles/{id.GetString()}.json",
                "workflow" => $"definitions/workflows/{id.GetString()}.yaml",
                "skill" => $"definitions/skills/{id.GetString()}.md",
                _ => null,
            };
            if (expected is not null && !string.Equals(path.GetString(), expected, StringComparison.Ordinal))
                errors.Add("definition.path is not a canonical definitions-only path.");
        }
    }

    private static string Load()
    {
        using var stream = typeof(BlueprintPackageSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Blueprint package schema: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
