using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Orchestrator;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class UserNotificationStorePostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "attention_" + Guid.NewGuid().ToString("N");
    private UserNotificationStore _store = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        _store = new(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NewestFirstPagingCountAndCursorAreCallerAndQueryBound()
    {
        await SeedAsync("project-a", "run-a", "old", Now);
        await SeedAsync("project-a", "run-b", "new", Now.AddSeconds(1));
        await SeedAsync("project-other", "run-c", "hidden", Now.AddSeconds(2));
        await SeedAsync("project-a", "run-d", "other-tenant", Now.AddSeconds(3), tenant: "tenant-b");
        var authority = UserNotificationContractTests.Authority();
        var first = await _store.ListAsync(authority, 1, null, default);
        Assert.Equal(2, first.Count);
        Assert.Equal("new", Assert.Single(first.Items).RequestId);
        Assert.NotNull(first.NextCursor);
        var second = await _store.ListAsync(authority, 1, first.NextCursor, default);
        Assert.Equal("old", Assert.Single(second.Items).RequestId);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, second.Count);
        var mismatch = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ListAsync(authority, 2, first.NextCursor, default));
        Assert.Equal("query_invalid", mismatch.Code);
        await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ListAsync(authority with { ActorId = Guid.NewGuid().ToString() }, 1, first.NextCursor, default));
        await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ListAsync(authority, 1, first.NextCursor, default, includeDismissed: true));
    }

    [Fact]
    public async Task DismissalIsDurableIdempotentAndDoesNotHideSharedFlowOrOtherCallers()
    {
        await SeedAsync("project-a", "run-a", "gate-a", Now);
        var authority = UserNotificationContractTests.Authority();
        var initial = Assert.Single((await _store.ListAsync(authority, 10, null, default)).Items);
        await _store.DismissAsync(authority, initial.NotificationId, default);
        await _store.DismissAsync(authority, initial.NotificationId, default);
        var restarted = new UserNotificationStore(fixture.DataSource, _schema);
        Assert.Equal(0, (await restarted.ListAsync(authority, 10, null, default)).Count);
        Assert.Single((await restarted.ListAsync(authority, 10, null, default, includeDismissed: true)).Items);
        Assert.Single((await restarted.ListAsync(
            authority with { ActorId = Guid.NewGuid().ToString() }, 10, null, default)).Items);
        await SeedAsync("project-a", "run-a", "new-occurrence", Now.AddSeconds(1));
        Assert.Equal("new-occurrence",
            Assert.Single((await restarted.ListAsync(authority, 10, null, default)).Items).RequestId);
    }

    [Fact]
    public async Task DismissalRollsBackIfAuthorityChangesBeforeCommit()
    {
        await SeedAsync("project-a", "run-a", "gate-a", Now);
        var authority = UserNotificationContractTests.Authority();
        var item = Assert.Single((await _store.ListAsync(authority, 10, null, default)).Items);
        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.DismissAsync(authority, item.NotificationId, default,
                _ => throw new CoordinationException("projects_authorization_changed", 409)));
        Assert.Equal("projects_authorization_changed", error.Code);
        Assert.Single((await _store.ListAsync(authority, 10, null, default)).Items);
    }

    [Fact]
    public async Task BoundTokensAndProjectFilterCannotWidenAuthority()
    {
        await SeedAsync("project-a", "run-a", "gate-a", Now);
        await SeedAsync("project-a", "run-b", "gate-b", Now);
        var authority = UserNotificationContractTests.Authority();
        var page = await _store.ListAsync(
            authority with { BoundProjectId = "project-a", BoundRunId = "run-b" }, 10, null, default);
        Assert.Equal("run-b", Assert.Single(page.Items).RunId);
        Assert.Empty((await _store.ListAsync(authority, 10, null, default, "other-project")).Items);
        await _store.DismissAsync(authority with { EffectiveAuthority = [] },
            UserNotificationStore.NotificationId("project-a", "run-a", "gate-a"), default);
        Assert.Equal(2, (await _store.ListAsync(authority, 10, null, default)).Count);
    }

    [Fact]
    public async Task ResolvedTerminalArchivedAndStaleFenceGatesDisappearWithoutDeletingDismissals()
    {
        await SeedAsync("project-a", "run-a", "live", Now);
        await SeedAsync("project-a", "run-b", "completed", Now, execution: "completed");
        await SeedAsync("project-a", "run-c", "cancelled", Now, lifecycle: "cancelled");
        await SeedAsync("project-a", "run-d", "stale", Now, fence: 2);
        await SeedAsync("project-a", "run-e", "resolved", Now, pending: false);
        Assert.Equal("live", Assert.Single((await _store.ListAsync(
            UserNotificationContractTests.Authority(), 100, null, default)).Items).RequestId);
    }

    [Fact]
    public async Task PromotionNoticeCountsUnarchivedPromotedTasksInsideTheWindowOncePerPromotion()
    {
        // Only one promotion is recent, visible, and has unarchived tasks; ordinary capture has no origin.
        var now = DateTimeOffset.UtcNow;
        var live = await SeedPromotionAsync("project-a", "run-a", "plan-a", now.AddMinutes(-5), tasks: 3, archived: 1);
        await SeedPromotionAsync("project-a", "run-b", "plan-b", now.AddMinutes(-4), tasks: 2, archived: 2);
        await SeedPromotionAsync("project-a", "run-c", "plan-c", now.AddHours(-25), tasks: 2);
        await SeedPromotionAsync("project-other", "run-d", "plan-d", now.AddMinutes(-3), tasks: 1);
        await SeedPromotionAsync("project-a", "run-e", "plan-e", now.AddMinutes(-2), tasks: 1, tenant: "tenant-b");
        await SeedOrdinaryTaskAsync("project-a", "captured");
        var page = await _store.ListAsync(UserNotificationContractTests.Authority(), 100, null, default);
        // Count tasks, not notices, and keep gate-only fields empty for this source.
        Assert.Equal(1, page.Count);
        var item = Assert.Single(page.Items);
        Assert.Equal(UserNotificationSources.BacklogPromoted, item.Source);
        Assert.Equal(UserNotificationStore.PromotionNotificationId("project-a", "run-a", live), item.NotificationId);
        Assert.Equal(("project-a", "run-a", "root", live, 2), (item.ProjectId, item.RunId, item.SessionId, item.PromotionId!.Value, item.TaskCount!.Value));
        Assert.Null(item.RequestId);
        Assert.Null(item.Kind);
        Assert.Null(item.GateKind);
        Assert.Null(item.StateVersion);
        Assert.Null(item.Prompt);
    }

    [Fact]
    public async Task GatesAndPromotionsShareOrderingCursorDismissalAndBoundAuthority()
    {
        // A newer promotion must page before the gate without dropping either source.
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("project-a", "run-a", "gate-a", now.AddMinutes(-2));
        var promotion = await SeedPromotionAsync("project-a", "run-b", "plan-b", now.AddMinutes(-1), tasks: 2);
        var authority = UserNotificationContractTests.Authority();
        var first = await _store.ListAsync(authority, 1, null, default);
        Assert.Equal(2, first.Count);
        Assert.Equal(promotion, Assert.Single(first.Items).PromotionId);
        var second = await _store.ListAsync(authority, 1, first.NextCursor, default);
        Assert.Equal("gate-a", Assert.Single(second.Items).RequestId);
        Assert.Null(second.NextCursor);
        // A run binding or project filter can narrow results but cannot grant new access.
        Assert.Equal("run-a", Assert.Single((await _store.ListAsync(
            authority with { BoundProjectId = "project-a", BoundRunId = "run-a" }, 10, null, default)).Items).RunId);
        Assert.Empty((await _store.ListAsync(authority, 10, null, default, "project-other")).Items);

        var id = first.Items[0].NotificationId;
        // Losing read authority prevents dismissal even when the notification ID is known.
        await _store.DismissAsync(authority with { EffectiveAuthority = [] }, id, default);
        Assert.Equal(2, (await _store.ListAsync(authority, 10, null, default)).Count);
        // Repeating an authorized dismissal survives store recreation and stays private to the caller.
        await _store.DismissAsync(authority, id, default);
        await _store.DismissAsync(authority, id, default);
        var restarted = new UserNotificationStore(fixture.DataSource, _schema);
        Assert.Equal("gate-a", Assert.Single((await restarted.ListAsync(authority, 10, null, default)).Items).RequestId);
        Assert.Equal(2, (await restarted.ListAsync(authority, 10, null, default, includeDismissed: true)).Count);
        Assert.Equal(2, (await restarted.ListAsync(
            authority with { ActorId = Guid.NewGuid().ToString() }, 10, null, default)).Count);
    }

    private async Task<Guid> SeedPromotionAsync(
        string project, string run, string workPlan, DateTimeOffset createdAt, int tasks, int archived = 0,
        string tenant = "tenant-a")
    {
        // Seed the persisted promotion and origin rows together; task creation alone is not promotion.
        var promotion = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".accepted_runs
                (project_id, run_id, tenant_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, execution_state)
            VALUES (@project, @run, @tenant, '{"{}"}', @hash, @issuer, @actor, 'idle')
            ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence, lifecycle_state)
            VALUES (@project, @run, 'root', 'root', 'coordinator', @issuer, @actor, 1, 'active')
            ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".backlog_projects (project_id) VALUES (@project) ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".backlog_plan_promotions
                (promotion_id, project_id, run_id, root_session_id, work_plan_id, workflow_id, workflow_revision,
                 decision_state_version, execution_fence, graph_revision, idempotency_key, request_hash,
                 task_count, created_at)
            VALUES (@promotion, @project, @run, 'root', @plan, 'workflow', '1', 1, 1, 1, @plan, @hash,
                @tasks, @created);
            INSERT INTO "{_schema}".backlog_tasks (project_id, task_id, title, is_archived, queue_position)
            SELECT @project, @plan || '-' || n, 'Task ' || n, n <= @archived, n - 1
            FROM generate_series(1, @tasks) n;
            INSERT INTO "{_schema}".backlog_task_origins
                (project_id, task_id, promotion_id, run_id, root_session_id, work_plan_id, work_item_id)
            SELECT @project, @plan || '-' || n, @promotion, @run, 'root', @plan, 'item-' || n
            FROM generate_series(1, @tasks) n;
            """, connection);
        command.Parameters.AddWithValue("promotion", promotion);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("plan", workPlan);
        command.Parameters.AddWithValue("issuer", UserNotificationContractTests.Issuer);
        command.Parameters.AddWithValue("actor", UserNotificationContractTests.Actor);
        command.Parameters.AddWithValue("hash", new string('A', 64));
        command.Parameters.AddWithValue("tasks", tasks);
        command.Parameters.AddWithValue("archived", archived);
        command.Parameters.AddWithValue("created", createdAt);
        await command.ExecuteNonQueryAsync();
        return promotion;
    }

    private async Task SeedOrdinaryTaskAsync(string project, string task)
    {
        // Deliberately omit origin rows so this fixture cannot produce a promotion notice.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".backlog_projects (project_id) VALUES (@project) ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".backlog_tasks (project_id, task_id, title, queue_position)
            VALUES (@project, @task, 'Captured', 0);
            """, connection);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("task", task);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync(
        string project, string run, string request, DateTimeOffset createdAt, string tenant = "tenant-a",
        string execution = "blocked", string lifecycle = "active", long fence = 1, bool pending = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var gate = new CoordinatorGateRequest(request, CoordinatorGateKind.Approval, "work",
            UserNotificationContractTests.Actor, 1, ["approve", "reject"], false, "Approve this work?");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".accepted_runs
                (project_id, run_id, tenant_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, execution_state)
            VALUES (@project, @run, @tenant, @selection, @hash, @issuer, @actor, @execution)
            ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence, lifecycle_state)
            VALUES (@project, @run, 'root', 'root', 'coordinator', @issuer, @actor, @fence, @lifecycle)
            ON CONFLICT DO NOTHING;
            INSERT INTO "{_schema}".coordinator_gates
                (project_id, run_id, session_id, request_id, gate_kind, gate_state, gate, allowed_choices,
                 created_by_issuer, created_by_subject, execution_fence, state_version, idempotency_key, created_at)
            VALUES (@project, @run, 'root', @request, 'approval', @state, @gate, '["approve","reject"]',
                @issuer, @actor, 1, 1, @request, @created);
            """, connection);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("issuer", UserNotificationContractTests.Issuer);
        command.Parameters.AddWithValue("actor", UserNotificationContractTests.Actor);
        command.Parameters.AddWithValue("hash", new string('A', 64));
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Jsonb, "{}");
        command.Parameters.AddWithValue("execution", execution);
        command.Parameters.AddWithValue("fence", fence);
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.Parameters.AddWithValue("request", request);
        command.Parameters.AddWithValue("state", pending ? "pending" : "cancelled");
        command.Parameters.AddWithValue("gate", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(gate, options));
        command.Parameters.AddWithValue("created", createdAt);
        await command.ExecuteNonQueryAsync();
    }
}
