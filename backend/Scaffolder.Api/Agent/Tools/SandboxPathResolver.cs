namespace Scaffolder.Api.Agent.Tools;

/// <summary>
/// Result of a sandbox path resolution attempt.
/// </summary>
public sealed class SandboxResolutionResult
{
    private SandboxResolutionResult() { }

    public bool IsSuccess { get; private init; }
    public string? ResolvedPath { get; private init; }
    public SandboxErrorCode? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static SandboxResolutionResult Success(string resolvedPath) => new()
    {
        IsSuccess = true,
        ResolvedPath = resolvedPath
    };

    public static SandboxResolutionResult Rejected(SandboxErrorCode code, string message) => new()
    {
        IsSuccess = false,
        ErrorCode = code,
        ErrorMessage = message
    };
}

public enum SandboxErrorCode
{
    PathEscape,
    NotFound,
    Permission,
    Unknown
}

/// <summary>
/// Resolves agent-provided paths against the run's artifact directory sandbox.
/// 
/// SECURITY CONTRACT (SC-002, FR-006, FR-007):
/// - No absolute paths are accepted.
/// - No .. traversal is accepted (single or multi-hop).
/// - All symlinks are resolved and the canonical path must stay inside artifactDir.
/// - 100% of out-of-sandbox I/O is rejected — no exceptions, no bypass.
/// </summary>
public sealed class SandboxPathResolver
{
    /// <summary>
    /// Resolves the requested relative path against the artifact directory.
    /// </summary>
    /// <param name="requestedPath">The path as provided by the agent (must be relative).</param>
    /// <param name="artifactDir">The canonical absolute path to the artifact directory.</param>
    /// <returns>A resolution result indicating success or the specific error code.</returns>
    public SandboxResolutionResult Resolve(string requestedPath, string artifactDir)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return SandboxResolutionResult.Rejected(
                SandboxErrorCode.PathEscape,
                "Path must not be empty.");
        }

        // Reject absolute paths
        if (Path.IsPathRooted(requestedPath))
        {
            return SandboxResolutionResult.Rejected(
                SandboxErrorCode.PathEscape,
                $"Absolute paths are not permitted. Received: '{requestedPath}'");
        }

        // Reject paths containing .. segments before combining
        // (defends against encoding tricks before Path.GetFullPath normalization)
        var normalizedRequest = requestedPath.Replace('\\', '/');
        var segments = normalizedRequest.Split('/');
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return SandboxResolutionResult.Rejected(
                    SandboxErrorCode.PathEscape,
                    $"Path traversal sequences ('..') are not permitted. Received: '{requestedPath}'");
            }
        }

        // Combine and get full canonical path
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(artifactDir, requestedPath));
        }
        catch (Exception ex)
        {
            return SandboxResolutionResult.Rejected(
                SandboxErrorCode.PathEscape,
                $"Path could not be resolved: {ex.Message}");
        }

        // Canonical sandbox root — ensure it ends with separator for prefix check
        var canonicalArtifactDir = Path.GetFullPath(artifactDir);
        var sandboxRoot = canonicalArtifactDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // Primary boundary check: resolved path must be inside the artifact directory
        if (!combined.StartsWith(sandboxRoot, StringComparison.OrdinalIgnoreCase))
        {
            return SandboxResolutionResult.Rejected(
                SandboxErrorCode.PathEscape,
                $"Resolved path escapes the artifact directory. Path: '{requestedPath}'");
        }

        // Symlink resolution: if the file exists, resolve real path and re-check
        if (File.Exists(combined) || Directory.Exists(combined))
        {
            try
            {
                var realPath = Path.GetFullPath(combined);

                // On systems that support it, resolve symlinks
                var fileInfo = new FileInfo(combined);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var linkTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (linkTarget is not null)
                    {
                        realPath = Path.GetFullPath(linkTarget.FullName);
                    }
                }

                if (!realPath.StartsWith(sandboxRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return SandboxResolutionResult.Rejected(
                        SandboxErrorCode.PathEscape,
                        $"Symlink target escapes the artifact directory. Path: '{requestedPath}'");
                }
            }
            catch (Exception ex)
            {
                return SandboxResolutionResult.Rejected(
                    SandboxErrorCode.PathEscape,
                    $"Symlink resolution failed: {ex.Message}");
            }
        }

        return SandboxResolutionResult.Success(combined);
    }
}
