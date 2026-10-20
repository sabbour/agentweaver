using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>Verifies durable new-chat create, start, and cancel semantics against PostgreSQL.</summary>
[Collection("Coordination PostgreSQL")]
public sealed class ProjectRunOwnerStorePostgresTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string Project = "project-1";
    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "project_runs_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private CoordinationOwnerStore _store = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;

    public ProjectRunOwnerStorePostgresTests(CoordinationPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var contexts = new CoordinatorRunSelectionContextStore(
            _fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        _decisions = new CoordinatorDecisionOwnerStore(_fixture.DataSource, _schema, contexts, TimeProvider.System);
        _store = new CoordinationOwnerStore(_fixture.DataSource, _schema, TimeProvider.System, _decisions);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CreateReplaysByKeyAndRejectsChangedInput()
    {
        var request = new CreateProjectRunRequest("Plan the release", 3);
        var first = await Create("key-1", request);
        var replay = await Create("key-1", request);
        Assert.Equal(first, replay);
        Assert.Equal("awaiting_binding", first.State);
        Assert.Equal("Plan the release", first.FirstMessage);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            Create("key-1", request with { FirstMessage = "Different" }));
        Assert.Equal("idempotency_conflict", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);

        var other = await Create("key-2", request);
        Assert.NotEqual(first.RunId, other.RunId);
        var otherActor = await _store.CreateProjectRunAsync(
            _actor with { Subject = Guid.NewGuid().ToString("D") }, Tenant, Project, "key-1", request,
            CancellationToken.None);
        Assert.NotEqual(first.RunId, otherActor.RunId);
    }

    [Theory]
    [InlineData("", 1, null)]
    [InlineData("hello", 0, null)]
    [InlineData("hello", 1, " ")]
    public async Task CreateRejectsInvalidInputBeforePersistence(string message, long revision, string? title)
    {
        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Create("bad", new CreateProjectRunRequest(message, revision, Title: title)));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(0, await CountAsync("project_run_requests"));
    }

    [Fact]
    public async Task StartDeliversTheFirstMessageOnceAndReplays()
    {
        var row = await Create("start", new CreateProjectRunRequest("Hello root", 1, WorkflowId: "wf-1"));
        var selection = Selection(row.RunId);
        var deliveries = 0;
        CoordinationMessageResult? delivered = null;
        var started = await Start(row.RunId, async (pending, token) =>
        {
            deliveries++;
            await _store.AcceptRootAsync(_actor, selection, pending.RootSessionId, token);
            delivered = await _store.SendMessageAsync(_actor,
                new SessionIdentity(Project, row.RunId, pending.RootSessionId),
                FirstMessage(pending, row.RunId, pending.FirstMessage, pending.WorkflowId), token);
        });
        Assert.Equal(new StartProjectRunResponse(row.RunId, row.RootSessionId, "started"), started);
        Assert.NotNull(delivered);

        var replay = await Start(row.RunId, (_, _) => throw new InvalidOperationException("Replay must not redeliver."));
        Assert.Equal(started, replay);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task SelfAddressedMessageMustMatchThePersistedFirstMessage()
    {
        var row = await Create("forge", new CreateProjectRunRequest("Persisted", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        var error = await Assert.ThrowsAsync<CoordinationException>(() => _store.SendMessageAsync(_actor,
            new SessionIdentity(Project, row.RunId, row.RootSessionId),
            FirstMessage(row, row.RunId, "Forged", null), CancellationToken.None));
        Assert.Equal("first_message_invalid", error.Code);
    }

    [Fact]
    public async Task StartRejectsUnknownForeignAndCrossTenantRuns()
    {
        var row = await Create("scope", new CreateProjectRunRequest("Hello", 1));
        var missing = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.StartProjectRunAsync(_actor, "tenant-2", Project, row.RunId, NoDelivery, NoCheck,
                CancellationToken.None));
        Assert.Equal(404, missing.StatusCode);
        var foreign = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.StartProjectRunAsync(_actor with { Subject = "intruder" }, Tenant, Project, row.RunId,
                NoDelivery, NoCheck, CancellationToken.None));
        Assert.Equal(403, foreign.StatusCode);
    }

    [Fact]
    public async Task CancelFencesPendingRunsReplaysReceiptsAndBlocksStart()
    {
        var row = await Create("cancel", new CreateProjectRunRequest("Hello", 1));
        var selection = Selection(row.RunId);
        var accepted = 0;
        var cancel = new CancelProjectRunRequest("No longer needed");
        var first = await _store.CancelProjectRunAsync(_actor, selection, "c-1", cancel,
            async (pending, token) =>
            {
                accepted++;
                await _store.AcceptRootAsync(_actor, selection, pending.RootSessionId, token);
            }, NoCheck, CancellationToken.None);
        Assert.Equal("cancelled", first.State);
        Assert.Equal(1, accepted);

        var replay = await _store.CancelProjectRunAsync(_actor, selection, "c-1", cancel,
            (_, _) => throw new InvalidOperationException("Cancelled requests are not re-accepted."),
            NoCheck, CancellationToken.None);
        Assert.Equal(first, replay);

        var changed = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CancelProjectRunAsync(_actor, selection, "c-1", new CancelProjectRunRequest("Other"),
                NoDelivery, NoCheck, CancellationToken.None));
        Assert.Equal("idempotency_conflict", changed.Code);
        var again = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CancelProjectRunAsync(_actor, selection, "c-2", cancel, NoDelivery, NoCheck,
                CancellationToken.None));
        Assert.Equal("run_terminal", again.Code);
        var start = await Assert.ThrowsAsync<CoordinationException>(() => Start(row.RunId, NoDelivery));
        Assert.Equal("run_terminal", start.Code);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT r.execution_state, r.cancelled_at IS NOT NULL, r.completed_at = r.cancelled_at,
                   s.lifecycle_state, q.state,
                   (SELECT count(*) FROM "{_schema}".outbox_events e
                    WHERE e.event_type = 'orchestrator.run.cancelled')
            FROM "{_schema}".accepted_runs r
            JOIN "{_schema}".coordination_sessions s ON s.project_id = r.project_id AND s.run_id = r.run_id
            JOIN "{_schema}".project_run_requests q ON q.project_id = r.project_id AND q.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run
            """, connection);
        query.Parameters.AddWithValue("project", Project);
        query.Parameters.AddWithValue("run", row.RunId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("cancelled", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("cancelled", reader.GetString(3));
        Assert.Equal("cancelled", reader.GetString(4));
        Assert.Equal(1L, reader.GetInt64(5));
    }

    private Task<CoordinationOwnerStore.ProjectRunRequestRow> Create(string key, CreateProjectRunRequest request) =>
        _store.CreateProjectRunAsync(_actor, Tenant, Project, key, request, CancellationToken.None);

    private Task<StartProjectRunResponse> Start(
        string runId, Func<CoordinationOwnerStore.ProjectRunRequestRow, CancellationToken, Task> deliver) =>
        _store.StartProjectRunAsync(_actor, Tenant, Project, runId, deliver, NoCheck, CancellationToken.None);

    private static Task NoDelivery(CoordinationOwnerStore.ProjectRunRequestRow row, CancellationToken token) =>
        throw new InvalidOperationException("Delivery was not expected.");

    private static Task NoCheck(CancellationToken token) => Task.CompletedTask;

    /// <summary>Builds the reserved root-to-root first message used by the start endpoint.</summary>
    private static CoordinationMessageRequest FirstMessage(
        CoordinationOwnerStore.ProjectRunRequestRow row, string runId, string text, string? workflowId) =>
        new(row.RootSessionId, $"{runId}:first", AddressedMessageDeliveryMode.Immediate,
            AddressedMessagePurpose.Steering, AddressedMessageKind.Text,
            JsonSerializer.SerializeToElement(new { text, workflowId }));

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".{table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Creates the Projects-authorized selection that the interactive selection helper would return.</summary>
    private AuthorizedRunSelection Selection(string runId)
    {
        using var snapshot = JsonDocument.Parse(
            """{"source":"projects-config","modelSelection":{"reference":"model:provider/alpha"}}""");
        var selection = new EffectiveRunSelection(Project, runId, 1, 1, 1, "context-v1", snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(selection, new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, Tenant, 1, Project, runId,
            ImmutableArray.Create(new ProjectsAuthority("project", Project,
                ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }
}
