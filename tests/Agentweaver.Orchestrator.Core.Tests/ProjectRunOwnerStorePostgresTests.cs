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

        // Only the reserved key replays the persisted message; another self-route cannot create a delivery.
        var identity = new SessionIdentity(Project, row.RunId, row.RootSessionId);
        var firstMessage = FirstMessage(row, row.RunId, row.FirstMessage, row.WorkflowId);
        var messageReplay = await _store.SendMessageAsync(_actor, identity, firstMessage, CancellationToken.None);
        Assert.Equal(delivered.OwnerMessageId, messageReplay.OwnerMessageId);
        var rejected = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.SendMessageAsync(_actor, identity, firstMessage with { IdempotencyKey = "another-key" },
                CancellationToken.None));
        Assert.Equal("first_message_invalid", rejected.Code);
        Assert.Equal(1L, await CountAsync("coordination_messages"));
        Assert.Equal(1L, await CountAsync("outbox_events"));
        var outbound = await _store.ReadOutboundMessageAsync(delivered.OwnerMessageId, CancellationToken.None);
        var route = await _store.ValidateMessageRouteAsync(
            _actor, identity, new MessageRouteValidationRequest(outbound), CancellationToken.None);
        Assert.Equal(identity, route.Recipient);
        var rejectedRoute = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ValidateMessageRouteAsync(_actor, identity,
                new MessageRouteValidationRequest(outbound with { IdempotencyKey = "another-key" }),
                CancellationToken.None));
        Assert.Equal("first_message_invalid", rejectedRoute.Code);
    }

    [Fact]
    public async Task StartRetryAfterLostDeliveryResultResendsTheSameFirstMessageOnce()
    {
        var row = await Create("resend", new CreateProjectRunRequest("Resend me", 1));
        var selection = Selection(row.RunId);
        var identity = new SessionIdentity(Project, row.RunId, row.RootSessionId);
        var ownerMessageIds = new List<string>();
        async Task Deliver(CoordinationOwnerStore.ProjectRunRequestRow pending, CancellationToken token)
        {
            await _store.AcceptRootAsync(_actor, selection, pending.RootSessionId, token);
            var sent = await _store.SendMessageAsync(_actor, identity,
                FirstMessage(pending, row.RunId, pending.FirstMessage, pending.WorkflowId), token);
            ownerMessageIds.Add(sent.OwnerMessageId);
        }

        // The message commits, but the caller loses the result, so the request stays unstarted.
        await Assert.ThrowsAsync<HttpRequestException>(() => Start(row.RunId, async (pending, token) =>
        {
            await Deliver(pending, token);
            throw new HttpRequestException("Lost response after delivery.");
        }));
        Assert.Equal(1L, await CountAsync("coordination_messages"));

        var started = await Start(row.RunId, Deliver);
        Assert.Equal("started", started.State);
        Assert.Equal(2, ownerMessageIds.Count);
        Assert.Equal(ownerMessageIds[0], ownerMessageIds[1]);
        Assert.Equal(1L, await CountAsync("coordination_messages"));
        Assert.Equal(1L, await CountAsync("outbox_events"));
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
        var selection = CancellationSelection(row.RunId, _actor);
        var cancel = new CancelProjectRunRequest("No longer needed");
        var first = await _store.CancelProjectRunAsync(
            _actor, selection, "c-1", cancel, NoCheck, CancellationToken.None);
        Assert.Equal("cancelled", first.State);
        Assert.False(first.AlreadyTerminal);
        Assert.Equal(0L, await CountAsync("accepted_runs"));

        var replay = await _store.CancelProjectRunAsync(_actor, selection, "c-1", cancel,
            NoCheck, CancellationToken.None);
        Assert.Equal(first, replay);

        var changed = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CancelProjectRunAsync(_actor, selection, "c-1", new CancelProjectRunRequest("Other"),
                NoCheck, CancellationToken.None));
        Assert.Equal("idempotency_conflict", changed.Code);
        var again = await _store.CancelProjectRunAsync(
            _actor, selection, "c-2", cancel, NoCheck, CancellationToken.None);
        Assert.True(again.AlreadyTerminal);
        var start = await Assert.ThrowsAsync<CoordinationException>(() => Start(row.RunId, NoDelivery));
        Assert.Equal("run_terminal", start.Code);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT q.state,
                   (SELECT count(*) FROM "{_schema}".outbox_events e
                    WHERE e.event_type = 'orchestrator.run.cancelled')
            FROM "{_schema}".project_run_requests q
            WHERE q.project_id = @project AND q.run_id = @run
            """, connection);
        query.Parameters.AddWithValue("project", Project);
        query.Parameters.AddWithValue("run", row.RunId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("cancelled", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    /// <summary>Project cancellation is independent of the accepted run's creator and receipts remain caller-scoped.</summary>
    [Fact]
    public async Task AnotherProjectContributorCancelsStartedRunAndTerminalRequestsSucceed()
    {
        var row = await Create("cross-actor", new CreateProjectRunRequest("Hello", 1));
        await Start(row.RunId, async (pending, token) =>
            await _store.AcceptRootAsync(_actor, Selection(row.RunId), pending.RootSessionId, token));
        var contributor = _actor with { Subject = "other-contributor" };
        var selection = CancellationSelection(row.RunId, contributor);
        var cancelled = await _store.CancelProjectRunAsync(contributor, selection,
            "shared-key", new(), NoCheck, CancellationToken.None);
        Assert.False(cancelled.AlreadyTerminal);
        var replay = await _store.CancelProjectRunAsync(contributor, selection,
            "shared-key", new(), NoCheck, CancellationToken.None);
        Assert.Equal(cancelled, replay);
        var otherCaller = await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor),
            "shared-key", new(), NoCheck, CancellationToken.None);
        Assert.True(otherCaller.AlreadyTerminal);
        Assert.Equal(2L, await CountAsync("project_run_cancellations"));
        Assert.Equal(1L, await CountAsync("outbox_events"));
    }

    /// <summary>Read-only project access never grants cancellation authority, even to a run creator.</summary>
    [Fact]
    public async Task ViewerCannotCancel()
    {
        var row = await Create("viewer", new CreateProjectRunRequest("Hello", 1));
        var selection = CancellationSelection(row.RunId, _actor);
        selection = selection with
        {
            Authorization = selection.Authorization with
            {
                EffectiveAuthority = [new ProjectsAuthority("project", Project, [new ProjectsPermissionGrant("readProjects", 1)])]
            }
        };
        var denied = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CancelProjectRunAsync(_actor, selection, "denied", new(), NoCheck, CancellationToken.None));
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(0L, await CountAsync("project_run_cancellations"));
    }

    /// <summary>Builds the unbound Projects permission evidence used only by project cancellation.</summary>
    private AuthorizedRunSelection CancellationSelection(string runId, CoordinationActor actor) =>
        Selection(runId) with
        {
            Authorization = new ProjectsAuthorizationContext(
            1, actor.Issuer, actor.Subject, Tenant, 1, null, null,
            [new ProjectsAuthority("project", Project, [new ProjectsPermissionGrant("cancelRun", 1)])])
        };

    /// <summary>Terminal success or failure is preserved and receives only a no-op cancellation receipt.</summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("indeterminate")]
    public async Task AlreadyEndedRunReturnsSuccessfulNoOp(string state)
    {
        var row = await Create("ended", new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs SET execution_state = @state,
                execution_cause_code = CASE WHEN @state IN ('failed', 'indeterminate') THEN 'fixture_failure' END,
                execution_reference = CASE WHEN @state IN ('failed', 'indeterminate') THEN 'fixture_reference' END
            WHERE project_id = @project AND run_id = @run
            """, connection);
        update.Parameters.AddWithValue("state", state);
        update.Parameters.AddWithValue("project", Project);
        update.Parameters.AddWithValue("run", row.RunId);
        await update.ExecuteNonQueryAsync();
        var result = await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor),
            "terminal", new(), NoCheck, CancellationToken.None);
        Assert.Equal(state, result.State);
        Assert.True(result.AlreadyTerminal);
        Assert.Null(result.CancelledAt);
        Assert.Equal(0L, await CountAsync("outbox_events"));
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
