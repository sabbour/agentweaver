namespace Agentweaver.Api.Skills;

/// <summary>
/// Path-safety helpers shared by skill upload, zip extraction, and worktree materialization to
/// prevent path-traversal / zip-slip escapes — including Windows drive-qualified (<c>C:\</c>),
/// UNC, and rooted paths, not just POSIX <c>../</c> sequences.
/// </summary>
internal static class SkillPaths
{
    /// <summary>
    /// Normalizes an untrusted relative path (zip entry name, uploaded file path, bundled resource
    /// path) to a forward-slash relative path safe to combine under a skill directory, or returns
    /// null when the path is unsafe: rooted, drive-qualified/UNC, or containing empty, <c>.</c>,
    /// <c>..</c>, or colon-bearing segments.
    /// </summary>
    public static string? NormalizeRelative(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Replace('\\', '/').Trim().TrimStart('/');
        if (s.Length == 0)
            return null;

        // Drive-qualified (C:/…) or otherwise rooted survives TrimStart('/'); reject it.
        if (Path.IsPathRooted(s))
            return null;

        var segments = s.Split('/', StringSplitOptions.None);
        foreach (var seg in segments)
        {
            if (seg.Length == 0) return null;      // empty segment (e.g. "a//b")
            if (seg is "." or "..") return null;   // traversal
            if (seg.Contains(':')) return null;    // drive letter / NTFS alternate data stream
        }
        return string.Join('/', segments);
    }

    /// <summary>
    /// True when <paramref name="target"/> resolves to a path at or inside <paramref name="baseDir"/>.
    /// A defense-in-depth check applied after <see cref="NormalizeRelative"/> and Path.Combine.
    /// </summary>
    public static bool IsContained(string baseDir, string target)
    {
        var root = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(target);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
