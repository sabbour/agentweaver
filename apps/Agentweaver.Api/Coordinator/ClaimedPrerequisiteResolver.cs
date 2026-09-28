using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

internal static class ClaimedPrerequisiteResolver
{
    internal sealed record Input(
        BacklogClaimedPrerequisite Claim,
        RunOutputRevision Revision,
        IReadOnlyList<RunOutputTree.File> Files);

    internal static async Task<IReadOnlyList<Input>> ResolveAsync(
        string? claimedPrerequisitesJson,
        Func<RunId, string, CancellationToken, Task<RunOutputRevision>> resolveRevision,
        CancellationToken ct)
    {
        if (claimedPrerequisitesJson is null)
            throw new RunOutputRevisionUnavailableException("missing_claimed_prerequisites");

        BacklogClaimedPrerequisite[] claims;
        try
        {
            claims = JsonSerializer.Deserialize<BacklogClaimedPrerequisite[]>(claimedPrerequisitesJson)
                ?? throw new RunOutputRevisionUnavailableException("invalid_claimed_prerequisites");
        }
        catch (JsonException)
        {
            throw new RunOutputRevisionUnavailableException("invalid_claimed_prerequisites");
        }

        var inputs = new List<Input>(claims.Length);
        foreach (var claim in claims)
        {
            if (claim is null || !RunId.TryParse(claim.RunId, out var upstreamRunId)
                || string.IsNullOrWhiteSpace(claim.OutputRevisionId)
                || claim.Outcome is not ("integrated" or "accepted_no_change"))
                throw new RunOutputRevisionUnavailableException("invalid_claimed_prerequisites");

            var revision = await resolveRevision(upstreamRunId, claim.OutputRevisionId, ct)
                .ConfigureAwait(false);
            if (revision.RunId != upstreamRunId
                || revision.RevisionId != claim.OutputRevisionId
                || revision.LifecycleGeneration != claim.LifecycleGeneration
                || revision.TreeHash != claim.TreeHash
                || revision.WorkflowDigest != claim.ExecutableWorkflowContentDigest
                || (claim.Outcome == "accepted_no_change") != revision.AcceptedNoChange
                || (revision.AcceptedNoChange
                    && (revision.SchemaVersion != RunOutputRevision.NoChangeSchemaVersion
                        || revision.MergedCommitHash != claim.MergedCommitHash))
                || (revision.MergedCommitHash is not null
                    && revision.MergedCommitHash != claim.MergedCommitHash))
                throw new RunOutputRevisionUnavailableException("claim_revision_mismatch");

            inputs.Add(new Input(claim, revision, revision.ResolveFiles()));
        }
        return inputs;
    }
}
