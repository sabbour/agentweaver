using Agentweaver.SandboxFs;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Builds a filesystem policy from a sandbox working root, an optional set of
/// additional read-only repository roots, and an optional
/// <see cref="SandboxPolicyEnrichment"/> from policy-discovery helpers.
/// All inputs are normalized through <see cref="SandboxPathValidator.ValidateAbsoluteContained"/>
/// so that symlink and junction escapes are detected at policy-construction time.
/// </summary>
public static class SandboxFsPolicyBuilder
{
    /// <summary>
    /// Builds a filesystem policy. The sandbox root is always added as read-write.
    /// Repository roots are read-only (unless equal to the sandbox root). Enrichment
    /// paths (tool dirs, temp dir) are merged in read-only / read-write as appropriate.
    /// </summary>
    public static SandboxFsPolicy Build(
        string sandboxRoot,
        string[] allowedRepositoryRoots,
        SandboxPolicyEnrichment? enrichment = null,
        IReadOnlyList<string>? additionalReadWriteRoots = null)
    {
        // Canonicalize sandbox root through the full validator and reject
        // symlink/junction roots before descendant containment trusts them.
        var canonicalRoot = SandboxPathValidator.ValidateSandboxRoot(sandboxRoot);

        var rwPaths = new List<string>();
        var roPaths = new List<string>();
        var seenRw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddUnique(rwPaths, seenRw, canonicalRoot);

        foreach (var root in allowedRepositoryRoots)
        {
            var resolved = SandboxPathValidator.ValidateAbsoluteContained(
                Path.GetFullPath(root), canonicalRoot);
            if (!string.Equals(resolved, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                AddUnique(roPaths, seenRo, resolved);
        }

        if (additionalReadWriteRoots is not null)
        {
            foreach (var root in additionalReadWriteRoots)
            {
                var resolved = SandboxPathValidator.ValidateAbsoluteContained(
                    Path.GetFullPath(root), Path.GetFullPath(root));
                AddUnique(rwPaths, seenRw, resolved);
            }
        }

        if (enrichment is not null)
        {
            foreach (var path in enrichment.AdditionalReadOnlyPaths)
                AddUnique(roPaths, seenRo, path);
            foreach (var path in enrichment.AdditionalReadWritePaths)
                AddUnique(rwPaths, seenRw, path);
        }

        return new SandboxFsPolicy(rwPaths, roPaths, []);
    }

    private static void AddUnique(List<string> paths, HashSet<string> seen, string path)
    {
        if (seen.Add(path))
            paths.Add(path);
    }
}
