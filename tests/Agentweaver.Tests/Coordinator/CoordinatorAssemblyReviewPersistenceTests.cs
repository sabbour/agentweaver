using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorAssemblyReviewPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorAssemblyReviewPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task UpsertReviewRequest_ClearsPriorDecisionAndFailureStamp()
    {
        const string coordinatorRunId = "coord-review-reset";
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice", "agentweaver/integration/old", "old-tree", default);
        await CoordinatorAssemblyReviewPersistence.PersistDecisionAsync(
            _scopeFactory,
            coordinatorRunId,
            new AssemblyReviewDecision(
                Approved: true,
                RequestChanges: false,
                Feedback: "old approval",
                TargetFiles: null,
                Reviewer: "alice"),
            default);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.AssemblyReviews.SingleAsync(r => r.CoordinatorRunId == coordinatorRunId);
            row.CoordinatorFailedAt = DateTimeOffset.UtcNow;
            row.CoordinatorFailureReason = "old failure";
            await db.SaveChangesAsync();
        }

        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice", "agentweaver/integration/new", "new-tree", default);

        using var assertScope = _provider.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await assertDb.AssemblyReviews.AsNoTracking()
            .SingleAsync(r => r.CoordinatorRunId == coordinatorRunId);
        record.IntegrationBranch.Should().Be("agentweaver/integration/new");
        record.AggregateTreeHash.Should().Be("new-tree");
        record.DecisionJson.Should().BeNull();
        record.Reviewer.Should().BeNull();
        record.DecisionSubmittedAt.Should().BeNull();
        record.CoordinatorFailedAt.Should().BeNull();
        record.CoordinatorFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task PersistDecisionForPendingRequest_RefusesWhenWorkPlanIsNotInReviewStage()
    {
        const string coordinatorRunId = "coord-review-not-pending";
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice", "agentweaver/integration/pending", "tree", default);

        var result = await CoordinatorAssemblyReviewPersistence.PersistDecisionForPendingRequestAsync(
            _scopeFactory,
            coordinatorRunId,
            new AssemblyReviewDecision(
                Approved: true,
                RequestChanges: false,
                Feedback: null,
                TargetFiles: null,
                Reviewer: "alice"),
            "alice",
            callerGitHubLogin: null,
                ct: default);

        result.Should().Be(AssemblyReviewPendingDecisionResult.NotPending);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking()
            .SingleAsync(r => r.CoordinatorRunId == coordinatorRunId);
        record.DecisionJson.Should().BeNull("a stale POST must not leave a deferred decision behind");
        record.DecisionSubmittedAt.Should().BeNull();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
