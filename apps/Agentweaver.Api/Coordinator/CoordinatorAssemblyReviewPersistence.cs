using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

internal static class CoordinatorAssemblyReviewPersistence
{
    public static Task<string> UpsertReviewRequestAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string ownerUser,
        string integrationBranch,
        string aggregateTreeHash,
        string revisionId,
        CancellationToken ct) =>
        UpsertReviewRequestCoreAsync(
            scopeFactory, coordinatorRunId, ownerUser, integrationBranch, aggregateTreeHash,
            revisionId, assemblyFencingToken: 0, reviewRequestId: coordinatorRunId, ct: ct);

    public static Task<string> UpsertReviewRequestAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string ownerUser,
        string integrationBranch,
        string aggregateTreeHash,
        string revisionId,
        long? assemblyFencingToken,
        CancellationToken ct) =>
        UpsertReviewRequestCoreAsync(
            scopeFactory, coordinatorRunId, ownerUser, integrationBranch, aggregateTreeHash,
            revisionId, assemblyFencingToken, Guid.NewGuid().ToString("N"), ct);

    private static async Task<string> UpsertReviewRequestCoreAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string ownerUser,
        string integrationBranch,
        string aggregateTreeHash,
        string revisionId,
        long? assemblyFencingToken,
        string reviewRequestId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.AssemblyReviews.Add(new CoordinatorAssemblyReviewRecord
            {
                CoordinatorRunId = coordinatorRunId,
                OwnerUser = ownerUser,
                IntegrationBranch = integrationBranch,
                AggregateTreeHash = aggregateTreeHash,
                ReviewRequestId = reviewRequestId,
                AssemblyFencingToken = assemblyFencingToken,
                OutputRevisionId = revisionId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.OwnerUser = ownerUser;
            existing.IntegrationBranch = integrationBranch;
            existing.AggregateTreeHash = aggregateTreeHash;
            existing.ReviewRequestId = reviewRequestId;
            existing.AssemblyFencingToken = assemblyFencingToken;
            existing.OutputRevisionId = revisionId;
            existing.DecisionJson = null;
            existing.Reviewer = null;
            existing.DecisionSubmittedAt = null;
            existing.CoordinatorFailedAt = null;
            existing.CoordinatorFailureReason = null;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return reviewRequestId;
    }

    public static async Task<bool> PersistDecisionAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        AssemblyReviewDecision decision,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(decision, JsonDefaults.Options);
        if (string.IsNullOrWhiteSpace(decision.OutputRevisionId))
            return false;
        var updated = await db.AssemblyReviews
            .Where(r => r.CoordinatorRunId == coordinatorRunId
                && r.ReviewRequestId == decision.ReviewRequestId
                && r.AssemblyFencingToken == decision.AssemblyFencingToken
                && r.OutputRevisionId == decision.OutputRevisionId
                && r.DecisionSubmittedAt == null && r.CoordinatorFailedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DecisionJson, json)
                .SetProperty(r => r.Reviewer, decision.Reviewer)
                .SetProperty(r => r.DecisionSubmittedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// The ONE shared delivery path for an assembly human-review decision (#226 S2). Both
    /// <c>POST /api/runs/{id}/assembly/review</c> AND the <c>AwaitingReview</c> branch of
    /// <see cref="CoordinatorSteeringService.SteerAsync"/> call this so at-most-once / replica-safety /
    /// ownership checks stay identical by construction. Sequence (mirrors the original endpoint block):
    /// <list type="number">
    /// <item>Validate a review is genuinely pending for this owner (<see cref="ValidatePendingRequestAsync"/>).</item>
    /// <item>Durably claim the pending decision before waking the in-memory gate, so the assembly loop
    /// cannot clear the review row before persistence finishes. The conditional update also ensures
    /// concurrent submissions have one winner.</item>
    /// <item>Attempt in-memory delivery to the armed gate (<see cref="AssemblyReviewGate.TrySubmit"/>).
    /// On <c>NotArmed</c> (another replica, or not yet armed), the durable decision is already available
    /// for the owning pod's deferred poller.</item>
    /// </list>
    /// </summary>
    public static async Task<AssemblyReviewDeliveryResult> DeliverDecisionAsync(
        IServiceScopeFactory scopeFactory,
        AssemblyReviewGate reviewGate,
        string coordinatorRunId,
        AssemblyReviewDecision decision,
        string callerUser,
        string? callerGitHubLogin,
        CancellationToken ct)
    {
        var pending = await ValidatePendingRequestAsync(
            scopeFactory, coordinatorRunId, callerUser, callerGitHubLogin, ct).ConfigureAwait(false);
        if (pending == AssemblyReviewPendingDecisionResult.Forbidden)
            return AssemblyReviewDeliveryResult.Forbidden;
        if (pending == AssemblyReviewPendingDecisionResult.StaleRevision)
            return AssemblyReviewDeliveryResult.StaleRevision;
        if (pending is not (AssemblyReviewPendingDecisionResult.Pending
            or AssemblyReviewPendingDecisionResult.Preserved))
            return AssemblyReviewDeliveryResult.NotPending;

        var record = await GetAsync(scopeFactory, coordinatorRunId, ct).ConfigureAwait(false);
        if (record is null
            || decision.ReviewRequestId != record.ReviewRequestId
            || decision.OutputRevisionId != record.OutputRevisionId
            || record.AssemblyFencingToken is null)
            return AssemblyReviewDeliveryResult.StaleRevision;
        decision = decision with { AssemblyFencingToken = record.AssemblyFencingToken };

        // A preserved review on a failed run is actionable only as a recorded decision for this
        // exact request/revision/fence. It must not wake a gate or continue failed assembly effects.
        var preservedRequest = pending == AssemblyReviewPendingDecisionResult.Preserved;
        var persisted = await PersistDecisionForPendingRequestAsync(
            scopeFactory,
            coordinatorRunId,
            decision,
            callerUser,
            callerGitHubLogin,
            CancellationToken.None,
            expectedPending: pending)
            .ConfigureAwait(false);
        if (persisted == AssemblyReviewPendingDecisionResult.PersistedPreserved)
            return AssemblyReviewDeliveryResult.PreservedDecisionRecorded;
        if (persisted != AssemblyReviewPendingDecisionResult.Persisted)
            return persisted switch
            {
                AssemblyReviewPendingDecisionResult.Forbidden => AssemblyReviewDeliveryResult.Forbidden,
                AssemblyReviewPendingDecisionResult.StaleRevision => AssemblyReviewDeliveryResult.StaleRevision,
                AssemblyReviewPendingDecisionResult.AlreadySubmitted => AssemblyReviewDeliveryResult.AlreadySubmitted,
                _ => AssemblyReviewDeliveryResult.NotPending,
            };

        if (preservedRequest)
            return AssemblyReviewDeliveryResult.PreservedDecisionRecorded;

        // Once the conditional write succeeds, finish delivery even if the HTTP request is cancelled.
        // Signalling the gate first lets the assembly loop clear the review row before this write,
        // incorrectly reporting a delivered steer as superseded.
        var submit = reviewGate.TrySubmit(coordinatorRunId, callerUser, decision, callerGitHubLogin);
        if (submit == AssemblyReviewSubmitResult.Accepted)
            return AssemblyReviewDeliveryResult.Accepted;
        if (submit == AssemblyReviewSubmitResult.Forbidden)
            return AssemblyReviewDeliveryResult.Forbidden;
        if (submit == AssemblyReviewSubmitResult.StaleRevision)
            return AssemblyReviewDeliveryResult.StaleRevision;

        // NotArmed: another replica's poller (or the pending arm) consumes the persisted decision.
        return AssemblyReviewDeliveryResult.Deferred;
    }

    public static async Task<AssemblyReviewPendingDecisionResult> ValidatePendingRequestAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string callerUser,
        string? callerGitHubLogin,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var existing = await db.AssemblyReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
        var workPlanState = await GetReviewWorkPlanStateAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        var validation = ValidatePendingRequest(existing, workPlanState, callerUser, callerGitHubLogin);
        return validation is (AssemblyReviewPendingDecisionResult.Pending
                or AssemblyReviewPendingDecisionResult.Preserved)
            && !await IsCurrentRevisionAsync(scope.ServiceProvider, coordinatorRunId, existing!.OutputRevisionId, ct)
                .ConfigureAwait(false)
            ? AssemblyReviewPendingDecisionResult.StaleRevision : validation;
    }

    public static async Task<AssemblyReviewPendingDecisionResult> PersistDecisionForPendingRequestAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        AssemblyReviewDecision decision,
        string callerUser,
        string? callerGitHubLogin,
        CancellationToken ct,
        AssemblyReviewPendingDecisionResult? expectedPending = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
        var workPlanState = await GetReviewWorkPlanStateAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        var validation = ValidatePendingRequest(existing, workPlanState, callerUser, callerGitHubLogin);
        if (expectedPending is not null && validation != expectedPending)
            return AssemblyReviewPendingDecisionResult.StaleRevision;
        if (validation is not (AssemblyReviewPendingDecisionResult.Pending
            or AssemblyReviewPendingDecisionResult.Preserved))
            return validation;
        if (string.IsNullOrWhiteSpace(decision.OutputRevisionId)
            || string.IsNullOrWhiteSpace(decision.ReviewRequestId)
            || decision.OutputRevisionId != existing!.OutputRevisionId
            || decision.ReviewRequestId != existing.ReviewRequestId
            || decision.AssemblyFencingToken != existing.AssemblyFencingToken
            || !await IsCurrentRevisionAsync(scope.ServiceProvider, coordinatorRunId, decision.OutputRevisionId, ct)
                .ConfigureAwait(false))
            return AssemblyReviewPendingDecisionResult.StaleRevision;

        var now = DateTimeOffset.UtcNow;
        var update = db.AssemblyReviews
            .Where(r => r.CoordinatorRunId == coordinatorRunId
                && r.ReviewRequestId == decision.ReviewRequestId
                && r.AssemblyFencingToken == decision.AssemblyFencingToken
                && r.OutputRevisionId == decision.OutputRevisionId
                && r.DecisionSubmittedAt == null);
        update = validation == AssemblyReviewPendingDecisionResult.Preserved
            ? update.Where(r => r.CoordinatorFailedAt != null
                && db.WorkPlans.Any(w => w.CoordinatorRunId == coordinatorRunId
                    && w.Status == WorkPlanStatus.AssemblyFailed
                    && w.AssemblyTerminalStage == AssemblyStage.Review
                    && w.AssemblyFencingToken == decision.AssemblyFencingToken))
            : update.Where(r => r.CoordinatorFailedAt == null
                && db.WorkPlans.Any(w => w.CoordinatorRunId == coordinatorRunId
                    && w.Status == WorkPlanStatus.InReview
                    && w.AssemblyStage == AssemblyStage.Review
                    && w.AssemblyFencingToken == decision.AssemblyFencingToken));
        var updated = await update.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DecisionJson, JsonSerializer.Serialize(decision, JsonDefaults.Options))
                .SetProperty(r => r.Reviewer, decision.Reviewer)
                .SetProperty(r => r.DecisionSubmittedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct).ConfigureAwait(false);
        if (updated == 1)
            return validation == AssemblyReviewPendingDecisionResult.Preserved
                ? AssemblyReviewPendingDecisionResult.PersistedPreserved
                : AssemblyReviewPendingDecisionResult.Persisted;

        // A competing same-revision request can claim the row after the validation above.
        // Re-read it so that loser is reported as an already-submitted decision, not a stale revision.
        db.ChangeTracker.Clear();
        existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
        workPlanState = await GetReviewWorkPlanStateAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        validation = ValidatePendingRequest(existing, workPlanState, callerUser, callerGitHubLogin);
        if (validation is AssemblyReviewPendingDecisionResult.Pending
            or AssemblyReviewPendingDecisionResult.Preserved)
            return AssemblyReviewPendingDecisionResult.StaleRevision;
        return validation;
    }

    public static async Task<CoordinatorAssemblyReviewRecord?> GetAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.AssemblyReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
    }

    public static async Task ClearAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.AssemblyReviews
            .Where(r => r.CoordinatorRunId == coordinatorRunId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Preserves an OPEN review gate when its coordinator run terminates in a failure state. If a
    /// review record exists with no decision submitted yet (the human never acted), it is marked
    /// <c>coordinator_failed</c> (stamping <see cref="CoordinatorAssemblyReviewRecord.CoordinatorFailedAt"/>
    /// and the reason) rather than deleted, so the human can still view the assembled changes. Returns
    /// <c>true</c> when an open gate was preserved; <c>false</c> when there is no record or the review
    /// was already decided (in which case the caller may clear it as before).
    /// </summary>
    public static async Task<bool> MarkCoordinatorFailedAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string reason,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);

        // Only preserve a gate that is genuinely still OPEN (no human decision submitted).
        if (existing is null || existing.DecisionSubmittedAt is not null)
            return false;

        var now = DateTimeOffset.UtcNow;
        existing.CoordinatorFailedAt = now;
        existing.CoordinatorFailureReason = reason;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public static async Task<bool> ReactivateOpenReviewAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string reviewRequestId,
        string outputRevisionId,
        long assemblyFencingToken,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var updated = await db.AssemblyReviews
            .Where(r => r.CoordinatorRunId == coordinatorRunId
                && r.ReviewRequestId == reviewRequestId
                && r.OutputRevisionId == outputRevisionId
                && r.AssemblyFencingToken == assemblyFencingToken
                && r.DecisionSubmittedAt == null
                && r.DecisionJson == null
                && r.CoordinatorFailedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.CoordinatorFailedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.CoordinatorFailureReason, (string?)null)
                .SetProperty(r => r.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static async Task<WorkPlanReviewState?> GetReviewWorkPlanStateAsync(
        MemoryDbContext db,
        string coordinatorRunId,
        CancellationToken ct)
    {
        var plan = await db.WorkPlans
            .AsNoTracking()
            .Where(w => w.CoordinatorRunId == coordinatorRunId)
            .Select(w => new WorkPlanReviewState(
                w.Status,
                w.AssemblyStage,
                w.AssemblyTerminalStage,
                w.AssemblyFencingToken))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return plan;
    }

    private static async Task<bool> IsCurrentRevisionAsync(
        IServiceProvider services, string runId, string? revisionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || !Agentweaver.Domain.RunId.TryParse(runId, out var parsed))
            return false;
        var run = await services.GetRequiredService<Agentweaver.Api.Infrastructure.IRunStore>()
            .GetAsync(parsed, ct).ConfigureAwait(false);
        return run is not null && run.CurrentOutputRevisionId == revisionId;
    }

    private static AssemblyReviewPendingDecisionResult ValidatePendingRequest(
        CoordinatorAssemblyReviewRecord? existing,
        WorkPlanReviewState? workPlan,
        string callerUser,
        string? callerGitHubLogin)
    {
        if (existing is null
            || string.IsNullOrEmpty(existing.IntegrationBranch)
            || string.IsNullOrEmpty(existing.AggregateTreeHash)
            || string.IsNullOrEmpty(existing.ReviewRequestId)
            || existing.AssemblyFencingToken is null
            || workPlan is null)
            return AssemblyReviewPendingDecisionResult.NotPending;

        if (!Owns(existing.OwnerUser, callerUser, callerGitHubLogin))
            return AssemblyReviewPendingDecisionResult.Forbidden;

        if (existing.DecisionSubmittedAt is not null || !string.IsNullOrEmpty(existing.DecisionJson))
            return AssemblyReviewPendingDecisionResult.AlreadySubmitted;

        if (workPlan.AssemblyFencingToken != existing.AssemblyFencingToken)
            return AssemblyReviewPendingDecisionResult.StaleRevision;
        if (workPlan.Status == WorkPlanStatus.InReview && workPlan.AssemblyStage == AssemblyStage.Review)
            return existing.CoordinatorFailedAt is null
                ? AssemblyReviewPendingDecisionResult.Pending
                : AssemblyReviewPendingDecisionResult.NotPending;
        if (workPlan.Status == WorkPlanStatus.AssemblyFailed
            && workPlan.AssemblyTerminalStage == AssemblyStage.Review)
            return existing.CoordinatorFailedAt is not null
                ? AssemblyReviewPendingDecisionResult.Preserved
                : AssemblyReviewPendingDecisionResult.NotPending;
        return AssemblyReviewPendingDecisionResult.NotPending;
    }

    private sealed record WorkPlanReviewState(
        string Status,
        string? AssemblyStage,
        string? AssemblyTerminalStage,
        long AssemblyFencingToken);

    private static bool Owns(string? ownerUser, string callerUser, string? callerGitHubLogin) =>
        ownerUser is not null
        && (string.Equals(ownerUser, callerUser, StringComparison.Ordinal)
            || (callerGitHubLogin is not null
                && string.Equals(ownerUser, callerGitHubLogin, StringComparison.Ordinal)));
}

public enum AssemblyReviewPendingDecisionResult
{
    Pending,
    Persisted,
    NotPending,
    Forbidden,
    AlreadySubmitted,
    StaleRevision,
    Preserved,
    PersistedPreserved,
}

/// <summary>
/// Outcome of <see cref="CoordinatorAssemblyReviewPersistence.DeliverDecisionAsync"/> so BOTH the
/// <c>/assembly/review</c> endpoint and the <c>AwaitingReview</c> steer branch map to identical
/// responses. <see cref="Accepted"/> = delivered to the armed local gate; <see cref="Deferred"/> =
/// durably persisted for the owning pod's poller (cross-replica); the rest mirror the validation
/// failure modes.
/// </summary>
public enum AssemblyReviewDeliveryResult
{
    Accepted,
    Deferred,
    PreservedDecisionRecorded,
    NotPending,
    Forbidden,
    AlreadySubmitted,
    StaleRevision,
}
