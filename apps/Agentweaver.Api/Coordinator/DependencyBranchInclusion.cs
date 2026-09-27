using Agentweaver.Api.Git;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// The result of deciding whether a SATISFIED dependency's committed worktree branch should be
/// included when assembling a dependency-base or final-collective integration branch (issue #197).
/// </summary>
internal enum BranchInclusionOutcome
{
    /// <summary>The branch is valid (exists and its tip tree matches the recorded handoff tree
    /// hash) — include it. This covers genuinely-empty (no-op) children too: their branch merges as
    /// a no-op / fast-forward and never deadlocks.</summary>
    Include,

    /// <summary>The dependency has no branch name or the branch does not exist in the repo. A satisfied
    /// child MUST have committed its branch, so this is a LOUD contract violation, not a normal skip.</summary>
    ExcludeMissingBranch,

    /// <summary>The branch has no recorded tree hash, or its tip TREE hash does not match the handoff
    /// hash — the output cannot be verified. Excluded loudly
    /// so a stale base is never silently propagated.</summary>
    ExcludeTreeMismatch,
}

/// <summary>
/// Single source of truth for the dependency-base / final-assembly branch INCLUSION predicate.
///
/// <para><b>Root cause (issue #197):</b> both the dependency-base rebuild
/// (<see cref="CoordinatorDispatchService"/>) and the final collective assembly
/// (<see cref="CoordinatorAssemblyService"/>) used to gate branch inclusion on
/// <c>!string.IsNullOrEmpty(run.Diff)</c>. But <c>run.Diff</c> is a best-effort textual DISPLAY
/// artifact: <see cref="Runs.WorktreeOperationsAdapter.GetDiff"/> swallows all exceptions and can
/// return an EMPTY string even after a real commit. The authoritative artifact is the committed
/// worktree BRANCH (its tip tree == the run's recorded <see cref="Run.TreeHash"/>), NOT the diff
/// string. Gating on the diff silently dropped committed children whose display diff was swallowed,
/// so dependents branched from a base missing upstream work.</para>
///
/// <para>This helper decides inclusion purely from branch VALIDITY (exists + tip tree matches the
/// recorded handoff contract). <c>run.Diff</c> is kept ONLY for UI / touched-file extraction, never
/// as inclusion authority.</para>
/// </summary>
internal static class DependencyBranchInclusion
{
    internal sealed record VerifiedBranch(BranchInclusionOutcome Outcome, IntegrationChildInput? Input);

    internal static bool RequiresArtifact(Subtask subtask) =>
        CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(subtask.DeclaredOutputPathsJson).State
        != CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty;

    /// <summary>Requires both a recorded handoff tree and a matching branch tip.</summary>
    internal static BranchInclusionOutcome Evaluate(
        WorktreeManager worktreeManager,
        string repositoryPath,
        string? worktreeBranch,
        string? treeHash) => Verify(worktreeManager, repositoryPath, worktreeBranch, treeHash).Outcome;

    internal static VerifiedBranch Verify(
        WorktreeManager worktreeManager,
        string repositoryPath,
        string? worktreeBranch,
        string? treeHash)
    {
        if (string.IsNullOrEmpty(worktreeBranch) || !worktreeManager.BranchExists(repositoryPath, worktreeBranch))
            return new(BranchInclusionOutcome.ExcludeMissingBranch, null);

        if (string.IsNullOrWhiteSpace(treeHash))
            return new(BranchInclusionOutcome.ExcludeTreeMismatch, null);

        var input = worktreeManager.GetVerifiedChildInput(repositoryPath, worktreeBranch, treeHash);
        return input is null
            ? new(BranchInclusionOutcome.ExcludeTreeMismatch, null)
            : new(BranchInclusionOutcome.Include, input);
    }
}
