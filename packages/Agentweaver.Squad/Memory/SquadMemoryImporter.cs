namespace Agentweaver.Squad.Memory;

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
    /// Legacy files without front-matter remain supported; malformed front-matter is reported.
    /// </summary>
    public IEnumerable<InboxImportDto> ScanInboxFiles() => ScanInbox().Entries;

    public InboxImportScanResult ScanInbox()
    {
        var inboxDir = Path.Combine(_workingDirectory, ".squad", "decisions", "inbox");
        if (!Directory.Exists(inboxDir))
            return new InboxImportScanResult([], []);

        var entries = new List<InboxImportDto>();
        var conflicts = new List<InboxImportConflict>();
        foreach (var file in Directory.EnumerateFiles(inboxDir, "*.md"))
        {
            try
            {
                var dto = ParseFile(file);
                if (dto is null)
                    conflicts.Add(new(Path.GetFileName(file), "Front-matter is missing a required field."));
                else
                    entries.Add(dto);
            }
            catch (Exception ex)
            {
                conflicts.Add(new(Path.GetFileName(file), ex.Message));
            }
        }

        return new InboxImportScanResult(entries, conflicts);
    }

    public IReadOnlyList<DecisionImportDto> ScanAcceptedDecisions()
    {
        var path = Path.Combine(_workingDirectory, ".squad", "decisions.md");
        if (!File.Exists(path))
            return [];

        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = ("\n" + text).Split("\n## ", StringSplitOptions.None);
        var decisions = new List<DecisionImportDto>();
        foreach (var section in sections.Skip(1))
        {
            var newline = section.IndexOf('\n');
            var title = (newline < 0 ? section : section[..newline]).Trim();
            var body = newline < 0 ? "" : section[(newline + 1)..].Trim();
            if (title.Length == 0)
                continue;

            var agent = "repository";
            var type = "process";
            string? rationale = null;
            if (body.StartsWith("**Type:** ", StringComparison.Ordinal))
            {
                var metadataEnd = body.IndexOf('\n');
                var metadata = metadataEnd < 0 ? body : body[..metadataEnd];
                body = metadataEnd < 0 ? "" : body[(metadataEnd + 1)..].Trim();
                type = ReadMetadata(metadata, "**Type:** ", " |") ?? type;
                agent = ReadMetadata(metadata, "**By:** ", " |") ?? agent;
            }
            else
            {
                const string consolidated = " — Consolidated decision: ";
                var consolidatedAt = title.IndexOf(consolidated, StringComparison.Ordinal);
                if (consolidatedAt >= 0)
                    title = title[(consolidatedAt + consolidated.Length)..].Trim();
            }

            var markerAt = body.IndexOf("<!-- squad-consolidated:", StringComparison.Ordinal);
            if (markerAt >= 0)
                body = body[..markerAt].Trim();
            if (body.EndsWith("---", StringComparison.Ordinal))
                body = body[..^3].Trim();

            const string rationalePrefix = "> **Rationale:** ";
            var rationaleAt = body.IndexOf(rationalePrefix, StringComparison.Ordinal);
            if (rationaleAt >= 0)
            {
                rationale = body[(rationaleAt + rationalePrefix.Length)..].Trim();
                body = body[..rationaleAt].Trim();
            }

            decisions.Add(new DecisionImportDto(
                agent, type, title, body.Length == 0 ? title : body, rationale));
        }

        return decisions;
    }

    private static InboxImportDto? ParseFile(string filePath)
    {
        var text = File.ReadAllText(filePath);

        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            var legacySlug = Path.GetFileNameWithoutExtension(filePath);
            var separator = legacySlug.IndexOf('-');
            var agent = separator > 0 ? legacySlug[..separator] : "repository";
            return new InboxImportDto(agent, legacySlug, "process", legacySlug, text.Trim(), null);
        }

        var end = text.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0)
            throw new FormatException("Front-matter is not terminated.");

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

    private static string? ReadMetadata(string metadata, string prefix, string terminator)
    {
        var start = metadata.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += prefix.Length;
        var end = metadata.IndexOf(terminator, start, StringComparison.Ordinal);
        return (end < 0 ? metadata[start..] : metadata[start..end]).Trim();
    }
}
