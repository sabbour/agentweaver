using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class ToolApprovalPersistenceTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task ProjectScopedAlwaysGrant_IsReplicaSafeAndRejectsLegacyOtherOwnerAndOtherProject()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var alice = $"alice-{suffix}";
        var bob = $"bob-{suffix}";
        var project = ProjectId.New();
        var otherProject = ProjectId.New();
        var sourceA = NewRun(alice, project);
        var sourceB = NewRun(alice, project);
        var aliceFuture = NewRun(alice, project);
        var bobFuture = NewRun(bob, project);
        var otherProjectFuture = NewRun(alice, otherProject);
        var runStore = new EfRunStore(pg.Factory);
        foreach (var run in new[] { sourceA, sourceB, aliceFuture, bobFuture, otherProjectFuture })
            await runStore.InsertAsync(run);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                pg.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var eventStream = new EfRunEventStream(pg.Factory);
        var stateA = new DurableRunControlState(scopeFactory, eventStream);
        var stateB = new DurableRunControlState(scopeFactory, eventStream);
        var gateA = new DurableToolApprovalGate(stateA, runStore: runStore);
        var gateB = new DurableToolApprovalGate(stateB, runStore: runStore);

        stateA.Append(
            "__agentweaver_tool_approvals__",
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });
        stateB.Append(
            DurableToolApprovalGate.ProjectPolicyBucket(project, bob),
            "tool.approval_policy_granted",
            new { policyKey = "web_fetch:" });

        var waitA = gateA.WaitForApprovalAsync(
            sourceA.Id.ToString(), "req-a", "web_fetch", "https://a.test",
            TimeSpan.FromSeconds(10), default);
        var waitB = gateB.WaitForApprovalAsync(
            sourceB.Id.ToString(), "req-b", "web_fetch", "https://b.test",
            TimeSpan.FromSeconds(10), default);

        var grants = await Task.WhenAll(
            gateA.GrantAsync(sourceA.Id.ToString(), "req-a", ApprovalScope.Always),
            gateB.GrantAsync(sourceB.Id.ToString(), "req-b", ApprovalScope.Always));

        grants.Should().OnlyContain(granted => granted);
        (await waitA).Should().BeTrue();
        (await waitB).Should().BeTrue();
        gateB.IsAutoApproved(aliceFuture.Id.ToString(), "web_fetch", "https://future.test")
            .Should().BeTrue();
        gateA.IsAutoApproved(bobFuture.Id.ToString(), "web_fetch", "https://bob.test")
            .Should().BeFalse();
        gateA.IsAutoApproved(otherProjectFuture.Id.ToString(), "web_fetch", "https://other-project.test")
            .Should().BeFalse();
    }

    [PostgresFact]
    public async Task ConcurrentOnceAndAlwaysDecisions_OnlyPersistTheWinningScopeAcrossReplicas()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = ProjectId.New();
        var source = NewRun($"alice-{suffix}", project);
        var future = NewRun($"alice-{suffix}", project);
        var runStore = new EfRunStore(pg.Factory);
        await runStore.InsertAsync(source);
        await runStore.InsertAsync(future);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                pg.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        using var provider = services.BuildServiceProvider();
        var eventStream = new EfRunEventStream(pg.Factory);
        var gateA = new DurableToolApprovalGate(
            new DurableRunControlState(provider.GetRequiredService<IServiceScopeFactory>(), eventStream),
            runStore: runStore);
        var gateB = new DurableToolApprovalGate(
            new DurableRunControlState(provider.GetRequiredService<IServiceScopeFactory>(), eventStream),
            runStore: runStore);
        var wait = gateA.WaitForApprovalAsync(
            source.Id.ToString(), "once-vs-always", "web_fetch", "https://source.test",
            TimeSpan.FromSeconds(10), default);

        var once = gateA.GrantAsync(source.Id.ToString(), "once-vs-always", ApprovalScope.Once);
        var always = gateB.GrantAsync(source.Id.ToString(), "once-vs-always", ApprovalScope.Always);
        await Task.WhenAll(once, always);

        (await once).Should().NotBe(await always);
        (await wait).Should().BeTrue();
        gateA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test")
            .Should().Be(await always);
    }

    private static Run NewRun(string owner, ProjectId projectId) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "postgres-tool-approval-test",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "Verify owner-scoped durable tool approval",
        SubmittingUser = owner,
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
    };
}
