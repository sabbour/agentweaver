using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Runs;

public sealed class DurableRunControlStateTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _providers = [];

    public DurableRunControlStateTests()
    {
        _connectionString = $"DataSource=file:run-control-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var scope = NewProvider().CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void RunOptions_AreVisibleAcrossReplicas()
    {
        var replicaA = NewOptionsStore();
        var replicaB = NewOptionsStore();

        replicaA.SetAutoApproveTools("run-1", true);
        replicaB.Get("run-1").AutoApproveTools.Should().BeTrue();

        replicaB.SetAutopilot("run-1", true);
        replicaA.Get("run-1").Should().Be(new RunOptions(AutoApproveTools: true, Autopilot: true));

        replicaA.Clear("run-1");
        replicaB.Get("run-1").Should().Be(new RunOptions());
    }

    [Fact]
    public async Task ApprovalGrant_OnAnotherReplica_ResolvesWaitingRun()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();

        var wait = owner.WaitForApprovalAsync(
            "run-2", "req-1", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);

        await WaitUntilAsync(() => secondary.GrantAsync("run-2", "req-1", ApprovalScope.Run));

        (await wait).Should().BeTrue();
        secondary.IsAutoApproved("run-2", "web_fetch", "https://example.test").Should().BeTrue();
    }

    [Fact]
    public async Task RunScopedApproval_OnChild_IsVisibleToSiblingViaParent()
    {
        var child = NewApprovalGate();
        var sibling = NewApprovalGate();
        child.RegisterParentRun("child-a", "parent-1");
        sibling.RegisterParentRun("child-b", "parent-1");

        var wait = child.WaitForApprovalAsync(
            "child-a", "req-2", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => sibling.GrantAsync("child-a", "req-2", ApprovalScope.Tool));

        (await wait).Should().BeTrue();
        sibling.IsAutoApproved("child-b", "web_fetch", "https://other.test").Should().BeTrue();
    }

    [Fact]
    public async Task ResolvedOrClearedRequests_DoNotApproveAgain()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();

        var approved = owner.WaitForApprovalAsync(
            "run-3", "req-3", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => secondary.GrantAsync("run-3", "req-3", ApprovalScope.Once));

        (await approved).Should().BeTrue();
        (await secondary.GrantAsync("run-3", "req-3", ApprovalScope.Once)).Should().BeFalse();
        secondary.Deny("run-3", "req-3").Should().BeFalse();

        var cleared = owner.WaitForApprovalAsync(
            "run-4", "req-4", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(async () =>
        {
            secondary.Clear("run-4");
            await Task.CompletedTask;
            return true;
        });

        (await cleared).Should().BeFalse();
        (await secondary.GrantAsync("run-4", "req-4", ApprovalScope.Once)).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_DoesNotRemoveAlwaysPolicy()
    {
        var owner = NewApprovalGate();
        var secondary = NewApprovalGate();

        var wait = owner.WaitForApprovalAsync(
            "run-5", "req-5", "web_fetch", "https://example.test", TimeSpan.FromSeconds(5), default);
        await WaitUntilAsync(() => secondary.GrantAsync("run-5", "req-5", ApprovalScope.Always));

        (await wait).Should().BeTrue();
        secondary.Clear("run-5");
        owner.IsAutoApproved("another-run", "web_fetch", "https://example.test").Should().BeTrue();
    }

    private DurableRunOptionsStore NewOptionsStore() => new(NewState());
    private DurableToolApprovalGate NewApprovalGate() => new(NewState());

    private DurableRunControlState NewState() =>
        new(NewProvider().GetRequiredService<IServiceScopeFactory>());

    private ServiceProvider NewProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> action)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await action())
                return;
            await Task.Delay(50);
        }

        false.Should().BeTrue("the pending approval context should become visible");
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _keepAlive.Dispose();
    }
}
