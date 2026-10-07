using System.Security.Claims;
using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[CollectionDefinition("Coordination PostgreSQL")]
public sealed class CoordinationPostgresCollection : ICollectionFixture<CoordinationPostgresFixture>;

public sealed class CoordinationPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[Collection("Coordination PostgreSQL")]
public sealed class CoordinationOwnerStorePostgresTests : IAsyncLifetime
{
    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "coordination_" + Guid.NewGuid().ToString("N");
    private CoordinationOwnerStore _store = null!;
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private readonly AuthorizedRunSelection _selection;
    private SessionIdentity _root;
    private AcceptedRoot _acceptedRoot = null!;
    private RegisteredChild _child = null!;

    public CoordinationOwnerStorePostgresTests(CoordinationPostgresFixture fixture)
    {
        _fixture = fixture;
        _selection = CreateSelection(_actor);
    }

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(_fixture.DataSource, _schema);
        _store = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        _root = new SessionIdentity(_selection.Selection.ProjectId, _selection.Selection.RunId, "root");
        _acceptedRoot = await _store.AcceptRootAsync(_actor, _selection, _root.SessionId, CancellationToken.None);
        _child = await _store.RegisterChildAsync(_actor, _root, "child", 100, 32, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RootAndChildRegistrationsAreIdempotentAndWriterBound()
    {
        var replay = await _store.AcceptRootAsync(
            _actor, _selection, _root.SessionId, CancellationToken.None);
        Assert.Equal(_acceptedRoot, replay);

        var childReplay = await _store.RegisterChildAsync(
            _actor, _root, _child.Identity.SessionId, 100, 32, CancellationToken.None);
        Assert.Equal(_child, childReplay);

        var otherActor = _actor with { Subject = Guid.NewGuid().ToString("D") };
        var unauthorized = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RegisterChildAsync(otherActor, _root, "forged-child", 100, 32, CancellationToken.None));
        Assert.Equal(403, unauthorized.StatusCode);

        var conflictingRoot = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.AcceptRootAsync(_actor, _selection, "other-root", CancellationToken.None));
        Assert.Equal(409, conflictingRoot.StatusCode);
    }

    [Fact]
    public async Task MessageAdmissionIsIdempotentAndSharesOwnerTransactionWithOutbox()
    {
        var request = NewQuestion(_child.PendingRequestId, Payload("""{"text":"first"}"""));
        var sent = await _store.SendMessageAsync(_actor, _root, request, CancellationToken.None);
        var replay = await _store.SendMessageAsync(_actor, _root, request, CancellationToken.None);
        Assert.Equal(sent.OwnerMessageId, replay.OwnerMessageId);
        Assert.Equal("pending", sent.Status);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.SendMessageAsync(
                _actor, _root, request with { Payload = Payload("""{"text":"changed"}""") },
                CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_messages),
                (SELECT count(*) FROM "{_schema}".outbox_events),
                (SELECT status FROM "{_schema}".coordination_messages WHERE message_id = @id)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, sent.OwnerMessageId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal("pending", reader.GetString(2));
    }

    [Fact]
    public async Task DeliveryIngressDeduplicatesWakeAndLeavesRequestUnapproved()
    {
        var initial = await _store.SendMessageAsync(
            _actor, _root, NewQuestion(_child.PendingRequestId, Payload("""{"text":"question"}""")),
            CancellationToken.None);
        var threadId = await ReadThreadIdAsync(initial.OwnerMessageId);
        var presenting = await _store.AdvanceTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            CancellationToken.None);
        var reply = await _store.SendMessageAsync(
            _actor,
            _child.Identity,
            new CoordinationMessageRequest(
                _root.SessionId,
                "reply-1",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.NeedsInput,
                AddressedMessageKind.Question,
                Payload("""{"text":"need input"}"""),
                ReplyToId: initial.OwnerMessageId,
                RequestId: _child.PendingRequestId,
                ReplyCorrelationId: _child.PendingRequestId),
            CancellationToken.None);
        var replyThreadId = await ReadThreadIdAsync(reply.OwnerMessageId);
        Assert.Equal(threadId, replyThreadId);
        var receipt = new MessageAdmissionReceipt(
            reply.OwnerMessageId,
            Guid.NewGuid(),
            replyThreadId,
            2,
            AddressedMessageStatus.Accepted,
            _child.PendingRequestId,
            AddressedMessagePurpose.NeedsInput,
            _child.Identity,
            _root,
            _child.ExecutionFence,
            _acceptedRoot.ExecutionFence);
        var ingress = new DeliveryIngressRequest(reply.OwnerMessageId, receipt);

        await _store.AdmitDeliveryAsync(_actor, ingress, CancellationToken.None);
        await _store.AdmitDeliveryAsync(_actor, ingress, CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT status FROM "{_schema}".coordination_messages WHERE message_id = @ownerMessage),
                (SELECT pending_wake FROM "{_schema}".coordination_sessions
                    WHERE project_id = @project AND run_id = @run AND session_id = 'root'),
                (SELECT gate_state FROM "{_schema}".coordination_requests
                    WHERE project_id = @project AND run_id = @run AND request_id = @request),
                (SELECT count(*) FROM "{_schema}".parent_notifications
                    WHERE project_id = @project AND message_id = @ownerMessage AND wakes_parent),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts
                    WHERE consumer_id = 'orchestrator.addressed-message-ingress')
            """, connection);
        command.Parameters.AddWithValue("ownerMessage", NpgsqlDbType.Uuid, reply.OwnerMessageId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, _child.PendingRequestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("admitted", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("input_available", reader.GetString(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));

        var resumed = await _store.AdvanceTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            CancellationToken.None);
        Assert.True(resumed.StateVersion > presenting.StateVersion);
        var active = await _store.CompleteTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            resumed.StateVersion,
            presentedMessage: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal("active", active.ExecutionState);
        Assert.True(active.PendingWake);
        Assert.Equal(active.StateVersion,
            (await _store.ReadCompletedTurnBoundaryAsync(
                _actor,
                _root,
                _acceptedRoot.ExecutionFence,
                _acceptedRoot.StateVersion,
                CancellationToken.None))!.StateVersion);

        var blocked = await _store.FinishTurnAsync(
            _actor,
            _root,
            new FinishTurnRequest(
                _acceptedRoot.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Blocked),
            CancellationToken.None);
        Assert.Equal("blocked", blocked.ExecutionState);
        var wakeTurn = await _store.AdvanceTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            blocked.StateVersion,
            CancellationToken.None);
        Assert.Equal("presenting", wakeTurn.ExecutionState);
    }

    [Fact]
    public async Task LogicalTurnBoundaryUsesCurrentFenceAndCompareAndSwapVersion()
    {
        var presenting = await _store.AdvanceTurnBoundaryAsync(
            _actor, _root, _acceptedRoot.ExecutionFence, _acceptedRoot.StateVersion, CancellationToken.None);
        Assert.Equal(1, presenting.LogicalTurnOrdinal);
        Assert.Equal("presenting", presenting.ExecutionState);

        var retry = await _store.AdvanceTurnBoundaryAsync(
            _actor, _root, _acceptedRoot.ExecutionFence, _acceptedRoot.StateVersion, CancellationToken.None);
        Assert.Equal(presenting, retry);

        var active = await _store.CompleteTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            presenting.StateVersion,
            presentedMessage: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal("active", active.ExecutionState);
        Assert.Equal(active, await _store.ReadCompletedTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            CancellationToken.None));
        var finished = await _store.FinishTurnAsync(
            _actor,
            _root,
            new FinishTurnRequest(
                _acceptedRoot.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Idle),
            CancellationToken.None);
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.AdvanceTurnBoundaryAsync(
                _actor, _root, _acceptedRoot.ExecutionFence, _acceptedRoot.StateVersion, CancellationToken.None));
        Assert.Equal(409, stale.StatusCode);

        var forged = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.AdvanceTurnBoundaryAsync(
                _actor with { Subject = Guid.NewGuid().ToString("D") }, _root,
                _acceptedRoot.ExecutionFence, finished.StateVersion, CancellationToken.None));
        Assert.Equal(403, forged.StatusCode);
    }

    private async Task<Guid> ReadThreadIdAsync(Guid messageId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT thread_id FROM "{_schema}".coordination_messages WHERE message_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, messageId);
        return (Guid)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException("Expected an owner message thread."));
    }

    private static CoordinationMessageRequest NewQuestion(string requestId, JsonElement payload) =>
        new(
            "child",
            "question-1",
            AddressedMessageDeliveryMode.Immediate,
            AddressedMessagePurpose.Question,
            AddressedMessageKind.Question,
            payload,
            RequestId: requestId);

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AuthorizedRunSelection CreateSelection(CoordinationActor actor)
    {
        using var snapshot = JsonDocument.Parse("""{"source":"projects-config"}""");
        var selection = new EffectiveRunSelection(
                "project-1",
                Guid.NewGuid().ToString("D"),
                1,
                1,
                1,
                "context-v1",
                snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(
            selection,
            new ProjectsAuthorizationContext(
                1,
                actor.Issuer,
                actor.Subject,
                "tenant-1",
                1,
                selection.ProjectId,
                selection.RunId,
                ImmutableArray.Create(new ProjectsAuthority(
                    "project",
                    selection.ProjectId,
                    ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }
}
