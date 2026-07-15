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
/// Progressive disclosure: only the skill NAME + DESCRIPTION metadata is injected into the prompt up
/// front. The full instruction body (and bundled resources) are written to disk under
/// <c>.agentweaver/skills/&lt;slug&gt;/</c> and read only when the agent decides a skill is relevant.
/// That directory is added to the worktree's git exclude so materialized skills never pollute the
/// agent's commit/diff. Only <see cref="SkillStatus.Active"/> skills assigned to this agent are
/// materialized, so removed or malformed skills never silently remain active.
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

        var materialized = new List<(Skill Skill, string Dir)>();
        if (haveWorktree)
        {
            foreach (var (skill, dir) in wanted)
            {
                try
                {
                    Materialize(worktreePath, dir, skill);
                    materialized.Add((skill, dir));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to materialize skill '{Skill}' into worktree", skill.Name);
                }
            }
        }
        else
        {
            materialized.AddRange(wanted);
        }

        return BuildMetadataBlock(materialized);
    }

    private static string BuildMetadataBlock(IReadOnlyList<(Skill Skill, string Dir)> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SkillPromptMarkers.SectionHeading);
        sb.AppendLine();
        sb.AppendLine(
            "You have specialized skill modules assigned to you. Only their NAME and DESCRIPTION are shown here. " +
            "When — and only when — a skill is relevant to the current task, read its full instructions from the " +
            "referenced `SKILL.md` file before applying it. Do not read them all up front.");
        sb.AppendLine();
        foreach (var (skill, dir) in skills)
        {
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
            sb.AppendLine($"  Full instructions: `{SkillsRelativeDir}/{dir}/SKILL.md`");
        }
        return sb.ToString().TrimEnd();
    }

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
