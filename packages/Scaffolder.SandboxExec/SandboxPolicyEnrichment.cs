using Sabbour.Mxc.Sdk.Policy;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Cached enrichment paths for the sandbox filesystem policy. Built ONCE at executor
/// construction time using <see cref="PolicyDiscovery"/> helpers (replicating Copilot CLI's
/// selective allowlist model — see specs/002-sandboxed-execution/plan.md § Phase 6).
///
/// On Windows: uses <see cref="PolicyDiscovery.GetAvailableToolsPolicy"/> and
/// <see cref="PolicyDiscovery.GetUserProfilePolicy"/>. Temp directories are NOT
/// added here — per-command isolated subdirs are injected in
/// <see cref="MxcSandboxExecutor"/> at execution time.
///
/// On Linux/WSL2: enrichment is derived directly inside the bwrap command builder
/// (targeted <c>--ro-bind-try</c> mounts); this class is Windows-only.
/// </summary>
public sealed class SandboxPolicyEnrichment
{
    /// <summary>Empty enrichment — no additional paths beyond the sandbox root.</summary>
    public static readonly SandboxPolicyEnrichment Empty = new([], []);

    /// <summary>Additional read-only paths to add to the sandbox filesystem policy.</summary>
    public IReadOnlyList<string> AdditionalReadOnlyPaths { get; }

    /// <summary>Additional read-write paths to add to the sandbox filesystem policy.</summary>
    public IReadOnlyList<string> AdditionalReadWritePaths { get; }

    private SandboxPolicyEnrichment(
        IReadOnlyList<string> readOnlyPaths,
        IReadOnlyList<string> readWritePaths)
    {
        AdditionalReadOnlyPaths = readOnlyPaths;
        AdditionalReadWritePaths = readWritePaths;
    }

    /// <summary>
    /// Builds enrichment for Windows processcontainer using the mxc SDK's
    /// <see cref="PolicyDiscovery"/> helpers. Cached once at executor construction;
    /// never called per-command.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>GetAvailableToolsPolicy</c> — minimal allowlist from PATH + known tool env vars.
    ///   <c>ContainerType="processcontainer"</c> skips dirs already accessible to
    ///   ALL_APPLICATION_PACKAGES so we don't duplicate implicit grants.</item>
    ///   <item><c>GetUserProfilePolicy</c> — safe user-profile dirs (e.g. LocalAppData\Programs
    ///   subdirectories) excluding the full home directory.</item>
    /// </list>
    /// Temp directories are not included — <see cref="MxcSandboxExecutor"/> creates a
    /// per-command isolated subdir at execution time (Finding 1 fix).
    /// All helpers fail-open: if discovery throws, the enrichment is empty (no paths added).
    /// </remarks>
    public static SandboxPolicyEnrichment BuildForWindows()
    {
        var roPaths = new List<string>();
        var rwPaths = new List<string>();

        try
        {
            var toolsPolicy = PolicyDiscovery.GetAvailableToolsPolicy(
                options: new ToolsPolicyOptions { ContainerType = "processcontainer" });
            roPaths.AddRange(toolsPolicy.ReadonlyPaths);
            rwPaths.AddRange(toolsPolicy.ReadwritePaths);
        }
        catch { /* Fail-open: discovery error does not break execution */ }

        try
        {
            var profilePolicy = PolicyDiscovery.GetUserProfilePolicy();
            roPaths.AddRange(profilePolicy.ReadonlyPaths);
            rwPaths.AddRange(profilePolicy.ReadwritePaths);
        }
        catch { }

        // Temp dir is NOT added here — per-command isolated subdirs are injected in
        // MxcSandboxExecutor.ExecuteAsync to prevent cross-sandbox temp contamination.

        return new SandboxPolicyEnrichment(
            FilterExisting(roPaths),
            FilterExisting(rwPaths));
    }

    private static IReadOnlyList<string> FilterExisting(List<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p) && seen.Add(p))
                    result.Add(p);
            }
            catch { }
        }
        return result;
    }
}
