using System.Text.RegularExpressions;

namespace Agentweaver.Api.Coordinator;

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
    /// no-op). Returns the ids of every NON-eligible subtask (empty ⇒ the plan may assemble).
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

    /// <summary>
    /// Returns only subtasks that are both terminal and assembly-ineligible. Pending/running children
    /// are "not ready yet", not a permanent assembly_blocked verdict.
    /// </summary>
    public static IReadOnlyList<int> TerminalIneligibleSubtasks(IReadOnlyDictionary<int, string> statusById)
    {
        var ineligible = new List<int>();
        foreach (var (id, status) in statusById)
            if (SubtaskStatus.IsTerminal(status) && !IsEligible(status))
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

    // UNIFIED AUTONOMOUS STEERING (rev8, §9): the fragile prose-parsing InferRedispatch heuristic and
    // its AssemblyRejectionPlan result record are DELETED. Reviewer feedback no longer selects
    // subtasks by parsing prose; the coordinator (or the deterministic direction-B executor in
    // CoordinatorAssemblyService.RequestChangesAsync) chooses targets explicitly. The tokenizers
    // (ExtractFileTokens / ExtractTouchedFiles / NormalizePath) are retained: output-conflict
    // detection still uses them.

    // -----------------------------------------------------------------------
    // #223 implicated-subtask scoping (shared by the deterministic direction-B executor and the
    // live steering path). Deterministic reverse-map from a reviewer's STRUCTURED file hint onto the
    // subtasks that touched those files — never prose, never subtask ids. Fail-safe over-include.
    // -----------------------------------------------------------------------

    /// <summary>Fallback reason: the reviewer emitted no structured <c>targetFiles</c> field at all.</summary>
    public const string ScopeFallbackNoField = "no_target_files_field";

    /// <summary>Fallback reason: a <c>targetFiles</c> field was present but reverse-mapped to no subtask.</summary>
    public const string ScopeFallbackNoMatch = "target_files_matched_nothing";

    /// <summary>
    /// #223 — the SINGLE implicated-scoping rule shared by <c>RequestChangesAsync</c> and the live
    /// <c>RouteAssemblyGateThroughSteeringAsync</c>. Reverse-maps a reviewer's STRUCTURED
    /// <paramref name="targetFiles"/> hint (repo-relative diff paths the reviewer actually saw — never
    /// prose, never subtask ids) onto the assembly-eligible subtasks that touched one of those files.
    /// Only these subtasks' authors produced a rejected artifact, so only these are eligible for author
    /// lockout — a prose/research/PRD/UX subtask that committed only unnamed files is EXCLUDED.
    /// <para>Fail-safe over-include: when no target files are provided (<paramref name="usedFallback"/> =
    /// true, reason <see cref="ScopeFallbackNoField"/>) OR the provided files match nothing
    /// (<paramref name="usedFallback"/> = true, reason <see cref="ScopeFallbackNoMatch"/>), the WHOLE
    /// contributor set is returned — a request-changes must always reset SOMETHING. The out-params make
    /// the reversion to broad behavior observable rather than silent.</para>
    /// </summary>
    /// <param name="touchedFilesBySubtask">Assembly-eligible subtask id → the (normalized) files it committed.</param>
    /// <param name="targetFiles">The reviewer's structured implicated-file hint (may be null/empty).</param>
    /// <param name="usedFallback">True when the broad all-contributors set was returned.</param>
    /// <param name="fallbackReason">
    /// <see cref="ScopeFallbackNoField"/> / <see cref="ScopeFallbackNoMatch"/> when
    /// <paramref name="usedFallback"/>; empty otherwise.</param>
    public static IReadOnlyList<int> ScopeImplicatedSubtasks(
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        IReadOnlyList<string>? targetFiles,
        out bool usedFallback,
        out string fallbackReason)
    {
        var candidateIds = touchedFilesBySubtask.Keys.OrderBy(x => x).ToList();

        var wanted = (targetFiles ?? [])
            .Select(NormalizePath)
            .Where(f => f.Length > 0)
            .ToList();

        if (wanted.Count == 0)
        {
            usedFallback = true;
            fallbackReason = ScopeFallbackNoField;
            return candidateIds;
        }

        var matched = touchedFilesBySubtask
            .Where(kv => kv.Value.Any(tf => wanted.Any(w =>
                string.Equals(tf, w, StringComparison.OrdinalIgnoreCase)
                || tf.EndsWith("/" + w, StringComparison.OrdinalIgnoreCase))))
            .Select(kv => kv.Key)
            .OrderBy(x => x)
            .ToList();

        if (matched.Count > 0)
        {
            usedFallback = false;
            fallbackReason = string.Empty;
            return matched;
        }

        usedFallback = true;
        fallbackReason = ScopeFallbackNoMatch;
        return candidateIds;
    }

    /// <summary>
    /// #223 — the transitive DEPENDENT closure of <paramref name="implicated"/>: every subtask that
    /// depends, directly or transitively, on an implicated subtask (following the
    /// <c>(SubtaskId, DependsOnSubtaskId)</c> edges in REVERSE). EXCLUDES the implicated subtasks
    /// themselves. The redispatch set is <c>implicated ∪ dependents</c>: a dependent did nothing wrong
    /// (its author is NEVER locked out) but must rebuild against the revised contract of the subtask it
    /// depends on. Deterministic (ascending id), cycle-safe (each id visited once).
    /// </summary>
    public static IReadOnlyList<int> TransitiveDependents(
        IReadOnlyCollection<int> implicated,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges)
    {
        var seed = implicated.ToHashSet();
        var dependents = new HashSet<int>();
        var queue = new Queue<int>(seed);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var e in edges.Where(e => e.DependsOnSubtaskId == current))
            {
                if (seed.Contains(e.SubtaskId))
                    continue;
                if (dependents.Add(e.SubtaskId))
                    queue.Enqueue(e.SubtaskId);
            }
        }
        return dependents.OrderBy(x => x).ToList();
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}
