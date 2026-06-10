// Smith: see spec FR-007, SC-002 - 100% path-escape rejection required
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Scaffolder.SandboxFs;

/// <summary>
/// Validates that agent-supplied paths resolve to a location strictly inside
/// the sandbox root, with no symlink, junction, or traversal escape. Uses an
/// open-then-verify strategy so that time-of-check/time-of-use races and
/// reparse-point redirection are both defeated.
/// </summary>
public static class SandboxPathValidator
{
    /// <summary>
    /// Validates that <paramref name="requestedPath"/> (relative, from the agent)
    /// resolves to a location inside <paramref name="sandboxRoot"/> with no
    /// symlink/junction escape. Returns the validated absolute path on success.
    /// Throws <see cref="SandboxViolationException"/> on any escape attempt.
    /// </summary>
    public static string ValidateAndResolve(string requestedPath, string sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new SandboxViolationException(requestedPath ?? string.Empty, sandboxRoot, "empty path is not permitted");

        // 1. Reject absolute paths outright.
        if (Path.IsPathRooted(requestedPath))
            throw new SandboxViolationException(requestedPath, sandboxRoot, "absolute paths are not permitted");

        // 2. Segment scan - reject any ".." component before combining.
        var segments = requestedPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new SandboxViolationException(requestedPath, sandboxRoot, "path traversal (..) is not permitted");

        // 3. Combine with sandbox root using lexical Path.GetFullPath.
        var combined = Path.GetFullPath(Path.Combine(sandboxRoot, requestedPath));

        // 4. Lexical prefix check (catches obvious escapes after normalization).
        var rootNoSep = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!combined.StartsWith(rootNoSep + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, rootNoSep, StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolationException(requestedPath, sandboxRoot, "path resolves outside sandbox boundary");

        // 5. Walk each existing ancestor and reject reparse points (symlinks, junctions).
        ValidateNoReparsePointsInAncestors(combined, sandboxRoot);

        return combined;
    }

    private static void ValidateNoReparsePointsInAncestors(string fullPath, string sandboxRoot)
    {
        var rootFull = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar);
        var current = fullPath;

        while (true)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;

            // Stop once we reach or pass the sandbox root - the root itself is trusted.
            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)) break;
            if (!current.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) break;

            if (Directory.Exists(current))
            {
                var di = new DirectoryInfo(current);
                if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(fullPath, sandboxRoot,
                        $"path component '{current}' is a symbolic link or junction");
            }
            else if (File.Exists(current))
            {
                var fi = new FileInfo(current);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(fullPath, sandboxRoot,
                        $"'{current}' is a symbolic link");
            }

            current = parent;
        }
    }

    /// <summary>
    /// Validates that <paramref name="absolutePath"/> (an absolute path from an
    /// external system such as the Copilot SDK permission request) resolves to a
    /// location strictly inside <paramref name="sandboxRoot"/>.
    /// Throws <see cref="SandboxViolationException"/> on any escape.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ValidateAndResolve"/>, this method EXPECTS an absolute
    /// path and does NOT reject it for being rooted. It performs:
    /// 1. Null/empty check.
    /// 2. Early-reject device paths (\\?\, \\.\) and UNC (\\server\share).
    /// 3. Early-reject drive-relative paths (e.g. C:foo — no separator after colon).
    /// 4. IsPathRooted assertion (must be absolute; relative input is a caller bug).
    /// 5. GetFullPath normalization (resolves ., .., trailing separators).
    /// 6. Lexical prefix check: normalized path must start with
    ///    (normalizedRoot + DirectorySeparatorChar) OR equal normalizedRoot exactly
    ///    (for directory-listing operations targeting the root itself).
    /// 7. Reparse-point ancestor walk (same as ValidateAndResolve).
    /// </remarks>
    public static string ValidateAbsoluteContained(string absolutePath, string sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new SandboxViolationException(absolutePath ?? string.Empty, sandboxRoot, "empty path is not permitted");

        // Early-reject device paths (\\?\ and \\.\)
        if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            absolutePath.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new SandboxViolationException(absolutePath, sandboxRoot, "device paths are not permitted");

        // Early-reject UNC paths (\\server\share)
        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new SandboxViolationException(absolutePath, sandboxRoot, "UNC paths are not permitted");

        // Early-reject drive-relative paths (e.g. C:foo — colon not followed by separator)
        if (absolutePath.Length >= 2 && absolutePath[1] == ':' &&
            (absolutePath.Length == 2 || (absolutePath[2] != Path.DirectorySeparatorChar && absolutePath[2] != Path.AltDirectorySeparatorChar)))
            throw new SandboxViolationException(absolutePath, sandboxRoot, "drive-relative paths are not permitted");

        // Must be absolute — relative input here is a caller bug
        if (!Path.IsPathRooted(absolutePath))
            throw new SandboxViolationException(absolutePath, sandboxRoot, "path must be absolute");

        // Normalize both paths
        var normalized = Path.GetFullPath(absolutePath);
        var rootNormalized = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar);
        var rootPrefix = rootNormalized + Path.DirectorySeparatorChar;

        // Lexical prefix check (case-insensitive on Windows)
        if (!normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, rootNormalized, StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolationException(absolutePath, sandboxRoot, "path resolves outside sandbox boundary");

        // Reparse-point ancestor walk
        ValidateNoReparsePointsInAncestors(normalized, sandboxRoot);

        return normalized;
    }

    /// <summary>
    /// After opening a file handle, resolve the real path and re-verify it is
    /// inside the sandbox. Defeats reparse-point redirection that a lexical
    /// check cannot see. Platform-specific: GetFinalPathNameByHandle on Windows,
    /// /proc/self/fd on Linux.
    /// </summary>
    public static void VerifyOpenedHandle(SafeFileHandle handle, string sandboxRoot, string originalPath)
    {
        string? realPath = OperatingSystem.IsWindows()
            ? GetFinalPathWindows(handle)
            : GetFinalPathUnix(handle);

        if (realPath is null)
            throw new SandboxViolationException(originalPath, sandboxRoot, "could not resolve real path of opened file");

        var rootNoSep = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!realPath.StartsWith(rootNoSep + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(realPath, rootNoSep, StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolationException(originalPath, sandboxRoot,
                $"opened file resolves to '{realPath}' which is outside sandbox boundary");
    }

    [SupportedOSPlatform("windows")]
    private static string? GetFinalPathWindows(SafeFileHandle handle)
    {
        const uint FILE_NAME_NORMALIZED = 0x0;
        var sb = new StringBuilder(32768);
        uint result = GetFinalPathNameByHandle(handle.DangerousGetHandle(), sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
        if (result == 0) return null;

        var path = sb.ToString();
        // Strip the \\?\ extended-length prefix if present.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    private static string? GetFinalPathUnix(SafeFileHandle handle)
    {
        // On Linux, /proc/self/fd/{fd} is a symlink to the real path.
        var fdPath = $"/proc/self/fd/{handle.DangerousGetHandle()}";
        if (File.Exists(fdPath) || Directory.Exists(fdPath))
        {
            var resolved = new FileInfo(fdPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (resolved is not null) return resolved;
        }

        return null;
    }
}
