using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class MafChildSpawnAuthorizationOwnerStoreTests(
    CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "maf_spawn_" + Guid.NewGuid().ToString("N");
    private readonly WorkPlanItem _workItem = new(
        "work-item-1",
        "implementation",
        "Implement feature",
        "Complete the implementation",
        "developer",
        "agent-1",
        "execute",
        "model-1",
        "sandbox",
        "isolated",
        [],
        []);
    private MafChildSpawnIntent _intent = null!;
    private MafChildSpawnAuthorizationAuthority _authority = null!;
    private Guid _decisionId;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _intent = new MafChildSpawnIntent(
            Guid.NewGuid(),
            "root-session",
            "root-session",
            "plan-1",
            4,
            "implementation",
            _workItem.Id,
            "context-1",
            7,
            _workItem);
        _authority = new MafChildSpawnAuthorizationAuthority(
            "project-1",
            "run-1",
            "https://identity.example/",
            "actor-1",
            "tenant-1",
            new string('A', 64),
            "context-1",
            1,
            1,
            1,
            2,
            3,
            AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion.ToString(),
            AgtPolicyProvider.OptionsSchemaVersion,
            "policy-options-1");
        await SeedCurrentDecisionAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DurableAllowCanBeConsumedIdempotentlyAndRevocationFailsClosed()
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var policyReceiptId = Guid.NewGuid();
        await InsertPolicyAllowAsync(grant, policyReceiptId);

        var firstEvidence = await store.StoreAllowAsync(
            _intent, _authority, policyReceiptId, CancellationToken.None);
        await using (var expiredConnection = await fixture.DataSource.OpenConnectionAsync())
        await using (var expireGrant = new NpgsqlCommand($"""
            UPDATE "{_schema}".executable_action_grants
            SET expires_at = clock_timestamp() - interval '1 minute'
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant
            """, expiredConnection))
        {
            expireGrant.Parameters.AddWithValue("project", _authority.ProjectId);
            expireGrant.Parameters.AddWithValue("run", _authority.RunId);
            expireGrant.Parameters.AddWithValue("grant", grant.GrantId);
            Assert.Equal(1, await expireGrant.ExecuteNonQueryAsync());
        }
        var replayedEvidence = await store.ReadCurrentAsync(
            _intent, _authority, CancellationToken.None);

        Assert.NotNull(replayedEvidence);
        Assert.Equal(firstEvidence.IntentHash, replayedEvidence.IntentHash);
        Assert.Equal(_authority.ProjectRevision, replayedEvidence.ProjectRevision);
        Assert.Equal(_authority.ProjectConfigurationRevision, replayedEvidence.ProjectConfigurationRevision);
        Assert.Equal(_authority.PlatformRuntimeRevision, replayedEvidence.PlatformRuntimeRevision);
        Assert.Equal(_authority.PolicyProviderId, replayedEvidence.PolicyProviderId);
        Assert.Equal(_authority.PolicyOptionsRevision, replayedEvidence.PolicyOptionsRevision);
        Assert.Equal(PolicyEvaluationOutcome.Allow, replayedEvidence.Outcome);
        Assert.Null(replayedEvidence.ConsumedAt);

        var concurrent = await Task.WhenAll(ConsumeAsync(), ConsumeAsync());
        Assert.Single(concurrent, result => !result.AlreadyConsumed);
        Assert.Single(concurrent, result => result.AlreadyConsumed);
        Assert.All(concurrent, result => Assert.Equal(firstEvidence.IntentHash, result.Evidence.IntentHash));

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var replay = await store.CheckAndConsumeWithinTransactionAsync(
                connection, transaction, _intent, _authority, CancellationToken.None);
            Assert.True(replay.AlreadyConsumed);
            Assert.Equal(firstEvidence.IntentHash, replay.Evidence.IntentHash);
            Assert.NotNull(replay.Evidence.ConsumedAt);
            await transaction.CommitAsync();
        }

        var changedIntent = _intent with
        {
            WorkItem = _intent.WorkItem with { Task = "different task" }
        };
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var conflict = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                store.CheckAndConsumeWithinTransactionAsync(
                    connection, transaction, changedIntent, _authority, CancellationToken.None));
            Assert.Equal("maf_child_spawn_intent_conflict", conflict.Code);
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var revoke = new NpgsqlCommand($"""
            UPDATE "{_schema}".executable_action_grants
            SET grant_state = 'revoked'
            WHERE grant_id = @grant AND project_id = @project AND run_id = @run
            """, connection))
        {
            revoke.Parameters.AddWithValue("grant", grant.GrantId);
            revoke.Parameters.AddWithValue("project", _authority.ProjectId);
            revoke.Parameters.AddWithValue("run", _authority.RunId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var revoked = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                store.CheckAndConsumeWithinTransactionAsync(
                    connection, transaction, _intent, _authority, CancellationToken.None));
            Assert.Equal("maf_child_spawn_policy_receipt_unavailable", revoked.Code);
        }

        async Task<MafChildSpawnAuthorizationConsumeResult> ConsumeAsync()
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var consumed = await store.CheckAndConsumeWithinTransactionAsync(
                connection, transaction, _intent, _authority, CancellationToken.None);
            await transaction.CommitAsync();
            return consumed;
        }
    }

    [Fact]
    public async Task AuthorizationRetryLocksOwnerStateBeforeAConcurrentGrantTransition()
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var applicationName = $"maf_spawn_lock_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = applicationName,
            CommandTimeout = 10
        }.ConnectionString;
        await using var retryDataSource = NpgsqlDataSource.Create(connectionString);
        var retryStore = CreateStore(retryDataSource);
        await using var transitionConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var transition = await transitionConnection.BeginTransactionAsync();
        await using (var lockOwner = new NpgsqlCommand($"""
            SELECT r.run_id
            FROM "{_schema}".accepted_runs AS r
            INNER JOIN "{_schema}".coordination_sessions AS s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @root
            FOR UPDATE OF r, s
            """, transitionConnection, transition))
        {
            lockOwner.Parameters.AddWithValue("project", _authority.ProjectId);
            lockOwner.Parameters.AddWithValue("run", _authority.RunId);
            lockOwner.Parameters.AddWithValue("root", _intent.RootSessionId);
            Assert.Equal(_authority.RunId, await lockOwner.ExecuteScalarAsync());
        }
        await using var observerConnection = await fixture.DataSource.OpenConnectionAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var retry = retryStore.IssueGrantAsync(_intent, _authority, cancellation.Token);
        var transitionCommitted = false;
        var retryObserved = false;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var waitingOnOwner = false;
            while (DateTime.UtcNow < deadline)
            {
                // Keep pg_stat_activity polling outside the lock-holding transaction so each poll gets a fresh snapshot.
                await using var activity = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity
                        WHERE datname = current_database()
                          AND application_name = @application
                          AND wait_event_type = 'Lock')
                    """, observerConnection);
                activity.Parameters.AddWithValue("application", applicationName);
                waitingOnOwner = (bool)(await activity.ExecuteScalarAsync())!;
                if (waitingOnOwner)
                    break;
                if (retry.IsCompleted)
                    await retry;
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            Assert.True(waitingOnOwner, "Authorization did not wait for the coordinator's owner-state lock.");

            await using (var supersede = new NpgsqlCommand($"""
                UPDATE "{_schema}".executable_action_grants
                SET is_current = false, grant_state = 'superseded'
                WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                  AND is_current AND grant_state = 'active'
                """, transitionConnection, transition))
            {
                supersede.Parameters.AddWithValue("project", _authority.ProjectId);
                supersede.Parameters.AddWithValue("run", _authority.RunId);
                supersede.Parameters.AddWithValue("grant", grant.GrantId);
                Assert.Equal(1, await supersede.ExecuteNonQueryAsync());
            }
            await transition.CommitAsync();
            transitionCommitted = true;

            var conflict = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => retry);
            retryObserved = true;
            Assert.Equal("maf_child_spawn_intent_conflict", conflict.Code);
        }
        finally
        {
            cancellation.Cancel();
            if (!transitionCommitted)
                await transition.RollbackAsync();
            if (!retryObserved)
            {
                try
                {
                    await retry;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private MafChildSpawnAuthorizationOwnerStore CreateStore(NpgsqlDataSource? source = null) =>
        new(
            source ?? fixture.DataSource,
            new OrchestratorOptions(
                "https://identity.example/",
                "orchestrator",
                "https://projects.example/",
                "projects",
                "https://events.example/",
                "events",
                _schema),
            TimeProvider.System);

    private async Task SeedCurrentDecisionAsync()
    {
        var plan = new WorkPlan("plan-1", "workflow-1", "revision-1", "catalog-1", [_workItem]);
        var envelope = new CoordinatorDecisionStateEnvelope(
            CoordinatorDecisionStateEnvelope.CurrentVersion,
            _authority.ActorIssuer,
            _authority.ActorId,
            _authority.TenantId,
            _authority.ProjectId,
            _authority.RunId,
            _intent.RootSessionId,
            _authority.AcceptedSelectionHash,
            _authority.ProjectRevision,
            _authority.ProjectConfigurationRevision,
            _authority.PlatformRuntimeRevision,
            _authority.ContextRevision,
            _intent.ExecutionFence,
            null,
            true,
            0,
            null,
            true,
            plan,
            null,
            null,
            null,
            null,
            null,
            [],
            0);
        var decisionJson = JsonSerializer.Serialize(
            new { envelope }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _decisionId = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var run = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".accepted_runs
                (project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, execution_fence, execution_state, tenant_id)
            VALUES (@project, @run, @selection::jsonb, @selectionHash, @issuer, @actor, @fence, 'active', @tenant)
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", _authority.ProjectId);
            run.Parameters.AddWithValue("run", _authority.RunId);
            run.Parameters.AddWithValue("selection", NpgsqlDbType.Jsonb, "{}");
            run.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, _authority.AcceptedSelectionHash);
            run.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
            run.Parameters.AddWithValue("actor", _authority.ActorId);
            run.Parameters.AddWithValue("fence", _intent.ExecutionFence);
            run.Parameters.AddWithValue("tenant", _authority.TenantId);
            await run.ExecuteNonQueryAsync();
        }
        await using (var session = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, parent_session_id, writer_issuer, writer_subject,
                 execution_fence, lifecycle_state, turn_state, root_session_id, node_kind)
            VALUES (@project, @run, @root, NULL, @issuer, @actor,
                    @fence, 'active', 'active', @root, 'coordinator')
            """, connection, transaction))
        {
            session.Parameters.AddWithValue("project", _authority.ProjectId);
            session.Parameters.AddWithValue("run", _authority.RunId);
            session.Parameters.AddWithValue("root", _intent.RootSessionId);
            session.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
            session.Parameters.AddWithValue("actor", _authority.ActorId);
            session.Parameters.AddWithValue("fence", _intent.ExecutionFence);
            await session.ExecuteNonQueryAsync();
        }
        await using (var decision = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id, actor_issuer, actor_subject,
                 execution_fence, state_version, action_kind, idempotency_key, command_hash,
                 decision_state, decision)
            VALUES (@project, @run, @root, @request, @decision, @issuer, @actor,
                    @fence, @version, 'work_plan.confirm', @idempotency, @hash, 'accepted', @payload::jsonb)
            """, connection, transaction))
        {
            decision.Parameters.AddWithValue("project", _authority.ProjectId);
            decision.Parameters.AddWithValue("run", _authority.RunId);
            decision.Parameters.AddWithValue("root", _intent.RootSessionId);
            decision.Parameters.AddWithValue("request", _intent.IntentId.ToString("N"));
            decision.Parameters.AddWithValue("decision", _decisionId);
            decision.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
            decision.Parameters.AddWithValue("actor", _authority.ActorId);
            decision.Parameters.AddWithValue("fence", _intent.ExecutionFence);
            decision.Parameters.AddWithValue("version", _intent.PlanRevision);
            decision.Parameters.AddWithValue("idempotency", "test-confirmed-plan");
            decision.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('b', 64));
            decision.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, decisionJson);
            await decision.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task InsertPolicyAllowAsync(ExecutableActionGrantReference grant, Guid receiptId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".policy_evaluation_receipts
                (project_id, run_id, receipt_id, issuer, actor_id, tenant_id, session_id, step_id,
                 grant_id, grant_revision, purpose, action_id, outcome, reason_code, execution_fence,
                 provider_id, adapter_version, options_schema_version, options_revision)
            VALUES (@project, @run, @receipt, @issuer, @actor, @tenant, @parent, @step,
                    @grant, @grantRevision, @purpose, @action, 'allow', 'allowed', @fence,
                    @provider, @adapterVersion, @optionsSchemaVersion, @optionsRevision)
            """, connection);
        command.Parameters.AddWithValue("project", _authority.ProjectId);
        command.Parameters.AddWithValue("run", _authority.RunId);
        command.Parameters.AddWithValue("receipt", receiptId);
        command.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
        command.Parameters.AddWithValue("actor", _authority.ActorId);
        command.Parameters.AddWithValue("tenant", _authority.TenantId);
        command.Parameters.AddWithValue("parent", _intent.ParentSessionId);
        command.Parameters.AddWithValue("step", _intent.WorkflowStepId);
        command.Parameters.AddWithValue("grant", grant.GrantId);
        command.Parameters.AddWithValue("grantRevision", grant.Revision);
        command.Parameters.AddWithValue("purpose", MafChildSpawnAuthorizationContract.Purpose);
        command.Parameters.AddWithValue("action", MafChildSpawnAuthorizationContract.ActionId);
        command.Parameters.AddWithValue("fence", _intent.ExecutionFence);
        command.Parameters.AddWithValue("provider", _authority.PolicyProviderId);
        command.Parameters.AddWithValue("adapterVersion", _authority.PolicyAdapterVersion);
        command.Parameters.AddWithValue("optionsSchemaVersion", _authority.PolicyOptionsSchemaVersion);
        command.Parameters.AddWithValue("optionsRevision", _authority.PolicyOptionsRevision);
        await command.ExecuteNonQueryAsync();
    }
}
