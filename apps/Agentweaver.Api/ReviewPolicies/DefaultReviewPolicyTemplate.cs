namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The canonical, code-embedded generator for the default review policy (Feature 010, FR-028/032).
/// This is the single source of truth for "the default review policy" — held in code, NOT a checked-in
/// repo file.
///
/// It is materialized per-project at instantiation time into
/// <c>&lt;projectWorkingDir&gt;/.agentweaver/review-policies/default.yaml</c> (see
/// <see cref="TryMaterialize"/>), mirroring the Scaffolder template materialization pattern: the user
/// can edit the generated file afterwards. It also doubles as the runtime fallback — a project with no
/// materialized <c>.agentweaver/review-policies/</c> still resolves this default through
/// <see cref="BuiltInReviewPolicies"/>, so no migration is required for existing projects.
///
/// The default mirrors the live Stage-1 run pipeline: RAI and the human-review gate already exist in
/// the default workflow and are absorbed by policy composition. This makes default policy + default
/// workflow an identity transform, avoiding duplicate RAI gates and unbound policy-injected nodes.
/// No emojis appear in any shipped surface (Principle VIII).
/// </summary>
public static class DefaultReviewPolicyTemplate
{
    /// <summary>The name a project binds to by default when no policy is explicitly configured.</summary>
    public const string DefaultPolicyName = "default";

    /// <summary>The relative path, within a project working directory, where the default is written.</summary>
    public const string RelativeFilePath = ".agentweaver/review-policies/default.yaml";

    /// <summary>The canonical default review-policy YAML. Parsed through the real loader everywhere it
    /// is used, so it is validated identically to any project-authored policy (Principle VII).</summary>
    public const string Yaml =
        """
        # Default Review Policy (Feature 010 — FR-028/FR-032). Generated into each project at
        # instantiation time and editable afterwards. The canonical generator lives in code
        # (DefaultReviewPolicyTemplate); this file is a materialized copy.
        #
        # The safe default mirrors the Stage-1 run pipeline: RAI first, then the existing human-review
        # gate before merge. Policy composition absorbs both onto the workflow's baked-in gates, so the
        # default overlay is identity and every gate has a live runtime binding.

        name: default
        description: Safe default review policy — mirrors the built-in RAI and human-review gates.

        steps:
          - kind: rai
            label: RAI review
          - kind: human-review
            label: Human review
        """;

    /// <summary>
    /// The pre-Stage-2 materialized default. It injected an executor-less rubberduck gate when composed
    /// with the live graph. Used only to normalize untouched project copies to the current default.
    /// </summary>
    internal const string LegacyRubberduckDefaultYaml =
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
            var path = Path.Combine(workingDirectory, ".agentweaver", "review-policies", "default.yaml");
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

    /// <summary>
    /// Rewrites an untouched legacy default file to the current identity-preserving default and returns
    /// the YAML callers should load. User-customized policies are never touched.
    /// </summary>
    public static bool TryNormalizeLegacyMaterializedDefault(
        string path,
        string existingYaml,
        out string yamlToLoad,
        out string? error)
    {
        yamlToLoad = existingYaml;
        error = null;

        if (!Path.GetFileName(path).Equals("default.yaml", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).Equals("default.yml", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(NormalizeLineEndings(existingYaml), NormalizeLineEndings(LegacyRubberduckDefaultYaml), StringComparison.Ordinal))
            return false;

        yamlToLoad = Yaml;
        try
        {
            File.WriteAllText(path, Yaml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
        }
        return true;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
}
