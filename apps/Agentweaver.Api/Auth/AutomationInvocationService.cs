using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Internal boundary between a validated automation trigger and its unattended run.  It accepts no
/// authority material from callers: the activation's immutable tuple is fenced before an invocation
/// is claimed, and is copied into the run's existing immutable capability-snapshot records.
/// </summary>
public sealed class AutomationInvocationService(
    MemoryDbContext db,
    TwoAppPersistenceStore persistence)
{
    public async Task<bool> TryClaimAsync(
        string activationId,
        string occurrenceKey,
        string? deliveryId,
        string? eventName,
        long? installationId,
        long? repositoryId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(activationId) ||
            string.IsNullOrWhiteSpace(occurrenceKey) ||
            !installationId.HasValue ||
            !repositoryId.HasValue)
            return false;

        var activation = await persistence.TryFenceAutomationActivationAsync(activationId, ct).ConfigureAwait(false);
        if (activation is null ||
            activation.InstallationId != installationId ||
            activation.RepositoryId != repositoryId)
            return false;

        var result = await persistence.ClaimInvocationAsync(new AutomationInvocationRecord
        {
            Id = SnapshotRef.Create().Value,
            ProjectId = activation.ProjectId,
            ActivationId = activation.ActivationId,
            OccurrenceKey = occurrenceKey,
            DeliveryId = deliveryId,
            EventName = eventName,
            InstallationId = installationId,
            RepositoryId = repositoryId,
            Outcome = AutomationInvocationOutcome.Claimed,
            ReceivedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        return result == InvocationClaimResult.Claimed;
    }

    /// <summary>
    /// Copies the precise activation tuple into the run before it starts. Existing records must be
    /// the exact same pair, which makes this replay-safe and prevents a later activation from being
    /// substituted for a claimed invocation.
    /// </summary>
    public async Task<bool> TryPrepareRunAsync(string invocationId, string runId, CancellationToken ct = default)
    {
        var invocation = await db.AutomationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invocationId && x.Outcome == AutomationInvocationOutcome.Claimed, ct)
            .ConfigureAwait(false);
        if (invocation is null)
            return false;

        var activation = await persistence.TryFenceAutomationActivationAsync(invocation.ActivationId, ct).ConfigureAwait(false);
        if (activation is null ||
            activation.ProjectId != invocation.ProjectId ||
            activation.InstallationId != invocation.InstallationId ||
            activation.RepositoryId != invocation.RepositoryId)
            return false;
        var binding = await db.ProjectCopilotBindings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == activation.CopilotBindingId &&
            x.ProjectId == activation.ProjectId &&
            x.GrantDigest == activation.CopilotBindingGrantDigest &&
            x.Status == GitHubBindingStatus.Active &&
            x.DeactivatedAt == null, ct).ConfigureAwait(false);
        if (binding is null)
            return false;

        var existing = await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false);
        if (existing.Count != 0)
            return MatchesActivation(existing, activation, binding);

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            // Recheck after taking the transaction so concurrent pickup attempts cannot select a
            // different snapshot set for the same run.
            existing = await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false);
            if (existing.Count != 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return MatchesActivation(existing, activation, binding);
            }

            db.RunGitHubCapabilitySnapshots.AddRange(
                new RunGitHubCapabilitySnapshotRecord
                {
                    SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
                    Purpose = GitHubCapabilityPurpose.UnattendedRepository, AppKind = GitHubAppKind.Repo,
                    SourceKind = GitHubCapabilitySnapshotSourceKind.RepositoryGrant, ProjectId = activation.ProjectId,
                    InstallationId = activation.InstallationId, RepositoryId = activation.RepositoryId,
                    GrantDigest = activation.RepositoryGrantDigest, CapturedAt = DateTimeOffset.UtcNow,
                },
                new RunGitHubCapabilitySnapshotRecord
                {
                    SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
                    Purpose = GitHubCapabilityPurpose.UnattendedCopilot, AppKind = GitHubAppKind.Copilot,
                    SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding, ProjectId = activation.ProjectId,
                    SourceBindingId = activation.CopilotBindingId,
                    CredentialReference = binding.CredentialReference, CredentialVersion = binding.CredentialVersion,
                    GrantDigest = activation.CopilotBindingGrantDigest,
                    CapturedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool MatchesActivation(
        IReadOnlyList<RunGitHubCapabilitySnapshotRecord> snapshots,
        FencedAutomationActivation activation,
        ProjectCopilotBindingRecord binding) =>
        snapshots.Count == 2 &&
        snapshots.Any(x => x.Purpose == GitHubCapabilityPurpose.UnattendedRepository &&
                           x.AppKind == GitHubAppKind.Repo &&
                           x.SourceKind == GitHubCapabilitySnapshotSourceKind.RepositoryGrant &&
                           x.ProjectId == activation.ProjectId &&
                           x.InstallationId == activation.InstallationId &&
                           x.RepositoryId == activation.RepositoryId &&
                           x.GrantDigest == activation.RepositoryGrantDigest) &&
        snapshots.Any(x => x.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
                           x.AppKind == GitHubAppKind.Copilot &&
                           x.SourceKind == GitHubCapabilitySnapshotSourceKind.CopilotBinding &&
                           x.ProjectId == activation.ProjectId &&
                           x.SourceBindingId == activation.CopilotBindingId &&
                           x.CredentialReference == binding.CredentialReference &&
                           x.CredentialVersion == binding.CredentialVersion &&
                           x.GrantDigest == activation.CopilotBindingGrantDigest);
}
