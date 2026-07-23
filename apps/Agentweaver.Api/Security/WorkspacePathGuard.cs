using Agentweaver.SandboxFs;

namespace Agentweaver.Api.Security;

/// <summary>
/// Shared containment guard for workspace file access. A purely lexical check
/// (<see cref="Path.GetFullPath(string)"/> + <c>StartsWith(root)</c>) validates only the
/// pathname string — it does <b>not</b> reject symbolic links or reparse points. A malicious
/// repository (agent workspace content) can plant a symlink inside the worktree that points
/// outside the workspace root (e.g. at a secrets-store mount), and the lexical check would pass
/// while the subsequent <c>FileStream</c> / <c>ReadAllText</c> / <c>WriteAllText</c> silently
/// follows it. Production Azure Files has <c>mfsymlinks</c> enabled, so this is concretely
/// exploitable, not Windows-only.
/// </summary>
/// <remarks>
/// This guard resolves symlinks/junctions/reparse points on <b>both</b> the workspace root and the
/// candidate path (via <see cref="RealPath.Resolve(string)"/>, which uses
/// <c>GetFinalPathNameByHandle</c> on Windows and <c>realpath(3)</c> on Unix — following every
/// path component, not just the leaf) before deciding containment. For a candidate that does not
/// yet exist (e.g. a file about to be created), the deepest existing ancestor is resolved and the
/// not-yet-existing remainder is appended; this still rejects the case where an existing ancestor
/// component is a symlink escaping the root.
///
/// TOCTOU residual: a symlink target could change between resolution and the actual open. Callers
/// SHOULD open the returned <paramref name="resolvedPath"/> (already the real target) rather than
/// the original candidate to minimise the window, consistent with the accepted threat model for
/// authenticated callers documented on <see cref="RepositoryRootValidator"/>.
/// </remarks>
public static class WorkspacePathGuard
{
    /// <summary>
    /// Resolves <paramref name="candidateFullPath"/> and verifies its real target is contained
    /// within <paramref name="workspaceRoot"/> after symlinks/reparse points are resolved on both.
    /// Returns <c>true</c> and sets <paramref name="resolvedPath"/> to the real, contained path
    /// (which callers should use for the actual file open). Returns <c>false</c> for any path that
    /// resolves outside the root, when the root itself cannot be resolved, or on invalid input.
    /// </summary>
    public static bool TryResolveContainedPath(
        string workspaceRoot, string candidateFullPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(candidateFullPath))
            return false;

        string realRoot;
        try
        {
            realRoot = RealPath.Resolve(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (IOException)
        {
            // The workspace root does not exist or cannot be resolved — it can contain nothing.
            return false;
        }

        var full = Path.GetFullPath(candidateFullPath);

        // Resolve the deepest existing ancestor of the candidate, following symlinks, then
        // re-append the non-existent remainder segments. This rejects a symlink at ANY existing
        // component that escapes the workspace root, while still allowing a not-yet-created target.
        var remainder = new List<string>();
        var cursor = full;
        string? realExisting = null;

        while (true)
        {
            try
            {
                realExisting = RealPath.Resolve(cursor);
                break;
            }
            catch (IOException)
            {
                var parent = Path.GetDirectoryName(cursor);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal))
                    return false; // Walked past the filesystem root without finding a real ancestor.

                var leaf = Path.GetFileName(cursor);
                if (!string.IsNullOrEmpty(leaf))
                    remainder.Add(leaf);
                cursor = parent;
            }
        }

        var resolved = realExisting.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (remainder.Count > 0)
        {
            remainder.Reverse();
            var parts = new List<string>(remainder.Count + 1) { resolved };
            parts.AddRange(remainder);
            resolved = Path.GetFullPath(Path.Combine(parts.ToArray()));
        }

        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSep = realRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(resolved, realRoot, cmp) && !resolved.StartsWith(rootWithSep, cmp))
            return false;

        resolvedPath = resolved;
        return true;
    }

    /// <summary>
    /// Boolean convenience overload of <see cref="TryResolveContainedPath"/> for call sites that
    /// only need a pass/fail containment decision.
    /// </summary>
    public static bool IsContained(string workspaceRoot, string candidateFullPath) =>
        TryResolveContainedPath(workspaceRoot, candidateFullPath, out _);
}
