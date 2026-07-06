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

        if (skills.Count == 0)
            return null;

        var materialized = new List<(Skill Skill, string Slug)>();
        if (!string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath))
        {
            TryEnsureGitExclude(worktreePath);
            foreach (var skill in skills)
            {
                var slug = Slugify(skill.Name);
                try
                {
                    Materialize(worktreePath, slug, skill);
                    materialized.Add((skill, slug));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to materialize skill '{Skill}' into worktree", skill.Name);
                }
            }
        }
        else
        {
            foreach (var skill in skills)
                materialized.Add((skill, Slugify(skill.Name)));
        }

        return BuildMetadataBlock(materialized);
    }

    private static string BuildMetadataBlock(IReadOnlyList<(Skill Skill, string Slug)> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine(
            "You have specialized skill modules assigned to you. Only their NAME and DESCRIPTION are shown here. " +
            "When — and only when — a skill is relevant to the current task, read its full instructions from the " +
            "referenced `SKILL.md` file before applying it. Do not read them all up front.");
        sb.AppendLine();
        foreach (var (skill, slug) in skills)
        {
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
            sb.AppendLine($"  Full instructions: `{SkillsRelativeDir}/{slug}/SKILL.md`");
        }
        return sb.ToString().TrimEnd();
    }

    private static void Materialize(string worktreePath, string slug, Skill skill)
    {
        var skillDir = Path.Combine(worktreePath, ".agentweaver", "skills", slug);
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
            var safeRel = resource.RelativePath.Replace('\\', '/').TrimStart('/');
            if (safeRel.Contains("..", StringComparison.Ordinal))
                continue; // never escape the skill dir
            var target = Path.Combine(skillDir, safeRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, resource.Content);
        }
    }

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
