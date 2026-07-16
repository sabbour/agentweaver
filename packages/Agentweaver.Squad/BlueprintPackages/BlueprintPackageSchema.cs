namespace Agentweaver.Squad.BlueprintPackages;

/// <summary>
/// Draft 2020-12 schema for the on-wire manifest. The validator is authoritative for byte-oriented
/// checks (hashes, exact numbers, duplicate JSON names); its grammar intentionally mirrors this schema.
/// </summary>
public static class BlueprintPackageSchema
{
    public const string Id = "https://agentweaver.dev/schemas/blueprint-package-v1.json";
    private const string ResourceName = "Agentweaver.Squad.BlueprintPackages.Schemas.blueprint-package-v1.schema.json";

    /// <summary>The versioned schema embedded with the validator so callers cannot drift from its grammar.</summary>
    public static string Json { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(BlueprintPackageSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Blueprint package schema: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
