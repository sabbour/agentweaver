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
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
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
}
