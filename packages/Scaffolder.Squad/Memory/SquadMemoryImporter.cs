namespace Scaffolder.Squad.Memory;

/// <summary>
/// Scans .squad/decisions/inbox/ for pending decision files and parses their front-matter.
/// No EF dependency — produces plain DTOs for the API layer to persist.
/// </summary>
public sealed class SquadMemoryImporter
{
    private readonly string _workingDirectory;

    public SquadMemoryImporter(string workingDirectory)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    /// <summary>
    /// Enumerates all *.md files under .squad/decisions/inbox/ and parses front-matter.
    /// Files that cannot be parsed are silently skipped.
    /// </summary>
    public IEnumerable<InboxImportDto> ScanInboxFiles()
    {
        var inboxDir = Path.Combine(_workingDirectory, ".squad", "decisions", "inbox");
        if (!Directory.Exists(inboxDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(inboxDir, "*.md"))
        {
            InboxImportDto? dto = null;
            try { dto = ParseFile(file); }
            catch { /* skip unparseable files */ }
            if (dto is not null) yield return dto;
        }
    }

    private static InboxImportDto? ParseFile(string filePath)
    {
        var text = File.ReadAllText(filePath);

        // Expect YAML front-matter delimited by ---
        if (!text.StartsWith("---")) return null;
        var end = text.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return null;

        var frontMatter = text[3..end].Trim();
        var body = text[(end + 3)..].Trim();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontMatter.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            fields[key] = value;
        }

        if (!fields.TryGetValue("agent", out var agentName) || string.IsNullOrWhiteSpace(agentName)) return null;
        if (!fields.TryGetValue("slug", out var slug) || string.IsNullOrWhiteSpace(slug)) return null;
        if (!fields.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type)) return null;
        if (!fields.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) return null;

        // Rationale may appear as a bold section in the body
        string? rationale = null;
        string content = body;
        const string rationalePrefix = "**Rationale:**";
        var rationaleIdx = body.IndexOf(rationalePrefix, StringComparison.Ordinal);
        if (rationaleIdx >= 0)
        {
            content = body[..rationaleIdx].Trim();
            rationale = body[(rationaleIdx + rationalePrefix.Length)..].Trim();
        }

        return new InboxImportDto(agentName, slug, type, title, content, rationale);
    }
}
