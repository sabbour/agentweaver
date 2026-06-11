using System.Text.RegularExpressions;
using Scaffolder.Api.Contracts;

namespace Scaffolder.Api.Git;

/// <summary>
/// Parses stored unified diff strings to extract file-entry metadata.
/// Used to serve the changed-file set for terminal-state runs whose worktrees
/// have been removed (merged, declined, failed).
/// </summary>
public static class WorkspaceFileEntryParser
{
    /// <summary>
    /// Parses a unified diff string and returns one WorkspaceFileEntry per file,
    /// all with scope "merged". Returns an empty list for an empty or null diff.
    /// </summary>
    public static IReadOnlyList<WorkspaceFileEntry> ParseUnifiedDiffEntries(string diff)
    {
        if (string.IsNullOrEmpty(diff)) return [];

        var entries = new List<WorkspaceFileEntry>();
        var sections = Regex.Split(diff, @"(?=^diff --git )", RegexOptions.Multiline);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

                // Anchor /dev/null detection to file header lines (before the first @@ hunk marker)
                // to avoid false matches against hunk content that happens to contain /dev/null.
                var headerLines = section
                    .Split('\n')
                    .TakeWhile(l => !l.StartsWith("@@"))
                    .ToList();

                bool isAdded   = headerLines.Any(l => l.StartsWith("--- /dev/null", StringComparison.Ordinal));
                bool isDeleted = headerLines.Any(l => l.StartsWith("+++ /dev/null", StringComparison.Ordinal));

                // Binary diffs use "Binary files ... differ" lines instead of --- / +++ headers.
                if (!isAdded && !isDeleted)
                {
                    if (headerLines.Any(l => Regex.IsMatch(l, @"^Binary files /dev/null and b/.+ differ$")))
                        isAdded = true;
                    else if (headerLines.Any(l => Regex.IsMatch(l, @"^Binary files a/.+ and /dev/null differ$")))
                        isDeleted = true;
                    // Generic "Binary files a/X and b/Y differ" falls through to the modified branch.
                }

            string  status;
            string? filePath = null;

            if (isAdded)
            {
                status = "added";
                var m = Regex.Match(section, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline);
                if (m.Success) filePath = m.Groups[1].Value.TrimEnd('\r');
            }
            else if (isDeleted)
            {
                status = "deleted";
                var m = Regex.Match(section, @"^--- a/(.+)$", RegexOptions.Multiline);
                if (m.Success) filePath = m.Groups[1].Value.TrimEnd('\r');
            }
            else
            {
                status = "modified";
                // Prefer the destination path (+++ b/...) to handle renames correctly.
                var m = Regex.Match(section, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline);
                if (m.Success)
                {
                    filePath = m.Groups[1].Value.TrimEnd('\r');
                }
                else
                {
                    var m2 = Regex.Match(section, @"^--- a/(.+)$", RegexOptions.Multiline);
                    if (m2.Success) filePath = m2.Groups[1].Value.TrimEnd('\r');
                }
            }

            // Fallback: extract destination from the "diff --git a/X b/Y" header.
            if (filePath is null)
            {
                var m = Regex.Match(section, @"^diff --git a/.+ b/(.+)$", RegexOptions.Multiline);
                if (m.Success) filePath = m.Groups[1].Value.TrimEnd('\r');
            }

            if (filePath is not null)
            {
                CountDiffLines(section, out int added, out int removed);
                entries.Add(new WorkspaceFileEntry
                {
                    Path         = filePath,
                    Status       = status,
                    Scope        = "merged",
                    AddedLines   = added,
                    RemovedLines = removed,
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Counts added and removed lines in a single-file unified diff section.
    /// Lines starting with '+' (excluding '+++' headers) count as added;
    /// lines starting with '-' (excluding '---' headers) count as removed.
    /// </summary>
    public static void CountDiffLines(string section, out int added, out int removed)
    {
        added   = 0;
        removed = 0;
        if (string.IsNullOrEmpty(section)) return;

        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++")) added++;
            else if (line.StartsWith('-') && !line.StartsWith("---")) removed++;
        }
    }

    /// <summary>
    /// Extracts a single file's diff section from a stored unified diff string.
    /// Returns (null, true) when the section is a binary comparison.
    /// Returns (null, false) when the path is not present in the diff.
    /// </summary>
    public static (string? Diff, bool IsBinary) ParseFileDiffFromUnifiedDiff(
        string unifiedDiff, string normalizedPath)
    {
        if (string.IsNullOrEmpty(unifiedDiff)) return (null, false);

        var sections = Regex.Split(unifiedDiff, @"(?=^diff --git )", RegexOptions.Multiline);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            bool matchesPath =
                section.Contains($"+++ b/{normalizedPath}\n",   StringComparison.Ordinal) ||
                section.Contains($"+++ b/{normalizedPath}\r\n", StringComparison.Ordinal) ||
                section.Contains($"--- a/{normalizedPath}\n",   StringComparison.Ordinal) ||
                section.Contains($"--- a/{normalizedPath}\r\n", StringComparison.Ordinal);

            // Cover binary-diff lines for both added ("Binary files /dev/null and b/X differ")
            // and deleted ("Binary files a/X and /dev/null differ") cases.
            if (!matchesPath && section.Contains("Binary files", StringComparison.Ordinal))
            {
                matchesPath =
                    Regex.IsMatch(
                        section,
                        $@"Binary files .+ b/{Regex.Escape(normalizedPath)} differ",
                        RegexOptions.Multiline) ||
                    Regex.IsMatch(
                        section,
                        $@"Binary files a/{Regex.Escape(normalizedPath)} and /dev/null differ",
                        RegexOptions.Multiline);
            }

            if (matchesPath)
            {
                bool isBinary = section.Contains("Binary files", StringComparison.Ordinal);
                return (isBinary ? null : section, isBinary);
            }
        }

        return (null, false);
    }
}
