using System.Text.RegularExpressions;

namespace Scaffolder.Api.Coordinator;

/// <summary>
/// Pure, side-effect-free planning logic for Phase 3 collective assembly. Extracted from the
/// orchestration service so the eligibility gate (D2), the topological merge order (D1), and the
/// rejection-inference rule (D6) can be unit tested cheaply without a database, git, or any live
/// agent. Mirrors the <see cref="SubtaskFrontier"/> philosophy (domain rules as pure functions).
/// </summary>
public static class AssemblyPlanning
{
    /// <summary>
    /// D2 eligibility gate. A subtask is <em>assembly-eligible</em> only when it reached
    /// <see cref="SubtaskStatus.AssembleReady"/> (produced changes to assemble) or
    /// <see cref="SubtaskStatus.Completed"/> (terminated with no changes — still a valid, mergeable
    /// no-op). Anything else — <c>pending</c>, <c>dispatched</c>, <c>running</c>,
    /// <c>rai_flagged</c>, or <c>failed</c> — makes the WHOLE plan ineligible (NO partial assembly).
    /// Returns the ids of every NON-eligible subtask (empty ⇒ the plan may assemble).
    /// </summary>
    public static IReadOnlyList<int> IneligibleSubtasks(IReadOnlyDictionary<int, string> statusById)
    {
        var ineligible = new List<int>();
        foreach (var (id, status) in statusById)
            if (!IsEligible(status))
                ineligible.Add(id);
        ineligible.Sort();
        return ineligible;
    }

    /// <summary>True when every subtask is assembly-eligible (the plan may assemble).</summary>
    public static bool AllEligible(IReadOnlyDictionary<int, string> statusById) =>
        statusById.Values.All(IsEligible);

    /// <summary>An eligible subtask is one that reached assemble_ready or completed.</summary>
    public static bool IsEligible(string status) =>
        status is SubtaskStatus.AssembleReady or SubtaskStatus.Completed;

    /// <summary>
    /// D1 merge order. Returns the given subtask ids in DEPENDENCY (topological) order — every
    /// dependency precedes its dependents — so child branches merge into the integration branch in
    /// a deterministic, prerequisite-first order. Ties are broken by ascending id for reproducibility.
    /// A dependency edge is <c>(SubtaskId, DependsOnSubtaskId)</c>: <c>DependsOnSubtaskId</c> must
    /// come first. Edges referencing ids outside <paramref name="subtaskIds"/> are ignored. A cycle
    /// (should never occur in a validated DAG) degrades gracefully by appending the remainder in id
    /// order rather than looping forever.
    /// </summary>
    public static IReadOnlyList<int> TopologicalOrder(
        IReadOnlyCollection<int> subtaskIds,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges)
    {
        var set = subtaskIds.ToHashSet();
        // prerequisites[x] = ids that must come before x.
        var prerequisites = set.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var (dependent, dependency) in edges)
            if (set.Contains(dependent) && set.Contains(dependency) && dependent != dependency)
                prerequisites[dependent].Add(dependency);

        var ordered = new List<int>(set.Count);
        var placed = new HashSet<int>();

        while (placed.Count < set.Count)
        {
            var ready = set
                .Where(id => !placed.Contains(id) && prerequisites[id].All(placed.Contains))
                .OrderBy(id => id)
                .ToList();

            if (ready.Count == 0)
            {
                // Cycle / unsatisfiable — append the remainder deterministically and stop.
                foreach (var id in set.Where(id => !placed.Contains(id)).OrderBy(id => id))
                {
                    ordered.Add(id);
                    placed.Add(id);
                }
                break;
            }

            foreach (var id in ready)
            {
                ordered.Add(id);
                placed.Add(id);
            }
        }

        return ordered;
    }

    // -----------------------------------------------------------------------
    // D6 rejection inference
    // -----------------------------------------------------------------------

    private static readonly Regex PathLikeToken = new(
        // path-with-separator (a/b/c.ext or a\b\c) OR bare filename-with-extension (foo.ts)
        @"(?<![\w./\\-])(?:[\w.+\-]+[/\\])+[\w.+\-]+|(?<![\w./\\-])[\w.+\-]+\.[A-Za-z0-9]{1,8}",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses path-like tokens out of free-text reviewer feedback (D6 step a). Recognises tokens
    /// that contain a path separator (<c>src/auth/login.ts</c>) or a bare filename with an extension
    /// (<c>login.ts</c>). Backslashes are normalised to forward slashes. Deterministic, deduplicated.
    /// </summary>
    public static IReadOnlyList<string> ExtractFileTokens(string? feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback)) return [];
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PathLikeToken.Matches(feedback))
        {
            var token = NormalizePath(m.Value);
            if (token.Length > 0 && seen.Add(token))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// Parses the set of repository-relative file paths touched by a unified git diff
    /// (D6 step b). Reads the <c>diff --git a/x b/y</c> headers (and <c>+++ b/y</c> as a fallback),
    /// taking the post-image path. Returns forward-slash normalised, deduplicated paths.
    /// </summary>
    public static IReadOnlySet<string> ExtractTouchedFiles(string? diff)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(diff)) return files;

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // diff --git a/<path> b/<path>
                var match = Regex.Match(line, @"^diff --git a/(.+?) b/(.+)$");
                if (match.Success)
                    files.Add(NormalizePath(match.Groups[2].Value));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                if (path == "/dev/null") continue;
                if (path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
                if (path.Length > 0) files.Add(NormalizePath(path));
            }
        }
        return files;
    }

    /// <summary>
    /// D6 rejection routing. Given reviewer feedback (free text + optional explicit
    /// <paramref name="targetFiles"/>), the files each child subtask touched, and the dependency
    /// edges, selects the subtasks to RE-DISPATCH: every child whose touched files intersect the
    /// inferred file set, PLUS all of their (transitive) dependents. FALLBACK: if no files can be
    /// inferred, or no child matches, ALL subtasks are selected (re-dispatch everything).
    /// </summary>
    public static AssemblyRejectionPlan InferRedispatch(
        string? feedback,
        IReadOnlyCollection<string>? targetFiles,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges)
    {
        var allIds = touchedFilesBySubtask.Keys.ToHashSet();

        // Inferred file set = explicit target_files ∪ tokens parsed from the free-text feedback.
        var inferred = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in (targetFiles ?? []).Concat(ExtractFileTokens(feedback)))
        {
            var n = NormalizePath(f);
            if (n.Length > 0 && seen.Add(n)) inferred.Add(n);
        }

        if (inferred.Count == 0)
            return new AssemblyRejectionPlan(allIds.OrderBy(x => x).ToList(), inferred, FellBackToAll: true);

        // Direct matches: a child whose touched files intersect the inferred set.
        var directlyMatched = new HashSet<int>();
        foreach (var (subtaskId, touched) in touchedFilesBySubtask)
            if (touched.Any(tf => inferred.Any(inf => FilesMatch(tf, inf))))
                directlyMatched.Add(subtaskId);

        if (directlyMatched.Count == 0)
            return new AssemblyRejectionPlan(allIds.OrderBy(x => x).ToList(), inferred, FellBackToAll: true);

        // Expand to include every (transitive) dependent of a matched subtask.
        var selected = new HashSet<int>(directlyMatched);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (dependent, dependency) in edges)
                if (selected.Contains(dependency) && allIds.Contains(dependent) && selected.Add(dependent))
                    changed = true;
        }

        return new AssemblyRejectionPlan(selected.OrderBy(x => x).ToList(), inferred, FellBackToAll: false);
    }

    /// <summary>
    /// A touched repo path <paramref name="touched"/> matches an inferred token <paramref name="inferred"/>
    /// when they are equal, when the touched path ends with <c>/inferred</c> (token is a suffix path),
    /// or when their file names match (token is a bare filename). All comparisons forward-slash,
    /// case-insensitive (cross-platform reviewer feedback is forgiving).
    /// </summary>
    private static bool FilesMatch(string touched, string inferred)
    {
        if (string.Equals(touched, inferred, StringComparison.OrdinalIgnoreCase)) return true;
        if (touched.EndsWith("/" + inferred, StringComparison.OrdinalIgnoreCase)) return true;
        if (inferred.EndsWith("/" + touched, StringComparison.OrdinalIgnoreCase)) return true;
        // Bare filename token (no separator) matches the touched path's filename.
        if (!inferred.Contains('/') &&
            string.Equals(FileName(touched), inferred, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FileName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}

/// <summary>
/// Outcome of <see cref="AssemblyPlanning.InferRedispatch"/>: the subtask ids to re-dispatch, the
/// inferred file set (for the <c>coordinator.assembly_changes_requested</c> event), and whether the
/// fallback (re-dispatch ALL) was triggered.
/// </summary>
public sealed record AssemblyRejectionPlan(
    IReadOnlyList<int> SubtaskIds,
    IReadOnlyList<string> InferredFiles,
    bool FellBackToAll);
