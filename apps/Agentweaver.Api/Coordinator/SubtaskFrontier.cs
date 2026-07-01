namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Canonical <c>Subtask.Status</c> values (data-model.md). Centralized so the dispatch engine,
/// the topology projection, and the readiness frontier never drift on string literals.
/// </summary>
public static class SubtaskStatus
{
    public const string Pending = "pending";
    public const string Dispatched = "dispatched";
    public const string Running = "running";
    public const string RaiFlagged = "rai_flagged";
    public const string AssembleReady = "assemble_ready";
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>
    /// A subtask whose AgentHost pod could not be admitted because the namespace had no CPU
    /// headroom. NOT terminal and does NOT satisfy dependents: the dispatch loop parks it here and
    /// retries (back-off, capped) once the reaper frees quota or the node pool scales out. After the
    /// retry cap is hit it transitions to <see cref="Failed"/> with reason <c>capacity_unavailable</c>.
    /// </summary>
    public const string PendingCapacity = "pending_capacity";

    /// <summary>
    /// A subtask whose upstream dependency stalled (TTL-expired without progress). Terminal and
    /// assembly-ineligible, but semantically distinct from <see cref="Failed"/>: the subtask was
    /// blocked by an upstream stall — it never ran — not by a self-owned failure. Allows the
    /// coordinator API and assembly-blocked diagnostic payload to distinguish dependency-blocked
    /// subtasks from independently failing ones, making recovery actions clearer to operators.
    /// </summary>
    public const string Blocked = "blocked";

    /// <summary>
    /// A subtask is <em>satisfied</em> for the purpose of unblocking its dependents only when it
    /// reached <see cref="AssembleReady"/> or <see cref="Completed"/>. A <see cref="RaiFlagged"/>,
    /// <see cref="Failed"/>, or <see cref="Blocked"/> predecessor does NOT satisfy a dependency —
    /// its dependents stay blocked (they never dispatch), which is the correct serial-ordering
    /// semantics.
    /// </summary>
    public static bool Satisfies(string status) =>
        status is AssembleReady or Completed;

    /// <summary>True once a subtask can make no further progress on its own.</summary>
    public static bool IsTerminal(string status) =>
        status is AssembleReady or Completed or RaiFlagged or Failed or Blocked;
}

/// <summary>
/// Pure, side-effect-free dependency-DAG logic over persisted subtasks. Extracted from the EF
/// layer so the dispatch-frontier / unblocking rules can be unit tested cheaply without a database
/// or any live agent (the heavier end-to-end child-run coverage lives in the QA wave).
/// </summary>
public static class SubtaskFrontier
{
    /// <summary>
    /// Returns the ids of subtasks that are <c>pending</c> and whose every dependency is satisfied
    /// (<see cref="SubtaskStatus.Satisfies"/>), i.e. the frontier that can be dispatched right now.
    /// Independent subtasks come back together (parallel); a dependent subtask only surfaces once
    /// all of its predecessors are assemble_ready/completed (serial ordering).
    /// </summary>
    /// <param name="statusById">Current status of every subtask in the plan, keyed by id.</param>
    /// <param name="edges">Dependency edges as (subtaskId, dependsOnSubtaskId) pairs.</param>
    public static IReadOnlyList<int> ReadyPending(
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges)
    {
        ValidateEdges(statusById, edges);

        var ready = new List<int>();
        foreach (var (id, status) in statusById)
        {
            if (status != SubtaskStatus.Pending)
                continue;

            var blocked = edges
                .Where(e => e.SubtaskId == id)
                .Any(e => !DependencySatisfied(statusById, e.DependsOnSubtaskId));

            if (!blocked)
                ready.Add(id);
        }

        // Deterministic order so parallel dispatch is reproducible and testable.
        ready.Sort();
        return ready;
    }

    /// <summary>
    /// True when no subtask is still pending and able to advance: every subtask is terminal, or the
    /// only remaining pending subtasks are permanently blocked by a non-satisfying (failed /
    /// rai_flagged) dependency. Used by the dispatch loop to know when to stop.
    /// </summary>
    public static bool IsQuiescent(
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        int inFlightCount)
    {
        ValidateEdges(statusById, edges);

        if (inFlightCount > 0)
            return false;
        return ReadyPending(statusById, edges).Count == 0;
    }

    private static void ValidateEdges(
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges)
    {
        foreach (var (subtaskId, dependsOnId) in edges)
        {
            if (!statusById.ContainsKey(subtaskId))
                throw new InvalidOperationException(
                    $"Corrupt work plan dependency: subtask {subtaskId} is not part of the plan.");

            if (!statusById.ContainsKey(dependsOnId))
                throw new InvalidOperationException(
                    $"Corrupt work plan dependency: subtask {subtaskId} depends on missing subtask {dependsOnId}.");
        }
    }

    private static bool DependencySatisfied(
        IReadOnlyDictionary<int, string> statusById, int dependsOnId) =>
        SubtaskStatus.Satisfies(statusById[dependsOnId]);
}
