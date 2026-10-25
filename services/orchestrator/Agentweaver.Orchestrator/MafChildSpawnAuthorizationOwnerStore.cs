using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>Persists guarded child-spawn grants and their intent-bound allow receipts in the Orchestrator owner.</summary>
internal sealed class MafChildSpawnAuthorizationOwnerStore(
    NpgsqlDataSource dataSource,
    OrchestratorOptions options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private const string ReceiptTable = "maf_child_spawn_authorization_receipts";
    private readonly string _schema = $"\"{options.Schema}\"";

    internal async Task<ExecutableActionGrantReference> IssueGrantAsync(
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        CancellationToken cancellationToken)
    {
        var intentHash = MafChildSpawnAuthorizationContract.ComputeIntentHash(intent);
        var grantId = GrantId(authority.ProjectId, authority.RunId, intent.IntentId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireMigrationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireIntentHashAsync(
            connection, transaction, authority, intent, cancellationToken).ConfigureAwait(false);
        var current = await RequireCurrentOwnerStateAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(15);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.executable_action_grants
                (project_id, run_id, grant_id, revision, is_current, grant_state,
                 issuer, actor_id, tenant_id, session_id, step_id, action_ids, purpose,
                 project_revision, project_configuration_revision, platform_runtime_revision,
                 context_revision, accepted_selection_hash, membership_revision, role_revision,
                 execution_fence, expires_at, source_state_version, source_decision_id, source_request_id)
            VALUES
                (@project, @run, @grant, '1', true, 'active',
                 @issuer, @actor, @tenant, @parent, @step, @actions, @purpose,
                 @projectRevision, @configurationRevision, @platformRevision,
                 @contextRevision, @selectionHash, @membershipRevision, @roleRevision,
                 @fence, @expiresAt, @stateVersion, @decision, @intentHash)
            ON CONFLICT (project_id, run_id, grant_id, revision) DO NOTHING
            """, connection, transaction))
        {
            BindAuthority(insert, authority);
            insert.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantId);
            insert.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, intent.ParentSessionId);
            insert.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, intent.WorkflowStepId);
            insert.Parameters.AddWithValue(
                "actions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new[] { MafChildSpawnAuthorizationContract.ActionId }));
            insert.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, MafChildSpawnAuthorizationContract.Purpose);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, intent.ExecutionFence);
            insert.Parameters.AddWithValue("expiresAt", NpgsqlDbType.TimestampTz, expiresAt);
            insert.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, current.StateVersion);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, current.DecisionId);
            insert.Parameters.AddWithValue("intentHash", NpgsqlDbType.Varchar, intentHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var exact = new NpgsqlCommand($"""
            SELECT 1
            FROM {_schema}.executable_action_grants
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant AND revision = '1'
              AND is_current AND grant_state = 'active'
              AND issuer = @issuer AND actor_id = @actor AND tenant_id = @tenant
              AND session_id = @parent AND step_id = @step
              AND action_ids = @actions::jsonb AND purpose = @purpose
              AND project_revision = @projectRevision
              AND project_configuration_revision = @configurationRevision
              AND platform_runtime_revision = @platformRevision
              AND context_revision = @contextRevision
              AND accepted_selection_hash = @selectionHash
              AND membership_revision = @membershipRevision AND role_revision = @roleRevision
              AND execution_fence = @fence AND source_state_version = @stateVersion
              AND source_decision_id = @decision AND source_request_id = @intentHash
            """, connection, transaction))
        {
            BindAuthority(exact, authority);
            exact.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantId);
            exact.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, intent.ParentSessionId);
            exact.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, intent.WorkflowStepId);
            exact.Parameters.AddWithValue(
                "actions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new[] { MafChildSpawnAuthorizationContract.ActionId }));
            exact.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, MafChildSpawnAuthorizationContract.Purpose);
            exact.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, intent.ExecutionFence);
            exact.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, current.StateVersion);
            exact.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, current.DecisionId);
            exact.Parameters.AddWithValue("intentHash", NpgsqlDbType.Varchar, intentHash);
            if (await exact.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new RuntimeAuthorizationException("maf_child_spawn_intent_conflict");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutableActionGrantReference(grantId, "1");
    }

    internal async Task<MafChildSpawnAuthorizationEvidence?> ReadCurrentAsync(
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireMigrationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireIntentHashAsync(
            connection, transaction, authority, intent, cancellationToken).ConfigureAwait(false);
        _ = await RequireCurrentOwnerStateAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);
        var evidence = await ReadEvidenceAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);
        if (evidence is not null)
        {
            RequireCurrentEvidence(evidence, intent, authority);
            await RequireCurrentPolicyReceiptAndGrantAsync(
                connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
            evidence = evidence with
            {
                ConsumedAt = await ReadConsumedAtAsync(
                    connection, transaction, evidence.ProjectId, evidence.RunId,
                    evidence.IntentId, evidence.IntentHash, cancellationToken).ConfigureAwait(false)
            };
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    internal async Task<MafChildSpawnAuthorizationEvidence> StoreAllowAsync(
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        Guid policyReceiptId,
        CancellationToken cancellationToken)
    {
        var evidence = MafChildSpawnAuthorizationContract.CreateEvidence(
            intent,
            authority,
            policyReceiptId,
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            timeProvider.GetUtcNow());
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireMigrationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireIntentHashAsync(
            connection, transaction, authority, intent, cancellationToken).ConfigureAwait(false);
        _ = await RequireCurrentOwnerStateAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.{ReceiptTable}
                (project_id, run_id, spawn_intent_id, intent_hash,
                 parent_session_id, root_session_id, actor_issuer, actor_id, tenant_id,
                 accepted_selection_hash, context_revision, project_revision,
                 project_configuration_revision, platform_runtime_revision,
                 membership_revision, role_revision, execution_fence, plan_id,
                 plan_revision, workflow_step_id, work_item_id, grant_id, grant_revision,
                 policy_receipt_id, policy_provider_id, policy_adapter_version,
                 policy_options_schema_version, policy_options_revision,
                 policy_outcome, policy_reason_code)
            VALUES
                (@project, @run, @intent, @intentHash,
                 @parent, @root, @issuer, @actor, @tenant,
                 @selectionHash, @contextRevision, @projectRevision,
                 @configurationRevision, @platformRevision,
                 @membershipRevision, @roleRevision, @fence, @plan,
                 @planRevision, @step, @workItem, @grant, '1',
                 @policyReceipt, @provider, @adapterVersion,
                 @optionsSchemaVersion, @optionsRevision, 'allow', 'allowed')
            ON CONFLICT (project_id, run_id, spawn_intent_id) DO NOTHING
            """, connection, transaction))
        {
            BindEvidence(insert, evidence);
            insert.Parameters.AddWithValue(
                "grant", NpgsqlDbType.Varchar, GrantId(authority.ProjectId, authority.RunId, intent.IntentId));
            insert.Parameters.AddWithValue("policyReceipt", NpgsqlDbType.Uuid, policyReceiptId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await ReadEvidenceAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("maf_child_spawn_authorization_unavailable");
        RequireCurrentEvidence(stored, intent, authority);
        await RequireCurrentPolicyReceiptAndGrantAsync(
            connection, transaction, stored, cancellationToken).ConfigureAwait(false);
        var consumedAt = await ReadConsumedAtAsync(
            connection, transaction, stored.ProjectId, stored.RunId,
            stored.IntentId, stored.IntentHash, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored with { ConsumedAt = consumedAt };
    }

    internal async Task<MafChildSpawnAuthorizationConsumeResult> CheckAndConsumeWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));

        await RequireMigrationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireIntentHashAsync(
            connection, transaction, authority, intent, cancellationToken).ConfigureAwait(false);
        // Keep the root decision and parent fence locked while the caller commits its child association and outbox.
        var current = await RequireCurrentOwnerStateAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);
        var evidence = await ReadEvidenceAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("maf_child_spawn_authorization_missing");
        RequireCurrentEvidence(evidence, intent, authority);
        await RequireCurrentPolicyReceiptAndGrantAsync(
            connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
        if (evidence.PlanRevision != current.StateVersion)
            throw new RuntimeAuthorizationException("maf_child_spawn_authorization_stale");

        var inserted = await InsertConsumptionAsync(
            connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
        var consumedAt = await ReadConsumedAtAsync(
            connection, transaction, evidence.ProjectId, evidence.RunId,
            evidence.IntentId, evidence.IntentHash, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("maf_child_spawn_authorization_stale");
        evidence = evidence with { ConsumedAt = consumedAt };
        return new MafChildSpawnAuthorizationConsumeResult(evidence, AlreadyConsumed: !inserted);
    }

    private async Task RequireMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM {_schema}.coordination_schema_migrations WHERE version = 37) = 1
                AND to_regclass(@receipts) IS NOT NULL
                AND to_regclass(@consumptions) IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue(
            "receipts", NpgsqlDbType.Text, $"{options.Schema}.{ReceiptTable}");
        command.Parameters.AddWithValue(
            "consumptions", NpgsqlDbType.Text,
            $"{options.Schema}.maf_child_spawn_authorization_consumptions");
        if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
    }

    private async Task RequireIntentHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnAuthorizationAuthority authority,
        MafChildSpawnIntent intent,
        CancellationToken cancellationToken)
    {
        var expectedHash = MafChildSpawnAuthorizationContract.ComputeIntentHash(intent);
        // Keep a spawn-intent identifier bound to its first exact payload, even before an allow is recorded.
        foreach (var table in new[] { ReceiptTable, "maf_child_spawn_authorization_consumptions" })
        {
            await using var command = new NpgsqlCommand($"""
                SELECT intent_hash
                FROM {_schema}.{table}
                WHERE project_id = @project AND run_id = @run AND spawn_intent_id = @intent
                FOR SHARE
                """, connection, transaction);
            BindScope(command, authority);
            command.Parameters.AddWithValue("intent", NpgsqlDbType.Uuid, intent.IntentId);
            var storedHash = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (storedHash is not null &&
                !string.Equals(storedHash.Trim(), expectedHash, StringComparison.Ordinal))
                throw new RuntimeAuthorizationException("maf_child_spawn_intent_conflict");
        }

        await using var grant = new NpgsqlCommand($"""
            SELECT source_request_id
            FROM {_schema}.executable_action_grants
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant AND revision = '1'
            FOR SHARE
            """, connection, transaction);
        BindScope(grant, authority);
        grant.Parameters.AddWithValue(
            "grant", NpgsqlDbType.Varchar, GrantId(authority.ProjectId, authority.RunId, intent.IntentId));
        var grantedHash = (string?)await grant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (grantedHash is not null && !string.Equals(grantedHash, expectedHash, StringComparison.Ordinal))
            throw new RuntimeAuthorizationException("maf_child_spawn_intent_conflict");
    }

    private async Task<bool> InsertConsumptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnAuthorizationEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.maf_child_spawn_authorization_consumptions
                (project_id, run_id, spawn_intent_id, intent_hash)
            VALUES (@project, @run, @intent, @intentHash)
            ON CONFLICT (project_id, run_id, spawn_intent_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, evidence.ProjectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, evidence.RunId);
            insert.Parameters.AddWithValue("intent", NpgsqlDbType.Uuid, evidence.IntentId);
            insert.Parameters.AddWithValue("intentHash", NpgsqlDbType.Char, evidence.IntentHash);
            return await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }

    private async Task<DateTimeOffset?> ReadConsumedAtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        Guid intentId,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT intent_hash, consumed_at
            FROM {_schema}.maf_child_spawn_authorization_consumptions
            WHERE project_id = @project AND run_id = @run AND spawn_intent_id = @intent
            FOR SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Uuid, intentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var storedHash = reader.GetString(0).Trim();
        if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
            throw new RuntimeAuthorizationException("maf_child_spawn_intent_conflict");
        return reader.GetFieldValue<DateTimeOffset>(1);
    }

    private async Task<CurrentDecision> RequireCurrentOwnerStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        CancellationToken cancellationToken)
    {
        MafChildSpawnAuthorizationContract.ValidateIntent(intent);
        // These shared locks keep the accepted run, parent/root fence, and latest decision stable through consumption.
        await using (var run = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR SHARE
            """, connection, transaction))
        {
            BindScope(run, authority);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != authority.TenantId ||
                !string.Equals(reader.GetString(1).Trim(), authority.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(2) != intent.ExecutionFence ||
                reader.GetString(3) is "completed" or "failed" or "indeterminate" or "cancelled")
                throw new RuntimeAuthorizationException("maf_child_spawn_authority_stale");
        }

        await using (var parent = new NpgsqlCommand($"""
            SELECT writer_issuer, writer_subject, node_kind, parent_session_id,
                   root_session_id, lifecycle_state, turn_state, execution_fence
            FROM {_schema}.coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @parent
            FOR SHARE
            """, connection, transaction))
        {
            BindScope(parent, authority);
            parent.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, intent.ParentSessionId);
            await using var reader = await parent.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != authority.ActorIssuer ||
                reader.GetString(1) != authority.ActorId ||
                reader.GetString(4) != intent.RootSessionId ||
                reader.GetString(5) != "active" ||
                reader.GetString(6) != "active" ||
                reader.GetInt64(7) != intent.ExecutionFence ||
                (intent.ParentSessionId == intent.RootSessionId
                    ? reader.GetString(2) != "coordinator" || !reader.IsDBNull(3)
                    : reader.GetString(2) != "child_work" || reader.IsDBNull(3)))
                throw new RuntimeAuthorizationException("maf_child_spawn_parent_unavailable");
        }

        await using (var root = new NpgsqlCommand($"""
            SELECT writer_issuer, writer_subject, node_kind, parent_session_id,
                   root_session_id, lifecycle_state, turn_state, execution_fence
            FROM {_schema}.coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @root
            FOR SHARE
            """, connection, transaction))
        {
            BindScope(root, authority);
            root.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, intent.RootSessionId);
            await using var reader = await root.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != authority.ActorIssuer ||
                reader.GetString(1) != authority.ActorId ||
                reader.GetString(2) != "coordinator" ||
                !reader.IsDBNull(3) ||
                reader.GetString(4) != intent.RootSessionId ||
                reader.GetString(5) != "active" ||
                reader.GetString(6) != "active" ||
                reader.GetInt64(7) != intent.ExecutionFence)
                throw new RuntimeAuthorizationException("maf_child_spawn_root_unavailable");
        }

        await using var decision = new NpgsqlCommand($"""
            SELECT decision_id, state_version, decision_state, decision::text
            FROM {_schema}.coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @root
            ORDER BY state_version DESC LIMIT 1
            FOR SHARE
            """, connection, transaction);
        BindScope(decision, authority);
        decision.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, intent.RootSessionId);
        await using var decisionReader = await decision.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await decisionReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            decisionReader.GetString(2) != "accepted")
            throw new RuntimeAuthorizationException("maf_child_spawn_decision_unavailable");
        var current = new CurrentDecision(
            decisionReader.GetGuid(0),
            decisionReader.GetInt64(1),
            decisionReader.GetString(3));
        ValidateConfirmedPlan(current, intent, authority);
        return current;
    }

    private static void ValidateConfirmedPlan(
        CurrentDecision current,
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority)
    {
        try
        {
            using var document = JsonDocument.Parse(current.DecisionJson);
            if (!document.RootElement.TryGetProperty("envelope", out var envelopeElement))
                throw new JsonException("The accepted decision has no state envelope.");
            var envelope = JsonSerializer.Deserialize<CoordinatorDecisionStateEnvelope>(
                envelopeElement.GetRawText(), DecisionJsonOptions)
                ?? throw new JsonException("The accepted decision state is missing.");
            var plan = envelope.ConfirmedWorkPlan;
            var item = plan?.Items.SingleOrDefault(candidate => candidate.Id == intent.WorkItemId);
            // Matching the canonical hash proves every work-item field, not only its identifier and step.
            if (envelope.Version != CoordinatorDecisionStateEnvelope.CurrentVersion ||
                envelope.Issuer != authority.ActorIssuer ||
                envelope.Subject != authority.ActorId ||
                envelope.TenantId != authority.TenantId ||
                envelope.ProjectId != authority.ProjectId ||
                envelope.RunId != authority.RunId ||
                envelope.RootSessionId != intent.RootSessionId ||
                !string.Equals(envelope.AcceptedSelectionHash, authority.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                envelope.ProjectRevision != authority.ProjectRevision ||
                envelope.ProjectConfigurationRevision != authority.ProjectConfigurationRevision ||
                envelope.PlatformRuntimeRevision != authority.PlatformRuntimeRevision ||
                envelope.ContextRevision != authority.ContextRevision ||
                envelope.Fence != intent.ExecutionFence ||
                !envelope.OutcomeConfirmed ||
                !envelope.WorkflowConfirmed ||
                plan is null ||
                plan.Id != intent.PlanId ||
                current.StateVersion != intent.PlanRevision ||
                envelope.CandidateWorkPlan is not null ||
                envelope.PendingGate is not null ||
                item is null ||
                item.WorkflowStepId != intent.WorkflowStepId ||
                MafChildSpawnAuthorizationContract.ComputeIntentHash(intent) !=
                    MafChildSpawnAuthorizationContract.ComputeIntentHash(intent with { WorkItem = item }))
                throw new RuntimeAuthorizationException("maf_child_spawn_plan_stale");
        }
        catch (RuntimeAuthorizationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new RuntimeAuthorizationException("maf_child_spawn_decision_unavailable", null, exception);
        }
    }

    private async Task<MafChildSpawnAuthorizationEvidence?> ReadEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT spawn_intent_id, intent_hash, project_id, run_id, parent_session_id, root_session_id,
                   actor_issuer, actor_id, tenant_id, accepted_selection_hash, context_revision,
                   project_revision, project_configuration_revision, platform_runtime_revision,
                   membership_revision, role_revision, execution_fence, plan_id, plan_revision,
                   workflow_step_id, work_item_id, policy_receipt_id, policy_provider_id,
                   policy_adapter_version, policy_options_schema_version, policy_options_revision,
                   policy_outcome, policy_reason_code, created_at
            FROM {_schema}.{ReceiptTable}
            WHERE project_id = @project AND run_id = @run AND spawn_intent_id = @intent
            FOR SHARE
            """, connection, transaction);
        BindScope(command, authority);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Uuid, intent.IntentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var outcome = reader.GetString(26) switch
        {
            "allow" => PolicyEvaluationOutcome.Allow,
            "deny" => PolicyEvaluationOutcome.Deny,
            "error" => PolicyEvaluationOutcome.Error,
            _ => throw new RuntimeAuthorizationException("maf_child_spawn_authorization_invalid")
        };
        var reason = reader.GetString(27) switch
        {
            "allowed" => PolicyEvaluationReasonCode.Allowed,
            _ => throw new RuntimeAuthorizationException("maf_child_spawn_authorization_invalid")
        };
        return new MafChildSpawnAuthorizationEvidence(
            reader.GetGuid(0), reader.GetString(1).Trim(), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9).Trim(), reader.GetString(10),
            reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13),
            reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16),
            reader.GetString(17), reader.GetInt64(18), reader.GetString(19),
            reader.GetString(20), reader.GetGuid(21), reader.GetString(22),
            reader.GetString(23), reader.GetInt32(24), reader.GetString(25),
            outcome, reason, reader.GetFieldValue<DateTimeOffset>(28));
    }

    private static void RequireCurrentEvidence(
        MafChildSpawnAuthorizationEvidence evidence,
        MafChildSpawnIntent intent,
        MafChildSpawnAuthorizationAuthority authority)
    {
        if (evidence.IntentId != intent.IntentId ||
            !string.Equals(
                evidence.IntentHash,
                MafChildSpawnAuthorizationContract.ComputeIntentHash(intent),
                StringComparison.Ordinal))
            throw new RuntimeAuthorizationException("maf_child_spawn_intent_conflict");
        if (!MafChildSpawnAuthorizationContract.MatchesCurrent(evidence, intent, authority))
            throw new RuntimeAuthorizationException("maf_child_spawn_authorization_stale");
    }

    private async Task RequireCurrentPolicyReceiptAndGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnAuthorizationEvidence evidence,
        CancellationToken cancellationToken)
    {
        // Join the allow receipt back to its current grant and accepted decision; pin the grant until commit.
        await using var command = new NpgsqlCommand($"""
            SELECT 1
            FROM {_schema}.policy_evaluation_receipts p
            JOIN {_schema}.executable_action_grants g
              ON g.project_id = p.project_id AND g.run_id = p.run_id
             AND g.grant_id = p.grant_id AND g.revision = p.grant_revision
            JOIN {_schema}.accepted_runs r
              ON r.project_id = g.project_id AND r.run_id = g.run_id
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = g.project_id AND s.run_id = g.run_id AND s.session_id = g.session_id
            JOIN {_schema}.coordinator_decisions d
              ON d.project_id = g.project_id AND d.run_id = g.run_id
             AND d.decision_id = g.source_decision_id AND d.state_version = g.source_state_version
            WHERE p.project_id = @project AND p.run_id = @run AND p.receipt_id = @receipt
              AND p.issuer = @issuer AND p.actor_id = @actor AND p.tenant_id = @tenant
              AND p.session_id = @parent AND p.step_id = @step
              AND p.grant_id = @grant AND p.grant_revision = '1'
              AND p.purpose = @purpose AND p.action_id = @action
              AND p.outcome = 'allow' AND p.reason_code = 'allowed'
              AND p.execution_fence = @fence
              AND p.provider_id = @provider AND p.adapter_version = @adapterVersion
              AND p.options_schema_version = @optionsSchemaVersion
              AND p.options_revision = @optionsRevision
              AND g.is_current AND g.grant_state = 'active'
              AND g.issuer = @issuer AND g.actor_id = @actor AND g.tenant_id = @tenant
              AND g.session_id = @parent AND g.step_id = @step
              AND g.action_ids = '["maf.child.spawn"]'::jsonb
              AND g.purpose = @purpose AND g.execution_fence = @fence
              AND g.project_revision = @projectRevision
              AND g.project_configuration_revision = @configurationRevision
              AND g.platform_runtime_revision = @platformRevision
              AND g.context_revision = @contextRevision
              AND g.accepted_selection_hash = @selectionHash
              AND g.membership_revision = @membershipRevision
              AND g.role_revision = @roleRevision
              AND g.source_state_version = @planRevision
              AND g.source_request_id = @intentHash
              AND r.execution_fence = @fence AND r.execution_state NOT IN
                  ('completed', 'failed', 'indeterminate', 'cancelled')
              AND s.lifecycle_state = 'active' AND s.execution_fence = @fence
              AND s.turn_state = 'active'
              AND d.decision_state = 'accepted'
              AND d.state_version = @planRevision
            FOR SHARE OF g
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, evidence.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, evidence.RunId);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, evidence.PolicyReceiptId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, evidence.ActorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, evidence.ActorId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, evidence.TenantId);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, evidence.ParentSessionId);
        command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, evidence.WorkflowStepId);
        command.Parameters.AddWithValue(
            "grant", NpgsqlDbType.Varchar, GrantId(evidence.ProjectId, evidence.RunId, evidence.IntentId));
        command.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, MafChildSpawnAuthorizationContract.Purpose);
        command.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, MafChildSpawnAuthorizationContract.ActionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, evidence.ExecutionFence);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, evidence.PolicyProviderId);
        command.Parameters.AddWithValue("adapterVersion", NpgsqlDbType.Varchar, evidence.PolicyAdapterVersion);
        command.Parameters.AddWithValue("optionsSchemaVersion", NpgsqlDbType.Integer, evidence.PolicyOptionsSchemaVersion);
        command.Parameters.AddWithValue("optionsRevision", NpgsqlDbType.Varchar, evidence.PolicyOptionsRevision);
        command.Parameters.AddWithValue("intentHash", NpgsqlDbType.Varchar, evidence.IntentHash);
        command.Parameters.AddWithValue("planRevision", NpgsqlDbType.Bigint, evidence.PlanRevision);
        command.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, evidence.ProjectRevision);
        command.Parameters.AddWithValue("configurationRevision", NpgsqlDbType.Bigint, evidence.ProjectConfigurationRevision);
        command.Parameters.AddWithValue("platformRevision", NpgsqlDbType.Bigint, evidence.PlatformRuntimeRevision);
        command.Parameters.AddWithValue("contextRevision", NpgsqlDbType.Varchar, evidence.ContextRevision);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, evidence.AcceptedSelectionHash);
        command.Parameters.AddWithValue("membershipRevision", NpgsqlDbType.Bigint, evidence.MembershipRevision);
        command.Parameters.AddWithValue("roleRevision", NpgsqlDbType.Bigint, evidence.RoleRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new RuntimeAuthorizationException("maf_child_spawn_policy_receipt_unavailable");
    }

    private static void BindEvidence(NpgsqlCommand command, MafChildSpawnAuthorizationEvidence evidence)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, evidence.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, evidence.RunId);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Uuid, evidence.IntentId);
        command.Parameters.AddWithValue("intentHash", NpgsqlDbType.Char, evidence.IntentHash);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, evidence.ParentSessionId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, evidence.RootSessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, evidence.ActorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, evidence.ActorId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, evidence.TenantId);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, evidence.AcceptedSelectionHash);
        command.Parameters.AddWithValue("contextRevision", NpgsqlDbType.Varchar, evidence.ContextRevision);
        command.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, evidence.ProjectRevision);
        command.Parameters.AddWithValue("configurationRevision", NpgsqlDbType.Bigint, evidence.ProjectConfigurationRevision);
        command.Parameters.AddWithValue("platformRevision", NpgsqlDbType.Bigint, evidence.PlatformRuntimeRevision);
        command.Parameters.AddWithValue("membershipRevision", NpgsqlDbType.Bigint, evidence.MembershipRevision);
        command.Parameters.AddWithValue("roleRevision", NpgsqlDbType.Bigint, evidence.RoleRevision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, evidence.ExecutionFence);
        command.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, evidence.PlanId);
        command.Parameters.AddWithValue("planRevision", NpgsqlDbType.Bigint, evidence.PlanRevision);
        command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, evidence.WorkflowStepId);
        command.Parameters.AddWithValue("workItem", NpgsqlDbType.Varchar, evidence.WorkItemId);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, evidence.PolicyProviderId);
        command.Parameters.AddWithValue("adapterVersion", NpgsqlDbType.Varchar, evidence.PolicyAdapterVersion);
        command.Parameters.AddWithValue("optionsSchemaVersion", NpgsqlDbType.Integer, evidence.PolicyOptionsSchemaVersion);
        command.Parameters.AddWithValue("optionsRevision", NpgsqlDbType.Varchar, evidence.PolicyOptionsRevision);
    }

    private static void BindAuthority(NpgsqlCommand command, MafChildSpawnAuthorizationAuthority authority)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, authority.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, authority.RunId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, authority.ActorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, authority.ActorId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, authority.TenantId);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, authority.AcceptedSelectionHash);
        command.Parameters.AddWithValue("contextRevision", NpgsqlDbType.Varchar, authority.ContextRevision);
        command.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, authority.ProjectRevision);
        command.Parameters.AddWithValue("configurationRevision", NpgsqlDbType.Bigint, authority.ProjectConfigurationRevision);
        command.Parameters.AddWithValue("platformRevision", NpgsqlDbType.Bigint, authority.PlatformRuntimeRevision);
        command.Parameters.AddWithValue("membershipRevision", NpgsqlDbType.Bigint, authority.MembershipRevision);
        command.Parameters.AddWithValue("roleRevision", NpgsqlDbType.Bigint, authority.RoleRevision);
    }

    private static void BindScope(NpgsqlCommand command, MafChildSpawnAuthorizationAuthority authority)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, authority.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, authority.RunId);
    }

    private static string GrantId(string projectId, string runId, Guid intentId)
    {
        // Existing grants use globally unique IDs, so include the owner scope in the deterministic key.
        var scope = Encoding.UTF8.GetBytes($"{projectId}\n{runId}\n{intentId:N}");
        return $"maf.spawn.{Convert.ToHexStringLower(SHA256.HashData(scope))}";
    }

    private sealed record CurrentDecision(Guid DecisionId, long StateVersion, string DecisionJson);
}
