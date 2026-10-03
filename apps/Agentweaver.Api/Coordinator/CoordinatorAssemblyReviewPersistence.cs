using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

internal static class CoordinatorAssemblyReviewPersistence
{
    public static async Task UpsertReviewRequestAsync(
        IServiceScopeFactory scopeFactory,
        string coordinatorRunId,
        string ownerUser,
        string integrationBranch,
        string aggregateTreeHash,
        string revisionId,
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
                OutputRevisionId = revisionId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            if (existing.OutputRevisionId == revisionId && existing.AggregateTreeHash == aggregateTreeHash)
                return;
            existing.OwnerUser = ownerUser;
            existing.IntegrationBranch = integrationBranch;
            existing.AggregateTreeHash = aggregateTreeHash;
            existing.OutputRevisionId = revisionId;
            existing.DecisionJson = null;
            existing.Reviewer = null;
            existing.DecisionSubmittedAt = null;
            existing.CoordinatorFailedAt = null;
            existing.CoordinatorFailureReason = null;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        if (pending != AssemblyReviewPendingDecisionResult.Pending)
            return AssemblyReviewDeliveryResult.NotPending;

        // Once the conditional write succeeds, finish delivery even if the HTTP request is cancelled.
        // Signalling the gate first lets the assembly loop clear the review row before this write,
        // incorrectly reporting a delivered steer as superseded.
        var persisted = await PersistDecisionForPendingRequestAsync(
            scopeFactory, coordinatorRunId, decision, callerUser, callerGitHubLogin, CancellationToken.None)
            .ConfigureAwait(false);
        if (persisted != AssemblyReviewPendingDecisionResult.Persisted)
            return persisted switch
            {
                AssemblyReviewPendingDecisionResult.Forbidden => AssemblyReviewDeliveryResult.Forbidden,
                AssemblyReviewPendingDecisionResult.StaleRevision => AssemblyReviewDeliveryResult.StaleRevision,
                AssemblyReviewPendingDecisionResult.AlreadySubmitted => AssemblyReviewDeliveryResult.AlreadySubmitted,
                _ => AssemblyReviewDeliveryResult.NotPending,
            };

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
        var workPlanInReview = await IsWorkPlanAwaitingReviewAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        var validation = ValidatePendingRequest(existing, workPlanInReview, callerUser, callerGitHubLogin);
        return validation == AssemblyReviewPendingDecisionResult.Pending
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
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
        var workPlanInReview = await IsWorkPlanAwaitingReviewAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        var validation = ValidatePendingRequest(existing, workPlanInReview, callerUser, callerGitHubLogin);
        if (validation != AssemblyReviewPendingDecisionResult.Pending)
            return validation;
        if (string.IsNullOrWhiteSpace(decision.OutputRevisionId)
            || decision.OutputRevisionId != existing!.OutputRevisionId
            || !await IsCurrentRevisionAsync(scope.ServiceProvider, coordinatorRunId, decision.OutputRevisionId, ct)
                .ConfigureAwait(false))
            return AssemblyReviewPendingDecisionResult.StaleRevision;

        var now = DateTimeOffset.UtcNow;
        var updated = await db.AssemblyReviews
            .Where(r => r.CoordinatorRunId == coordinatorRunId
                && r.OutputRevisionId == decision.OutputRevisionId
                && r.DecisionSubmittedAt == null && r.CoordinatorFailedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DecisionJson, JsonSerializer.Serialize(decision, JsonDefaults.Options))
                .SetProperty(r => r.Reviewer, decision.Reviewer)
                .SetProperty(r => r.DecisionSubmittedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct).ConfigureAwait(false);
        if (updated == 1)
            return AssemblyReviewPendingDecisionResult.Persisted;

        // A competing same-revision request can claim the row after the validation above.
        // Re-read it so that loser is reported as an already-submitted decision, not a stale revision.
        db.ChangeTracker.Clear();
        existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);
        workPlanInReview = await IsWorkPlanAwaitingReviewAsync(db, coordinatorRunId, ct).ConfigureAwait(false);
        validation = ValidatePendingRequest(existing, workPlanInReview, callerUser, callerGitHubLogin);
        if (validation != AssemblyReviewPendingDecisionResult.Pending)
            return validation;

        // The row is still pending but no longer eligible for this conditional update, which means
        // its revision or ownership fence changed concurrently.
        return AssemblyReviewPendingDecisionResult.StaleRevision;
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

    private static async Task<bool> IsWorkPlanAwaitingReviewAsync(
        MemoryDbContext db,
        string coordinatorRunId,
        CancellationToken ct) =>
        await db.WorkPlans
            .AsNoTracking()
            .AnyAsync(w => w.CoordinatorRunId == coordinatorRunId
                && w.Status == WorkPlanStatus.InReview
                && w.AssemblyStage == AssemblyStage.Review, ct)
            .ConfigureAwait(false);

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
        bool workPlanInReview,
        string callerUser,
        string? callerGitHubLogin)
    {
        if (existing is null
            || !workPlanInReview
            || existing.CoordinatorFailedAt is not null
            || string.IsNullOrEmpty(existing.IntegrationBranch)
            || string.IsNullOrEmpty(existing.AggregateTreeHash))
            return AssemblyReviewPendingDecisionResult.NotPending;

        if (!Owns(existing.OwnerUser, callerUser, callerGitHubLogin))
            return AssemblyReviewPendingDecisionResult.Forbidden;

        return existing.DecisionSubmittedAt is not null || !string.IsNullOrEmpty(existing.DecisionJson)
            ? AssemblyReviewPendingDecisionResult.AlreadySubmitted
            : AssemblyReviewPendingDecisionResult.Pending;
    }

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
    NotPending,
    Forbidden,
    AlreadySubmitted,
    StaleRevision,
}
