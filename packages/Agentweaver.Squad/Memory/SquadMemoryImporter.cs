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
        var entries = new List<InboxImportDto>();
        var conflicts = new List<InboxImportConflict>();
        try
        {
            EnsureSafePath(inboxDir);
            if (!Directory.Exists(inboxDir))
                return new InboxImportScanResult([], []);

            foreach (var file in Directory.EnumerateFiles(inboxDir, "*.md"))
            {
                try
                {
                    EnsureSafePath(file);
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
        }
        catch (Exception ex)
        {
            conflicts.Add(new(".squad/decisions/inbox", ex.Message));
        }

        return new InboxImportScanResult(entries, conflicts);
    }

    public IReadOnlyList<DecisionImportDto> ScanAcceptedDecisions()
    {
        var path = Path.Combine(_workingDirectory, ".squad", "decisions.md");
        EnsureSafePath(path);
        if (!File.Exists(path))
            return [];

        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var decisions = new List<DecisionImportDto>();
        const string marker = "<!-- agentweaver-decision:id=";
        var starts = FindMarkerOffsets(text, marker);
        if (starts.Count == 0)
        {
            if (!string.Equals(text.Trim(), "# Team Decisions", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(text))
                decisions.Add(new DecisionImportDto(
                    null, null, "repository", "process", "Repository decision ledger import",
                    text.Trim(), null, false));
            return decisions;
        }

        foreach (var (start, end) in starts.Zip(starts.Skip(1).Append(text.Length)))
        {
            var section = text[start..end];
            var markerEnd = section.IndexOf("-->", StringComparison.Ordinal);
            if (markerEnd < 0)
                throw new FormatException("Decision record marker is not terminated.");
            var markerContent = section[..markerEnd];
            var attributes = markerContent["<!-- agentweaver-decision:".Length..];
            var recordId = ReadMarkerInt(attributes, "id");
            var contentHash = ReadMarkerValue(attributes, "sha256");
            if (recordId is null || string.IsNullOrWhiteSpace(contentHash))
                throw new FormatException("Decision record marker is missing id or sha256.");

            var record = section[(markerEnd + 3)..].Trim();
            if (!record.StartsWith("## ", StringComparison.Ordinal))
                throw new FormatException($"Decision record {recordId} is missing its title.");
            var newline = record.IndexOf('\n');
            var title = (newline < 0 ? record[3..] : record[3..newline]).Trim();
            var body = newline < 0 ? "" : record[(newline + 1)..].Trim();
            if (body.EndsWith("---", StringComparison.Ordinal))
                body = body[..^3].Trim();

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
            const string rationalePrefix = "> **Rationale:** ";
            var rationaleAt = body.IndexOf(rationalePrefix, StringComparison.Ordinal);
            if (rationaleAt >= 0)
            {
                rationale = body[(rationaleAt + rationalePrefix.Length)..].Trim();
                body = body[..rationaleAt].Trim();
            }

            decisions.Add(new DecisionImportDto(
                recordId, contentHash, agent, type, title, body.Length == 0 ? title : body, rationale, true));
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

    private static List<int> FindMarkerOffsets(string text, string marker)
    {
        var offsets = new List<int>();
        for (var offset = 0; (offset = text.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0; offset += marker.Length)
            offsets.Add(offset);
        return offsets;
    }

    private static int? ReadMarkerInt(string text, string name) =>
        int.TryParse(ReadMarkerValue(text, name), out var value) ? value : null;

    private static string? ReadMarkerValue(string text, string name)
    {
        var prefix = name + "=";
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = text.IndexOf(';', start);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    private void EnsureSafePath(string path)
    {
        var root = Path.GetFullPath(_workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ledger path escapes the repository root.");
        var relative = Path.GetRelativePath(_workingDirectory, fullPath);
        var current = Path.GetFullPath(_workingDirectory);
        EnsurePathIsNotReparsePoint(current);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "." or "") continue;
            current = Path.Combine(current, segment);
            EnsurePathIsNotReparsePoint(current);
        }
    }

    private static void EnsurePathIsNotReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Ledger paths must not traverse symbolic links or reparse points.");
        }
        catch (FileNotFoundException)
        {
            // LinkTarget identifies dangling links that File.GetAttributes cannot resolve.
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new InvalidOperationException("Ledger paths must not traverse symbolic links or reparse points.");
        }
        catch (DirectoryNotFoundException)
        {
            // A nonexistent ordinary target is allowed; later segments cannot exist.
        }
    }
}
