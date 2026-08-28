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

    [PostgresFact]
    public async Task ConcurrentAlwaysGrantAndDeny_DenialWinnerLeavesNoProjectPolicy()
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
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var eventStream = new EfRunEventStream(pg.Factory);
        var stateA = new DurableRunControlState(scopeFactory, eventStream);
        var stateB = new DurableRunControlState(scopeFactory, eventStream);
        var gateA = new DurableToolApprovalGate(stateA, runStore: runStore);
        var gateB = new DurableToolApprovalGate(stateB, runStore: runStore);
        var runId = source.Id.ToString();
        var wait = gateA.WaitForApprovalAsync(
            runId, "always-vs-deny", "web_fetch", "https://source.test",
            TimeSpan.FromSeconds(10), default);
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = stateA.ExecuteExclusivelyAsync(
            [runId],
            async (_, _) =>
            {
                lockHeld.SetResult();
                await releaseLock.Task;
                return true;
            });
        await lockHeld.Task;

        var deny = Task.Run(() => gateB.Deny(runId, "always-vs-deny"));
        await Task.Delay(100);
        var always = gateA.GrantAsync(runId, "always-vs-deny", ApprovalScope.Always);
        await Task.Delay(100);
        releaseLock.SetResult();

        (await deny).Should().BeTrue();
        (await always).Should().BeFalse();
        await blocker;
        (await wait).Should().BeFalse();
        gateA.GetRequestState(runId, "always-vs-deny").Should().Be(ToolApprovalRequestState.Denied);
        gateA.IsAutoApproved(future.Id.ToString(), "web_fetch", "https://future.test")
            .Should().BeFalse("a denied request must not leave an Always policy behind");
    }

    [PostgresFact]
    public async Task TimeoutLosingClaim_ReturnsConcurrentGrantResolution()
    {
        var source = NewRun($"alice-{Guid.NewGuid():N}", ProjectId.New());
        var runStore = new EfRunStore(pg.Factory);
        await runStore.InsertAsync(source);

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
        var runId = source.Id.ToString();
        var requestId = "timeout-grant-race";
        var wait = gateA.WaitForApprovalAsync(
            runId, requestId, "web_fetch", "https://source.test",
            TimeSpan.FromMilliseconds(100), default);

        await WaitUntilAsync(() => gateA.IsKnownRequest(runId, requestId));
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = stateA.ExecuteExclusivelyAsync(
            [runId],
            async (_, _) =>
            {
                lockHeld.SetResult();
                await releaseLock.Task;
                return true;
            });
        await lockHeld.Task;

        var grant = gateB.GrantAsync(runId, requestId, ApprovalScope.Once);
        await Task.Delay(200);
        releaseLock.SetResult();

        (await grant).Should().BeTrue("the grant acquired the request lock before the timeout claim");
        await blocker;
        (await wait).Should().BeTrue("a timeout loser must return the winning approval resolution");
        gateA.GetRequestState(runId, requestId).Should().Be(ToolApprovalRequestState.Approved);
    }

    [PostgresFact]
    public async Task RunScopedChildGrant_FailsWhenParentIsAwaitingReview()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = ProjectId.New();
        var parent = NewRun($"alice-{suffix}", project, RunStatus.AwaitingReview);
        var child = NewRun($"alice-{suffix}", project);
        var sibling = NewRun($"alice-{suffix}", project);
        var runStore = new EfRunStore(pg.Factory);
        foreach (var run in new[] { parent, child, sibling })
            await runStore.InsertAsync(run);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                pg.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        using var provider = services.BuildServiceProvider();
        var eventStream = new EfRunEventStream(pg.Factory);
        var gate = new DurableToolApprovalGate(
            new DurableRunControlState(provider.GetRequiredService<IServiceScopeFactory>(), eventStream),
            runStore: runStore);
        gate.RegisterParentRun(child.Id.ToString(), parent.Id.ToString());

        var wait = gate.WaitForApprovalAsync(
            child.Id.ToString(), "inactive-parent", "web_fetch", "https://child.test",
            TimeSpan.FromSeconds(5), default);

        (await gate.GrantAsync(child.Id.ToString(), "inactive-parent", ApprovalScope.Run)).Should().BeFalse(
            "Postgres must lock and require every policy destination run to remain active");
        gate.Deny(child.Id.ToString(), "inactive-parent").Should().BeTrue();
        (await wait).Should().BeFalse();

        (await runStore.TryTransitionReviewToInProgressAsync(parent.Id)).Should().BeTrue();
        gate.RegisterParentRun(sibling.Id.ToString(), parent.Id.ToString());
        gate.IsAutoApproved(sibling.Id.ToString(), "web_fetch", "https://later-child.test").Should().BeFalse(
            "a rejected late child grant must not authorize children after the parent resumes");
    }

    [PostgresFact]
    public async Task RunScopedChildGrant_DoesNotAuthorizeSiblingAfterParentFails()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = ProjectId.New();
        var parent = NewRun($"alice-{suffix}", project);
        var child = NewRun($"alice-{suffix}", project);
        var sibling = NewRun($"alice-{suffix}", project);
        var futureChild = NewRun($"alice-{suffix}", project);
        var runStore = new EfRunStore(pg.Factory);
        foreach (var run in new[] { parent, child, sibling, futureChild })
            await runStore.InsertAsync(run);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                pg.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        using var provider = services.BuildServiceProvider();
        var eventStream = new EfRunEventStream(pg.Factory);
        var gate = new DurableToolApprovalGate(
            new DurableRunControlState(provider.GetRequiredService<IServiceScopeFactory>(), eventStream),
            runStore: runStore);
        var parentId = parent.Id.ToString();
        var childId = child.Id.ToString();
        var siblingId = sibling.Id.ToString();
        gate.RegisterParentRun(childId, parentId);
        gate.RegisterParentRun(siblingId, parentId);

        var wait = gate.WaitForApprovalAsync(
            childId, "failed-parent", "web_fetch", "https://child.test",
            TimeSpan.FromSeconds(5), default);

        (await gate.GrantAsync(childId, "failed-parent", ApprovalScope.Run)).Should().BeTrue();
        (await wait).Should().BeTrue();
        gate.IsAutoApproved(siblingId, "web_fetch", "https://before-failure.test").Should().BeTrue(
            "active coordinators continue to propagate session policies");

        await runStore.UpdateStatusAsync(parent.Id, RunStatus.Failed, DateTimeOffset.UtcNow);

        (await runStore.GetAsync(sibling.Id))!.Status.Should().Be(RunStatus.InProgress);
        gate.IsAutoApproved(siblingId, "web_fetch", "https://after-failure.test").Should().BeFalse(
            "a failed coordinator's policy must not authorize an active sibling");

        gate.RegisterParentRun(futureChild.Id.ToString(), parentId);
        gate.IsAutoApproved(futureChild.Id.ToString(), "web_fetch", "https://future-child.test").Should().BeFalse(
            "a failed coordinator's policy must not authorize a future child");
    }

    [PostgresFact]
    public async Task RunScopedChildGrant_DoesNotReviveAfterParentRecovery()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = ProjectId.New();
        var parent = NewRun($"alice-{suffix}", project);
        var firstChild = NewRun($"alice-{suffix}", project);
        var activeChild = NewRun($"alice-{suffix}", project);
        var runStore = new EfRunStore(pg.Factory);
        foreach (var run in new[] { parent, firstChild, activeChild })
            await runStore.InsertAsync(run);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                pg.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        using var provider = services.BuildServiceProvider();
        var eventStream = new EfRunEventStream(pg.Factory);
        var gate = new DurableToolApprovalGate(
            new DurableRunControlState(provider.GetRequiredService<IServiceScopeFactory>(), eventStream),
            runStore: runStore);
        var parentId = parent.Id.ToString();

        gate.RegisterParentRun(firstChild.Id.ToString(), parentId);
        var wait = gate.WaitForApprovalAsync(
            firstChild.Id.ToString(), "before-terminalization", "web_fetch", "https://child.test",
            TimeSpan.FromSeconds(10), default);
        (await gate.GrantAsync(firstChild.Id.ToString(), "before-terminalization", ApprovalScope.Run)).Should().BeTrue();
        (await wait).Should().BeTrue();

        gate.RegisterParentRun(activeChild.Id.ToString(), parentId);
        gate.IsAutoApproved(activeChild.Id.ToString(), "web_fetch", "https://active.test").Should().BeTrue(
            "an active coordinator's policy remains available to new children");

        await runStore.UpdateStatusAsync(parent.Id, RunStatus.Failed, DateTimeOffset.UtcNow);
        await runStore.UpdateStatusAsync(parent.Id, RunStatus.InProgress, endedAt: null);

        var recoveredChild = NewRun($"alice-{suffix}", project);
        await runStore.InsertAsync(recoveredChild);
        gate.RegisterParentRun(recoveredChild.Id.ToString(), parentId);
        gate.IsAutoApproved(recoveredChild.Id.ToString(), "web_fetch", "https://recovered.test").Should().BeFalse(
            "a recovered coordinator must not reactivate a policy granted during its prior lifecycle");
    }

    private static Run NewRun(string owner, ProjectId projectId, RunStatus status = RunStatus.InProgress) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "postgres-tool-approval-test",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "Verify owner-scoped durable tool approval",
        SubmittingUser = owner,
        Status = status,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        false.Should().BeTrue("the pending approval context should become visible");
    }
}
