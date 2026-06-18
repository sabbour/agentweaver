using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the D4 exactly-once compare-and-swap in <see cref="CoordinatorAssemblyStore"/>.
/// <c>TryStartAssemblyAsync</c> must transition <c>awaiting_assembly → assembling</c> for exactly one
/// caller and return false for all others, even under concurrent contention. Real EF + in-memory SQLite.
/// </summary>
public sealed class CoordinatorAssemblyStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoordinatorAssemblyStore _sut;

    public CoordinatorAssemblyStoreTests()
    {
        // Shared-cache in-memory DB so each scope opens its OWN connection (real concurrency); the
        // keep-alive connection keeps the shared in-memory database alive for the test's lifetime.
        // Microsoft.Data.Sqlite's built-in busy retry serializes concurrent writers without throwing.
        var connectionString = $"DataSource=file:assemblycas-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _sut = new CoordinatorAssemblyStore(_scopeFactory);
    }

    [Fact]
    public async Task TryStartAssembly_ReturnsTrueOnce_FalseOnSecondCall()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.AwaitingAssembly);

        var first = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);
        var second = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);

        first.Should().BeTrue();
        second.Should().BeFalse();

        var state = await _sut.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.Assembling);
        state.IntegrationBranch.Should().Be("agentweaver/integration/x");
    }

    [Fact]
    public async Task TryStartAssembly_ReturnsFalse_WhenNotAwaitingAssembly()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.Dispatching);

        var result = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryStartAssembly_ConcurrentCallers_ExactlyOneWins()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.AwaitingAssembly);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "exactly one caller may claim the assembly");
    }

    private async Task<int> SeedPlanAsync(string status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1",
            CoordinatorRunId = "coord-cas-" + Guid.NewGuid().ToString("N"),
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-1",
            CoordinatorRunId = spec.CoordinatorRunId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }
}
