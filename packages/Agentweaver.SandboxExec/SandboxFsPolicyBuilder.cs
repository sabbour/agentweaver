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
        SandboxPolicyEnrichment? enrichment = null)
    {
        // Canonicalize sandbox root through the full validator.
        var canonicalRoot = SandboxPathValidator.ValidateAbsoluteContained(
            Path.GetFullPath(sandboxRoot), Path.GetFullPath(sandboxRoot));

        var rwPaths = new List<string> { canonicalRoot };
        var roPaths = new List<string>();

        foreach (var root in allowedRepositoryRoots)
        {
            var resolved = SandboxPathValidator.ValidateAbsoluteContained(
                Path.GetFullPath(root), canonicalRoot);
            if (!string.Equals(resolved, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                roPaths.Add(resolved);
        }

        if (enrichment is not null)
        {
            roPaths.AddRange(enrichment.AdditionalReadOnlyPaths);
            rwPaths.AddRange(enrichment.AdditionalReadWritePaths);
        }

        return new SandboxFsPolicy(rwPaths, roPaths, []);
    }
}
