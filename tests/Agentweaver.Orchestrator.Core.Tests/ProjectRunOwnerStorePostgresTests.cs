using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
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
    public async Task P1RequestPersistsFullReceiptAndRejectsChangedOrReusedSetupProvenance()
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
        var first = await CreateP1("p1-create", request);
        var replay = await CreateP1("p1-create", request);

        Assert.Equal(first, replay);
        Assert.Equal(receipt.InvocationId, first.P1SetupInvocationId);
        Assert.Equal(1L, await CountAsync("project_run_requests"));
        Assert.Equal(0L, await CountAsync("p1_run_request_provenance"));

        Task StartWithReceipt(P1ProjectSetupReceiptSnapshot setupReceipt) =>
            _store.StartProjectRunAsync(
                _actor, Tenant, Project, first.RunId, (_, _) => Task.CompletedTask, NoCheck,
                CancellationToken.None,
                acceptRootInTransaction: (connection, transaction, pending, token) =>
                    _store.AcceptP1InteractiveRootInTransactionAsync(
                        connection, transaction, _actor, Selection(first.RunId), pending, setupReceipt, token));

        await StartWithReceipt(receipt);
        Assert.Equal(1L, await CountAsync("p1_run_request_provenance"));
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT setup_receipt_hash, receipt_snapshot::text,
                   receipt_snapshot->>'ownerAssignmentId',
                   receipt_snapshot->>'ownerAssignmentRevision',
                   receipt_snapshot->>'ownerAuthorityAuditEventId'
            FROM "{_schema}".p1_run_request_provenance
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            query.Parameters.AddWithValue("project", Project);
            query.Parameters.AddWithValue("run", first.RunId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(CoordinationOwnerStore.HashP1SetupReceipt(receipt), reader.GetString(0).TrimEnd());
            Assert.Equal(receipt.InvocationId.ToString(), JsonDocument.Parse(reader.GetString(1))
                .RootElement.GetProperty("invocationId").GetString());
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
        }

        var changed = receipt with { OwnerAuthorityAuditEventId = Guid.NewGuid() };
        var changedError = await Assert.ThrowsAsync<CoordinationException>(() => StartWithReceipt(changed));
        Assert.Equal("p1_setup_receipt_changed", changedError.Code);

        var reused = await CreateP1("p1-reuse", request);
        var reusedError = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.StartProjectRunAsync(
                _actor, Tenant, Project, reused.RunId, (_, _) => Task.CompletedTask, NoCheck,
                CancellationToken.None,
                acceptRootInTransaction: (connection, transaction, pending, token) =>
                    _store.AcceptP1InteractiveRootInTransactionAsync(
                        connection, transaction, _actor, Selection(reused.RunId), pending, receipt, token)));
        Assert.Equal("p1_setup_invocation_conflict", reusedError.Code);
        Assert.Equal(2L, await CountAsync("project_run_requests"));
        Assert.Equal(1L, await CountAsync("p1_run_request_provenance"));

        await using var guardConnection = await _fixture.DataSource.OpenConnectionAsync();
        foreach (var statement in new[]
        {
            $"""UPDATE "{_schema}".p1_run_request_provenance SET setup_receipt_hash = repeat('0', 64)""",
            $"""DELETE FROM "{_schema}".p1_run_request_provenance""",
            $"""TRUNCATE "{_schema}".p1_run_request_provenance CASCADE"""
        })
            await using (var guarded = new NpgsqlCommand(statement, guardConnection))
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() => guarded.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RaiseException, denied.SqlState);
                Assert.Equal("P1 invocation provenance is append-only", denied.MessageText);
            }
    }

    [Fact]
    public async Task P1RequestIsCreatedBeforeReceiptVerificationAndRejectsForeignReceiptBeforeRootAcceptance()
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);

        var row = await CreateP1("p1-deferred", request);
        Assert.Equal(receipt.InvocationId, row.P1SetupInvocationId);
        Assert.Equal(1L, await CountAsync("project_run_requests"));
        Assert.Equal(0L, await CountAsync("p1_run_request_provenance"));

        var foreign = receipt with { Subject = "foreign-actor" };
        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.StartProjectRunAsync(
                _actor, Tenant, Project, row.RunId, (_, _) => Task.CompletedTask, NoCheck,
                CancellationToken.None,
                acceptRootInTransaction: (connection, transaction, pending, token) =>
                    _store.AcceptP1InteractiveRootInTransactionAsync(
                        connection, transaction, _actor, Selection(row.RunId), pending, foreign, token)));

        Assert.Equal("p1_setup_receipt_stale", error.Code);
        Assert.Equal(0L, await CountAsync("p1_run_request_provenance"));
        Assert.Equal(0L, await CountAsync("accepted_runs"));
        Assert.Equal(0L, await CountAsync("coordination_sessions"));
        Assert.Equal(0L, await CountAsync("coordinator_decisions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
    }

    [Fact]
    public async Task P1RootDecisionAndBindingCommitTogetherAndDeliveryRetriesAfterCommit()
    {
        var receipt = P1Receipt() with
        {
            OwnerAssignmentId = Guid.NewGuid(),
            OwnerAssignmentRevision = 2,
            OwnerAuthorityAuditEventId = Guid.NewGuid()
        };
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
        var row = await CreateP1("p1-accept", request);
        var selection = Selection(row.RunId);
        var sends = 0;
        Task Bind(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending,
            CancellationToken token) =>
            _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);

        await Assert.ThrowsAsync<HttpRequestException>(() => _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId,
            (_, _) =>
            {
                sends++;
                throw new HttpRequestException("Events did not confirm delivery.");
            },
            NoCheck, CancellationToken.None, acceptRootInTransaction: Bind));
        Assert.Equal(1, sends);
        Assert.Equal(1L, await CountAsync("accepted_runs"));
        Assert.Equal(1L, await CountAsync("coordination_sessions"));
        Assert.Equal(1L, await CountAsync("coordinator_decisions"));
        Assert.Equal(1L, await CountAsync("p1_invocation_bindings"));
        Assert.Equal("awaiting_binding", await ReadRequestStateAsync(row.RunId));

        var started = await _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId,
            (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            },
            NoCheck, CancellationToken.None, acceptRootInTransaction: Bind);
        Assert.Equal("started", started.State);
        Assert.Equal(2, sends);
        Assert.Equal("started", await ReadRequestStateAsync(row.RunId));

        var binding = await _store.ReadCurrentP1InvocationBindingAsync(_actor, selection, CancellationToken.None);
        Assert.Equal(receipt.InvocationId, binding.SetupInvocationId);
        Assert.Equal(CoordinationOwnerStore.HashP1SetupReceipt(receipt), binding.SetupReceiptHash);
        Assert.Equal(row.RunId, binding.RunId);
        Assert.Equal(row.RootSessionId, binding.RootSessionId);
        Assert.Equal(_actor.Issuer, binding.ActorIssuer);
        Assert.Equal(_actor.Subject, binding.ActorSubject);
        Assert.Equal(1L, await CountAsync("p1_invocation_bindings"));

        await using var guardConnection = await _fixture.DataSource.OpenConnectionAsync();
        foreach (var statement in new[]
        {
            $"""UPDATE "{_schema}".p1_invocation_bindings SET execution_fence = 2""",
            $"""DELETE FROM "{_schema}".p1_invocation_bindings""",
            $"""TRUNCATE "{_schema}".p1_invocation_bindings"""
        })
            await using (var guarded = new NpgsqlCommand(statement, guardConnection))
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() => guarded.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.RaiseException, denied.SqlState);
                Assert.Equal("P1 invocation provenance is append-only", denied.MessageText);
            }
    }

    [Fact]
    public async Task P1RootAcceptanceRejectsChangedSelectionActorAndTenantBeforeAnyRootWrite()
    {
        var cases = new[]
        {
            (Key: "p1-changed-selection", Change: (Func<AuthorizedRunSelection, AuthorizedRunSelection>)(selection =>
                selection with
                {
                    Selection = selection.Selection with { ProjectConfigurationRevision = 2 }
                })),
            (Key: "p1-changed-actor", Change: (Func<AuthorizedRunSelection, AuthorizedRunSelection>)(selection =>
                selection with
                {
                    Authorization = selection.Authorization with { ActorId = "foreign-actor" }
                })),
            (Key: "p1-changed-tenant", Change: (Func<AuthorizedRunSelection, AuthorizedRunSelection>)(selection =>
                selection with
                {
                    Authorization = selection.Authorization with { TenantId = "foreign-tenant" }
                }))
        };
        foreach (var testCase in cases)
        {
            var receipt = P1Receipt();
            var request = new CreateProjectRunRequest(
                "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
            var row = await CreateP1(testCase.Key, request);
            var selection = testCase.Change(Selection(row.RunId));
            Task Bind(
                Npgsql.NpgsqlConnection connection,
                Npgsql.NpgsqlTransaction transaction,
                CoordinationOwnerStore.ProjectRunRequestRow pending,
                CancellationToken token) =>
                _store.AcceptP1InteractiveRootInTransactionAsync(
                    connection, transaction, _actor, selection, pending, receipt, token);

            var error = await Assert.ThrowsAsync<CoordinationException>(() => _store.StartProjectRunAsync(
                _actor, Tenant, Project, row.RunId, NoDelivery, NoCheck, CancellationToken.None,
                acceptRootInTransaction: Bind));
            Assert.Equal("p1_setup_receipt_stale", error.Code);
        }

        Assert.Equal(0L, await CountAsync("accepted_runs"));
        Assert.Equal(0L, await CountAsync("coordination_sessions"));
        Assert.Equal(0L, await CountAsync("coordinator_decisions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
        Assert.Equal(3L, await CountAsync("project_run_requests"));
        Assert.Equal(0L, await CountAsync("p1_run_request_provenance"));
    }

    [Fact]
    public async Task ConcurrentP1StartsReuseOneAtomicBindingWithoutRequestLockDeadlock()
    {
        var receipt = P1Receipt();
        var row = await CreateP1("p1-concurrent",
            new CreateProjectRunRequest("Concurrent start", 1, P1SetupInvocationId: receipt.InvocationId));
        var selection = Selection(row.RunId);
        Task Bind(NpgsqlConnection connection, NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending, CancellationToken token) =>
            _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            _store.StartProjectRunAsync(
                _actor, Tenant, Project, row.RunId, (_, _) => Task.CompletedTask,
                NoCheck, CancellationToken.None, acceptRootInTransaction: Bind)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(results, result => Assert.Equal("started", result.State));
        Assert.Equal(1L, await CountAsync("accepted_runs"));
        Assert.Equal(1L, await CountAsync("coordinator_decisions"));
        Assert.Equal(1L, await CountAsync("p1_invocation_bindings"));
    }

    [Fact]
    public async Task CurrentP1BindingRejectsChangedAuthoritySelectionAndExecutionFence()
    {
        var receipt = P1Receipt();
        var row = await CreateP1("p1-current",
            new CreateProjectRunRequest("Read current binding", 1, P1SetupInvocationId: receipt.InvocationId));
        var selection = Selection(row.RunId);
        Task Bind(NpgsqlConnection connection, NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending, CancellationToken token) =>
            _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);
        await _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId, (_, _) => Task.CompletedTask,
            NoCheck, CancellationToken.None, acceptRootInTransaction: Bind);
        var original = await _store.ReadCurrentP1InvocationBindingAsync(_actor, selection, CancellationToken.None);
        foreach (var changed in new[]
        {
            selection with { Authorization = selection.Authorization with { MembershipRevision = 2 } },
            selection with { Authorization = selection.Authorization with { ActorId = "another-actor" } },
            selection with { Authorization = selection.Authorization with { TenantId = "another-tenant" } },
            selection with { Selection = selection.Selection with { ProjectConfigurationRevision = 2 } },
        })
        {
            var error = await Assert.ThrowsAsync<CoordinationException>(() =>
                _store.ReadCurrentP1InvocationBindingAsync(_actor, changed, CancellationToken.None));
            Assert.Equal("p1_invocation_binding_stale", error.Code);
        }
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs SET execution_fence = execution_fence + 1
            WHERE project_id = @project AND run_id = @run
            """, connection);
        update.Parameters.AddWithValue("project", Project);
        update.Parameters.AddWithValue("run", row.RunId);
        await update.ExecuteNonQueryAsync();
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.ReadCurrentP1InvocationBindingAsync(_actor, selection, CancellationToken.None));
        Assert.Equal("p1_invocation_binding_stale", stale.Code);
        Assert.NotEqual(Guid.Empty, original.CoreInvocationId);
    }

    [Fact]
    public async Task P1AcceptanceDoesNotEnrollAnAlreadyAcceptedRootWithoutItsBinding()
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
        var row = await CreateP1("p1-no-posthoc", request);
        var selection = Selection(row.RunId);
        await _store.AcceptRootAsync(_actor, selection, row.RootSessionId, CancellationToken.None);
        Task Bind(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending,
            CancellationToken token) =>
            _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);

        var error = await Assert.ThrowsAsync<CoordinationException>(() => _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId, NoDelivery, NoCheck, CancellationToken.None,
            acceptRootInTransaction: Bind));

        Assert.Equal("p1_invocation_binding_missing", error.Code);
        Assert.Equal(1L, await CountAsync("accepted_runs"));
        Assert.Equal(1L, await CountAsync("coordination_sessions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
        Assert.Equal("awaiting_binding", await ReadRequestStateAsync(row.RunId));
    }

    [Fact]
    public async Task ConcurrentLegacyAcceptanceCannotEnrollAP1BindingAfterTheRootCommits()
    {
        var receipt = P1Receipt();
        var row = await CreateP1("p1-legacy-race",
            new CreateProjectRunRequest("Race acceptance", 1, P1SetupInvocationId: receipt.InvocationId));
        var selection = Selection(row.RunId);
        var application = "p1-race-" + Guid.NewGuid().ToString("N");
        var lockId = Guid.NewGuid().GetHashCode();
        await using var control = await _fixture.DataSource.OpenConnectionAsync();
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".pause_p1_acceptance() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_setting('application_name') = '{application}' THEN
                    PERFORM pg_advisory_xact_lock({lockId});
                END IF;
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER pause_p1_acceptance BEFORE INSERT ON "{_schema}".accepted_runs
                FOR EACH ROW EXECUTE FUNCTION "{_schema}".pause_p1_acceptance();
            SELECT pg_advisory_lock({lockId});
            """, control))
            await install.ExecuteNonQueryAsync();
        async Task Bind(NpgsqlConnection connection, NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending, CancellationToken token)
        {
            await using var set = new NpgsqlCommand(
                $"SET LOCAL application_name = '{application}'", connection, transaction);
            await set.ExecuteNonQueryAsync(token);
            await _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);
        }
        var accepting = _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId, NoDelivery, NoCheck, CancellationToken.None,
            acceptRootInTransaction: Bind);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                        WHERE application_name = @application AND wait_event = 'advisory')
                    """, control);
                waiting.Parameters.AddWithValue("application", application);
                if ((bool)(await waiting.ExecuteScalarAsync(timeout.Token))!)
                    break;
                await Task.Delay(20, timeout.Token);
            }
            await _store.AcceptRootAsync(_actor, selection, row.RootSessionId, timeout.Token);
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({lockId})", control);
            await release.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<CoordinationException>(() => accepting);
        Assert.Equal("p1_invocation_binding_missing", error.Code);
        Assert.Equal(1L, await CountAsync("accepted_runs"));
        Assert.Equal(0L, await CountAsync("coordinator_decisions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
    }

    [Theory]
    [InlineData("coordinator_decisions")]
    [InlineData("p1_invocation_bindings")]
    public async Task P1RootAcceptanceRollsBackRootDecisionAndBindingOnOwnerWriteFailure(string table)
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
        var row = await CreateP1("p1-rollback", request);
        var selection = Selection(row.RunId);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".reject_test_decision() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'decision fixture failure';
            END
            $$;
            CREATE TRIGGER reject_test_decision BEFORE INSERT ON "{_schema}"."{table}"
                FOR EACH ROW EXECUTE FUNCTION "{_schema}".reject_test_decision()
            """, connection))
            await install.ExecuteNonQueryAsync();
        Task Bind(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending,
            CancellationToken token) =>
            _store.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, selection, pending, receipt, token);

        await Assert.ThrowsAsync<PostgresException>(() => _store.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId, NoDelivery, NoCheck, CancellationToken.None,
            acceptRootInTransaction: Bind));
        Assert.Equal(0L, await CountAsync("accepted_runs"));
        Assert.Equal(0L, await CountAsync("coordination_sessions"));
        Assert.Equal(0L, await CountAsync("coordinator_decisions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
        Assert.Equal(1L, await CountAsync("project_run_requests"));
        Assert.Equal(0L, await CountAsync("p1_run_request_provenance"));
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
        var ownerMessageIds = new List<Guid>();
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
    public async Task StartReplayValidatesTheSavedRequestWithoutRedelivering()
    {
        var row = await Create("validate-replay", new CreateProjectRunRequest("Saved prompt", 1));
        var deliveries = 0;
        await Start(row.RunId, (_, _) =>
        {
            deliveries++;
            return Task.CompletedTask;
        }, request => Assert.Equal("Saved prompt", request.FirstMessage));

        var replay = await Start(row.RunId, (_, _) =>
        {
            deliveries++;
            return Task.CompletedTask;
        }, request => Assert.Equal("Saved prompt", request.FirstMessage));
        Assert.Equal("started", replay.State);
        Assert.Equal(1, deliveries);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            Start(row.RunId, NoDelivery, request =>
                CastingGenerationExecutionContract.RequireProjectRunStart(
                    request, row.RunId, row.RootSessionId, "Changed prompt")));
        Assert.Equal("casting_generation_run_conflict", conflict.Code);
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

    [Fact]
    public async Task CancelCapturesStartupCleanupAndExactReplayDoesNotReopenCompletedCleanup()
    {
        var row = await Create("cancel-startup-cleanup", new CreateProjectRunRequest("Hello", 1));
        var runSelection = Selection(row.RunId);
        await Start(row.RunId, async (pending, token) =>
        {
            await _store.AcceptRootAsync(_actor, runSelection, pending.RootSessionId, token);
            await _store.SendMessageAsync(_actor,
                new SessionIdentity(Project, row.RunId, pending.RootSessionId),
                FirstMessage(pending, row.RunId, pending.FirstMessage, pending.WorkflowId), token);
        });
        await InsertRuntimeStartIntentAsync(row);

        var selection = CancellationSelection(row.RunId, _actor);
        var request = new CancelProjectRunRequest("Stopped");
        var response = await _store.CancelProjectRunAsync(
            _actor, selection, "cleanup-key", request, NoCheck, CancellationToken.None);
        Assert.False(response.AlreadyTerminal);
        var pendingCleanup = await ReadRuntimeStartCleanupAsync(row.RunId);
        Assert.Equal("cleanup_pending", pendingCleanup.State);
        Assert.True(pendingCleanup.Obligation);
        var committedOutboxCount = await CountAsync("outbox_events");

        Assert.Equal(response, await _store.CancelProjectRunAsync(
            _actor, selection, "cleanup-key", request, NoCheck, CancellationToken.None));
        Assert.Equal(pendingCleanup, await ReadRuntimeStartCleanupAsync(row.RunId));
        Assert.Equal(committedOutboxCount, await CountAsync("outbox_events"));

        await SetRuntimeStartCleanupCompleteAsync(row.RunId);
        var complete = await ReadRuntimeStartCleanupAsync(row.RunId);
        Assert.Equal("cleanup_complete", complete.State);
        Assert.True(complete.Obligation);
        Assert.Equal(response, await _store.CancelProjectRunAsync(
            _actor, selection, "cleanup-key", request, NoCheck, CancellationToken.None));
        Assert.Equal(complete, await ReadRuntimeStartCleanupAsync(row.RunId));
        Assert.Equal(committedOutboxCount, await CountAsync("outbox_events"));
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

    /// <summary>Terminal outcomes are preserved while their resources are marked for cleanup.</summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("indeterminate")]
    public async Task AlreadyEndedRunPreservesOutcomeAndRequestsCleanup(string state)
    {
        var row = await Create("ended", new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await InsertRuntimeStartIntentAsync(row);
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
        var cleanup = await ReadRuntimeStartCleanupAsync(row.RunId);
        Assert.True(cleanup.Obligation);
        Assert.Equal("cleanup_pending", cleanup.State);
        var replay = await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor),
            "terminal", new(), NoCheck, CancellationToken.None);
        Assert.Equal(result, replay);
        Assert.Equal(cleanup, await ReadRuntimeStartCleanupAsync(row.RunId));
        Assert.Equal(0L, await CountAsync("outbox_events"));
    }

    /// <summary>Runs completed by older code stay completed; cancel never rewrites a completed outcome.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelKeepsALegacyCompletedRunCompleted(bool completedAtOnly)
    {
        var row = await Create("legacy-completed", new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var legacy = new NpgsqlCommand(completedAtOnly
            ? $"UPDATE \"{_schema}\".accepted_runs SET completed_at = now() WHERE run_id = @run"
            : $"""
              UPDATE "{_schema}".coordination_sessions SET lifecycle_state = 'completed'
              WHERE run_id = @run AND parent_session_id IS NULL
              """, connection))
        {
            legacy.Parameters.AddWithValue("run", row.RunId);
            Assert.Equal(1, await legacy.ExecuteNonQueryAsync());
        }
        var before = await ReadExecutionAsync(row.RunId);
        Assert.Equal("idle", before.State);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var direct = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs SET execution_state = 'cancelled' WHERE run_id = @run
            """, connection))
        {
            direct.Parameters.AddWithValue("run", row.RunId);
            var guarded = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync());
            Assert.Equal("run_terminal_state_final", guarded.MessageText);
        }

        var result = await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor),
            "legacy", new(), NoCheck, CancellationToken.None);
        Assert.Equal("completed", result.State);
        Assert.True(result.AlreadyTerminal);
        Assert.Null(result.CancelledAt);
        var after = await ReadExecutionAsync(row.RunId);
        Assert.Equal(("completed", before.Fence), (after.State, after.Fence));

        var cleanup = await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor),
            "legacy-cleanup", new() { ExpectedTerminalState = "completed", ExpectedTerminalFence = before.Fence },
            NoCheck, CancellationToken.None);
        Assert.Equal("completed", cleanup.State);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var direct = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs SET execution_state = 'cancelled' WHERE run_id = @run
            """, connection))
        {
            direct.Parameters.AddWithValue("run", row.RunId);
            var guarded = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync());
            Assert.Equal("run_terminal_state_final", guarded.MessageText);
        }
    }

    [Fact]
    public async Task TerminalCleanupBlocksNewAndStaleSnapshotRecoveryWithoutAStartupIntent()
    {
        var row = await CreateFailedRunAsync("cleanup-recovery");
        var before = await ReadExecutionAsync(row.RunId);
        await using var staleConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var stale = await staleConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var snapshot = new NpgsqlCommand(
            $"SELECT execution_fence FROM \"{_schema}\".accepted_runs WHERE run_id = @run",
            staleConnection, stale))
        {
            snapshot.Parameters.AddWithValue("run", row.RunId);
            Assert.Equal(before.Fence, (long)(await snapshot.ExecuteScalarAsync())!);
        }

        var request = new CancelProjectRunRequest
        {
            ExpectedTerminalState = "failed",
            ExpectedTerminalFence = before.Fence
        };
        var receipt = await _store.CancelProjectRunAsync(
            _actor, CancellationSelection(row.RunId, _actor), "cleanup", request, NoCheck, CancellationToken.None);
        Assert.True(receipt.AlreadyTerminal);
        Assert.Equal("failed", receipt.State);
        Assert.Equal(before, await ReadExecutionAsync(row.RunId));
        Assert.Equal(0L, await CountAsync("runtime_run_start_intents"));

        await using (var staleUpdate = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_state = 'idle', execution_fence = execution_fence + 1
            WHERE run_id = @run
            """, staleConnection, stale))
        {
            staleUpdate.Parameters.AddWithValue("run", row.RunId);
            var staleFailure = await Assert.ThrowsAsync<PostgresException>(() => staleUpdate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.SerializationFailure, staleFailure.SqlState);
        }

        await using (var current = await _fixture.DataSource.OpenConnectionAsync())
        await using (var oldWriter = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_state = 'idle', execution_fence = execution_fence + 1
            WHERE run_id = @run
            """, current))
        {
            oldWriter.Parameters.AddWithValue("run", row.RunId);
            var guarded = await Assert.ThrowsAsync<PostgresException>(() => oldWriter.ExecuteNonQueryAsync());
            Assert.Equal("runtime_start_cleanup_recovery_blocked", guarded.MessageText);
        }
        var recovery = new RecoverRunExecutionRequest(before.Fence, before.Version, "new-recovery", "operator", "ref");
        var blocked = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RecoverRunExecutionAsync(_actor, Project, row.RunId, recovery, CancellationToken.None));
        Assert.Equal("runtime_start_cleanup_recovery_blocked", blocked.Code);
    }

    [Fact]
    public async Task CleanupExpectationCannotCancelARecoveredRunAndRecoveryReplayRemainsStable()
    {
        var row = await CreateFailedRunAsync("recovery-wins");
        var failed = await ReadExecutionAsync(row.RunId);
        var recovery = new RecoverRunExecutionRequest(failed.Fence, failed.Version, "recover", "operator", "ref");
        var store = new CoordinationOwnerStore(_fixture.DataSource, _schema, TimeProvider.System);
        var recovered = await store.RecoverRunExecutionAsync(
            _actor, Project, row.RunId, recovery, CancellationToken.None);
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor), "stale",
                new CancelProjectRunRequest
                {
                    ExpectedTerminalState = "failed",
                    ExpectedTerminalFence = failed.Fence
                }, NoCheck, CancellationToken.None));
        Assert.Equal("terminal_cleanup_expectation_stale", stale.Code);
        Assert.Equal("idle", (await ReadExecutionAsync(row.RunId)).State);
        Assert.Equal(0L, await CountAsync("project_run_cancellations"));

        await SetFailedAsync(row.RunId);
        var failedAgain = await ReadExecutionAsync(row.RunId);
        await _store.CancelProjectRunAsync(_actor, CancellationSelection(row.RunId, _actor), "cleanup",
            new CancelProjectRunRequest
            {
                ExpectedTerminalState = "failed",
                ExpectedTerminalFence = failedAgain.Fence
            }, NoCheck, CancellationToken.None);
        var replay = await store.RecoverRunExecutionAsync(
            _actor, Project, row.RunId, recovery, CancellationToken.None);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(recovered.OperationId, replay.OperationId);
        Assert.Equal("failed", (await ReadExecutionAsync(row.RunId)).State);
    }

    [Fact]
    public async Task HistoricalFailureReplayCannotAdmitCleanupForANewerFence()
    {
        Assert.Equal("""{"reason":null}""",
            JsonSerializer.Serialize(new CancelProjectRunRequest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var row = await CreateFailedRunAsync("legacy-replay");
        await InsertRuntimeStartIntentAsync(row);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var remove = new NpgsqlCommand($"""
            DROP TRIGGER accepted_run_terminal_cleanup_guard ON "{_schema}".accepted_runs;
            DROP FUNCTION "{_schema}".guard_terminal_cleanup_recovery();
            DELETE FROM "{_schema}".coordination_schema_migrations WHERE version = 47;
            """, connection))
            await remove.ExecuteNonQueryAsync();
        var selection = CancellationSelection(row.RunId, _actor);
        var original = await _store.CancelProjectRunAsync(
            _actor, selection, "legacy", new(), NoCheck, CancellationToken.None);
        Assert.True(original.AlreadyTerminal);
        Assert.False((await ReadRuntimeStartCleanupAsync(row.RunId)).Obligation);
        var failed = await ReadExecutionAsync(row.RunId);
        var store = new CoordinationOwnerStore(_fixture.DataSource, _schema, TimeProvider.System);
        await store.RecoverRunExecutionAsync(_actor, Project, row.RunId,
            new RecoverRunExecutionRequest(failed.Fence, failed.Version, "recover-legacy", "operator", "ref"),
            CancellationToken.None);
        await SetFailedAsync(row.RunId);
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);

        Assert.Equal(original, await _store.CancelProjectRunAsync(
            _actor, selection, "legacy", new(), NoCheck, CancellationToken.None));
        Assert.False((await ReadRuntimeStartCleanupAsync(row.RunId)).Obligation);
        await _store.CancelProjectRunAsync(_actor, selection, "fresh", new(), NoCheck, CancellationToken.None);
        Assert.True((await ReadRuntimeStartCleanupAsync(row.RunId)).Obligation);
    }

    [Fact]
    public async Task DisabledGuardBlocksVersionTwoButPreservesLegacyCancelledCleanup()
    {
        var row = await Create("disabled-guard", new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await InsertRuntimeStartIntentAsync(row);
        var selection = CancellationSelection(row.RunId, _actor);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var disable = new NpgsqlCommand($"""
            ALTER TABLE "{_schema}".accepted_runs DISABLE TRIGGER accepted_run_terminal_cleanup_guard
            """, connection))
            await disable.ExecuteNonQueryAsync();

        var cancelled = await _store.CancelProjectRunAsync(
            _actor, selection, "legacy", new(), NoCheck, CancellationToken.None);
        Assert.Equal("cancelled", cancelled.State);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var legacy = await starts.ReadCleanupProjectionAsync(
            Project, row.RunId, row.RootSessionId, selection, CancellationToken.None);
        Assert.Equal(1, legacy.ContractVersion);
        Assert.True(legacy.CancellationFence > legacy.ExecutionFence);
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            starts.ReadCleanupProjectionAsync(
                Project, row.RunId, row.RootSessionId, selection, CancellationToken.None, contractVersion: 2));
        Assert.Equal("runtime_start_cleanup_guard_unavailable", failure.Code);
        Assert.Equal(cancelled, await _store.CancelProjectRunAsync(
            _actor, selection, "legacy", new(), NoCheck, CancellationToken.None));

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var sameFence = new NpgsqlCommand($"""
            UPDATE "{_schema}".runtime_run_start_intents SET execution_fence = @fence
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            sameFence.Parameters.AddWithValue("project", Project);
            sameFence.Parameters.AddWithValue("run", row.RunId);
            sameFence.Parameters.AddWithValue("fence", legacy.CancellationFence);
            await sameFence.ExecuteNonQueryAsync();
        }
        Assert.Equal(cancelled, await _store.CancelProjectRunAsync(
            _actor, selection, "legacy", new(), NoCheck, CancellationToken.None));
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            starts.ReadCleanupProjectionAsync(
                Project, row.RunId, row.RootSessionId, selection, CancellationToken.None));
        Assert.Equal("runtime_start_cleanup_fence_stale", stale.Code);
    }

    [Fact]
    public async Task CleanupReadsWorkOnSchemasBeforeTheGenerationJobAndStartIntentTables()
    {
        var row = await CreateFailedRunAsync("older-schema");
        await InsertRuntimeStartIntentAsync(row);
        var selection = CancellationSelection(row.RunId, _actor);
        await _store.CancelProjectRunAsync(_actor, selection, "cleanup", new(), NoCheck, CancellationToken.None);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);

        await DropTableAsync("workflow_generation_jobs");
        Assert.Equal("failed", (await starts.ReadCleanupProjectionAsync(
            Project, row.RunId, row.RootSessionId, selection, CancellationToken.None, contractVersion: 2)).RunState);
        Assert.NotNull(await starts.ReadCleanupSummaryAsync(Tenant, Project, row.RunId, CancellationToken.None));

        await DropTableAsync("runtime_run_start_intents");
        Assert.Null(await starts.ReadCleanupSummaryAsync(Tenant, Project, row.RunId, CancellationToken.None));
        Assert.Empty(await starts.FindCleanupIntentsAsync(Tenant, Project, row.RunId, CancellationToken.None));
        var unavailable = await Assert.ThrowsAsync<CoordinationException>(() => starts.ReadCleanupProjectionAsync(
            Project, row.RunId, row.RootSessionId, selection, CancellationToken.None));
        Assert.Equal("runtime_start_cleanup_unavailable", unavailable.Code);
    }

    [Fact]
    public async Task ChildStartIntentRequiresTheTerminalCleanupGuard()
    {
        var row = await Create("child-schema", new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand(
            $"DELETE FROM \"{_schema}\".coordination_schema_migrations WHERE version = 47", connection))
            await drop.ExecuteNonQueryAsync();

        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        await using var write = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await write.BeginTransactionAsync();
        var pending = await Assert.ThrowsAsync<CoordinationException>(() => starts.PersistChildIntentInTransactionAsync(
            write, transaction, Tenant, Project, row.RunId, new string('a', 64), 1,
            new RuntimeChildStartBinding(row.RootSessionId, "child-1", "plan-1", "association-1", null, "step-1",
                "worker", "model-ref", _actor.Issuer, _actor.Subject, "context-1", 1, 1, Guid.NewGuid(),
                Guid.NewGuid(), new string('b', 64)), CancellationToken.None));
        Assert.Equal("runtime_child_start_schema_pending", pending.Code);
        Assert.Equal(503, pending.StatusCode);
    }

    private async Task DropTableAsync(string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP TABLE \"{_schema}\".{table} CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RootCleanupWaitsForTrackedChildrenAndUnmatchedActiveRegistrations()
    {
        var row = await CreateFailedRunAsync("aggregate-cleanup");
        await InsertRuntimeStartIntentAsync(row);
        const string child = "legacy-child";
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, parent_session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, @child, @root, @root, 'child_work', @issuer, @actor, 1)
            """, connection))
        {
            insert.Parameters.AddWithValue("project", Project);
            insert.Parameters.AddWithValue("run", row.RunId);
            insert.Parameters.AddWithValue("child", child);
            insert.Parameters.AddWithValue("root", row.RootSessionId);
            insert.Parameters.AddWithValue("issuer", _actor.Issuer);
            insert.Parameters.AddWithValue("actor", _actor.Subject);
            await insert.ExecuteNonQueryAsync();
        }
        var selection = CancellationSelection(row.RunId, _actor);
        await _store.CancelProjectRunAsync(_actor, selection, "cleanup", new(), NoCheck, CancellationToken.None);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        async Task<RuntimeRunStartCleanupProjection> RootProjection() =>
            await starts.ReadCleanupProjectionAsync(
                Project, row.RunId, row.RootSessionId, selection, CancellationToken.None, contractVersion: 2);
        // A child session without a start intent had no runtime started by this path.
        Assert.Equal("failed", (await RootProjection()).RunState);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id, state, current_step, cleanup_obligation, receipt_links)
            VALUES (@tenant, @project, @run, @child, @selection, 1, @intent, @bootstrap,
                    @consume, @exchange, 'cleanup_pending', 'cleanup', true,
                    jsonb_build_object('childStart', jsonb_build_object()))
            """, connection))
        {
            insert.Parameters.AddWithValue("tenant", Tenant);
            insert.Parameters.AddWithValue("project", Project);
            insert.Parameters.AddWithValue("run", row.RunId);
            insert.Parameters.AddWithValue("child", child);
            insert.Parameters.AddWithValue("selection",
                CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant());
            insert.Parameters.AddWithValue("intent", Guid.NewGuid());
            insert.Parameters.AddWithValue("bootstrap", Guid.NewGuid());
            insert.Parameters.AddWithValue("consume", Guid.NewGuid());
            insert.Parameters.AddWithValue("exchange", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }
        Assert.Equal("runtime_start_child_cleanup_pending",
            (await Assert.ThrowsAsync<CoordinationException>(RootProjection)).Code);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var complete = new NpgsqlCommand($"""
            UPDATE "{_schema}".runtime_run_start_intents SET state = 'cleanup_complete'
            WHERE project_id = @project AND run_id = @run AND root_session_id = @child
            """, connection))
        {
            complete.Parameters.AddWithValue("project", Project);
            complete.Parameters.AddWithValue("run", row.RunId);
            complete.Parameters.AddWithValue("child", child);
            await complete.ExecuteNonQueryAsync();
        }
        Assert.Equal("failed", (await RootProjection()).RunState);

        var runtime = Guid.NewGuid();
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".runtime_registration_heads(runtime_instance_id, binding_hash, current_revision)
            VALUES (@runtime, repeat('d', 64), 1);
            INSERT INTO "{_schema}".runtime_registration_revisions
                (runtime_instance_id, revision, binding_json, binding_hash, state, expires_at, recorded_at)
            VALUES (@runtime, 1,
                    jsonb_build_object('ProjectId', @project, 'RunId', @run, 'SessionId', @child)::text,
                    repeat('d', 64), 0, clock_timestamp() + interval '1 hour', clock_timestamp())
            """, connection))
        {
            insert.Parameters.AddWithValue("runtime", runtime);
            insert.Parameters.AddWithValue("project", Project);
            insert.Parameters.AddWithValue("run", row.RunId);
            insert.Parameters.AddWithValue("child", child);
            await insert.ExecuteNonQueryAsync();
        }
        Assert.Equal("runtime_start_child_cleanup_pending",
            (await Assert.ThrowsAsync<CoordinationException>(RootProjection)).Code);
        var summary = await starts.ReadCleanupSummaryAsync(Tenant, Project, row.RunId, CancellationToken.None);
        Assert.Equal("pending", summary!.State);
        Assert.True(summary.Pending > 0);
    }

    private async Task<CoordinationOwnerStore.ProjectRunRequestRow> CreateFailedRunAsync(string key)
    {
        var row = await Create(key, new CreateProjectRunRequest("Hello", 1));
        await _store.AcceptRootAsync(_actor, Selection(row.RunId), row.RootSessionId, CancellationToken.None);
        await SetFailedAsync(row.RunId);
        return row;
    }

    private async Task SetFailedAsync(string runId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_state = 'failed', execution_cause_code = 'fixture_failure',
                execution_reference = 'fixture_reference'
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(string State, long Fence, long Version)> ReadExecutionAsync(string runId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT execution_state, execution_fence, state_version
            FROM "{_schema}".accepted_runs WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private Task<CoordinationOwnerStore.ProjectRunRequestRow> Create(string key, CreateProjectRunRequest request) =>
        _store.CreateProjectRunAsync(_actor, Tenant, Project, key, request, CancellationToken.None);

    private Task<CoordinationOwnerStore.ProjectRunRequestRow> CreateP1(
        string key,
        CreateProjectRunRequest request) =>
        _store.CreateProjectRunAsync(_actor, Tenant, Project, key, request, CancellationToken.None);

    private Task<StartProjectRunResponse> Start(
        string runId,
        Func<CoordinationOwnerStore.ProjectRunRequestRow, CancellationToken, Task> deliver,
        Action<CoordinationOwnerStore.ProjectRunRequestRow>? validateRequest = null) =>
        _store.StartProjectRunAsync(
            _actor, Tenant, Project, runId, deliver, NoCheck, CancellationToken.None, validateRequest);

    private async Task InsertRuntimeStartIntentAsync(
        CoordinationOwnerStore.ProjectRunRequestRow row)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id)
            SELECT @tenant, @project, @run, @root, lower(accepted_selection_hash), execution_fence,
                   @intent, @bootstrap, @consume, @exchange
            FROM "{_schema}".accepted_runs WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", row.RunId);
        command.Parameters.AddWithValue("root", row.RootSessionId);
        command.Parameters.AddWithValue("intent", Guid.NewGuid());
        command.Parameters.AddWithValue("bootstrap", Guid.NewGuid());
        command.Parameters.AddWithValue("consume", Guid.NewGuid());
        command.Parameters.AddWithValue("exchange", Guid.NewGuid());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(string State, bool Obligation, long Version)> ReadRuntimeStartCleanupAsync(string runId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT state, cleanup_obligation, row_version
            FROM "{_schema}".runtime_run_start_intents
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2));
    }

    private async Task SetRuntimeStartCleanupCompleteAsync(string runId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".runtime_run_start_intents
            SET state = 'cleanup_complete', cleanup_obligation = true, row_version = row_version + 1
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

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

    private async Task<string> ReadRequestStateAsync(string runId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT state FROM "{_schema}".project_run_requests
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private P1ProjectSetupReceiptSnapshot P1Receipt() =>
        new(Guid.NewGuid(), _actor.Issuer, _actor.Subject, Tenant, Guid.NewGuid(), 1,
            Project, _actor.Subject, 1, 1, Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow);

    /// <summary>Creates the Projects-authorized selection that the interactive selection helper would return.</summary>
    private AuthorizedRunSelection Selection(string runId)
    {
        using var snapshot = JsonDocument.Parse(
            """
            {
              "source": "projects-config",
              "modelSelection": {"reference": "model:provider/alpha"},
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              }
            }
            """);
        var selection = new EffectiveRunSelection(Project, runId, 1, 1, 1, "context-v1", snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(selection, new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, Tenant, 1, Project, runId,
            ImmutableArray.Create(new ProjectsAuthority("project", Project,
                ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }
}
