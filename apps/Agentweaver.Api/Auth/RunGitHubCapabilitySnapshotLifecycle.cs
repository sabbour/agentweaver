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
    internal async Task<bool> PrepareForLaunchAsync(
        Run run,
        CancellationToken ct,
        string? expectedCopilotBindingId = null,
        string? expectedCopilotCredentialVersion = null)

    {

        var runId = run.Id.ToString();

        var projectId = run.ProjectId?.ToString();

        if (string.IsNullOrWhiteSpace(projectId))

            return false;

        var sourceRunId = run.RetriedFrom ?? run.ParentRunId;

        if (!string.IsNullOrWhiteSpace(sourceRunId))

        {

            var repositoryOnly = run.ModelSource == ModelSource.Byok
                || (!string.IsNullOrWhiteSpace(run.RetriedFrom) && expectedCopilotBindingId is not null);
            var inherited = repositoryOnly
                ? await persistence.TryInheritRepositoryCapabilitySnapshotAsync(sourceRunId, runId, projectId, ct).ConfigureAwait(false)
                : await persistence.TryInheritCapabilitySnapshotsAsync(sourceRunId, runId, projectId, ct).ConfigureAwait(false);
            if (!inherited)

                return false;

            if (repositoryOnly && run.ModelSource == ModelSource.GitHubCopilot
                && expectedCopilotBindingId is not null
                && !await persistence.CaptureAcceptedCopilotSnapshotAsync(
                    runId, projectId, expectedCopilotBindingId, expectedCopilotCredentialVersion, ct).ConfigureAwait(false))
                return false;

        }

        else if ((await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false)).Count == 0)

        {

            // Trusted production root construction: select and insert-only create every currently

            // live v2 snapshot directly from authoritative sources. The finite v1 legacy table is a

            // one-time migration input only and is never consulted on this new-run capture path.

            var capture = await persistence.CaptureRootCapabilitySnapshotsAsync(
                runId, projectId, ct, includeCopilot: run.ModelSource == ModelSource.GitHubCopilot)

                .ConfigureAwait(false);

            if (capture.Unavailable != 0)

                return false;

        }



        var snapshots = await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false);
        if ((!string.IsNullOrWhiteSpace(expectedCopilotBindingId)
                || !string.IsNullOrWhiteSpace(expectedCopilotCredentialVersion))
            && !snapshots.Any(snapshot =>
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.CopilotBinding
                && MatchesExpectedCopilot(
                    snapshot,
                    expectedCopilotBindingId,
                    expectedCopilotCredentialVersion)))
        {
            return false;
        }

        foreach (var snapshot in snapshots)

        {
            if (run.ModelSource == ModelSource.Byok
                && snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot)
                continue;

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
        Run run,
        CancellationToken ct,
        bool platformScoped = false,
        string? userScopedEntraObjectId = null,
        string? expectedCopilotBindingId = null,
        string? expectedCopilotCredentialVersion = null)
    {
        RunGitHubCapabilitySnapshotRecord? copilotSnapshot;
        if (run.ProjectId is { } && !platformScoped)
        {
            if (!await PrepareForLaunchAsync(
                    run,
                    ct,
                    expectedCopilotBindingId,
                    expectedCopilotCredentialVersion).ConfigureAwait(false))
                return false;

            copilotSnapshot = (await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString(), ct)
                .ConfigureAwait(false))
                .SingleOrDefault(snapshot => snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot);
        }
        else
        {
            copilotSnapshot = !string.IsNullOrWhiteSpace(userScopedEntraObjectId)
                ? await persistence.RefreshUserUnattendedCopilotSnapshotAsync(
                    run.Id.ToString(), userScopedEntraObjectId, ct).ConfigureAwait(false)
                : await persistence.RefreshPlatformDefaultUnattendedCopilotSnapshotAsync(
                    run.Id.ToString(), ct).ConfigureAwait(false);
        }

        if ((!string.IsNullOrWhiteSpace(expectedCopilotBindingId)
                || !string.IsNullOrWhiteSpace(expectedCopilotCredentialVersion))
            && !MatchesExpectedCopilot(
                copilotSnapshot,
                expectedCopilotBindingId,
                expectedCopilotCredentialVersion))
        {
            return false;
        }
        if (copilotSnapshot is null)
            return false;

        return await broker.TryUseCopilotCredentialAsync(
                new SnapshotRef(copilotSnapshot.SnapshotRef),
                DateTimeOffset.UtcNow,
                static (_, _) => Task.CompletedTask,
                ct).ConfigureAwait(false) == GitHubCapabilityBrokerOutcome.Issued;
    }

    private static bool MatchesExpectedCopilot(
        RunGitHubCapabilitySnapshotRecord? snapshot,
        string? expectedBindingId,
        string? expectedCredentialVersion) =>
        snapshot is not null
        && (string.IsNullOrWhiteSpace(expectedBindingId)
            || string.Equals(snapshot.SourceBindingId, expectedBindingId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(expectedCredentialVersion)
            || string.Equals(
                snapshot.CredentialVersion,
                expectedCredentialVersion,
                StringComparison.Ordinal));

}
