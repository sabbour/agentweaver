using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class MafChildSpawnFixedWorkAuthorizationOwnerStoreTests(
    CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "maf_fixed_spawn_" + Guid.NewGuid().ToString("N");
    private readonly FixedWorkSpecification _specification = new(
        "Implement feature",
        "Complete the implementation",
        "developer",
        "execute",
        "isolated",
        ["src/feature.cs"]);
    private readonly MafChildSpawnAuthorizationAuthority _authority = new(
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

    private MafChildSpawnFixedWorkIntent _intent = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _intent = CreateIntent();
        await SeedCurrentDecisionAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DurableAllowRetryUsesTheSameReceiptAndCanBeConsumedIdempotently()
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var firstReceiptId = Guid.NewGuid();
        var retryReceiptId = Guid.NewGuid();
        await InsertPolicyReceiptAsync(grant, firstReceiptId, "allow", "allowed");
        await InsertPolicyReceiptAsync(grant, retryReceiptId, "allow", "allowed");

        var first = await store.StoreAllowAsync(
            _intent, _authority, firstReceiptId, CancellationToken.None);
        var retry = await store.StoreAllowAsync(
            _intent, _authority, retryReceiptId, CancellationToken.None);
        var readback = await store.ReadCurrentAsync(
            _intent, _authority, CancellationToken.None);

        Assert.Equal(first.IntentHash, retry.IntentHash);
        Assert.Equal(first.PolicyReceiptId, retry.PolicyReceiptId);
        Assert.Equal(first.CreatedAt, retry.CreatedAt);
        Assert.Equal(_intent.WorkflowStepId, retry.WorkItemId);
        Assert.NotNull(readback);
        Assert.Equal(first.PolicyReceiptId, readback.PolicyReceiptId);

        var firstConsumption = await ConsumeAsync(store);
        var retriedConsumption = await ConsumeAsync(store);
        Assert.False(firstConsumption.AlreadyConsumed);
        Assert.True(retriedConsumption.AlreadyConsumed);
        Assert.Equal(first.IntentHash, retriedConsumption.Evidence.IntentHash);
    }

    [Fact]
    public async Task ReusingIntentIdWithDifferentHashConflicts()
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var receiptId = Guid.NewGuid();
        await InsertPolicyReceiptAsync(grant, receiptId, "allow", "allowed");
        await store.StoreAllowAsync(_intent, _authority, receiptId, CancellationToken.None);

        var changed = _intent with
        {
            RoleBinding = _intent.RoleBinding with { AgentId = "agent-2" }
        };
        var conflict = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.StoreAllowAsync(changed, _authority, receiptId, CancellationToken.None));

        Assert.Equal("maf_child_spawn_intent_conflict", conflict.Code);
    }

    [Theory]
    [InlineData("deny", "platform_rule_denied")]
    [InlineData("error", "evaluation_failed")]
    public async Task DenyAndErrorPolicyReceiptsCannotAuthorizeFixedWork(string outcome, string reason)
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var receiptId = Guid.NewGuid();
        await InsertPolicyReceiptAsync(grant, receiptId, outcome, reason);

        var denied = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.StoreAllowAsync(_intent, _authority, receiptId, CancellationToken.None));

        Assert.Equal("maf_child_spawn_policy_receipt_unavailable", denied.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedPolicyReceiptsFailClosed(bool mismatch)
    {
        var store = CreateStore();
        var grant = await store.IssueGrantAsync(_intent, _authority, CancellationToken.None);
        var receiptId = Guid.NewGuid();
        if (mismatch)
            await InsertPolicyReceiptAsync(grant, receiptId, "allow", "allowed", stepId: "different-step");

        var unavailable = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.StoreAllowAsync(_intent, _authority, receiptId, CancellationToken.None));

        Assert.Equal("maf_child_spawn_policy_receipt_unavailable", unavailable.Code);
    }

    [Fact]
    public async Task FixedSpecificationMustMatchTheConfirmedWorkflowStep()
    {
        var store = CreateStore();
        var changed = _intent with
        {
            FixedWorkSpecificationHash = new string('b', 64)
        };

        var stale = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.IssueGrantAsync(changed, _authority, CancellationToken.None));

        Assert.Equal("maf_child_spawn_plan_stale", stale.Code);
    }

    [Fact]
    public async Task SelectedRoleBindingMustMatchTheConfirmedRunSelection()
    {
        var store = CreateStore();
        var changed = _intent with
        {
            RoleBinding = _intent.RoleBinding with { AgentId = "agent-2" }
        };

        var stale = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.IssueGrantAsync(changed, _authority, CancellationToken.None));

        Assert.Equal("maf_child_spawn_plan_stale", stale.Code);
    }

    private MafChildSpawnAuthorizationOwnerStore CreateStore() =>
        new(
            fixture.DataSource,
            new OrchestratorOptions(
                "https://identity.example/",
                "orchestrator",
                "https://projects.example/",
                "projects",
                "https://events.example/",
                "events",
                _schema),
            TimeProvider.System);

    private MafChildSpawnFixedWorkIntent CreateIntent() => new(
        Guid.NewGuid(),
        "root-session",
        "root-session",
        "plan-1",
        4,
        "implementation",
        _authority.ContextRevision,
        7,
        "workflow-1",
        "refs/tags/v1",
        MafChildSpawnAuthorizationContract.ComputeFixedWorkSpecificationHash(_specification),
        new MafChildSpawnRoleBinding(
            "developer", "agent-1", "model:provider/alpha", "sandbox/provider", _specification.IsolationChoice));

    private async Task SeedCurrentDecisionAsync()
    {
        var workflow = new WorkflowDefinition(
            "workflow-1",
            "refs/tags/v1",
            "catalog-1",
            WorkflowDefinitionOrigin.BuiltIn,
            1,
            [
                new WorkflowStepDefinition(
                    "implementation",
                    "Implement the feature",
                    WorkflowStepMode.Fixed,
                    1,
                    new WorkflowCardinality(1, 1),
                    [],
                    ["developer"],
                    ["execute"],
                    ["isolated"],
                    [],
                    _specification,
                    null)
            ]);
        var plan = new WorkPlan("plan-1", workflow.Id, workflow.Revision, workflow.CatalogVersion, []);
        var selectionContext = new WorkPlanSelectionContextEnvelope(
            [
                new RoleRunSelection(
                    "developer", ["agent-1"], ["model:provider/alpha"], ["isolated"])
            ],
            [
                new ModelTaskSelectionReference(
                    ModelTaskKind.Straightforward, "developer", "agent-1", "model:provider/alpha")
            ],
            new PinnedProviderBindingEnvelope(
                _authority.RunId,
                ProviderSeam.Sandbox,
                "sandbox/provider",
                "1.0.0",
                1,
                "sandbox-options-1",
                ProviderHostingPattern.InProcess,
                "sandbox-resource",
                1,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)));
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
            workflow,
            true,
            plan,
            selectionContext,
            null,
            null,
            null,
            null,
            [],
            0);
        var decisionJson = JsonSerializer.Serialize(
            new { envelope }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
            decision.Parameters.AddWithValue("decision", Guid.NewGuid());
            decision.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
            decision.Parameters.AddWithValue("actor", _authority.ActorId);
            decision.Parameters.AddWithValue("fence", _intent.ExecutionFence);
            decision.Parameters.AddWithValue("version", _intent.PlanRevision);
            decision.Parameters.AddWithValue("idempotency", "test-confirmed-fixed-work");
            decision.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('b', 64));
            decision.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, decisionJson);
            await decision.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task InsertPolicyReceiptAsync(
        ExecutableActionGrantReference grant,
        Guid receiptId,
        string outcome,
        string reason,
        string? stepId = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".policy_evaluation_receipts
                (project_id, run_id, receipt_id, issuer, actor_id, tenant_id, session_id, step_id,
                 grant_id, grant_revision, purpose, action_id, outcome, reason_code, execution_fence,
                 provider_id, adapter_version, options_schema_version, options_revision)
            VALUES (@project, @run, @receipt, @issuer, @actor, @tenant, @parent, @step,
                    @grant, @grantRevision, @purpose, @action, @outcome, @reason, @fence,
                    @provider, @adapterVersion, @optionsSchemaVersion, @optionsRevision)
            """, connection);
        command.Parameters.AddWithValue("project", _authority.ProjectId);
        command.Parameters.AddWithValue("run", _authority.RunId);
        command.Parameters.AddWithValue("receipt", receiptId);
        command.Parameters.AddWithValue("issuer", _authority.ActorIssuer);
        command.Parameters.AddWithValue("actor", _authority.ActorId);
        command.Parameters.AddWithValue("tenant", _authority.TenantId);
        command.Parameters.AddWithValue("parent", _intent.ParentSessionId);
        command.Parameters.AddWithValue("step", stepId ?? _intent.WorkflowStepId);
        command.Parameters.AddWithValue("grant", grant.GrantId);
        command.Parameters.AddWithValue("grantRevision", grant.Revision);
        command.Parameters.AddWithValue("purpose", MafChildSpawnAuthorizationContract.Purpose);
        command.Parameters.AddWithValue("action", MafChildSpawnAuthorizationContract.ActionId);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("fence", _intent.ExecutionFence);
        command.Parameters.AddWithValue("provider", _authority.PolicyProviderId);
        command.Parameters.AddWithValue("adapterVersion", _authority.PolicyAdapterVersion);
        command.Parameters.AddWithValue("optionsSchemaVersion", _authority.PolicyOptionsSchemaVersion);
        command.Parameters.AddWithValue("optionsRevision", _authority.PolicyOptionsRevision);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<MafChildSpawnAuthorizationConsumeResult> ConsumeAsync(
        MafChildSpawnAuthorizationOwnerStore store)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.CheckAndConsumeWithinTransactionAsync(
            connection, transaction, _intent, _authority, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }
}
