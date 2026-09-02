using Agentweaver.Domain;
using Agentweaver.Api.Memory;



namespace Agentweaver.Api.Auth;



/// <summary>

/// Captures or inherits immutable capability snapshots at trusted run lifecycle boundaries.
/// General launch preparation fences metadata; unattended Copilot launch preparation additionally
/// proves that the fenced credential can be redeemed before sandbox creation.

/// </summary>

internal sealed class RunGitHubCapabilitySnapshotLifecycle(

    GitHubConnectionsPersistenceStore persistence,

    GitHubCapabilityBroker broker)
{
    internal async Task<bool> PrepareForLaunchAsync(Run run, CancellationToken ct)

    {

        var runId = run.Id.ToString();

        var projectId = run.ProjectId?.ToString();

        if (string.IsNullOrWhiteSpace(projectId))

            return false;

        var sourceRunId = run.RetriedFrom ?? run.ParentRunId;

        if (!string.IsNullOrWhiteSpace(sourceRunId))

        {

            if (!await persistence.TryInheritCapabilitySnapshotsAsync(sourceRunId, runId, projectId, ct).ConfigureAwait(false))

                return false;

        }

        else if ((await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false)).Count == 0)

        {

            // Trusted production root construction: select and insert-only create every currently

            // live v2 snapshot directly from authoritative sources. The finite v1 legacy table is a

            // one-time migration input only and is never consulted on this new-run capture path.

            var capture = await persistence.CaptureRootCapabilitySnapshotsAsync(runId, projectId, ct)

                .ConfigureAwait(false);

            if (capture.Unavailable != 0)

                return false;

        }



        foreach (var snapshot in await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false))

        {

            if (await broker.TryFenceAsync(

                    snapshot.Purpose,

                    new SnapshotRef(snapshot.SnapshotRef),

                    DateTimeOffset.UtcNow,

                    ct).ConfigureAwait(false) is null)

                return false;

        }



        return true;
    }

    /// <summary>
    /// Prepares the normal immutable snapshot set, then proves that the unattended Copilot
    /// capability is present, fenced, and redeemable. A partial or metadata-only snapshot set is not
    /// sufficient: accepting it would defer a missing credential until after execution has started.
    /// </summary>
    /// <param name="platformScoped">
    /// When <c>true</c>, the run's credential is always resolved from the PLATFORM-level Copilot
    /// connection (<c>PlatformDefaultCopilotBindings</c>), even when <paramref name="run"/> carries a
    /// non-null <see cref="Run.ProjectId"/>. This is for personal/Operator ("Assistant") sessions:
    /// their <c>ProjectId</c> is only incidental UI context (e.g. the project the user happened to be
    /// viewing when they opened the chat) — never a real, repo-scoped run — so their credential must
    /// never depend on that project's own (possibly broken/missing) Copilot binding. Project-scoped
    /// work (Coordinator runs, subtasks, retries) must keep passing <c>false</c> (the default) so it
    /// continues to require ITS OWN project-bound capability snapshot.
    /// </param>
    internal async Task<bool> PrepareForUnattendedCopilotLaunchAsync(
        Run run, CancellationToken ct, bool platformScoped = false)
    {
        if (run.ProjectId is { } && !platformScoped)
        {
            if (!await PrepareForLaunchAsync(run, ct).ConfigureAwait(false))
                return false;
        }
        else
        {
            var existing = await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString(), ct)
                .ConfigureAwait(false);
            if (existing.Count == 0)
            {
                if (!await persistence.TryCapturePlatformDefaultUnattendedCopilotSnapshotAsync(
                        run.Id.ToString(),
                        ct).ConfigureAwait(false))
                    return false;
            }
            else if (!existing.Any(snapshot => snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot))
            {
                return false;
            }
        }

        var copilotSnapshot = (await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString(), ct)
            .ConfigureAwait(false))
            .SingleOrDefault(snapshot => snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot);
        return copilotSnapshot is not null &&
            await broker.TryUseCopilotCredentialAsync(
                new SnapshotRef(copilotSnapshot.SnapshotRef),
                DateTimeOffset.UtcNow,
                static (_, _) => Task.CompletedTask,
                ct).ConfigureAwait(false) == GitHubCapabilityBrokerOutcome.Issued;
    }

}
