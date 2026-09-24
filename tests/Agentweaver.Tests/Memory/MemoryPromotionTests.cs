using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Memory;

public sealed class MemoryPromotionTests
{
    [Fact]
    public async Task Promotion_DoesNotApproveContentEditedAfterReview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

        var originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var setup = new MemoryDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AgentMemory.Add(new AgentMemory
            {
                ProjectId = "project-1",
                AgentName = "smith",
                Type = "learning",
                Importance = "medium",
                Content = "Reviewed content",
                TrustState = MemoryTrustStates.Pending,
                CreatedAt = originalUpdatedAt,
                UpdatedAt = originalUpdatedAt,
            });
            await setup.SaveChangesAsync();
        }

        AgentMemory reviewed;
        await using (var review = new MemoryDbContext(options))
        {
            reviewed = await review.AgentMemory.AsNoTracking().SingleAsync();
        }

        await using (var update = new MemoryDbContext(options))
        {
            var memory = await update.AgentMemory.SingleAsync();
            memory.Content = "Edited after review";
            memory.TrustState = MemoryTrustStates.Pending;
            memory.ApprovedBy = null;
            memory.ApprovedAt = null;
            memory.UpdatedAt = originalUpdatedAt.AddSeconds(1);
            await update.SaveChangesAsync();
        }

        await using (var promotion = new MemoryDbContext(options))
        {
            var promoted = await MemoryPromotionHelpers.TryPromoteReviewedAsync(
                promotion,
                reviewed,
                "reviewer-1",
                originalUpdatedAt.AddSeconds(2),
                CancellationToken.None);

            promoted.Should().BeFalse();
        }

        await using var verification = new MemoryDbContext(options);
        var current = await verification.AgentMemory.AsNoTracking().SingleAsync();
        current.Content.Should().Be("Edited after review");
        current.TrustState.Should().Be(MemoryTrustStates.Pending);
        current.ApprovedBy.Should().BeNull();
        current.ApprovedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateLoadedBeforePromotion_AtomicallyRevokesConcurrentApproval()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

        var originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var setup = new MemoryDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AgentMemory.Add(new AgentMemory
            {
                ProjectId = "project-1",
                AgentName = "smith",
                Type = "learning",
                Importance = "medium",
                Content = "Reviewed content",
                TrustState = MemoryTrustStates.Pending,
                CreatedAt = originalUpdatedAt,
                UpdatedAt = originalUpdatedAt,
            });
            await setup.SaveChangesAsync();
        }

        AgentMemory loadedForUpdate;
        await using (var updateRead = new MemoryDbContext(options))
        {
            loadedForUpdate = await updateRead.AgentMemory.AsNoTracking().SingleAsync();
        }

        await using (var promotion = new MemoryDbContext(options))
        {
            (await MemoryPromotionHelpers.TryPromoteReviewedAsync(
                promotion,
                loadedForUpdate,
                "reviewer-1",
                originalUpdatedAt.AddSeconds(1),
                CancellationToken.None)).Should().BeTrue();
        }

        await using (var updateWrite = new MemoryDbContext(options))
        {
            (await MemoryPromotionHelpers.TryApplyUpdateAsync(
                updateWrite,
                loadedForUpdate,
                loadedForUpdate.Type,
                loadedForUpdate.Importance,
                "Edited after review",
                loadedForUpdate.Tags,
                originalUpdatedAt.AddSeconds(2),
                CancellationToken.None)).Should().BeTrue();
        }

        await using var verification = new MemoryDbContext(options);
        var current = await verification.AgentMemory.AsNoTracking().SingleAsync();
        current.Content.Should().Be("Edited after review");
        current.TrustState.Should().Be(MemoryTrustStates.Pending);
        current.ApprovedBy.Should().BeNull();
        current.ApprovedAt.Should().BeNull();
    }
}
