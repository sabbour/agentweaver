using Scaffolder.SandboxFs;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Builds a filesystem policy for the sandbox using a reparse-safe path
/// canonicalization chain. All inputs are normalized through
/// SandboxPathValidator.ValidateAbsoluteContained so that symlink and junction
/// escapes are detected at policy-construction time.
/// </summary>
public static class SandboxFsPolicyBuilder
{
    /// <summary>
    /// Builds a filesystem policy from a sandbox working root and an optional
    /// set of additional read-only repository roots. .ssh and .gnupg directories
    /// are always added to the denied (hidden) path list as a defense-in-depth measure.
    /// </summary>
    public static SandboxFsPolicy Build(string sandboxRoot, string[] allowedRepositoryRoots)
    {
        // Canonicalize sandbox root through the full validator.
        // Passing the same path as both arguments does a normalized self-check
        // (reparse-point ancestor walk + normalization).
        var canonicalRoot = SandboxPathValidator.ValidateAbsoluteContained(
            Path.GetFullPath(sandboxRoot), Path.GetFullPath(sandboxRoot));

        var rwPaths = new List<string> { canonicalRoot };

        var roPaths = new List<string>();
        foreach (var root in allowedRepositoryRoots)
        {
            var resolved = SandboxPathValidator.ValidateAbsoluteContained(
                Path.GetFullPath(root), Path.GetFullPath(root));
            if (!string.Equals(resolved, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                roPaths.Add(resolved);
        }

        var deniedPaths = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            deniedPaths.Add(Path.Combine(home, ".ssh"));
            deniedPaths.Add(Path.Combine(home, ".gnupg"));
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            deniedPaths.Add(Path.Combine(home, ".ssh"));
            deniedPaths.Add(Path.Combine(home, ".gnupg"));
        }

        return new SandboxFsPolicy(rwPaths, roPaths, deniedPaths);
    }
}
