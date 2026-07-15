using System.Text;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Assembles the progressive-disclosure skill section for an agent's system prompt and materializes
/// the assigned skills into the run worktree so the agent can read the full <c>SKILL.md</c> on demand.
///
/// Progressive disclosure: when a skill's body can be written to the worktree, only its NAME +
/// DESCRIPTION metadata plus a pointer to the on-disk <c>SKILL.md</c> is injected into the prompt up
/// front. The full instruction body (and bundled resources) are written to disk under
/// <c>.agentweaver/skills/&lt;slug&gt;/</c> and read only when the agent decides a skill is relevant.
/// That directory is added to the worktree's git exclude so materialized skills never pollute the
/// agent's commit/diff. Only <see cref="SkillStatus.Active"/> skills assigned to this agent are
/// materialized, so removed or malformed skills never silently remain active.
///
/// <para>Delivery is fail-closed on CONTENT: a <c>SKILL.md</c> pointer is only emitted for a skill
/// whose file was actually written. If no writable worktree is available (e.g. pod-per-run/warm-pool
/// execution where the agent runs on a different filesystem) or a write fails, the skill's full
/// instructions are inlined directly into the prompt block instead of emitting a dangling
/// <c>SKILL.md</c> reference the agent could never read (issue #336).</para>
/// </summary>
public sealed class SkillPromptComposer
{
    public const string SkillsRelativeDir = ".agentweaver/skills";

    private readonly ISkillStore _skills;
    private readonly ILogger<SkillPromptComposer> _logger;

    public SkillPromptComposer(ISkillStore skills, ILogger<SkillPromptComposer> logger)
    {
        _skills = skills;
        _logger = logger;
    }

    /// <summary>
    /// Materializes the agent's assigned active skills into <paramref name="worktreePath"/> and returns
    /// the metadata block to append to the system prompt, or null when the agent has no active skills.
    /// </summary>
    public async Task<string?> ComposeAsync(
        ProjectId projectId, string agentName, string worktreePath, CancellationToken ct)
    {
        IReadOnlyList<Skill> skills;
        try
        {
            skills = await _skills.ListActiveSkillsForAgentAsync(projectId, agentName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill lookup failed for agent {Agent} in project {Project}", agentName, projectId);
            return null;
        }

        var haveWorktree = !string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath);

        // Staging dir name is keyed by skill id (not just the slug) so two names that slugify the same
        // ("PR Review" vs "pr-review") never collide and overwrite each other.
        var wanted = skills.Select(s => (Skill: s, Dir: StagingDirName(s))).ToList();

        if (haveWorktree)
        {
            TryEnsureGitExclude(worktreePath);
            // Reconcile: remove any previously-materialized skill dir that is no longer active+assigned
            // so removed/unassigned/malformed skills drop out immediately (no stale full instructions
            // linger on a reused/retried worktree).
            ReconcileStaleDirs(worktreePath, wanted.Select(w => w.Dir));
        }

        if (skills.Count == 0)
            return null;

        // Track, per skill, whether its SKILL.md was genuinely written to a location the agent can
        // read. This is what makes composition fail-CLOSED on delivery: a skill only gets a lazy-load
        // `SKILL.md` pointer when that file actually exists; otherwise its full instructions are
        // inlined so the content still reaches the agent (see BuildMetadataBlock). Emitting a pointer
        // to a file that was never materialized was the #336 defect — the agent saw a dangling
        // `.agentweaver/skills/.../SKILL.md` reference with no file behind it.
        var composed = new List<SkillComposition>();
        if (haveWorktree)
        {
            foreach (var (skill, dir) in wanted)
            {
                try
                {
                    Materialize(worktreePath, dir, skill);
                    composed.Add(new SkillComposition(skill, dir, Materialized: true));
                }
                catch (Exception ex)
                {
                    // Do NOT drop the skill: fall back to inlining its instructions so an assigned
                    // skill is never silently lost just because a file write failed.
                    _logger.LogWarning(ex,
                        "Failed to materialize skill '{Skill}' into worktree; inlining its instructions instead", skill.Name);
                    composed.Add(new SkillComposition(skill, dir, Materialized: false));
                }
            }
        }
        else
        {
            // No writable worktree on this host (e.g. pod-per-run / warm-pool execution, where the
            // agent runs on a different filesystem than the one composing the prompt). A SKILL.md
            // pointer would be a dangling reference the agent cannot read, so inline the full
            // instructions to guarantee delivery.
            _logger.LogInformation(
                "No materialization target for {Count} assigned skill(s) of agent '{Agent}' (worktree '{Worktree}' unavailable); inlining full instructions into the system prompt.",
                wanted.Count, agentName, string.IsNullOrEmpty(worktreePath) ? "(none)" : worktreePath);
            foreach (var (skill, dir) in wanted)
                composed.Add(new SkillComposition(skill, dir, Materialized: false));
        }

        return BuildMetadataBlock(composed);
    }

    /// <summary>One assigned skill and whether its SKILL.md was written to the worktree.</summary>
    private readonly record struct SkillComposition(Skill Skill, string Dir, bool Materialized);

    private static string BuildMetadataBlock(IReadOnlyList<SkillComposition> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SkillPromptMarkers.SectionHeading);
        sb.AppendLine();
        sb.AppendLine(
            "You have specialized skill modules assigned to you. Apply a skill only when it is relevant " +
            "to the current task. For a skill shown with a `SKILL.md` path, read that file for its full " +
            "instructions when the skill is relevant (do not read them all up front). For a skill whose " +
            "instructions are inlined below, the full instructions are already present here.");
        sb.AppendLine();
        foreach (var (skill, dir, materialized) in skills)
        {
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
            if (materialized)
            {
                sb.AppendLine($"  Full instructions: `{SkillsRelativeDir}/{dir}/SKILL.md`");
            }
            else
            {
                // Inline the full instructions verbatim so the skill's content reaches the agent even
                // when no on-disk SKILL.md could be materialized for it.
                sb.AppendLine("  Full instructions (inlined — no on-disk SKILL.md available for this run):");
                sb.AppendLine();
                foreach (var line in NormalizeLines(skill.Instructions))
                    sb.AppendLine(line.Length == 0 ? string.Empty : "  " + line);
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> NormalizeLines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Deletes materialized skill dirs under the worktree that are not in the current set.</summary>
    private void ReconcileStaleDirs(string worktreePath, IEnumerable<string> keepDirs)
    {
        var root = Path.Combine(worktreePath, ".agentweaver", "skills");
        if (!Directory.Exists(root))
            return;
        var keep = new HashSet<string>(keepDirs, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (keep.Contains(name))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove stale skill dir {Dir}", dir);
            }
        }
    }

    private static void Materialize(string worktreePath, string dirName, Skill skill)
    {
        var skillsRoot = Path.Combine(worktreePath, ".agentweaver", "skills");
        var skillDir = Path.Combine(skillsRoot, dirName);
        Directory.CreateDirectory(skillDir);

        // Reconstruct the SKILL.md with its frontmatter so the on-disk module is standards-compatible.
        var md = new StringBuilder();
        md.Append("---\n");
        md.Append("name: ").Append(skill.Name).Append('\n');
        md.Append("description: ").Append(EscapeYaml(skill.Description)).Append('\n');
        md.Append("---\n\n");
        md.Append(skill.Instructions).Append('\n');
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), md.ToString());

        foreach (var resource in skill.Resources)
        {
            // Zip-slip / rooted-path defense: reject unsafe relative paths, then verify the resolved
            // target is still inside the skill dir before writing.
            var safeRel = SkillPaths.NormalizeRelative(resource.RelativePath);
            if (safeRel is null)
                continue;
            var target = Path.Combine(skillDir, safeRel.Replace('/', Path.DirectorySeparatorChar));
            if (!SkillPaths.IsContained(skillDir, target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, resource.Content);
        }
    }

    /// <summary>Unique per-skill staging dir name: slug + short id suffix (guards slug collisions).</summary>
    internal static string StagingDirName(Skill skill) =>
        $"{Slugify(skill.Name)}-{skill.Id.Value.ToString("N")[..8]}";

    private static string EscapeYaml(string value) =>
        value.Contains(':') || value.Contains('#') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\\\"").Replace("\n", " ") + "\""
            : value;

    /// <summary>Adds the skills dir to the worktree's git exclude so materialized files never commit.</summary>
    private void TryEnsureGitExclude(string worktreePath)
    {
        try
        {
            using var repo = new Repository(worktreePath);
            var gitDir = repo.Info.Path; // for a linked worktree this is .git/worktrees/<id>/
            var commonDir = ResolveCommonDir(gitDir);
            var infoDir = Path.Combine(commonDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            const string pattern = "/.agentweaver/skills/";
            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : "";
            if (!existing.Contains(pattern, StringComparison.Ordinal))
                File.AppendAllText(excludePath, (existing.EndsWith('\n') || existing.Length == 0 ? "" : "\n") + pattern + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update git exclude for worktree {Worktree}", worktreePath);
        }
    }

    private static string ResolveCommonDir(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return gitDir;
        var content = File.ReadAllText(commonDirFile).Trim();
        if (string.IsNullOrEmpty(content))
            return gitDir;
        return Path.IsPathRooted(content)
            ? content
            : Path.GetFullPath(Path.Combine(gitDir, content));
    }

    internal static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "skill" : slug;
    }
}
