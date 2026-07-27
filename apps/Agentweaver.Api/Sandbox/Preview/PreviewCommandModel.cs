using System.Text;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Inputs for the model-backed (Phase-2) preview-command fallback. Carries only the identity needed
/// to mint a Copilot model turn plus the worktree path — the compact worktree view is built
/// internally so the model implementation stays a pure single completion.
/// </summary>
public sealed record PreviewCommandModelContext(
    string RunId,
    string? ProjectId,
    string SubmittingUser,
    string WorktreePath);

/// <summary>
/// Model-proposed run command. <see cref="Previewable"/> is <see langword="false"/> when the model
/// decided the worktree cannot be previewed at all; a <see langword="null"/> proposal from
/// <see cref="IPreviewCommandModel.ProposeCommandAsync"/> means the model was unavailable, timed out,
/// or produced an unparseable answer. Either way <see cref="PreviewStep"/> falls back to the terminal
/// <c>preview_command_unresolved</c> outcome — this tier can never force a preview to succeed.
/// </summary>
public sealed record PreviewCommandProposal(
    bool Previewable,
    string? Command,
    string? Cwd);

/// <summary>
/// LLM-powered fallback that runs ONLY when the deterministic <see cref="PreviewCommandResolver"/>
/// heuristics return <see cref="PreviewCommandResolution.Unresolved"/> (issue #541). It gives a
/// model a bounded view of the worktree and asks it to decide whether the project is previewable and,
/// if so, the exact shell command + working directory to run it. It never bypasses the sandboxed
/// AgentHost start / port-observe / approval pipeline — only the origin of the command string changes.
/// </summary>
public interface IPreviewCommandModel
{
    /// <summary>
    /// Proposes a run command for a worktree the heuristics could not resolve. Returns
    /// <see langword="null"/> on any failure/timeout/ambiguity so the caller preserves the current
    /// terminal <c>preview_command_unresolved</c> behavior.
    /// </summary>
    Task<PreviewCommandProposal?> ProposeCommandAsync(PreviewCommandModelContext context, CancellationToken ct);
}

/// <summary>
/// Builds a compact, token-bounded textual view of a worktree for the model-backed fallback: a
/// pruned file listing (build/output/VCS directories excluded) plus truncated contents of the files
/// that most reliably reveal how to run a project (manifests, README, Dockerfile, entrypoints). The
/// digest is deliberately capped so the fallback stays cheap/fast — it is a hint surface, not a full
/// checkout dump.
/// </summary>
public static class PreviewWorktreeDigest
{
    /// <summary>Directory names never descended into (build output, dependencies, VCS metadata).</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out", ".next", ".nuxt",
        "target", ".venv", "venv", "__pycache__", ".gradle", ".idea", ".vs", "vendor",
        "coverage", ".turbo", ".cache", ".svelte-kit",
    };

    /// <summary>
    /// Files whose (truncated) contents are included verbatim because they most directly reveal a run
    /// command. Matched case-insensitively by file name anywhere in the (pruned) tree.
    /// </summary>
    private static readonly HashSet<string> KeyFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "readme.md", "readme", "readme.txt", "dockerfile", "makefile",
        "index.html", "deno.json", "deno.jsonc", "cargo.toml", "go.mod", "requirements.txt",
        "pyproject.toml", "gemfile", "procfile", "docker-compose.yml", "docker-compose.yaml",
        "vite.config.js", "vite.config.ts", "next.config.js", "angular.json",
    };

    private const int MaxListedFiles = 250;
    private const int MaxKeyFiles = 12;
    private const int MaxKeyFileChars = 2000;
    private const int MaxDepth = 6;

    /// <summary>
    /// Produces the digest string. Never throws: any IO error degrades to whatever was collected so
    /// far (possibly an empty string), which the model treats as "not previewable".
    /// </summary>
    public static string Build(string worktreePath)
    {
        var sb = new StringBuilder();
        try
        {
            var root = new DirectoryInfo(worktreePath);
            if (!root.Exists)
                return string.Empty;

            var files = new List<string>();
            CollectFiles(root, root.FullName, 0, files);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("FILE LISTING (relative paths, build/dependency/VCS directories excluded):");
            var listed = 0;
            foreach (var rel in files)
            {
                if (listed++ >= MaxListedFiles)
                {
                    sb.AppendLine($"... ({files.Count - MaxListedFiles} more files omitted)");
                    break;
                }
                sb.Append("- ").AppendLine(rel);
            }

            var keyEmitted = 0;
            foreach (var rel in files)
            {
                if (keyEmitted >= MaxKeyFiles)
                    break;
                var name = Path.GetFileName(rel);
                if (!KeyFileNames.Contains(name))
                    continue;

                string content;
                try
                {
                    content = File.ReadAllText(Path.Combine(root.FullName, rel));
                }
                catch
                {
                    continue;
                }

                if (content.Length > MaxKeyFileChars)
                    content = content[..MaxKeyFileChars] + "\n... (truncated)";

                sb.AppendLine();
                sb.AppendLine($"----- FILE: {rel} -----");
                sb.AppendLine(content);
                keyEmitted++;
            }
        }
        catch
        {
            // Degrade to whatever was collected.
        }

        return sb.ToString();
    }

    private static void CollectFiles(DirectoryInfo dir, string rootFullPath, int depth, List<string> acc)
    {
        if (depth > MaxDepth || acc.Count > MaxListedFiles * 4)
            return;

        IEnumerable<FileInfo> entries;
        try { entries = dir.EnumerateFiles(); }
        catch { return; }

        foreach (var file in entries)
        {
            var rel = Path.GetRelativePath(rootFullPath, file.FullName);
            acc.Add(rel);
            if (acc.Count > MaxListedFiles * 4)
                return;
        }

        IEnumerable<DirectoryInfo> subdirs;
        try { subdirs = dir.EnumerateDirectories(); }
        catch { return; }

        foreach (var sub in subdirs)
        {
            if (ExcludedDirs.Contains(sub.Name) || (sub.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            CollectFiles(sub, rootFullPath, depth + 1, acc);
        }
    }
}
