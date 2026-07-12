namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// #223 — deterministic parser for the OPTIONAL <c>TARGET_FILES:</c> directive a reviewer (rubber-duck
/// or build-test) may emit alongside its PASS/REVISE verdict. This is STRUCTURED output — a dedicated,
/// machine-readable line the reviewer is explicitly instructed to write — NOT natural-language prose
/// scraping. The coordinator reverse-maps these repo-relative paths onto the subtasks that touched them
/// so a request-changes only implicates the authors who actually produced the rejected files. Absent or
/// blank directives yield <c>null</c> (the coordinator then fails safe to the whole contributor set).
/// </summary>
public static class ReviewTargetFiles
{
    private const string Marker = "TARGET_FILES";

    /// <summary>
    /// Parses every <c>TARGET_FILES:</c> directive line in <paramref name="response"/> into a
    /// deduplicated, forward-slash-normalized path list. Returns <c>null</c> when no directive is
    /// present (so the caller can distinguish "no field" from "empty field").
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in response.Split('\n'))
        {
            if (!TrySplitDirective(rawLine, out var payload))
                continue;

            foreach (var part in payload.Split(
                [',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var file = part.Trim().Trim('`', '"', '\'').Trim().Replace('\\', '/').TrimStart('/');
                if (file.Length > 0 && seen.Add(file))
                    files.Add(file);
            }
        }

        return files.Count > 0 ? files : null;
    }

    /// <summary>
    /// True when <paramref name="line"/> is a <c>TARGET_FILES:</c> directive, so verdict-feedback
    /// extractors can drop it from the human-facing prose feedback.
    /// </summary>
    public static bool IsDirectiveLine(string? line) => TrySplitDirective(line, out _);

    private static bool TrySplitDirective(string? rawLine, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(rawLine)) return false;

        var line = StripLeadingMarkers(rawLine);
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;

        var key = line[..colon].Trim().Replace('-', '_').Replace(" ", string.Empty);
        if (!key.Equals(Marker, StringComparison.OrdinalIgnoreCase)) return false;

        payload = line[(colon + 1)..].Trim();
        return true;
    }

    private static string StripLeadingMarkers(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is ' ' or '\t' or '\r' or '-' or '*' or '#' or '>' or '`')
                i++;
            else
                break;
        }
        return i > 0 ? line[i..] : line;
    }
}
