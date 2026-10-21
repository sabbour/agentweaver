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
