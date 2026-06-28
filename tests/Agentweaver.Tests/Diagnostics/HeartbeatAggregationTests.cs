using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Diagnostics;

/// <summary>
/// Aggregation tests for <see cref="DiagnosticsService.GetHeartbeatStatusAsync"/> across multiple
/// pod rows. At <c>replicas:2</c> the diagnostics read may land on a pod that has not yet ticked
/// while another pod is ticking healthily; the endpoint must aggregate the shared
/// <c>HeartbeatStatuses</c> table so it reports the most-recent tick across all pods and a per-pod
/// breakdown, rather than the reader pod's local-only view.
/// </summary>
public sealed class HeartbeatAggregationTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;

    public HeartbeatAggregationTests()
    {
        _connectionString = $"DataSource=file:heartbeat-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    private async Task SeedPodAsync(string pod, DateTimeOffset tick, int acted, int errors, string? error)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.HeartbeatStatuses.Add(new HeartbeatStatusRecord
        {
            PodName = pod,
            LastTickUtc = tick,
            ActedCount = acted,
            ErrorCount = errors,
            DurationMs = 5,
            Error = error,
            Enabled = true,
            IntervalSeconds = 10,
        });
        await db.SaveChangesAsync();
    }

    private DiagnosticsService NewService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var heartbeatStore = new HeartbeatStatusStore(config);
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        // Only _heartbeatStore and _scopeFactory are used by GetHeartbeatStatusAsync; the remaining
        // dependencies are not dereferenced by that path.
        return new DiagnosticsService(
            db: null!, projectStore: null!, workspaceProvider: null!,
            heartbeatStore: heartbeatStore, workflowRegistry: null!, reviewPolicyRegistry: null!,
            configuration: config, scopeFactory: scopeFactory);
    }

    [Fact]
    public async Task GetHeartbeatStatusAsync_AggregatesAcrossPods()
    {
        var older = DateTimeOffset.UtcNow.AddSeconds(-30);
        var newer = DateTimeOffset.UtcNow.AddSeconds(-2);
        await SeedPodAsync("pod-a", older, acted: 1, errors: 0, error: null);
        await SeedPodAsync("pod-b", newer, acted: 3, errors: 0, error: null);

        var status = await NewService().GetHeartbeatStatusAsync();

        status.ServiceStatus.Should().Be("running", "at least one pod has ticked");
        status.LastTickUtc.Should().BeCloseTo(newer, TimeSpan.FromMilliseconds(50),
            "the aggregate last_tick_utc is the most recent across pods");
        status.Pods.Should().HaveCount(2);
        status.Pods.Select(p => p.PodName).Should().BeEquivalentTo(new[] { "pod-a", "pod-b" });
        status.Pods.Single(p => p.PodName == "pod-b").ActedCount.Should().Be(3);
    }

    [Fact]
    public async Task GetHeartbeatStatusAsync_SurfacesMostRecentPodError()
    {
        await SeedPodAsync("pod-a", DateTimeOffset.UtcNow.AddSeconds(-5), 1, 0, null);
        await SeedPodAsync("pod-b", DateTimeOffset.UtcNow.AddSeconds(-1), 0, 1, "boom on pod-b");

        var status = await NewService().GetHeartbeatStatusAsync();

        status.LastError.Should().Be("boom on pod-b",
            "the newest cross-pod error must surface when the local pod is error-free");
    }

    [Fact]
    public async Task GetHeartbeatStatusAsync_NoRows_FallsBackToLocalView()
    {
        var status = await NewService().GetHeartbeatStatusAsync();

        // No pod has persisted a tick yet; the local store has never ticked either.
        status.ServiceStatus.Should().Be("waiting_first_tick");
        status.Pods.Should().BeEmpty();
        status.LastTickUtc.Should().BeNull();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }
}
