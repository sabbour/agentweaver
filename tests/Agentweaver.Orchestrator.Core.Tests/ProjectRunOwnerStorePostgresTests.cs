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
    public async Task P1RequestPersistsFullReceiptAndRejectsChangedOrReusedSetupProvenance()
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);
        var first = await CreateP1("p1-create", request, receipt);
        var replay = await CreateP1("p1-create", request, receipt);

        Assert.Equal(first, replay);
        Assert.Equal(receipt.InvocationId, first.P1SetupInvocationId);
        Assert.Equal(1L, await CountAsync("project_run_requests"));
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
        var changedError = await Assert.ThrowsAsync<CoordinationException>(
            () => CreateP1("p1-create", request, changed));
        Assert.Equal("p1_setup_receipt_changed", changedError.Code);

        var reused = await Assert.ThrowsAsync<CoordinationException>(
            () => CreateP1("p1-reuse", request, receipt));
        Assert.Equal("p1_setup_invocation_conflict", reused.Code);
        Assert.Equal(1L, await CountAsync("project_run_requests"));
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
    public async Task P1RequestRejectsMissingForeignAndStaleReceiptBeforeAnyWrite()
    {
        var receipt = P1Receipt();
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);

        var missing = await Assert.ThrowsAsync<CoordinationException>(() =>
            CreateP1("p1-missing", request, null));
        Assert.Equal("p1_setup_receipt_stale", missing.Code);
        Assert.Equal(0L, await CountAsync("project_run_requests"));

        var foreign = await Assert.ThrowsAsync<CoordinationException>(() =>
            CreateP1("p1-foreign", request, receipt with { Subject = "foreign-actor" }));
        Assert.Equal("p1_setup_receipt_stale", foreign.Code);
        Assert.Equal(0L, await CountAsync("project_run_requests"));

        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            CreateP1("p1-stale", request, receipt with { ProjectConfigurationRevision = 2 }));
        Assert.Equal("p1_setup_receipt_stale", stale.Code);
        Assert.Equal(0L, await CountAsync("project_run_requests"));

        var foreignTenant = await Assert.ThrowsAsync<CoordinationException>(() =>
            CreateP1("p1-foreign-tenant", request, receipt with { TenantId = "foreign-tenant" }));
        Assert.Equal("p1_setup_receipt_stale", foreignTenant.Code);
        Assert.Equal(0L, await CountAsync("project_run_requests"));

        var revokedMembership = await Assert.ThrowsAsync<CoordinationException>(() =>
            CreateP1("p1-revoked", request, receipt with { MembershipRevision = 0 }));
        Assert.Equal("p1_setup_receipt_stale", revokedMembership.Code);
        Assert.Equal(0L, await CountAsync("project_run_requests"));
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
        var row = await CreateP1("p1-accept", request, receipt);
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
            var row = await CreateP1(testCase.Key, request, receipt);
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
            Assert.Equal(testCase.Key == "p1-changed-tenant"
                ? "p1_setup_provenance_unavailable" : "p1_setup_receipt_stale", error.Code);
        }

        Assert.Equal(0L, await CountAsync("accepted_runs"));
        Assert.Equal(0L, await CountAsync("coordination_sessions"));
        Assert.Equal(0L, await CountAsync("coordinator_decisions"));
        Assert.Equal(0L, await CountAsync("p1_invocation_bindings"));
        Assert.Equal(3L, await CountAsync("project_run_requests"));
        Assert.Equal(3L, await CountAsync("p1_run_request_provenance"));
    }

    [Fact]
    public async Task ConcurrentP1StartsReuseOneAtomicBindingWithoutRequestLockDeadlock()
    {
        var receipt = P1Receipt();
        var row = await CreateP1("p1-concurrent",
            new CreateProjectRunRequest("Concurrent start", 1, P1SetupInvocationId: receipt.InvocationId),
            receipt);
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
            new CreateProjectRunRequest("Read current binding", 1, P1SetupInvocationId: receipt.InvocationId),
            receipt);
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
        var row = await CreateP1("p1-no-posthoc", request, receipt);
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
            new CreateProjectRunRequest("Race acceptance", 1, P1SetupInvocationId: receipt.InvocationId),
            receipt);
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
        var row = await CreateP1("p1-rollback", request, receipt);
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
        Assert.Equal(1L, await CountAsync("p1_run_request_provenance"));
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

    private Task<CoordinationOwnerStore.ProjectRunRequestRow> CreateP1(
        string key,
        CreateProjectRunRequest request,
        P1ProjectSetupReceiptSnapshot? receipt) =>
        _store.CreateProjectRunAsync(_actor, Tenant, Project, key, request, CancellationToken.None, receipt);

    private Task<StartProjectRunResponse> Start(
        string runId,
        Func<CoordinationOwnerStore.ProjectRunRequestRow, CancellationToken, Task> deliver,
        Action<CoordinationOwnerStore.ProjectRunRequestRow>? validateRequest = null) =>
        _store.StartProjectRunAsync(
            _actor, Tenant, Project, runId, deliver, NoCheck, CancellationToken.None, validateRequest);

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
            """{"source":"projects-config","modelSelection":{"reference":"model:provider/alpha"}}""");
        var selection = new EffectiveRunSelection(Project, runId, 1, 1, 1, "context-v1", snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(selection, new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, Tenant, 1, Project, runId,
            ImmutableArray.Create(new ProjectsAuthority("project", Project,
                ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }
}
