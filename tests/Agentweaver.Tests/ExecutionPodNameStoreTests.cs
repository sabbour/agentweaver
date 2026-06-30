using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runtime;

public sealed class ExecutionPodNameStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly DbContextOptions<MemoryDbContext> _options;
    private readonly List<ServiceProvider> _providers = [];

    public ExecutionPodNameStoreTests()
    {
        _dir = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", "execution-pods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "memory.db")}")
            .Options;

        using var db = new MemoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void PodNameRegistry_ResolvesPodFromSharedRunEvents_WhenLocalCacheIsEmpty()
    {
        var runId = "child-run-1";
        var writer = new PodNameRegistry(CreateStore());
        writer.Register(runId, "agent-host-pod-a");

        var readerWithEmptyCache = new PodNameRegistry(CreateStore());

        readerWithEmptyCache.TryGet(runId).Should().Be("agent-host-pod-a");
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private RunEventExecutionPodNameStore CreateStore()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(opts =>
            opts.UseSqlite($"Data Source={Path.Combine(_dir, "memory.db")}"));
        var scopeProvider = services.BuildServiceProvider();
        _providers.Add(scopeProvider);

        return new RunEventExecutionPodNameStore(
            new EfRunEventStream(new TestMemoryDbContextFactory(_options)),
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunEventExecutionPodNameStore>.Instance);
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }
}
