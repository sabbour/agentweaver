using System.Security.Claims;
using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
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
    public async Task CappedRootAdmissionPersistsWithTheRootAndReplaysWithoutRepricing()
    {
        var run = "capped-" + Guid.NewGuid().ToString("N");
        var selection = _selection with
        {
            Selection = _selection.Selection with
            {
                RunId = run, Snapshot = CoordinatorRunAdmissionTests.Selection(_root.ProjectId, run)
            },
            Authorization = _selection.Authorization with { BoundRunId = run }
        };
        var admission = CoordinatorRunAdmissionTests.Receipt(selection);
        var reads = 0;
        var first = await _store.AcceptRootAsync(_actor, selection, "capped-root", CancellationToken.None,
            _ => { reads++; return Task.FromResult(admission); }, _ => Task.CompletedTask);
        var restarted = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        var stored = await restarted.ReadRunAdmissionAsync(_actor, selection, CancellationToken.None);
        Assert.Equal(admission.ModelBindingPin, stored!.ModelBindingPin);
        Assert.Equal(admission.ModelSelection, stored.ModelSelection);
        Assert.Equal(first, await restarted.AcceptRootAsync(_actor, selection, "capped-root", CancellationToken.None,
            _ => throw new InvalidOperationException("An identical accepted run must not be repriced."),
            _ => Task.CompletedTask));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unpriced")]
    [InlineData("boundary")]
    [InlineData("revoked")]
    public async Task RejectedCappedAdmissionLeavesNoRootRunOrAdmissionOutbox(string fault)
    {
        var run = "rejected-" + Guid.NewGuid().ToString("N");
        var selection = _selection with
        {
            Selection = _selection.Selection with
            {
                RunId = run, Snapshot = CoordinatorRunAdmissionTests.Selection(_root.ProjectId, run)
            },
            Authorization = _selection.Authorization with { BoundRunId = run }
        };
        var receipt = CoordinatorRunAdmissionTests.Receipt(selection, fault == "boundary" ? 10 : 0);
        if (fault == "unpriced")
            receipt = receipt with { CostBinding = null };
        var checks = 0;
        await Assert.ThrowsAsync<CoordinationException>(() => _store.AcceptRootAsync(
            _actor, selection, "rejected-root", CancellationToken.None,
            fault == "missing" ? null : _ => Task.FromResult(receipt),
            _ => fault == "revoked" && ++checks == 2
                ? Task.FromException(new CoordinationException("run_selection_permission_denied", 403))
                : Task.CompletedTask));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".accepted_runs WHERE run_id = @run),
                   (SELECT count(*) FROM "{_schema}".coordination_sessions WHERE run_id = @run),
                   (SELECT count(*) FROM "{_schema}".outbox_events
                    WHERE event_type = 'orchestrator.run.copilot_admission' AND payload->>'runId' = @run)
            """, connection);
        command.Parameters.AddWithValue("run", run);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 3), column => Assert.Equal(0L, reader.GetInt64(column)));
    }

    [Fact]
    public async Task RootAndChildRegistrationsAreIdempotentAndWriterBound()
    {
        Assert.Equal("idle", await _store.ReadCurrentExecutionStateAsync(
            _root.ProjectId, _root.RunId, CancellationToken.None));

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
    public async Task CoordinatorExecutionCheckpointsAppendLinkedValuesAndReadBackFromTheirStore()
    {
        var runId = Guid.NewGuid().ToString("D");
        var selection = new AuthorizedRunSelection(
            _selection.Selection with
            {
                RunId = runId,
                Snapshot = Payload("""{"modelSelection":{"reference":"model-1"}}""")
            },
            _selection.Authorization with { BoundRunId = runId });
        var root = await _store.AcceptRootAsync(
            _actor, selection, "checkpoint-root", CancellationToken.None);
        var identity = new SessionIdentity(selection.Selection.ProjectId, runId, "checkpoint-root");
        var binding = new MafCheckpointBinding(
            identity,
            _actor,
            root.ExecutionFence,
            MafExecutionCheckpointStore.CurrentSdkVersion,
            "model-1",
            CacheReference: null,
            StoreName: MafExecutionCheckpointContract.StoreName);
        var checkpointStore = new PostgresMafCheckpointStore(
            _fixture.DataSource, _schema, objectStore: null).ForRun(binding);

        var first = await checkpointStore.CreateCheckpointAsync(
            identity.SessionId, Payload("""{"revision":1}"""));
        var second = await checkpointStore.CreateCheckpointAsync(
            identity.SessionId, Payload("""{"revision":2}"""), first);

        var index = await checkpointStore.RetrieveIndexAsync(identity.SessionId);
        Assert.Equal(2, index.Count());
        Assert.Contains(index, checkpoint => checkpoint.CheckpointId == first.CheckpointId);
        Assert.Contains(index, checkpoint => checkpoint.CheckpointId == second.CheckpointId);

        var latest = await checkpointStore.ReadLatestCheckpointAsync(binding, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(second.CheckpointId, latest.Value.Info.CheckpointId);
        Assert.Equal(2, latest.Value.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task SpawnCannotPersistAnUnvalidatedWorkPlanItemId()
    {
        var request = new SpawnSessionRequest(
            "planned-child",
            CoordinationSessionKind.ChildWork,
            "planned-child-spawn",
            "Execute the confirmed work item.",
            WorkPlanItemId: "work-item-1");

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.SpawnSessionAsync(
                _actor, _root, request, 100, 32, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);

        var tree = await _store.ReadSessionTreeAsync(_actor, _root, CancellationToken.None);
        Assert.DoesNotContain(tree.Nodes, node => node.Identity.SessionId == request.SessionId);
    }

    [Fact]
    public async Task ExplicitForkPersistsTheVerifiedLineageAndReplaysFromTheOwnerStore()
    {
        var request = NewForkRequest(_root, "fork-child", "fork-once");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var prepared = await _store.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.RegistrationPending, prepared.RegistrationState);
        Assert.False(prepared.IsDuplicate);

        var reservedTarget = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RegisterChildAsync(
                _actor, _root, request.TargetSessionId, 100, 32, CancellationToken.None));
        Assert.Equal(409, reservedTarget.StatusCode);

        var invalidEventsFork = NewEventsFork(_root, request);
        var lineageMismatch = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CompleteSessionForkAsync(
                _actor,
                _root,
                request,
                selectionHash,
                selectionHash,
                selectionIsCurrent: true,
                invalidEventsFork with
                {
                    Lineage = invalidEventsFork.Lineage with { SourceCursor = "different-cursor" }
                },
                100,
                32,
                isDuplicate: false,
                CancellationToken.None));
        Assert.Equal("session_fork_contract_invalid", lineageMismatch.Code);

        var completed = await _store.CompleteSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            selectionHash,
            selectionIsCurrent: true,
            NewEventsFork(_root, request),
            100,
            32,
            isDuplicate: false,
            CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Registered, completed.RegistrationState);
        Assert.False(completed.IsDuplicate);
        Assert.Equal(request.SourceEventId, completed.Lineage!.SourceEventId);
        Assert.Equal(request.SourceCursor, completed.Lineage.SourceCursor);
        Assert.Equal(_root.SessionId, completed.Node!.ParentSessionId);
        Assert.Equal(_root.SessionId, completed.Node.RootSessionId);
        Assert.Equal(CoordinationSessionKind.ChildWork, completed.Node.Kind);
        Assert.NotNull(completed.PendingRequestId);

        var restartedStore = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        var replay = await restartedStore.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Registered, replay.RegistrationState);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(completed.CommandId, replay.CommandId);
        Assert.Equal(completed.Lineage, replay.Lineage);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            restartedStore.PrepareSessionForkAsync(
                _actor,
                _root,
                request with { SourceCursor = "different-cursor" },
                selectionHash,
                100,
                32,
                CancellationToken.None));
        Assert.Equal("idempotency_conflict", conflict.Code);

        var tree = await restartedStore.ReadSessionTreeAsync(_actor, _root, CancellationToken.None);
        Assert.Contains(tree.Nodes, node => node.Identity.SessionId == request.TargetSessionId);
        Assert.Equal(1, await ReadForkOutboxCountAsync("orchestrator.session.forked"));

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_fence = execution_fence + 1, state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
            update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var replayAfterFenceChange = await restartedStore.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Registered, replayAfterFenceChange.RegistrationState);
        Assert.True(replayAfterFenceChange.IsDuplicate);
        Assert.Equal(completed.CommandId, replayAfterFenceChange.CommandId);
    }

    [Fact]
    public async Task ForkAdmissionRequiresTheExactPendingRequestAndReservedSelection()
    {
        var request = NewForkRequest(_root, "admitted-fork-child", "admitted-fork-once");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var prepared = await _store.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        var eventsRequest = new SessionForkRequest(
            request.TargetSessionId,
            request.SourceEventId,
            request.SourceCursor,
            request.IdempotencyKey);

        var receipt = await _store.ValidateSessionForkAdmissionAsync(
            _actor, _root, eventsRequest, selectionHash, CancellationToken.None);
        Assert.Equal(prepared.CommandId, receipt.CommandId);
        Assert.Equal(_root, receipt.Source);
        Assert.Equal(request.TargetSessionId, receipt.TargetSessionId);
        Assert.Equal(request.SourceEventId, receipt.SourceEventId);
        Assert.Equal(request.SourceCursor, receipt.SourceCursor);
        Assert.Equal(request.IdempotencyKey, receipt.IdempotencyKey);
        Assert.Equal(request.ExecutionFence, receipt.ExecutionFence);
        Assert.Equal(_actor.Issuer, receipt.ActorIssuer);
        Assert.Equal(_actor.Subject, receipt.ActorSubject);
        Assert.Equal(selectionHash, receipt.AcceptedSelectionHash);

        var changedRequest = eventsRequest with { SourceCursor = "another-cursor" };
        var changedRequestError = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ValidateSessionForkAdmissionAsync(
                _actor, _root, changedRequest, selectionHash, CancellationToken.None));
        Assert.Equal("idempotency_conflict", changedRequestError.Code);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".coordination_tree_commands
            SET fork_accepted_selection_hash = @selectionHash
            WHERE command_id = @command
            """, connection))
        {
            update.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, new string('B', 64));
            update.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, prepared.CommandId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var staleSelection = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ValidateSessionForkAdmissionAsync(
                _actor, _root, eventsRequest, selectionHash, CancellationToken.None));
        Assert.Equal("session_fork_admission_denied", staleSelection.Code);
    }

    [Fact]
    public async Task PrepareForkRevalidatesAfterThePendingCommandInsertWaitAndRollsBack()
    {
        var request = NewForkRequest(_root, "blocked-prepare-target", "blocked-prepare");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var validations = 0;
        Task Revalidate(CancellationToken _) =>
            Interlocked.Increment(ref validations) == 1
                ? Task.CompletedTask
                : Task.FromException(new CoordinationException("run_selection_permission_denied", 403));

        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var observer = await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand(
            $"""LOCK TABLE "{_schema}".coordination_tree_commands IN SHARE MODE""",
            blocker,
            blockerTransaction))
            await lockTable.ExecuteNonQueryAsync();

        var prepare = _store.PrepareSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            100,
            32,
            CancellationToken.None,
            Revalidate);
        await WaitUntilBlockedAsync(
            observer,
            blocker.ProcessID,
            $"""INSERT INTO "{_schema}".coordination_tree_commands""");
        await blockerTransaction.RollbackAsync();

        var denied = await Assert.ThrowsAsync<CoordinationException>(() => prepare);
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(2, validations);
        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT count(*)
            FROM "{_schema}".coordination_tree_commands
            WHERE project_id = @project AND run_id = @run
              AND requested_target_session_id = @target
            """, verifyConnection);
        verify.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        verify.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        verify.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, request.TargetSessionId);
        Assert.Equal(0L, await verify.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(403, "run_selection_permission_denied")]
    [InlineData(409, "coordinator_selection_stale")]
    public async Task CompleteForkRevalidatesAfterOutboxWaitAndFinalizesUnregistered(
        int statusCode,
        string unavailableCode)
    {
        var request = NewForkRequest(_root, $"blocked-complete-{statusCode}", $"blocked-complete-{statusCode}");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var prepared = await _store.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        var streamId = $"coordination/{_root.ProjectId}/{_root.RunId}/{_root.SessionId}";
        await using (var seedConnection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".outbox_streams (stream_id) VALUES (@stream)
            ON CONFLICT (stream_id) DO NOTHING
            """, seedConnection))
        {
            seed.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
            await seed.ExecuteNonQueryAsync();
        }

        var validations = 0;
        Task Revalidate(CancellationToken _) =>
            Interlocked.Increment(ref validations) == 1
                ? Task.CompletedTask
                : Task.FromException(new CoordinationException(unavailableCode, statusCode));

        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var observer = await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockStream = new NpgsqlCommand($"""
            SELECT stream_id FROM "{_schema}".outbox_streams
            WHERE stream_id = @stream FOR UPDATE
            """, blocker, blockerTransaction))
        {
            lockStream.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
            Assert.Equal(streamId, await lockStream.ExecuteScalarAsync());
        }

        var complete = _store.CompleteSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            selectionHash,
            selectionIsCurrent: true,
            NewEventsFork(_root, request),
            100,
            32,
            isDuplicate: prepared.IsDuplicate,
            CancellationToken.None,
            Revalidate);
        await WaitUntilBlockedAsync(
            observer,
            blocker.ProcessID,
            $"""UPDATE "{_schema}".outbox_streams SET last_sequence""");
        await blockerTransaction.RollbackAsync();

        var unregistered = await complete;
        Assert.Equal(2, validations);
        Assert.Equal(CoordinationForkRegistrationState.Unregistered, unregistered.RegistrationState);
        Assert.Equal(unavailableCode, unregistered.UnavailableCode);
        Assert.Null(unregistered.Node);
        Assert.Null(unregistered.PendingRequestId);

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_sessions
                 WHERE project_id = @project AND run_id = @run AND session_id = @target),
                (SELECT count(*) FROM "{_schema}".coordination_requests
                 WHERE project_id = @project AND run_id = @run AND recipient_session_id = @target),
                (SELECT count(*) FROM "{_schema}".outbox_events
                 WHERE id = @command AND event_type = 'orchestrator.session.forked'),
                (SELECT count(*) FROM "{_schema}".outbox_events
                 WHERE id = @command AND event_type = 'orchestrator.session.fork_unregistered'),
                (SELECT result ->> 'registrationState' FROM "{_schema}".coordination_tree_commands
                 WHERE command_id = @command)
            """, verifyConnection);
        verify.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        verify.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        verify.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, request.TargetSessionId);
        verify.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, prepared.CommandId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal("unregistered", reader.GetString(4));
    }

    [Fact]
    public async Task PrepareRegisteredDuplicateRevalidatesAfterCommandRowWait()
    {
        var request = NewForkRequest(_root, "registered-duplicate-target", "registered-duplicate");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var prepared = await _store.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        var registered = await _store.CompleteSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            selectionHash,
            selectionIsCurrent: true,
            NewEventsFork(_root, request),
            100,
            32,
            isDuplicate: false,
            CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Registered, registered.RegistrationState);
        var liveDuplicate = await _store.PrepareSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            100,
            32,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.True(liveDuplicate.IsDuplicate);
        Assert.Equal(prepared.CommandId, liveDuplicate.CommandId);

        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var observer = await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"""
            SELECT command_id FROM "{_schema}".coordination_tree_commands
            WHERE command_id = @command FOR UPDATE
            """, blocker, blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, prepared.CommandId);
            Assert.Equal(prepared.CommandId, await lockCommand.ExecuteScalarAsync());
        }

        var validations = 0;
        Task Revalidate(CancellationToken _) =>
            Interlocked.Increment(ref validations) == 1
                ? Task.CompletedTask
                : Task.FromException(new CoordinationException("run_selection_permission_denied", 403));
        var duplicate = _store.PrepareSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            100,
            32,
            CancellationToken.None,
            Revalidate);
        await WaitUntilBlockedAsync(
            observer, blocker.ProcessID, $"""coordination_tree_commands%FOR UPDATE""");
        await blockerTransaction.RollbackAsync();

        var denied = await Assert.ThrowsAsync<CoordinationException>(() => duplicate);
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(2, validations);
    }

    [Fact]
    public async Task AdmissionFailureRegisteredDuplicateRevalidatesAfterCommandRowWait()
    {
        var request = NewForkRequest(_root, "failure-duplicate-target", "failure-duplicate");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var prepared = await _store.PrepareSessionForkAsync(
            _actor, _root, request, selectionHash, 100, 32, CancellationToken.None);
        var registered = await _store.CompleteSessionForkAsync(
            _actor,
            _root,
            request,
            selectionHash,
            selectionHash,
            selectionIsCurrent: true,
            NewEventsFork(_root, request),
            100,
            32,
            isDuplicate: false,
            CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Registered, registered.RegistrationState);

        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var observer = await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"""
            SELECT command_id FROM "{_schema}".coordination_tree_commands
            WHERE command_id = @command FOR UPDATE
            """, blocker, blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, prepared.CommandId);
            Assert.Equal(prepared.CommandId, await lockCommand.ExecuteScalarAsync());
        }

        var liveDuplicate = _store.FinalizeSessionForkAdmissionFailureAsync(
            _actor,
            _root,
            request,
            selectionHash,
            CancellationToken.None,
            _ => Task.CompletedTask);
        await WaitUntilBlockedAsync(
            observer, blocker.ProcessID, $"""coordination_tree_commands%FOR UPDATE""");
        await blockerTransaction.RollbackAsync();
        var replay = await liveDuplicate;
        Assert.Equal(CoordinationForkRegistrationState.Registered, replay.RegistrationState);
        Assert.True(replay.IsDuplicate);

        await using var revokedBlocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var revokedObserver = await _fixture.DataSource.OpenConnectionAsync();
        await using var revokedTransaction = await revokedBlocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"""
            SELECT command_id FROM "{_schema}".coordination_tree_commands
            WHERE command_id = @command FOR UPDATE
            """, revokedBlocker, revokedTransaction))
        {
            lockCommand.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, prepared.CommandId);
            Assert.Equal(prepared.CommandId, await lockCommand.ExecuteScalarAsync());
        }

        var deniedDuplicate = _store.FinalizeSessionForkAdmissionFailureAsync(
            _actor,
            _root,
            request,
            selectionHash,
            CancellationToken.None,
            _ => Task.FromException(
                new CoordinationException("run_selection_permission_denied", 403)));
        await WaitUntilBlockedAsync(
            revokedObserver, revokedBlocker.ProcessID, $"""coordination_tree_commands%FOR UPDATE""");
        await revokedTransaction.RollbackAsync();
        var denied = await Assert.ThrowsAsync<CoordinationException>(() => deniedDuplicate);
        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public async Task ForkWithAChangedAcceptedSelectionIsPersistedAsUnregistered()
    {
        var request = NewForkRequest(_child.Identity, "selection-stale-fork", "selection-stale-fork-once");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        _ = await _store.PrepareSessionForkAsync(
            _actor, _child.Identity, request, selectionHash, 100, 32, CancellationToken.None);

        var unregistered = await _store.CompleteSessionForkAsync(
            _actor,
            _child.Identity,
            request,
            selectionHash,
            new string('b', 64),
            selectionIsCurrent: false,
            NewEventsFork(_child.Identity, request),
            100,
            32,
            isDuplicate: false,
            CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Unregistered, unregistered.RegistrationState);
        Assert.Equal("coordinator_selection_stale", unregistered.UnavailableCode);
        Assert.Null(unregistered.Node);
        Assert.NotNull(unregistered.Lineage);
        Assert.Equal(1, await ReadForkOutboxCountAsync("orchestrator.session.fork_unregistered"));
    }

    [Fact]
    public async Task ForkWithAChangedFenceIsPersistedAsUnregisteredAndCannotBeAdopted()
    {
        var request = NewForkRequest(_child.Identity, "stale-fork-child", "stale-fork-once");
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        _ = await _store.PrepareSessionForkAsync(
            _actor, _child.Identity, request, selectionHash, 100, 32, CancellationToken.None);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_fence = execution_fence + 1, state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
            update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var unregistered = await _store.CompleteSessionForkAsync(
            _actor,
            _child.Identity,
            request,
            selectionHash,
            selectionHash,
            selectionIsCurrent: true,
            NewEventsFork(_child.Identity, request),
            100,
            32,
            isDuplicate: false,
            CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Unregistered, unregistered.RegistrationState);
        Assert.Equal("execution_fence_stale", unregistered.UnavailableCode);
        Assert.Null(unregistered.Node);
        Assert.Equal(request.SourceCursor, unregistered.Lineage!.SourceCursor);

        var replay = await _store.PrepareSessionForkAsync(
            _actor, _child.Identity, request, selectionHash, 100, 32, CancellationToken.None);
        Assert.Equal(CoordinationForkRegistrationState.Unregistered, replay.RegistrationState);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(unregistered.CommandId, replay.CommandId);

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @target
            """, verifyConnection);
        verify.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        verify.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        verify.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, request.TargetSessionId);
        Assert.Equal(0L, await verify.ExecuteScalarAsync());
        Assert.Equal(1, await ReadForkOutboxCountAsync("orchestrator.session.fork_unregistered"));
    }

    [Fact]
    public async Task RuntimeOwnerStateRejectsUnmappedChildEvenForAnActiveTurn()
    {
        await StartActiveTurnAsync(_child.Identity, _acceptedRoot.StateVersion);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ReadRuntimeOwnerStateAsync(_actor, _child.Identity, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
    }

    [Theory]
    [InlineData("selection-hash")]
    [InlineData("fence")]
    [InlineData("dispatch")]
    [InlineData("work-plan-item")]
    public async Task SpawnRequiresTheLatestDecisionEnvelopeToAuthorizeItsWorkPlanItem(string mismatch)
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        await SeedCoordinatorDecisionVersionAsync(
            1,
            planItemId: mismatch == "work-plan-item" ? "other-item" : "work-item-1",
            acceptedSelectionHash: mismatch == "selection-hash" ? new string('b', 64) : null,
            executionFence: mismatch == "fence" ? _acceptedRoot.ExecutionFence + 1 : null,
            dispatchable: mismatch != "dispatch");
        var request = new SpawnSessionRequest(
            "invalid-envelope-child",
            CoordinationSessionKind.ChildWork,
            "invalid-envelope-spawn",
            "Execute the confirmed work item.",
            WorkPlanItemId: "work-item-1");

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.SpawnSessionAsync(
                _actor,
                _root,
                request,
                100,
                32,
                CancellationToken.None,
                new ConfirmedWorkPlanItemAssociation("work-item-1", 1, selectionHash)));
        Assert.Equal(409, error.StatusCode);

        var tree = await _store.ReadSessionTreeAsync(_actor, _root, CancellationToken.None);
        Assert.DoesNotContain(tree.Nodes, node => node.Identity.SessionId == request.SessionId);
    }

    [Fact]
    public async Task RuntimeOwnerStateReadsPersistedWorkPlanLinkAndRejectsStaleDecisionVersion()
    {
        await SeedCoordinatorDecisionVersionAsync(1);
        var selectionHash = await ReadAcceptedSelectionHashAsync();
        var request = new SpawnSessionRequest(
            "planned-child",
            CoordinationSessionKind.ChildWork,
            "planned-child-spawn",
            "Execute the confirmed work item.",
            WorkPlanItemId: "work-item-1");
        var spawned = await _store.SpawnSessionAsync(
            _actor,
            _root,
            request,
            100,
            32,
            CancellationToken.None,
            new ConfirmedWorkPlanItemAssociation("work-item-1", 1, selectionHash));
        var active = await StartActiveTurnAsync(spawned.Node.Identity, _acceptedRoot.StateVersion);

        var owner = await _store.ReadRuntimeOwnerStateAsync(
            _actor, spawned.Node.Identity, CancellationToken.None);
        Assert.Equal("work-item-1", owner.WorkPlanItemId);
        Assert.Equal(_selection.Authorization.TenantId, owner.TenantId);
        Assert.Equal(selectionHash, owner.AcceptedSelectionHash);
        Assert.Equal(_acceptedRoot.ExecutionFence, owner.ExecutionFence);
        Assert.Equal(active.LogicalTurnOrdinal, owner.LogicalTurnOrdinal);
        Assert.Equal(active.StateVersion, owner.StateVersion);
        Assert.Equal(active.RuntimeTurnId, owner.RuntimeTurnId);

        await SeedCoordinatorDecisionVersionAsync(2);
        var staleRequest = request with
        {
            SessionId = "stale-planned-child",
            IdempotencyKey = "stale-planned-child-spawn"
        };
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.SpawnSessionAsync(
                _actor,
                _root,
                staleRequest,
                100,
                32,
                CancellationToken.None,
                new ConfirmedWorkPlanItemAssociation("work-item-1", 1, selectionHash)));
        Assert.Equal(409, stale.StatusCode);
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
        Assert.StartsWith("turn-", presenting.RuntimeTurnId, StringComparison.Ordinal);

        var retry = await _store.AdvanceTurnBoundaryAsync(
            _actor, _root, _acceptedRoot.ExecutionFence, _acceptedRoot.StateVersion, CancellationToken.None);
        Assert.Equal(presenting, retry);
        Assert.Equal(presenting.RuntimeTurnId, retry.RuntimeTurnId);

        var active = await _store.CompleteTurnBoundaryAsync(
            _actor,
            _root,
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            presenting.StateVersion,
            presentedMessage: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal("active", active.ExecutionState);
        Assert.Equal(presenting.RuntimeTurnId, active.RuntimeTurnId);
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
        Assert.Equal(active.RuntimeTurnId, finished.RuntimeTurnId);
        var nextTurn = await _store.AdvanceTurnBoundaryAsync(
            _actor, _root, _acceptedRoot.ExecutionFence, finished.StateVersion, CancellationToken.None);
        Assert.Equal(2, nextTurn.LogicalTurnOrdinal);
        Assert.NotEqual(finished.RuntimeTurnId, nextTurn.RuntimeTurnId);
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

    [Fact]
    public async Task OwnerStatusSeparatesLogicalTurnStopIntentAndRuntimeEffects()
    {
        var idle = await _store.ReadSessionStatusAsync(_actor, _root, CancellationToken.None);
        Assert.Equal(CoordinationActivityState.Idle, idle.Activity);
        Assert.Null(idle.ActivityUnavailableCode);
        Assert.Equal("unavailable", idle.RuntimeEffectsState);
        Assert.Equal("runtime_effect_owner_unavailable", idle.RuntimeEffectsUnavailableCode);

        var stop = await _store.SendMessageAsync(
            _actor,
            _root,
            new CoordinationMessageRequest(
                _child.Identity.SessionId,
                "stop-child",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Steering,
                AddressedMessageKind.Steering,
                Payload("""{"action":"stop","instruction":{"reason":"user requested stop"}}""")),
            CancellationToken.None);
        var childStatus = await _store.ReadSessionStatusAsync(
            _actor, _child.Identity, CancellationToken.None);
        Assert.Equal(CoordinationInterruptionIntentState.Requested, childStatus.InterruptionIntent.State);
        Assert.Equal(stop.OwnerMessageId, childStatus.InterruptionIntent.OwnerMessageId);
        Assert.Equal("operator_stop_requested", childStatus.InterruptionIntent.CauseCode);
        Assert.Equal(CoordinationActivityState.Idle, childStatus.Activity);
        Assert.Equal("unavailable", childStatus.RuntimeEffectsState);
        Assert.Equal("idle", childStatus.RunExecution.State);
        Assert.Null(childStatus.RunExecution.CauseCode);

        var active = await StartActiveTurnAsync(_root, idle.StateVersion);
        var busy = await _store.ReadSessionStatusAsync(_actor, _root, CancellationToken.None);
        Assert.Equal(CoordinationActivityState.Busy, busy.Activity);
        Assert.Null(busy.ActivityUnavailableCode);
        Assert.Equal("unavailable", busy.RuntimeEffectsState);

        var finished = await _store.FinishTurnAsync(
            _actor,
            _root,
            new FinishTurnRequest(
                _acceptedRoot.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Idle),
            CancellationToken.None);
        var returnedToIdle = await _store.ReadSessionStatusAsync(_actor, _root, CancellationToken.None);
        Assert.Equal("idle", finished.ExecutionState);
        Assert.Equal(CoordinationActivityState.Idle, returnedToIdle.Activity);
        Assert.Equal(CoordinationInterruptionIntentState.None, returnedToIdle.InterruptionIntent.State);
        Assert.Equal("unavailable", returnedToIdle.RuntimeEffectsState);
    }

    [Fact]
    public async Task IndeterminateRunOutcomeIsOwnerPersistedAndRecoveredUnderANewFence()
    {
        var active = await StartActiveTurnAsync(_root, _acceptedRoot.StateVersion);
        var failureRequest = new ReportRunFailureRequest(
            _acceptedRoot.ExecutionFence,
            _acceptedRoot.StateVersion,
            active.StateVersion,
            "failure-once",
            OwnerRunFailureState.Indeterminate,
            "runtime_lost",
            "attempt-0001");
        var failed = await _store.ReportRunFailureAsync(
            _actor, _root, failureRequest, CancellationToken.None);

        Assert.Equal("indeterminate", failed.State);
        Assert.Equal(_acceptedRoot.ExecutionFence, failed.PreviousExecutionFence);
        Assert.Equal(_acceptedRoot.ExecutionFence + 1, failed.ExecutionFence);
        Assert.Equal("runtime_lost", failed.CauseCode);
        Assert.Equal("attempt-0001", failed.Reference);
        Assert.True(failed.RunStateVersion > failed.PreviousRunStateVersion);
        Assert.True(failed.SessionStateVersion > failed.PreviousSessionStateVersion);
        Assert.Contains(failed.FencedSessions, fenced =>
            fenced.SessionId == _root.SessionId && fenced.State == "indeterminate");
        Assert.Contains(failed.FencedSessions, fenced =>
            fenced.SessionId == _child.Identity.SessionId && fenced.StateVersion > 1);

        var failedStatus = await _store.ReadOwnerRunStatusAsync(
            _actor, _root.ProjectId, _root.RunId, CancellationToken.None);
        Assert.Equal("indeterminate", failedStatus.ExecutionState);
        Assert.Equal("runtime_lost", failedStatus.CauseCode);
        Assert.Equal("attempt-0001", failedStatus.Reference);
        Assert.Equal(failed.RunStateVersion, failedStatus.StateVersion);
        var failedSessionStatus = await _store.ReadSessionStatusAsync(
            _actor, _root, CancellationToken.None);
        Assert.Equal(CoordinationActivityState.Unknown, failedSessionStatus.Activity);
        Assert.Equal("logical_turn_indeterminate", failedSessionStatus.ActivityUnavailableCode);
        Assert.Equal("unavailable", failedSessionStatus.RuntimeEffectsState);
        Assert.Equal("indeterminate", failedSessionStatus.RunExecution.State);
        Assert.Equal("runtime_lost", failedSessionStatus.RunExecution.CauseCode);
        Assert.Equal("attempt-0001", failedSessionStatus.RunExecution.Reference);

        var restartedStore = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        var failedAfterRestart = await restartedStore.ReadOwnerRunStatusAsync(
            _actor, _root.ProjectId, _root.RunId, CancellationToken.None);
        Assert.Equal("indeterminate", failedAfterRestart.ExecutionState);
        Assert.Equal("runtime_lost", failedAfterRestart.CauseCode);
        Assert.Equal("attempt-0001", failedAfterRestart.Reference);

        var recoveryRequest = new RecoverRunExecutionRequest(
            failed.ExecutionFence,
            failed.RunStateVersion,
            "recovery-once",
            "operator_recovery",
            "recovery-0001");
        var recovered = await restartedStore.RecoverRunExecutionAsync(
            _actor, _root.ProjectId, _root.RunId, recoveryRequest, CancellationToken.None);
        Assert.Equal("indeterminate", recovered.PreviousState);
        Assert.Equal("idle", recovered.State);
        Assert.Equal(failed.ExecutionFence, recovered.PreviousExecutionFence);
        Assert.Equal(failed.ExecutionFence + 1, recovered.ExecutionFence);
        Assert.Equal("runtime_lost", recovered.PreviousCauseCode);
        Assert.Equal("attempt-0001", recovered.PreviousReference);
        Assert.Equal("operator_recovery", recovered.CauseCode);
        Assert.Equal("recovery-0001", recovered.Reference);
        Assert.True(recovered.RunStateVersion > recovered.PreviousRunStateVersion);
        Assert.Contains(recovered.FencedSessions, fenced =>
            fenced.SessionId == _root.SessionId && fenced.State == "idle");
        Assert.Contains(recovered.FencedSessions, fenced =>
            fenced.SessionId == _child.Identity.SessionId && fenced.StateVersion > 1);

        var recoveredStatus = await _store.ReadOwnerRunStatusAsync(
            _actor, _root.ProjectId, _root.RunId, CancellationToken.None);
        Assert.Equal("idle", recoveredStatus.ExecutionState);
        Assert.Null(recoveredStatus.CauseCode);
        Assert.Null(recoveredStatus.Reference);
        var recoveredSessionStatus = await _store.ReadSessionStatusAsync(
            _actor, _root, CancellationToken.None);
        Assert.Equal(CoordinationActivityState.Idle, recoveredSessionStatus.Activity);
        Assert.Equal("unavailable", recoveredSessionStatus.RuntimeEffectsState);

        var failureReplay = await restartedStore.ReportRunFailureAsync(
            _actor, _root, failureRequest, CancellationToken.None);
        var recoveryReplay = await restartedStore.RecoverRunExecutionAsync(
            _actor, _root.ProjectId, _root.RunId, recoveryRequest, CancellationToken.None);
        Assert.True(failureReplay.IsDuplicate);
        Assert.Equal(failed.OperationId, failureReplay.OperationId);
        Assert.Equal(failed.ExecutionFence, failureReplay.ExecutionFence);
        Assert.True(recoveryReplay.IsDuplicate);
        Assert.Equal(recovered.OperationId, recoveryReplay.OperationId);
        Assert.Equal(recovered.ExecutionFence, recoveryReplay.ExecutionFence);

        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            restartedStore.ReportRunFailureAsync(
                _actor,
                _root,
                failureRequest with { IdempotencyKey = "stale-failure" },
                CancellationToken.None));
        Assert.Equal(409, stale.StatusCode);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var events = new NpgsqlCommand($"""
            SELECT event_type, payload
            FROM "{_schema}".outbox_events
            WHERE event_type IN (
                'orchestrator.run.execution_indeterminate',
                'orchestrator.run.execution_recovered')
            ORDER BY occurred_at, id
            """, connection);
        await using var reader = await events.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("orchestrator.run.execution_indeterminate", reader.GetString(0));
        using (var payload = JsonDocument.Parse(reader.GetString(1)))
        {
            var result = payload.RootElement.GetProperty("result");
            Assert.Equal("runtime_lost", result.GetProperty("causeCode").GetString());
            Assert.Equal("attempt-0001", result.GetProperty("reference").GetString());
            Assert.Equal("unavailable", payload.RootElement.GetProperty("runtimeEffectsState").GetString());
        }
        Assert.True(await reader.ReadAsync());
        Assert.Equal("orchestrator.run.execution_recovered", reader.GetString(0));
        using (var payload = JsonDocument.Parse(reader.GetString(1)))
        {
            var result = payload.RootElement.GetProperty("result");
            Assert.Equal("runtime_lost", result.GetProperty("previousCauseCode").GetString());
            Assert.Equal("attempt-0001", result.GetProperty("previousReference").GetString());
            Assert.Equal(failed.ExecutionFence, result.GetProperty("previousExecutionFence").GetInt64());
            Assert.Equal(recovered.ExecutionFence, result.GetProperty("executionFence").GetInt64());
            Assert.False(payload.RootElement.GetProperty("physicalEffectsReplayed").GetBoolean());
        }
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task FailedRunOutcomeRequiresOwnerFenceAndPublishesItsCause()
    {
        var active = await StartActiveTurnAsync(_root, _acceptedRoot.StateVersion);
        var result = await _store.ReportRunFailureAsync(
            _actor,
            _root,
            new ReportRunFailureRequest(
                _acceptedRoot.ExecutionFence,
                _acceptedRoot.StateVersion,
                active.StateVersion,
                "failure-terminal",
                OwnerRunFailureState.Failed,
                "logical_execution_failed",
                "work-item-1"),
            CancellationToken.None);

        Assert.Equal("failed", result.State);
        var status = await _store.ReadOwnerRunStatusAsync(
            _actor, _root.ProjectId, _root.RunId, CancellationToken.None);
        Assert.Equal("failed", status.ExecutionState);
        Assert.Equal("logical_execution_failed", status.CauseCode);
        Assert.Equal("work-item-1", status.Reference);
        var sessionStatus = await _store.ReadSessionStatusAsync(_actor, _root, CancellationToken.None);
        Assert.Equal(CoordinationActivityState.Unknown, sessionStatus.Activity);
        Assert.Equal("logical_turn_failed", sessionStatus.ActivityUnavailableCode);
        Assert.Equal("unavailable", sessionStatus.RuntimeEffectsState);

        var rejected = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.AdvanceTurnBoundaryAsync(
                _actor,
                _root,
                result.ExecutionFence,
                result.SessionStateVersion,
                CancellationToken.None));
        Assert.Equal(409, rejected.StatusCode);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = 'orchestrator.run.execution_failed'
                AND payload -> 'result' ->> 'causeCode' = 'logical_execution_failed'
                AND payload -> 'result' ->> 'reference' = 'work-item-1'
            """, connection);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task IdleSubscriptionsPublishActualOwnerTransitionsOnceOrAlways()
    {
        var once = await _store.SubscribeToIdleAsync(
            _actor,
            _root,
            new SubscribeToIdleRequest(
                _child.Identity.SessionId,
                _acceptedRoot.ExecutionFence,
                "idle-once",
                IdleNotificationMode.Once),
            CancellationToken.None);
        var always = await _store.SubscribeToIdleAsync(
            _actor,
            _root,
            new SubscribeToIdleRequest(
                _child.Identity.SessionId,
                _acceptedRoot.ExecutionFence,
                "idle-always",
                IdleNotificationMode.Always),
            CancellationToken.None);
        Assert.Equal(IdleNotificationSourceState.Available, once.NotificationSourceState);
        Assert.Null(once.NotificationSourceUnavailableCode);
        Assert.Equal(IdleNotificationSourceState.Available, always.NotificationSourceState);

        var active = await StartActiveTurnAsync(_root, _acceptedRoot.StateVersion);
        var firstIdle = await _store.FinishTurnAsync(
            _actor,
            _root,
            new FinishTurnRequest(
                _acceptedRoot.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Idle),
            CancellationToken.None);
        active = await StartActiveTurnAsync(_root, firstIdle.StateVersion);
        await _store.FinishTurnAsync(
            _actor,
            _root,
            new FinishTurnRequest(
                _acceptedRoot.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Idle),
            CancellationToken.None);

        Assert.Equal(1, await ReadIdleNotificationCountAsync(once.SubscriptionId));
        Assert.Equal(2, await ReadIdleNotificationCountAsync(always.SubscriptionId));
        Assert.False(await ReadIdleSubscriptionActiveAsync(once.SubscriptionId));
        Assert.True(await ReadIdleSubscriptionActiveAsync(always.SubscriptionId));
        Assert.Equal(3, await ReadIdleOutboxCountAsync());
    }

    private async Task<TurnBoundaryResult> StartActiveTurnAsync(
        SessionIdentity identity,
        long expectedStateVersion)
    {
        var presenting = await _store.AdvanceTurnBoundaryAsync(
            _actor, identity, _acceptedRoot.ExecutionFence, expectedStateVersion, CancellationToken.None);
        return await _store.CompleteTurnBoundaryAsync(
            _actor,
            identity,
            _acceptedRoot.ExecutionFence,
            expectedStateVersion,
            presenting.StateVersion,
            presentedMessage: null,
            cancellationToken: CancellationToken.None);
    }

    private async Task SeedCoordinatorDecisionVersionAsync(
        long stateVersion,
        string planItemId = "work-item-1",
        string? acceptedSelectionHash = null,
        long? executionFence = null,
        bool dispatchable = true)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        if (stateVersion == 1)
        {
            var envelope = JsonSerializer.Serialize(new
            {
                envelope = new
                {
                    version = CoordinatorDecisionStateEnvelope.CurrentVersion,
                    issuer = _actor.Issuer,
                    subject = _actor.Subject,
                    tenantId = _selection.Authorization.TenantId,
                    projectId = _root.ProjectId,
                    runId = _root.RunId,
                    rootSessionId = _root.SessionId,
                    acceptedSelectionHash = acceptedSelectionHash ??
                        CoordinationOwnerStore.HashSelection(_selection.Selection),
                    projectRevision = _selection.Selection.ProjectRevision,
                    projectConfigurationRevision = _selection.Selection.ProjectConfigurationRevision,
                    platformRuntimeRevision = _selection.Selection.PlatformRuntimeRevision,
                    contextRevision = _selection.Selection.ContextRevision,
                    fence = executionFence ?? _acceptedRoot.ExecutionFence,
                    outcomeSpecification = new { id = "outcome-1" },
                    outcomeConfirmed = dispatchable,
                    nextClarifyingQuestionIndex = 0,
                    selectedWorkflow = new { id = "workflow-1" },
                    workflowConfirmed = true,
                    confirmedWorkPlan = new { items = new[] { new { id = planItemId } } },
                    confirmedSelectionContext = (object?)null,
                    candidateWorkPlan = (object?)null,
                    candidateSelectionContext = (object?)null,
                    lastScopeDiff = (object?)null,
                    pendingGate = (object?)null,
                    decisionReceipts = Array.Empty<object>()
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".coordinator_decisions
                    (project_id, run_id, session_id, request_id, decision_id,
                     actor_issuer, actor_subject, execution_fence, state_version,
                     action_kind, idempotency_key, command_hash, decision_state, decision)
                VALUES (@project, @run, @session, @request, @decision,
                        @issuer, @subject, @fence, @version, 'test', 'runtime-owner-test',
                        @hash, 'accepted', @payload)
                """, connection);
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, _root.SessionId);
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, "runtime-owner-test");
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, Guid.NewGuid());
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, _actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, _actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, _acceptedRoot.ExecutionFence);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('a', 64));
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, envelope);
            await insert.ExecuteNonQueryAsync();
            return;
        }

        await using var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".coordinator_decisions
            SET state_version = @version
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            """, connection);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, _root.SessionId);
        update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
        await update.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadAcceptedSelectionHashAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash
            FROM "{_schema}".accepted_runs
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _root.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _root.RunId);
        return ((string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException("The accepted selection hash was not persisted."))).TrimEnd();
    }

    private async Task<long> ReadIdleNotificationCountAsync(Guid subscriptionId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".coordination_idle_notifications
            WHERE subscription_id = @subscription
            """, connection);
        command.Parameters.AddWithValue("subscription", NpgsqlDbType.Uuid, subscriptionId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<bool> ReadIdleSubscriptionActiveAsync(Guid subscriptionId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT active FROM "{_schema}".coordination_idle_subscriptions
            WHERE subscription_id = @subscription
            """, connection);
        command.Parameters.AddWithValue("subscription", NpgsqlDbType.Uuid, subscriptionId);
        return (bool)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException("Expected the idle subscription to be persisted."));
    }

    private async Task<long> ReadIdleOutboxCountAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = 'orchestrator.session.idle_notification_requested'
            """, connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
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

    private async Task<long> ReadForkOutboxCountAsync(string eventType)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = @eventType
            """, connection);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Varchar, eventType);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task WaitUntilBlockedAsync(
        NpgsqlConnection observer,
        int blockerProcessId,
        string queryPattern)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var wait = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE @locker = ANY(pg_blocking_pids(pid))
                      AND query ILIKE '%' || @pattern || '%')
                """, observer);
            wait.Parameters.AddWithValue("locker", NpgsqlDbType.Integer, blockerProcessId);
            wait.Parameters.AddWithValue("pattern", NpgsqlDbType.Text, queryPattern);
            if ((bool)(await wait.ExecuteScalarAsync(timeout.Token) ?? false))
                return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static CoordinationSessionForkRequest NewForkRequest(
        SessionIdentity source,
        string targetSessionId,
        string idempotencyKey) =>
        new(
            1,
            idempotencyKey,
            targetSessionId,
            Guid.NewGuid(),
            "committed-cursor-v1",
            CoordinationSessionKind.ChildWork);

    private static SessionForkResult NewEventsFork(
        SessionIdentity source,
        CoordinationSessionForkRequest request)
    {
        var target = new SessionIdentity(source.ProjectId, source.RunId, request.TargetSessionId);
        var lineage = new SessionForkLineage(
            source,
            request.SourceEventId,
            3,
            1,
            1,
            1,
            new string('a', 64),
            request.SourceCursor);
        return new SessionForkResult(
            new SessionRecord(target, DateTimeOffset.UtcNow, 3),
            lineage,
            IsDuplicate: false);
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
