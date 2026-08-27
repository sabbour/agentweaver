using Agentweaver.Domain;



namespace Agentweaver.Api.Auth;



/// <summary>

/// Captures or inherits immutable capability snapshots at trusted run lifecycle boundaries.

/// It deliberately fences metadata only: #947 owns capability delivery to repository and

/// Copilot adapters.

/// </summary>

internal sealed class RunGitHubCapabilitySnapshotLifecycle(

    TwoAppPersistenceStore persistence,

    GitHubCapabilityBroker broker)

{

    internal async Task<bool> PrepareForLaunchAsync(Run run, CancellationToken ct)

    {

        var runId = run.Id.ToString();

        var sourceRunId = run.RetriedFrom ?? run.ParentRunId;

        if (!string.IsNullOrWhiteSpace(sourceRunId))

        {

            if (!await persistence.TryInheritCapabilitySnapshotsAsync(sourceRunId, runId, ct).ConfigureAwait(false))

                return false;

        }

        else if ((await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false)).Count == 0)

        {

            var capture = await persistence.BackfillCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false);

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

}
