using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Resolves the submitting user of a run from its run id so the pod-per-run executor can inject
/// <c>AgentHost__UserId</c> into the AgentHost pod.
///
/// <para>
/// Without this, the in-pod <c>CopilotAIAgent</c> receives a null user id, logs
/// "No submitting user was available", and falls back to the installation GitHub token (which has
/// no Copilot entitlement) instead of the submitting user's signed-in token. That fallback fails
/// the very first model turn with the opaque SDK error
/// "Session was not created with authentication info or custom provider".
/// </para>
/// </summary>
public interface IRunSubmittingUserResolver
{
    /// <summary>
    /// Returns the submitting user id for <paramref name="runId"/>, or <see langword="null"/> if the
    /// run is unknown or has no submitting user.
    /// </summary>
    Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Returns the per-run working directory (the shared orchestration worktree path,
    /// <see cref="Run.WorktreePath"/>) for <paramref name="runId"/>, or <see langword="null"/> if the
    /// run is unknown or has no worktree path yet.
    ///
    /// <para>
    /// Delivered to the warm-pool pod via <c>POST /configure</c> so its in-pod <c>SetupAsync</c> uses
    /// the SAME working directory the run's system prompt references. Without this, warm pods default
    /// to the static <c>AgentHost__WorkingDirectory</c> env (<c>/workspace</c> root) while the prompt
    /// points at <c>/workspace/{worktree}</c>, so sibling agents of one parent write to divergent
    /// directories and later stages cannot find files produced by earlier stages.
    /// </para>
    /// </summary>
    Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IRunStore"/>-backed <see cref="IRunSubmittingUserResolver"/>. Looks up the run row and
/// returns its <see cref="Run.SubmittingUser"/> — the same identity the API resolved as the run's
/// owner when it was created.
/// </summary>
public sealed class RunStoreSubmittingUserResolver : IRunSubmittingUserResolver
{
    // Coordinator creates sub-runs by appending these suffixes to the parent run ID. The sub-run IDs
    // are not stored in the run store; strip the suffix to find the parent run's submitting user.
    private static readonly string[] _coordinatorSuffixes =
    [
        "-coordinator-draft",
        "-coordinator-decompose",
        "-coordinator-orchestrate",
    ];

    private readonly IRunStore _runStore;

    public RunStoreSubmittingUserResolver(IRunStore runStore) => _runStore = runStore;

    public async Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default)
    {
        var run = await ResolveRunAsync(runId, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(run?.SubmittingUser) ? null : run!.SubmittingUser;
    }

    public async Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default)
    {
        var run = await ResolveRunAsync(runId, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(run?.WorktreePath) ? null : run!.WorktreePath;
    }

    /// <summary>
    /// Loads the run row for <paramref name="runId"/>, stripping known coordinator sub-run suffixes
    /// first so callers using sub-run IDs (e.g. <c>{parent}-coordinator-decompose</c>) resolve the
    /// parent run — whose submitting user and shared orchestration worktree the sub-run inherits.
    /// </summary>
    private async Task<Run?> ResolveRunAsync(string runId, CancellationToken ct)
    {
        foreach (var suffix in _coordinatorSuffixes)
        {
            if (runId.EndsWith(suffix, StringComparison.Ordinal))
            {
                runId = runId[..^suffix.Length];
                break;
            }
        }

        if (!RunId.TryParse(runId, out var id))
            return null;

        return await _runStore.GetAsync(id, ct).ConfigureAwait(false);
    }
}
