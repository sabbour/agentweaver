using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

public sealed record AutomationInvocationClaim(string InvocationId);
public sealed record AutomationInvocationTaskReservation(BacklogTaskId BacklogTaskId, bool IsBound);
public sealed record OutstandingScheduleInvocation(string InvocationId, string OccurrenceKey);

public interface IAutomationInvocationService
{
    Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId,
        string occurrenceKey,
        string? deliveryId,
        string? eventName,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an existing event invocation only when its active server-owned activation and
    /// occurrence identity are still the exact values supplied by the trusted trigger path.
    /// </summary>
    Task<AutomationInvocationClaim?> TryGetClaimedInvocationForProjectAsync(
        ProjectId projectId,
        string occurrenceKey,
        string? deliveryId,
        string? eventName,
        CancellationToken ct = default);

    Task<bool> TryBindBacklogTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default);

    Task<AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId,
        ProjectId projectId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically associates a legacy staged provisional task with an otherwise unbound invocation.
    /// The caller must first prove the task is the sole exact server-owned occurrence match.
    /// </summary>
    Task<bool> TryAdoptLegacyProvisionalTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default);

    Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId,
        string occurrenceKeyPrefix,
        IReadOnlyCollection<string> legacyProvisionalOccurrenceKeys,
        int maximumCount,
        CancellationToken ct = default);

    Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default);

    /// <summary>
    /// Discards a claimed invocation only after its provisional task has been durably removed.
    /// The task id fences the cleanup so a caller cannot release an unrelated invocation.
    /// </summary>
    Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default);

    Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId,
        BacklogTaskId backlogTaskId,
        string runId,
        CancellationToken ct = default);
}

/// <summary>
/// Internal boundary between a validated automation trigger and its unattended run.  It accepts no
/// authority material from callers: the activation's immutable tuple is fenced before an invocation
/// is claimed, and is copied into the run's existing immutable capability-snapshot records.
/// </summary>
public sealed class AutomationInvocationService(
    MemoryDbContext db,
    GitHubConnectionsPersistenceStore persistence) : IAutomationInvocationService
{
    /// <summary>
    /// Claims an invocation only from the sole active activation for the project. Trigger producers
    /// supply no activation, repository, or installation identity; all three are recovered and
    /// fenced from server-owned state before the durable claim is written.
    /// </summary>
    public async Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId,
        string occurrenceKey,
        string? deliveryId,
        string? eventName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(occurrenceKey))
            return null;

        var activation = await db.AutomationActivations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId.ToString() &&
                                      x.Status == AutomationActivationStatus.Active, ct)
            .ConfigureAwait(false);
        if (activation is null ||
            !await TryClaimAsync(
                activation.Id,
                occurrenceKey,
                deliveryId,
                eventName,
                activation.InstallationId,
                activation.RepositoryId,
                ct).ConfigureAwait(false))
            return null;

        var invocationId = await db.AutomationInvocations.AsNoTracking()
            .Where(x => x.ActivationId == activation.Id &&
                        x.OccurrenceKey == occurrenceKey &&
                        x.ProjectId == projectId.ToString())
            .Select(x => x.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(invocationId) ? null : new(invocationId);
    }

    public async Task<AutomationInvocationClaim?> TryGetClaimedInvocationForProjectAsync(
        ProjectId projectId,
        string occurrenceKey,
        string? deliveryId,
        string? eventName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(occurrenceKey))
            return null;

        var activation = await db.AutomationActivations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId.ToString() &&
                                      x.Status == AutomationActivationStatus.Active, ct)
            .ConfigureAwait(false);
        if (activation is null)
            return null;

        var invocation = await db.AutomationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActivationId == activation.Id &&
                                      x.ProjectId == projectId.ToString() &&
                                      x.OccurrenceKey == occurrenceKey &&
                                      x.DeliveryId == deliveryId &&
                                      x.EventName == eventName &&
                                      x.Outcome == AutomationInvocationOutcome.Claimed, ct)
            .ConfigureAwait(false);
        if (invocation is null)
            return null;

        var fencedActivation = await persistence.TryFenceAutomationActivationAsync(activation.Id, ct)
            .ConfigureAwait(false);
        return fencedActivation is null ||
               fencedActivation.ProjectId != invocation.ProjectId ||
               fencedActivation.ProjectId != projectId.ToString() ||
               fencedActivation.InstallationId != invocation.InstallationId ||
               fencedActivation.RepositoryId != invocation.RepositoryId
            ? null
            : new(invocation.Id);
    }

    public async Task<bool> TryBindBacklogTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default)
    {
        var changed = await db.AutomationInvocations
            .Where(x => x.Id == invocationId &&
                        x.ProjectId == projectId.ToString() &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        x.BacklogTaskId == null &&
                        (x.PendingBacklogTaskId == null || x.PendingBacklogTaskId == backlogTaskId.ToString()))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BacklogTaskId, backlogTaskId.ToString()), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return true;

        return await db.AutomationInvocations.AsNoTracking().AnyAsync(x =>
            x.Id == invocationId &&
            x.ProjectId == projectId.ToString() &&
            x.Outcome == AutomationInvocationOutcome.Claimed &&
            x.BacklogTaskId == backlogTaskId.ToString(), ct).ConfigureAwait(false);
    }

    public async Task<AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        var invocation = await db.AutomationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invocationId &&
                                      x.ProjectId == projectId.ToString() &&
                                      x.Outcome == AutomationInvocationOutcome.Claimed, ct)
            .ConfigureAwait(false);
        if (invocation is null)
            return null;
        if (!string.IsNullOrWhiteSpace(invocation.BacklogTaskId))
            return new(BacklogTaskId.Parse(invocation.BacklogTaskId), IsBound: true);
        if (!string.IsNullOrWhiteSpace(invocation.PendingBacklogTaskId))
            return new(BacklogTaskId.Parse(invocation.PendingBacklogTaskId), IsBound: false);

        var taskId = BacklogTaskId.New();
        var changed = await db.AutomationInvocations
            .Where(x => x.Id == invocationId &&
                        x.ProjectId == projectId.ToString() &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        x.BacklogTaskId == null &&
                        x.PendingBacklogTaskId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.PendingBacklogTaskId, taskId.ToString()), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return new(taskId, IsBound: false);

        invocation = await db.AutomationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invocationId &&
                                      x.ProjectId == projectId.ToString() &&
                                      x.Outcome == AutomationInvocationOutcome.Claimed, ct)
            .ConfigureAwait(false);
        if (invocation is null)
            return null;
        if (!string.IsNullOrWhiteSpace(invocation.BacklogTaskId))
            return new(BacklogTaskId.Parse(invocation.BacklogTaskId), IsBound: true);
        return string.IsNullOrWhiteSpace(invocation.PendingBacklogTaskId)
            ? null
            : new(BacklogTaskId.Parse(invocation.PendingBacklogTaskId), IsBound: false);
    }

    public async Task<bool> TryAdoptLegacyProvisionalTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default)
    {
        var changed = await db.AutomationInvocations
            .Where(x => x.Id == invocationId &&
                        x.ProjectId == projectId.ToString() &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        x.BacklogTaskId == null &&
                        x.PendingBacklogTaskId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.PendingBacklogTaskId, backlogTaskId.ToString()), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return true;

        return await db.AutomationInvocations.AsNoTracking().AnyAsync(x =>
            x.Id == invocationId &&
            x.ProjectId == projectId.ToString() &&
            x.Outcome == AutomationInvocationOutcome.Claimed &&
            (x.BacklogTaskId == backlogTaskId.ToString() ||
             x.PendingBacklogTaskId == backlogTaskId.ToString()), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId,
        string occurrenceKeyPrefix,
        IReadOnlyCollection<string> legacyProvisionalOccurrenceKeys,
        int maximumCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(occurrenceKeyPrefix))
            throw new ArgumentException("A schedule occurrence key prefix is required.", nameof(occurrenceKeyPrefix));
        if (maximumCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        var legacyKeys = legacyProvisionalOccurrenceKeys.Distinct(StringComparer.Ordinal).ToArray();
        var invocations = await db.AutomationInvocations.AsNoTracking()
            .Where(x => x.ProjectId == projectId.ToString() &&
                        x.EventName == "schedule" &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        x.OccurrenceKey.StartsWith(occurrenceKeyPrefix) &&
                        (x.BacklogTaskId == null ||
                         x.PendingBacklogTaskId != null ||
                         legacyKeys.Contains(x.OccurrenceKey)))
            .OrderBy(x => x.OccurrenceKey)
            .ThenBy(x => x.Id)
            .Select(x => new OutstandingScheduleInvocation(x.Id, x.OccurrenceKey))
            .Take(maximumCount + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (invocations.Count > maximumCount)
            throw new InvalidOperationException(
                $"Outstanding schedule invocation recovery exceeds the safe limit of {maximumCount}.");
        return invocations;
    }

    public async Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default)
    {
        var changed = await db.AutomationInvocations
            .Where(x => x.Id == invocationId &&
                        x.ProjectId == projectId.ToString() &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        x.BacklogTaskId == backlogTaskId.ToString() &&
                        (x.PendingBacklogTaskId == backlogTaskId.ToString() ||
                         x.PendingBacklogTaskId == null))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PendingBacklogTaskId, (string?)null), ct)
            .ConfigureAwait(false);
        return changed == 1 ||
            await db.AutomationInvocations.AsNoTracking().AnyAsync(x =>
                x.Id == invocationId &&
                x.ProjectId == projectId.ToString() &&
                x.Outcome == AutomationInvocationOutcome.Claimed &&
                x.BacklogTaskId == backlogTaskId.ToString() &&
                x.PendingBacklogTaskId == null, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId,
        ProjectId projectId,
        BacklogTaskId backlogTaskId,
        CancellationToken ct = default)
    {
        var changed = await db.AutomationInvocations
            .Where(x => x.Id == invocationId &&
                        x.ProjectId == projectId.ToString() &&
                        x.Outcome == AutomationInvocationOutcome.Claimed &&
                        ((x.BacklogTaskId == null && x.PendingBacklogTaskId == null) ||
                         x.BacklogTaskId == backlogTaskId.ToString() ||
                         x.PendingBacklogTaskId == backlogTaskId.ToString()))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

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
    public async Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId,
        BacklogTaskId backlogTaskId,
        string runId,
        CancellationToken ct = default)
    {
        var invocation = await db.AutomationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == expectedProjectId.ToString() &&
                                      x.BacklogTaskId == backlogTaskId.ToString() &&
                                      x.Outcome == AutomationInvocationOutcome.Claimed, ct)
            .ConfigureAwait(false);
        if (invocation is null)
            return false;

        var activation = await persistence.TryFenceAutomationActivationAsync(invocation.ActivationId, ct).ConfigureAwait(false);
        if (activation is null ||
            activation.ProjectId != expectedProjectId.ToString() ||
            activation.ProjectId != invocation.ProjectId ||
            activation.InstallationId != invocation.InstallationId ||
            activation.RepositoryId != invocation.RepositoryId)
            return false;
        var binding = await persistence.GetLiveAutomationCopilotBindingAsync(
            activation.ProjectId,
            activation.CopilotBindingId,
            activation.CopilotBindingGrantDigest,
            ct).ConfigureAwait(false);
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
        CopilotBindingSnapshotSource binding) =>
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
