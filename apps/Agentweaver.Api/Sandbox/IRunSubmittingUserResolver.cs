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
        // Strip known coordinator sub-run suffixes so callers using sub-run IDs resolve the
        // parent run's submitting user (and therefore the correct user token).
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

        var run = await _runStore.GetAsync(id, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(run?.SubmittingUser) ? null : run!.SubmittingUser;
    }
}
