namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The canonical, code-embedded generator for the default review policy (Feature 010, FR-028/032).
/// This is the single source of truth for "the default review policy" — held in code, NOT a checked-in
/// repo file.
///
/// It is materialized per-project at instantiation time into
/// <c>&lt;projectWorkingDir&gt;/.scaffolders/review-policies/default.yaml</c> (see
/// <see cref="TryMaterialize"/>), mirroring the Scaffolder template materialization pattern: the user
/// can edit the generated file afterwards. It also doubles as the runtime fallback — a project with no
/// materialized <c>.scaffolders/review-policies/</c> still resolves this default through
/// <see cref="BuiltInReviewPolicies"/>, so no migration is required for existing projects.
///
/// The default is the safe policy required by FR-032: the Rubber-duck step and the RAI step at the
/// implicit pre-merge review point. The Human-review step is opt-in (FR-026/032) and is therefore NOT
/// part of the default. Step order mirrors the live run pipeline (agent, rai, review, merge): the RAI
/// gate runs first, then the rubber-duck review, then merge. No emojis appear in any shipped surface
/// (Principle VIII).
/// </summary>
public static class DefaultReviewPolicyTemplate
{
    /// <summary>The name a project binds to by default when no policy is explicitly configured.</summary>
    public const string DefaultPolicyName = "default";

    /// <summary>The relative path, within a project working directory, where the default is written.</summary>
    public const string RelativeFilePath = ".scaffolders/review-policies/default.yaml";

    /// <summary>The canonical default review-policy YAML. Parsed through the real loader everywhere it
    /// is used, so it is validated identically to any project-authored policy (Principle VII).</summary>
    public const string Yaml =
        """
        # Default Review Policy (Feature 010 — FR-028/FR-032). Generated into each project at
        # instantiation time and editable afterwards. The canonical generator lives in code
        # (DefaultReviewPolicyTemplate); this file is a materialized copy.
        #
        # The safe default runs the RAI content-safety gate and the rubber-duck review at the implicit
        # pre-merge review point. Order mirrors the live run pipeline (agent, rai, review, merge): RAI
        # first, then the rubber-duck review, then merge. The human-review gate is opt-in: add a step
        # with kind 'human-review' to require explicit human approval before merge and other
        # irreversible actions.

        name: default
        description: Safe default review policy — RAI content-safety gate and rubber-duck review run pre-merge.

        steps:
          - kind: rai
            label: RAI review
          - kind: rubberduck
            label: Rubber-duck review
        """;

    /// <summary>
    /// Best-effort materialization of the default review policy into a project's working directory at
    /// <see cref="RelativeFilePath"/>. Returns true if the file was written, false if it already existed
    /// (never clobbered) or the write failed. Never throws — project creation must not fail if this
    /// write fails, because the registry regenerates the default from this same template at runtime.
    /// </summary>
    public static bool TryMaterialize(string workingDirectory, out string? error)
    {
        error = null;
        try
        {
            var path = Path.Combine(workingDirectory, ".scaffolders", "review-policies", "default.yaml");
            if (File.Exists(path)) return false;

            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, Yaml);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
