using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox;

/// <summary>Resolves the immutable AgentHost launch descriptor for a persisted run.</summary>
public interface IRunAgentHostContextResolver
{
    Task<AgentHostLaunchContext> ResolveAsync(string runId, CancellationToken ct = default);
}

/// <summary>
/// Selects writable pod-local execution for coordinator implementation children while preserving
/// the shared-worktree launch contract for ordinary runs.
/// </summary>
public sealed class RunAgentHostContextResolver : IRunAgentHostContextResolver
{
    private static readonly string[] CoordinatorAgentSuffixes =
    [
        "-coordinator-draft",
        "-coordinator-decompose",
        "-coordinator-orchestrate",
    ];

    private readonly IRunStore _runStore;
    private readonly WorktreeManager _worktreeManager;
    private readonly bool _implementationEnabled;

    public RunAgentHostContextResolver(
        IRunStore runStore,
        WorktreeManager worktreeManager,
        bool implementationEnabled)
    {
        _runStore = runStore;
        _worktreeManager = worktreeManager;
        _implementationEnabled = implementationEnabled;
    }

    public async Task<AgentHostLaunchContext> ResolveAsync(
        string runId,
        CancellationToken ct = default)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
        {
            if (CoordinatorAgentSuffixes.Any(
                    suffix => runId.EndsWith(suffix, StringComparison.Ordinal)))
            {
                return new AgentHostLaunchContext(SharedWorkingDirectory: null);
            }

            throw new InvalidOperationException($"Run id '{runId}' is invalid.");
        }

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{runId}' was not found.");
        var sharedContext = new AgentHostLaunchContext(run.WorktreePath);

        if (!_implementationEnabled
            || string.IsNullOrWhiteSpace(run.ParentRunId)
            || string.IsNullOrWhiteSpace(run.SubtaskId))
        {
            return sharedContext;
        }

        var expectedBranch = WorktreeManager.BranchNameFor(run.Id);
        if (string.IsNullOrWhiteSpace(run.WorktreePath)
            || !string.Equals(run.WorktreeBranch, expectedBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Implementation child '{runId}' must use its authoritative branch '{expectedBranch}'.");
        }

        var commitSha = _worktreeManager.GetBranchTipCommitSha(
            run.RepositoryPath,
            expectedBranch);
        var treeSha = _worktreeManager.GetBranchTipTreeSha(
            run.RepositoryPath,
            expectedBranch);
        if (!PodLocalExecutionWorkspace.IsGitObjectId(commitSha)
            || !PodLocalExecutionWorkspace.IsGitObjectId(treeSha))
        {
            throw new InvalidOperationException(
                $"Implementation child '{runId}' branch '{expectedBranch}' has no resolvable commit/tree.");
        }

        return new AgentHostLaunchContext(
            SharedWorkingDirectory: run.WorktreePath,
            SourceRepositoryPath: run.RepositoryPath,
            SourceRef: expectedBranch,
            BaseCommitSha: commitSha,
            ExpectedTreeHash: treeSha,
            WorkspaceMode: ExecutionWorkspaceMode.LocalWritable,
            Purpose: AgentHostPurpose.ImplementationTurn,
            ScratchRoot: PodLocalExecutionWorkspace.DefaultScratchRoot,
            CommitAuthorName: _worktreeManager.CommitAuthorName,
            CommitAuthorEmail: _worktreeManager.CommitAuthorEmail);
    }
}
