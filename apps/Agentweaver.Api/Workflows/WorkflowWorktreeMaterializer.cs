using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Materializes the authoritative RESOLVED workflow definition into a run's worktree so the agent can
/// inspect the selected workflow file from inside its sandbox. Follows the skills-style delivery
/// pattern: write directly into the run worktree and add the materialized directory to git exclude,
/// so the file is available to the agent without polluting repository history.
/// </summary>
public sealed class WorkflowWorktreeMaterializer
{
    private readonly ILogger<WorkflowWorktreeMaterializer> _logger;

    public WorkflowWorktreeMaterializer(ILogger<WorkflowWorktreeMaterializer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Best-effort materialization of <paramref name="definition"/> into
    /// <c>.agentweaver/workflows/&lt;id&gt;.yaml</c> under <paramref name="worktreePath"/>. If the target
    /// path is already tracked in git, leaves it untouched to avoid mutating repository-owned workflow
    /// files such as a committed <c>default.yaml</c>.
    /// </summary>
    public void TryMaterialize(string worktreePath, WorkflowDefinition? definition)
    {
        if (definition is null
            || string.IsNullOrWhiteSpace(worktreePath)
            || !Directory.Exists(worktreePath))
            return;

        try
        {
            using var repo = new Repository(worktreePath);
            TryEnsureGitExclude(repo);

            var relativePath = $"{WorkflowRegistry.WorkflowsRelativePath.Replace('\\', '/')}/{definition.Id}.yaml";
            if (repo.Index[relativePath] is not null)
                return;

            var fullPath = Path.Combine(
                worktreePath,
                ".agentweaver",
                "workflows",
                $"{definition.Id}.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, WorkflowDefinitionYamlSerializer.Serialize(definition));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to materialize selected workflow '{WorkflowId}' into worktree {WorktreePath}.",
                definition.Id,
                worktreePath);
        }
    }

    private void TryEnsureGitExclude(Repository repo)
    {
        try
        {
            var gitDir = repo.Info.Path;
            var commonDir = ResolveCommonDir(gitDir);
            var infoDir = Path.Combine(commonDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            const string pattern = "/.agentweaver/workflows/";
            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : "";
            if (!existing.Contains(pattern, StringComparison.Ordinal))
                File.AppendAllText(excludePath, (existing.EndsWith('\n') || existing.Length == 0 ? "" : "\n") + pattern + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update git exclude for workflow materialization in {Worktree}.", repo.Info.WorkingDirectory);
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
}
