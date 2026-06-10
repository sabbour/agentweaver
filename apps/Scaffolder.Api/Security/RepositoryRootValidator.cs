using Microsoft.Extensions.Logging;
using Scaffolder.Api.Runs;
using Scaffolder.SandboxFs;

namespace Scaffolder.Api.Security;

/// <summary>
/// Validates and canonicalizes repository paths submitted via POST /api/runs.
/// Enforces an optional allowlist of permitted repository roots and rejects
/// UNC, device, relative, and drive-relative paths unconditionally.
/// </summary>
public sealed class RepositoryRootValidator
{
    private readonly string[] _resolvedRoots;
    private readonly StringComparison _pathComparison;
    private readonly ILogger<RepositoryRootValidator> _logger;

    // Categorical message shared across not-found, not-allowed, and resolution-failure
    // to avoid leaking a path-existence oracle (Seraph M3, S2).
    private const string PathRejectedMessage =
        "repository_path is not within an allowed repository root.";

    public RepositoryRootValidator(IConfiguration configuration, ILogger<RepositoryRootValidator> logger)
    {
        _logger = logger;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var configuredRoots = configuration.GetSection("Runs:AllowedRepositoryRoots").Get<string[]>() ?? [];

        var resolved = new List<string>();
        foreach (var root in configuredRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            var canonical = Path.GetFullPath(root);
            try
            {
                var real = RealPath.Resolve(canonical);
                resolved.Add(real.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (IOException)
            {
                // Root does not exist or cannot be resolved at startup — skip it.
                // Log at debug level; the warning below covers the empty case.
                _logger.LogDebug("Configured repository root could not be resolved and will be skipped.");
            }
        }

        _resolvedRoots = resolved.ToArray();

        if (_resolvedRoots.Length == 0)
        {
            _logger.LogWarning(
                "No repository allowlist configured — any local absolute repository path will be accepted. " +
                "Configure Runs:AllowedRepositoryRoots for shared or exposed deployments.");
        }
    }

    /// <summary>
    /// Validates and canonicalizes <paramref name="repositoryPath"/>.
    /// Returns the canonical path (via <see cref="Path.GetFullPath"/>) on success.
    /// Throws <see cref="RunSubmissionValidationException"/> on any rejection.
    /// </summary>
    /// <remarks>
    /// TOCTOU residual: the path could be swapped (e.g., symlink target changed) between
    /// validation at submission time and actual use by WorktreeManager. This is accepted
    /// under the authenticated-API threat model where callers are trusted principals and
    /// the allowlist is a defense-in-depth control, not a hard security boundary against
    /// a local attacker with filesystem write access.
    /// </remarks>
    public string ValidateAndCanonicalize(string repositoryPath)
    {
        // 1. Null/empty/whitespace
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new RunSubmissionValidationException("repository_path must be a non-empty absolute path.");

        // 2. Reject Alternate Data Streams: a ':' at any position other than index 1 (drive letter)
        for (int i = 0; i < repositoryPath.Length; i++)
        {
            if (repositoryPath[i] == ':' && i != 1)
                throw new RunSubmissionValidationException("repository_path must be a non-empty absolute path.");
        }

        // 3. Reject UNC and device paths (both separator variants)
        RejectUncAndDevicePaths(repositoryPath);

        // 3b. Reject drive-relative paths (e.g., C:foo — colon at [1] not followed by separator)
        if (repositoryPath.Length >= 2 && repositoryPath[1] == ':' &&
            (repositoryPath.Length == 2 ||
             (repositoryPath[2] != Path.DirectorySeparatorChar && repositoryPath[2] != Path.AltDirectorySeparatorChar)))
        {
            throw new RunSubmissionValidationException("repository_path must be an absolute path.");
        }

        // 4. Must be rooted
        if (!Path.IsPathRooted(repositoryPath))
            throw new RunSubmissionValidationException("repository_path must be an absolute path.");

        // 5. Canonicalize
        var canonical = Path.GetFullPath(repositoryPath);

        // 6. Re-apply UNC/device/drive-relative rejection to canonical form
        //    (GetFullPath can produce \\?\ extended-length or \\server\share forms — Morpheus M3)
        RejectUncAndDevicePaths(canonical);
        if (canonical.Length >= 2 && canonical[1] == ':' &&
            (canonical.Length == 2 ||
             (canonical[2] != Path.DirectorySeparatorChar && canonical[2] != Path.AltDirectorySeparatorChar)))
        {
            throw new RunSubmissionValidationException("repository_path must be an absolute path.");
        }

        // 7. Resolve real path (follows symlinks, junctions, 8.3 names)
        string real;
        try
        {
            real = RealPath.Resolve(canonical);
        }
        catch (IOException)
        {
            // Resolution failure uses the same categorical message as allowlist miss (Seraph M3)
            throw new RunSubmissionValidationException(PathRejectedMessage);
        }

        // 8. Allowlist check (if configured)
        if (_resolvedRoots.Length > 0)
        {
            var realNormalized = real.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool allowed = false;

            foreach (var root in _resolvedRoots)
            {
                // Exact match or child-of (with separator boundary to prevent /allowed-foo matching /allowed)
                if (string.Equals(realNormalized, root, _pathComparison) ||
                    realNormalized.StartsWith(root + Path.DirectorySeparatorChar, _pathComparison))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
                throw new RunSubmissionValidationException(PathRejectedMessage);
        }

        // 9. If no allowlist configured: permissive (UNC/device/relative already rejected above).

        // 10. Return canonical path (not the real/resolved path) — this is what gets stored
        //     on Run.RepositoryPath and used by git operations.
        return canonical;
    }

    private static void RejectUncAndDevicePaths(string path)
    {
        // Check both separator variants for UNC/device prefix (Morpheus M2)
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith("//?/", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith("//./", StringComparison.Ordinal))
        {
            throw new RunSubmissionValidationException("repository_path must be an absolute path.");
        }

        // UNC paths: \\server or //server — device prefixes (\\?\ etc.) already threw above.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new RunSubmissionValidationException("repository_path must be an absolute path.");
        }
    }
}
