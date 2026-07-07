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
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.OwnerUser = ownerUser;
            existing.IntegrationBranch = integrationBranch;
            existing.AggregateTreeHash = aggregateTreeHash;
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
        var existing = await db.AssemblyReviews
            .FirstOrDefaultAsync(r => r.CoordinatorRunId == coordinatorRunId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.AssemblyReviews.Add(new CoordinatorAssemblyReviewRecord
            {
                CoordinatorRunId = coordinatorRunId,
                DecisionJson = json,
                Reviewer = decision.Reviewer,
                DecisionSubmittedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.DecisionJson = json;
            existing.Reviewer = decision.Reviewer;
            existing.DecisionSubmittedAt = now;
            existing.CoordinatorFailedAt = null;
            existing.CoordinatorFailureReason = null;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
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
        return ValidatePendingRequest(existing, workPlanInReview, callerUser, callerGitHubLogin);
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

        var now = DateTimeOffset.UtcNow;
        existing!.DecisionJson = JsonSerializer.Serialize(decision, JsonDefaults.Options);
        existing.Reviewer = decision.Reviewer;
        existing.DecisionSubmittedAt = now;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return AssemblyReviewPendingDecisionResult.Persisted;
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
}
