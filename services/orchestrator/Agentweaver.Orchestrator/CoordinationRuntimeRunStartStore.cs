using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class CoordinationRuntimeRunStartStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly RuntimeExecutionEvidenceLedgerStore _executionEvidence;

    public CoordinationRuntimeRunStartStore(NpgsqlDataSource dataSource, string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 63 ||
            schema.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !char.IsAsciiLetter(schema[0]))
            throw new ArgumentException("The Orchestrator schema name is invalid.", nameof(schema));
        _dataSource = dataSource;
        _schema = schema;
        _executionEvidence = new(schema);
    }

    public async Task<RuntimeRunStartIntent> EnsureIntentAsync(
        RuntimeOwnerContext owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ContractVersion != 1 ||
            owner.AgentId != CoordinationEndpoints.CoordinatorRuntimeAgentId ||
            owner.ExecutionFence <= 0 || owner.LogicalTurnOrdinal != 0 ||
            string.IsNullOrWhiteSpace(owner.TenantId) ||
            string.IsNullOrWhiteSpace(owner.AcceptedSelectionHash))
            throw new CoordinationException("runtime_start_owner_invalid", StatusCodes.Status409Conflict);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireCurrentRootAsync(connection, transaction, owner, cancellationToken).ConfigureAwait(false);
        var proposed = new RuntimeRunStartIntent(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId,
            owner.AcceptedSelectionHash, owner.ExecutionFence,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "pending", "accepted", null, null, null, "{}", false, 1);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id)
            VALUES (@tenant, @project, @run, @root, @selection, @fence, @intent,
                    @bootstrap, @consume, @exchange)
            ON CONFLICT (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash)
                DO NOTHING
            """, connection, transaction))
        {
            AddKey(insert, proposed);
            insert.Parameters.AddWithValue("intent", proposed.IntentId);
            insert.Parameters.AddWithValue("bootstrap", proposed.BootstrapOperationId);
            insert.Parameters.AddWithValue("consume", proposed.ConsumeOperationId);
            insert.Parameters.AddWithValue("exchange", proposed.ExchangeOperationId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var intent = await ReadIntentAsync(connection, transaction, proposed, forUpdate: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_start_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (intent.ExecutionFence != owner.ExecutionFence)
            throw new CoordinationException("runtime_start_fence_changed", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent;
    }

    public async Task PersistChildIntentInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string runId,
        string selectionHash,
        long executionFence,
        RuntimeChildStartBinding binding,
        CancellationToken cancellationToken)
    {
        await RequireChildStartSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var proposed = new RuntimeRunStartIntent(
            tenantId, projectId, runId, binding.ChildSessionId, selectionHash.ToLowerInvariant(),
            executionFence, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "pending", "accepted", null, null, null,
            JsonSerializer.Serialize(new { childStart = binding }, JsonOptions), false, 1);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id, receipt_links)
            VALUES (@tenant, @project, @run, @root, @selection, @fence, @intent,
                    @bootstrap, @consume, @exchange, @receipts)
            ON CONFLICT (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash)
                DO NOTHING
            """, connection, transaction))
        {
            AddKey(insert, proposed);
            insert.Parameters.AddWithValue("intent", proposed.IntentId);
            insert.Parameters.AddWithValue("bootstrap", proposed.BootstrapOperationId);
            insert.Parameters.AddWithValue("consume", proposed.ConsumeOperationId);
            insert.Parameters.AddWithValue("exchange", proposed.ExchangeOperationId);
            insert.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, proposed.ReceiptLinks);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var current = await ReadIntentAsync(
            connection, transaction, proposed, forUpdate: true, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_start_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (current.ExecutionFence != executionFence || ReadChildBinding(current) != binding)
            throw new CoordinationException("runtime_child_start_binding_conflict", StatusCodes.Status409Conflict);
    }

    public async Task<RuntimeRunStartIntent> EnsureChildIntentAsync(
        RuntimeOwnerContext owner, CancellationToken cancellationToken)
    {
        if (owner.ContractVersion != 1 || owner.AgentId == CoordinationEndpoints.CoordinatorRuntimeAgentId ||
            owner.LogicalTurnOrdinal < 1 || owner.ExecutionFence <= 0 ||
            string.IsNullOrWhiteSpace(owner.WorkflowStepId))
            throw new CoordinationException("runtime_child_start_owner_invalid", StatusCodes.Status409Conflict);
        var key = new RuntimeRunStartIntent(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId,
            owner.AcceptedSelectionHash.ToLowerInvariant(), owner.ExecutionFence,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, "", "", null, null, null, "{}", false, 0);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireChildStartSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var observed = await ReadIntentAsync(connection, transaction, key, forUpdate: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_child_start_intent_unavailable", StatusCodes.Status409Conflict);
        await RequireCurrentChildAsync(connection, transaction, observed, owner, cancellationToken)
            .ConfigureAwait(false);
        var intent = await ReadIntentAsync(connection, transaction, key, forUpdate: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_child_start_intent_unavailable", StatusCodes.Status409Conflict);
        if (intent.CleanupObligation || intent.ExecutionFence != owner.ExecutionFence ||
            ReadChildBinding(intent) != ReadChildBinding(observed))
            throw new CoordinationException("runtime_child_start_owner_stale", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent;
    }

    internal static RuntimeChildStartBinding ReadChildBinding(RuntimeRunStartIntent intent)
    {
        using var links = JsonDocument.Parse(intent.ReceiptLinks);
        if (!links.RootElement.TryGetProperty("childStart", out var value) ||
            value.Deserialize<RuntimeChildStartBinding>(JsonOptions) is not { } binding)
            throw new CoordinationException("runtime_child_start_binding_invalid", StatusCodes.Status409Conflict);
        return binding;
    }

    private async Task RequireCurrentChildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRunStartIntent intent,
        RuntimeOwnerContext owner,
        CancellationToken cancellationToken)
    {
        var binding = ReadChildBinding(intent);
        if (intent.CleanupObligation || owner.TenantId != intent.TenantId ||
            owner.ProjectId != intent.ProjectId || owner.RunId != intent.RunId ||
            owner.SessionId != intent.RootSessionId || binding.ChildSessionId != owner.SessionId ||
            owner.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            owner.ExecutionFence != intent.ExecutionFence ||
            owner.ActorIssuer != binding.ActorIssuer || owner.ActorId != binding.ActorId ||
            owner.AgentId != binding.AgentId || owner.ModelSelectionReference != binding.ModelSelectionReference ||
            owner.WorkflowStepId != binding.WorkflowStepId || owner.ContextRevision != binding.ContextRevision ||
            owner.DecisionStateVersion != binding.DecisionStateVersion)
            throw new CoordinationException("runtime_child_start_owner_stale", StatusCodes.Status409Conflict);
        await using var command = new NpgsqlCommand($"""
            SELECT r.tenant_id, r.accepted_selection_hash, r.execution_fence, r.execution_state,
                   r.accepted_by_issuer, r.accepted_by_subject,
                   s.parent_session_id, s.root_session_id, s.node_kind, s.lifecycle_state,
                   s.turn_state, s.logical_turn_ordinal, s.state_version, s.execution_fence,
                   s.work_plan_item_id, s.writer_issuer, s.writer_subject,
                   (SELECT d.state_version FROM {_schema}.coordinator_decisions d
                    WHERE d.project_id = r.project_id AND d.run_id = r.run_id
                      AND d.session_id = s.root_session_id
                    ORDER BY d.state_version DESC LIMIT 1)
            FROM {_schema}.accepted_runs r
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @session
              AND r.completed_at IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM {_schema}.coordination_sessions root
                  WHERE root.project_id = r.project_id AND root.run_id = r.run_id
                    AND root.parent_session_id IS NULL AND root.lifecycle_state = 'completed')
            FOR UPDATE OF r, s
            """, connection, transaction);
        command.Parameters.AddWithValue("project", owner.ProjectId);
        command.Parameters.AddWithValue("run", owner.RunId);
        command.Parameters.AddWithValue("session", owner.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != owner.TenantId ||
            !string.Equals(reader.GetString(1).Trim(), owner.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt64(2) != owner.ExecutionFence ||
            reader.GetString(3) is "completed" or "failed" or "indeterminate" or "cancelled" ||
            reader.GetString(4) != owner.ActorIssuer || reader.GetString(5) != owner.ActorId ||
            reader.IsDBNull(6) || reader.GetString(6) != binding.ParentRootSessionId ||
            reader.GetString(7) != binding.ParentRootSessionId ||
            reader.GetString(8) != "child_work" || reader.GetString(9) != "active" ||
            reader.GetString(10) != "active" || reader.GetInt64(11) != owner.LogicalTurnOrdinal ||
            reader.GetInt64(12) != owner.OwnerStateVersion || reader.GetInt64(13) != owner.ExecutionFence ||
            (reader.IsDBNull(14) ? null : reader.GetString(14)) != binding.WorkPlanItemId ||
            reader.GetString(15) != owner.ActorIssuer || reader.GetString(16) != owner.ActorId ||
            reader.IsDBNull(17) || reader.GetInt64(17) != owner.DecisionStateVersion)
            throw new CoordinationException("runtime_child_start_owner_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Child runtimes need the migration 047 terminal guard, because only version 2 cleanup can release them.
    /// </summary>
    private async Task RequireChildStartSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await IsTerminalCleanupGuardInstalledAsync(connection, transaction, _schema, cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException(
                "runtime_child_start_schema_pending", StatusCodes.Status503ServiceUnavailable);
    }

    public async Task<RuntimeRunStartLock> AcquireAsync(
        RuntimeRunStartIntent intent, CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtextextended(@key, 0))", connection);
            command.Parameters.AddWithValue("key", LockKey(intent));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new RuntimeRunStartLock(connection, LockKey(intent));
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Takes the intent lease only when no other owner holds it; request paths must not wait.</summary>
    public async Task<RuntimeRunStartLock?> TryAcquireAsync(
        RuntimeRunStartIntent intent, CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", connection);
            command.Parameters.AddWithValue("key", LockKey(intent));
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return new RuntimeRunStartLock(connection, LockKey(intent));
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RuntimeRunStartIntent?> ReadAsync(
        RuntimeRunStartIntent key, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadIntentAsync(connection, null, key, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeStartupSummary?> ReadSummaryAsync(
        string projectId, string runId, string rootSessionId, CancellationToken cancellationToken)
    {
        return (await ReadIntentForStatusAsync(projectId, runId, rootSessionId, cancellationToken)
            .ConfigureAwait(false))?.Summary;
    }

    public async Task<RuntimeRunStartIntent?> ReadIntentForStatusAsync(
        string projectId, string runId, string rootSessionId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, intent_id,
                   bootstrap_operation_id, consume_operation_id, exchange_operation_id, state,
                   current_step, failure_code, environment_id, runtime_instance_id,
                   receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
            LIMIT 2
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("root", rootSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var intent = new RuntimeRunStartIntent(
            reader.GetString(0),
            projectId,
            runId,
            rootSessionId,
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11),
            reader.GetString(12),
            reader.GetBoolean(13),
            reader.GetInt64(14));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_intent_ambiguous", StatusCodes.Status409Conflict);
        return intent;
    }

    public async Task<(RuntimeRunStartIntent Intent, RuntimeReplacementOperation Operation)> BeginReplacementAsync(
        RuntimeRunStartIntent intent,
        RuntimeOwnerContext owner,
        RuntimeReplacementRequest request,
        RuntimeRegistration registration,
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRootTurnCompletionLedgerReceipt completion,
        CancellationToken cancellationToken)
    {
        request.Validate();
        placement.Validate();
        RuntimeContractValidation.Validate(registration);
        completion.Validate();
        if (owner.TenantId != intent.TenantId || owner.ProjectId != intent.ProjectId ||
            owner.RunId != intent.RunId || owner.SessionId != intent.RootSessionId ||
            owner.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            owner.ExecutionFence != intent.ExecutionFence ||
            request.ExpectedExecutionFence != intent.ExecutionFence ||
            request.ExpectedAcceptedSelectionHash != intent.AcceptedSelectionHash ||
            request.ExpectedRuntimeInstanceId != registration.RuntimeInstanceId ||
            request.ExpectedRegistrationRevision != registration.Revision ||
            completion.Completion.SuccessfulToolCount != 0 ||
            completion.Completion.FailedToolCount != 0 ||
            !completion.Completion.CacheBaselineIsComplete ||
            completion.Completion.Registration.Binding.SessionId != intent.RootSessionId ||
            completion.Completion.Registration.Binding.ExecutionFence != intent.ExecutionFence ||
            completion.Completion.Registration.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            registration.Binding.SessionId != intent.RootSessionId ||
            registration.Binding.ExecutionFence != intent.ExecutionFence ||
            registration.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash)
            throw new CoordinationException("runtime_replacement_request_stale", StatusCodes.Status409Conflict);

        var requestHash = RuntimeContractValidation.Hash(
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));
        var initialLinks = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["previousPlacement"] = placement,
            ["previousRegistration"] = registration,
            ["completion"] = completion,
            ["previousBootstrapOperationId"] = intent.BootstrapOperationId,
            ["previousConsumeOperationId"] = intent.ConsumeOperationId,
            ["previousExchangeOperationId"] = intent.ExchangeOperationId
        };
        var linksJson = JsonSerializer.Serialize(initialLinks, JsonOptions);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireCurrentRootAsync(connection, transaction, owner, cancellationToken).ConfigureAwait(false);
        var currentIntent = await ReadIntentAsync(
            connection, transaction, intent, forUpdate: true, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_start_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (currentIntent.CleanupObligation ||
            currentIntent.ExecutionFence != owner.ExecutionFence ||
            currentIntent.AcceptedSelectionHash != owner.AcceptedSelectionHash)
            throw new CoordinationException("runtime_replacement_request_stale", StatusCodes.Status409Conflict);

        var existing = await ReadReplacementOperationAsync(
            connection, transaction, currentIntent, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash ||
                existing.ExpectedRuntimeInstanceId != request.ExpectedRuntimeInstanceId ||
                existing.ExpectedRegistrationRevision != request.ExpectedRegistrationRevision)
                throw new CoordinationException("runtime_replacement_operation_conflict", StatusCodes.Status409Conflict);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (currentIntent, existing);
        }

        if (currentIntent.State != "ready" ||
            currentIntent.RuntimeInstanceId != request.ExpectedRuntimeInstanceId)
            throw new CoordinationException("runtime_replacement_in_progress", StatusCodes.Status409Conflict);
        var currentLinks = ReadReceiptLinks(currentIntent);
        if (!currentLinks.TryGetValue("registration", out var currentRegistrationElement) ||
            currentRegistrationElement.Deserialize<RuntimeRegistration>(JsonOptions) != registration ||
            !currentLinks.TryGetValue("placement", out var currentPlacementElement) ||
            currentPlacementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) != placement)
            throw new CoordinationException("runtime_replacement_request_stale", StatusCodes.Status409Conflict);

        await RequireCompleteRootWorkAsync(
            connection, transaction, currentIntent, registration, completion, cancellationToken)
            .ConfigureAwait(false);

        await using (var pending = new NpgsqlCommand($"""
            SELECT 1 FROM {_schema}.runtime_replacement_operations
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND state = 'pending'
            LIMIT 1
            """, connection, transaction))
        {
            AddKey(pending, currentIntent);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("runtime_replacement_in_progress", StatusCodes.Status409Conflict);
        }

        var operation = new RuntimeReplacementOperation(
            intent.TenantId, intent.ProjectId, intent.RunId, intent.RootSessionId,
            request.OperationId, requestHash, request.ExpectedRuntimeInstanceId,
            request.ExpectedRegistrationRevision, request.ExpectedAcceptedSelectionHash,
            request.ExpectedExecutionFence, "pending", "replacement-retirement", null, linksJson, 1);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.runtime_replacement_operations
                (tenant_id, project_id, run_id, root_session_id, operation_id, request_hash,
                 expected_runtime_instance_id, expected_registration_revision,
                 accepted_selection_hash, execution_fence, state, current_step, receipt_links)
            VALUES (@tenant, @project, @run, @root, @operation, @hash, @runtime,
                    @revision, @selection, @fence, 'pending', 'replacement-retirement', @receipts)
            """, connection, transaction))
        {
            AddKey(insert, currentIntent);
            insert.Parameters.AddWithValue("operation", request.OperationId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
            insert.Parameters.AddWithValue("runtime", request.ExpectedRuntimeInstanceId);
            insert.Parameters.AddWithValue("revision", request.ExpectedRegistrationRevision);
            insert.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, linksJson);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        currentLinks["replacementOperationId"] =
            JsonSerializer.SerializeToElement(request.OperationId, JsonOptions);
        var updatedIntent = await UpdateInTransactionAsync(
            connection, transaction, currentIntent, currentIntent.RowVersion,
            "pending", "replacement-retirement", null, currentIntent.EnvironmentId,
            currentIntent.RuntimeInstanceId, currentLinks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (updatedIntent, operation);
    }

    private async Task RequireCompleteRootWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRunStartIntent intent,
        RuntimeRegistration registration,
        RuntimeRootTurnCompletionLedgerReceipt expectedLatest,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var admissions = new List<(Guid RequestId, string PromptHash)>();
        await using (var command = new NpgsqlCommand($"""
            SELECT request_id, prompt_hash
            FROM {_schema}.maf_root_model_turn_admissions
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
              AND actor_issuer = @issuer AND actor_id = @actor AND tenant_id = @tenant
              AND accepted_selection_hash = @selection AND execution_fence = @fence
            ORDER BY created_at DESC, request_id DESC
            LIMIT 65
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", binding.ProjectId);
            command.Parameters.AddWithValue("run", binding.RunId);
            command.Parameters.AddWithValue("root", binding.SessionId);
            command.Parameters.AddWithValue("issuer", binding.ActorIssuer);
            command.Parameters.AddWithValue("actor", binding.ActorId);
            command.Parameters.AddWithValue("tenant", binding.TenantId);
            command.Parameters.AddWithValue("selection", binding.AcceptedSelectionHash);
            command.Parameters.AddWithValue("fence", binding.ExecutionFence);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                admissions.Add((reader.GetGuid(0), reader.GetString(1).Trim()));
        }

        if (admissions.Count is 0 or > 64)
            throw new CoordinationException("runtime_replacement_root_work_unavailable", StatusCodes.Status409Conflict);
        RuntimeRootTurnCompletionLedgerReceipt? latest = null;
        foreach (var admission in admissions)
        {
            var stored = await _executionEvidence.ReadRootTurnCompletionAsync(
                connection, transaction, intent.ProjectId, intent.RunId, admission.RequestId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null ||
                stored.Completion.PromptHash != admission.PromptHash ||
                stored.Completion.SuccessfulToolCount != 0 ||
                stored.Completion.FailedToolCount != 0 ||
                !await HasVerifiedReplacementLineageAsync(
                    intent, stored.Completion.Registration, registration, cancellationToken)
                    .ConfigureAwait(false))
                throw new CoordinationException("runtime_replacement_root_work_pending", StatusCodes.Status409Conflict);
            if (latest is null)
                latest = stored;
        }

        if (latest is null || !latest.HasSameReceipt(expectedLatest))
            throw new CoordinationException("runtime_replacement_root_work_stale", StatusCodes.Status409Conflict);
    }

    public async Task<RuntimeReplacementOperation?> ReadReplacementOperationAsync(
        RuntimeRunStartIntent intent, Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReplacementOperationAsync(
            connection, null, intent, operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasVerifiedReplacementLineageAsync(
        RuntimeRunStartIntent intent,
        RuntimeRegistration source,
        RuntimeRegistration current,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.Validate(source);
        RuntimeContractValidation.Validate(current);
        if (source == current)
            return true;

        var sourceBinding = source.Binding;
        var currentBinding = current.Binding;
        if (sourceBinding.TenantId != intent.TenantId || currentBinding.TenantId != intent.TenantId ||
            sourceBinding.ProjectId != intent.ProjectId || currentBinding.ProjectId != intent.ProjectId ||
            sourceBinding.RunId != intent.RunId || currentBinding.RunId != intent.RunId ||
            sourceBinding.SessionId != intent.RootSessionId || currentBinding.SessionId != intent.RootSessionId ||
            sourceBinding.ExecutionFence != intent.ExecutionFence ||
            currentBinding.ExecutionFence != intent.ExecutionFence ||
            sourceBinding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            currentBinding.AcceptedSelectionHash != intent.AcceptedSelectionHash)
            return false;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var operations = new List<RuntimeReplacementOperation>();
        await using (var command = new NpgsqlCommand($"""
            SELECT operation_id, request_hash, expected_runtime_instance_id,
                   expected_registration_revision, accepted_selection_hash, execution_fence,
                   state, current_step, failure_code, receipt_links::text, row_version
            FROM {_schema}.runtime_replacement_operations
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND state = 'ready'
            """, connection, transaction))
        {
            AddKey(command, intent);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                operations.Add(new RuntimeReplacementOperation(
                    intent.TenantId, intent.ProjectId, intent.RunId, intent.RootSessionId,
                    reader.GetGuid(0), reader.GetString(1).Trim(), reader.GetGuid(2), reader.GetInt64(3),
                    reader.GetString(4).Trim(), reader.GetInt64(5), reader.GetString(6),
                    reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetString(9), reader.GetInt64(10)));
            }
        }

        var pending = new List<(
            RuntimeReplacementOperation Operation,
            RuntimeRegistration Previous,
            RuntimeRegistration Replacement,
            RuntimeRootTurnCompletionLedgerReceipt Completion)>();
        foreach (var operation in operations)
        {
            using var links = JsonDocument.Parse(operation.ReceiptLinks);
            var root = links.RootElement;
            if (!root.TryGetProperty("previousRegistration", out var previousValue) ||
                !root.TryGetProperty("currentRegistration", out var currentValue) ||
                !root.TryGetProperty("previousPlacement", out var previousPlacementValue) ||
                !root.TryGetProperty("currentPlacement", out var currentPlacementValue) ||
                !root.TryGetProperty("completion", out var completionValue) ||
                !root.TryGetProperty("currentActivation", out var activationValue))
                return false;

            var previous = previousValue.Deserialize<RuntimeRegistration>(JsonOptions);
            var replacement = currentValue.Deserialize<RuntimeRegistration>(JsonOptions);
            var previousPlacement = previousPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions);
            var currentPlacement = currentPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions);
            var completion = completionValue.Deserialize<RuntimeRootTurnCompletionLedgerReceipt>(JsonOptions);
            var activation = activationValue.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions);
            if (previous is null || replacement is null || previousPlacement is null ||
                currentPlacement is null || completion is null || activation?.Recovery is null ||
                operation.State != "ready" || operation.CurrentStep != "ready" ||
                operation.ExpectedRuntimeInstanceId != previous.RuntimeInstanceId ||
                operation.ExpectedRegistrationRevision != previous.Revision ||
                RuntimeRegistrationStore.CreateReplacementId(
                    previous.RuntimeInstanceId, operation.OperationId) != replacement.RuntimeInstanceId ||
                previous.Binding.TenantId != intent.TenantId ||
                previous.Binding.ProjectId != intent.ProjectId ||
                previous.Binding.RunId != intent.RunId ||
                previous.Binding.SessionId != intent.RootSessionId ||
                previous.Binding.ExecutionFence != intent.ExecutionFence ||
                previous.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
                replacement.Binding.TenantId != intent.TenantId ||
                replacement.Binding.ProjectId != intent.ProjectId ||
                replacement.Binding.RunId != intent.RunId ||
                replacement.Binding.SessionId != intent.RootSessionId ||
                replacement.Binding.ExecutionFence != intent.ExecutionFence ||
                replacement.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash)
                return false;

            previousPlacement.Validate();
            currentPlacement.Validate();
            var owner = RuntimeRunStartContinuation.CreateOwnerContext(previous.Binding);
            RuntimeRunStartContinuation.RequireReplacementRegistration(owner, previousPlacement, previous);
            RuntimeRunStartContinuation.RequireReplacementPlacement(
                previousPlacement,
                currentPlacement,
                RuntimeRunStartKeys.ReplacementOperationId(operation.OperationId, "bootstrap"),
                owner);
            RuntimeRunStartContinuation.RequireReplacementRegistration(owner, currentPlacement, replacement);
            _ = RuntimeRunStartContinuation.CreateReplacementSummary(
                operation, previous, previousPlacement);
            completion.Validate();
            _ = new RuntimeHostReplacementRecovery(
                completion.Completion.Registration,
                completion.Completion.ToEvidence(),
                completion.Completion.CacheBaselineCursor).ValidateFor(replacement);
            var recovery = activation.Recovery.ValidateFor(replacement);
            if (recovery.RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
                recovery.NativeCacheAcknowledgment?.Reference != completion.Completion.CacheMaterial.Reference ||
                !RuntimeRunStartContinuation.MatchesReplacementReconstructionInterval(
                    recovery, completion.Completion.CacheBaselineCursorHash))
                return false;
            var storedCompletion = await _executionEvidence.ReadRootTurnCompletionAsync(
                connection, transaction, completion.ProjectId, completion.RunId,
                completion.RequestId, cancellationToken).ConfigureAwait(false);
            if (storedCompletion is null || !storedCompletion.HasSameReceipt(completion))
                return false;
            pending.Add((operation, previous, replacement, completion));
        }

        var edges = new List<(RuntimeRegistration Previous, RuntimeRegistration Current)>();
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var candidate = pending[index];
                var completionRegistration = candidate.Completion.Completion.Registration;
                if (completionRegistration != candidate.Previous &&
                    (!edges.Any(edge =>
                        edge.Previous == completionRegistration || edge.Current == completionRegistration) ||
                     !HasPath(completionRegistration.RuntimeInstanceId, candidate.Previous.RuntimeInstanceId, edges)))
                    continue;
                if (edges.Any(edge =>
                    edge.Previous.RuntimeInstanceId == candidate.Previous.RuntimeInstanceId ||
                    edge.Current.RuntimeInstanceId == candidate.Replacement.RuntimeInstanceId))
                    return false;
                edges.Add((candidate.Previous, candidate.Replacement));
                pending.RemoveAt(index);
                progressed = true;
            }
            if (!progressed)
                return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return HasPath(source.RuntimeInstanceId, current.RuntimeInstanceId, edges) &&
            edges.Any(edge => edge.Previous == source) &&
            edges.Any(edge => edge.Current == current);
    }

    public async Task<(RuntimeRunStartIntent Intent, RuntimeReplacementOperation Operation)>
        UpdateReplacementProgressAsync(
            RuntimeRunStartIntent intent,
            RuntimeReplacementOperation operation,
            string state,
            string step,
            string? failureCode,
            string operationReceiptLinks,
            string intentReceiptLinks,
            Guid? bootstrapOperationId,
            Guid? consumeOperationId,
            Guid? exchangeOperationId,
            string? environmentId,
            Guid? runtimeInstanceId,
            CancellationToken cancellationToken)
    {
        if (state is not ("pending" or "ready" or "blocked" or "failed") ||
            string.IsNullOrWhiteSpace(step) || step.Length > 48 ||
            failureCode is { Length: > 96 })
            throw new ArgumentException("The runtime replacement update is invalid.");
        using var opDocument = JsonDocument.Parse(operationReceiptLinks);
        using var intentDocument = JsonDocument.Parse(intentReceiptLinks);
        if (opDocument.RootElement.ValueKind != JsonValueKind.Object ||
            intentDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Runtime replacement receipt links must be objects.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentIntent = await ReadIntentAsync(
            connection, transaction, intent, forUpdate: true, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_start_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        var currentOperation = await ReadReplacementOperationAsync(
            connection, transaction, intent, operation.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_replacement_operation_not_found", StatusCodes.Status404NotFound);
        if (currentOperation.RowVersion != operation.RowVersion ||
            currentOperation.RequestHash != operation.RequestHash ||
            currentIntent.CleanupObligation ||
            currentIntent.RowVersion != intent.RowVersion)
            throw new CoordinationException("runtime_replacement_operation_stale", StatusCodes.Status409Conflict);

        await using (var updateOperation = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_replacement_operations
            SET state = @state, current_step = @step, failure_code = @failure,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND operation_id = @operation
              AND row_version = @version AND state = 'pending'
            RETURNING row_version
            """, connection, transaction))
        {
            AddKey(updateOperation, currentIntent);
            updateOperation.Parameters.AddWithValue("operation", operation.OperationId);
            updateOperation.Parameters.AddWithValue("state", state);
            updateOperation.Parameters.AddWithValue("step", step);
            updateOperation.Parameters.AddWithValue("failure", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
            updateOperation.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, operationReceiptLinks);
            updateOperation.Parameters.AddWithValue("version", operation.RowVersion);
            _ = await updateOperation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                as long? ?? throw new CoordinationException(
                    "runtime_replacement_operation_stale", StatusCodes.Status409Conflict);
        }

        var intentState = state == "ready" ? "ready" : "pending";
        await using (var updateIntent = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET bootstrap_operation_id = COALESCE(@bootstrap, bootstrap_operation_id),
                consume_operation_id = COALESCE(@consume, consume_operation_id),
                exchange_operation_id = COALESCE(@exchange, exchange_operation_id),
                state = @state, current_step = @step, failure_code = @failure,
                environment_id = @environment, runtime_instance_id = @runtime,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND row_version = @version AND NOT cleanup_obligation
            RETURNING row_version
            """, connection, transaction))
        {
            AddKey(updateIntent, currentIntent);
            updateIntent.Parameters.AddWithValue("bootstrap", NpgsqlDbType.Uuid, (object?)bootstrapOperationId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("consume", NpgsqlDbType.Uuid, (object?)consumeOperationId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("exchange", NpgsqlDbType.Uuid, (object?)exchangeOperationId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("state", intentState);
            updateIntent.Parameters.AddWithValue("step", step);
            updateIntent.Parameters.AddWithValue("failure", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("environment", NpgsqlDbType.Varchar, (object?)environmentId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, (object?)runtimeInstanceId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, intentReceiptLinks);
            updateIntent.Parameters.AddWithValue("version", intent.RowVersion);
            _ = await updateIntent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                as long? ?? throw new CoordinationException(
                    "runtime_start_intent_stale", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (
            currentIntent with
            {
                BootstrapOperationId = bootstrapOperationId ?? currentIntent.BootstrapOperationId,
                ConsumeOperationId = consumeOperationId ?? currentIntent.ConsumeOperationId,
                ExchangeOperationId = exchangeOperationId ?? currentIntent.ExchangeOperationId,
                State = intentState,
                CurrentStep = step,
                FailureCode = failureCode,
                EnvironmentId = environmentId,
                RuntimeInstanceId = runtimeInstanceId,
                ReceiptLinks = intentReceiptLinks,
                RowVersion = currentIntent.RowVersion + 1
            },
            operation with
            {
                State = state,
                CurrentStep = step,
                FailureCode = failureCode,
                ReceiptLinks = operationReceiptLinks,
                RowVersion = operation.RowVersion + 1
            });
    }

    public async Task<RuntimeRunStartCleanupProjection> ReadCleanupProjectionAsync(
        string projectId,
        string runId,
        string rootSessionId,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken,
        int contractVersion = 1,
        bool requireQuiescence = true)
    {
        if (contractVersion is not (1 or 2))
            throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
        var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, null, "runtime_run_start_intents", cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
        if (contractVersion == 2 &&
            !await IsTerminalCleanupGuardInstalledAsync(connection, null, _schema, cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_guard_unavailable", StatusCodes.Status503ServiceUnavailable);
        string tenantId;
        string runState;
        long terminalFence;
        await using (var run = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            run.Parameters.AddWithValue("project", projectId);
            run.Parameters.AddWithValue("run", runId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                (contractVersion == 1
                    ? reader.GetString(3) != "cancelled"
                    : reader.GetString(3) is not ("cancelled" or "completed" or "failed" or "indeterminate")) ||
                !string.Equals(reader.GetString(1).Trim(), selectionHash, StringComparison.OrdinalIgnoreCase))
                throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
            tenantId = reader.GetString(0);
            terminalFence = reader.GetInt64(2);
            runState = reader.GetString(3);
        }

        if (contractVersion == 2 && runState != "cancelled" &&
            !await HasTerminalCleanupMarkerAsync(
                connection, null, _schema, tenantId, projectId, runId, runState, terminalFence, cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
        if (requireQuiescence && await HasUnresolvedNativeWorkAsync(
                connection, null, projectId, runId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_work_pending", StatusCodes.Status409Conflict);
        await using var command = new NpgsqlCommand($"""
            SELECT i.execution_fence, i.intent_id, i.bootstrap_operation_id, i.consume_operation_id,
                   i.exchange_operation_id, i.state, i.current_step, i.failure_code, i.environment_id,
                   i.runtime_instance_id, i.receipt_links::text, i.cleanup_obligation, i.row_version,
                   s.root_session_id
            FROM {_schema}.runtime_run_start_intents i
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = i.project_id AND s.run_id = i.run_id
             AND s.session_id = i.root_session_id
            WHERE i.tenant_id = @tenant AND i.project_id = @project AND i.run_id = @run
              AND i.root_session_id = @root AND i.accepted_selection_hash = @selection
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("root", rootSessionId);
        command.Parameters.AddWithValue("selection", selectionHash);
        await using var intentReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await intentReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !intentReader.GetBoolean(11) ||
            intentReader.GetString(5) is not ("cleanup_pending" or "cleanup_complete") ||
            contractVersion == 1 && intentReader.GetString(13) != rootSessionId)
            throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
        var executionFence = intentReader.GetInt64(0);
        if (runState == "cancelled" ? terminalFence <= executionFence : terminalFence < executionFence)
            throw new CoordinationException("runtime_start_cleanup_fence_stale", StatusCodes.Status409Conflict);
        using var links = JsonDocument.Parse(intentReader.GetString(10));
        var projection = new RuntimeRunStartCleanupProjection(
            contractVersion,
            tenantId,
            projectId,
            runId,
            rootSessionId,
            selectionHash,
            runState,
            contractVersion == 1 ? terminalFence : 0,
            intentReader.GetGuid(1),
            intentReader.GetInt64(12),
            executionFence,
            intentReader.GetGuid(2),
            intentReader.IsDBNull(8) ? null : intentReader.GetString(8),
            intentReader.GetString(5),
            links.RootElement.Clone())
        {
            TerminalFence = contractVersion == 2 ? terminalFence : null,
            ParentRootSessionId = contractVersion == 2 ? intentReader.GetString(13) : null
        }.Validate();
        var isRoot = intentReader.GetString(13) == rootSessionId;
        await intentReader.CloseAsync().ConfigureAwait(false);
        if (requireQuiescence && isRoot && await HasIncompleteChildCleanupAsync(
                connection, null, projectId, runId, rootSessionId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_child_cleanup_pending", StatusCodes.Status409Conflict);
        return projection;
    }

    private async Task<bool> HasUnresolvedNativeWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        // Workflow generation jobs start at migration 046; earlier schemas have no such work.
        var hasJobs = await TableExistsAsync(
            connection, transaction, "workflow_generation_jobs", cancellationToken).ConfigureAwait(false);
        var jobs = hasJobs
            ? $"""
                SELECT 1 FROM {_schema}.workflow_generation_jobs
                WHERE project_id = @project AND run_id = @run
                  AND execution_phase = 'send-admitted'
                  AND status IN ('running', 'cancelled', 'failed')
                UNION ALL
                """
            : string.Empty;
        var quiescedJob = hasJobs
            ? $"""
                  AND NOT EXISTS (
                      SELECT 1 FROM {_schema}.workflow_generation_jobs j
                      WHERE j.project_id = a.project_id AND j.run_id = a.run_id
                        AND j.native_request_id = a.request_id AND j.execution_phase = 'quiesced')
                """
            : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                {jobs}
                SELECT 1 FROM {_schema}.maf_execution_dispatches
                WHERE project_id = @project AND run_id = @run
                  AND dispatch_state IN ('sending', 'terminal_pending_accounting', 'indeterminate')
                UNION ALL
                SELECT 1 FROM {_schema}.maf_root_model_turn_admissions a
                WHERE a.project_id = @project AND a.run_id = @run
                  AND NOT EXISTS (
                      SELECT 1 FROM {_schema}.maf_runtime_evidence_receipts e
                      WHERE e.project_id = a.project_id AND e.run_id = a.run_id
                        AND e.attempt_id = a.request_id
                        AND e.receipt_kind IN ('native-root-turn-completion', 'numeric-limit-native-no-send'))
                {quiescedJob}
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Text, $"{_schema}.{table}");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal static async Task<bool> IsTerminalCleanupGuardInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (SELECT 1 FROM {schema}.coordination_schema_migrations WHERE version = 47)
                AND EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = to_regclass(@runs)
                      AND tgname = 'accepted_run_terminal_cleanup_guard'
                      AND tgfoid = to_regprocedure(@guard)
                      AND tgtype = 19 AND tgenabled = 'O' AND NOT tgisinternal)
            """, connection, transaction);
        command.Parameters.AddWithValue("runs", $"{schema}.accepted_runs");
        command.Parameters.AddWithValue("guard", $"{schema}.guard_terminal_cleanup_recovery()");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> IsTerminalCleanupGuardInstalledAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IsTerminalCleanupGuardInstalledAsync(
            connection, null, _schema, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> HasTerminalCleanupMarkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schema,
        string tenantId,
        string projectId,
        string runId,
        string state,
        long fence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {schema}.project_run_cancellations
                WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                  AND result -> 'terminalCleanup' ->> 'state' = @state
                  AND result -> 'terminalCleanup' ->> 'fence' = @fence)
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("fence", fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<RuntimeRunStartIntent?> FindCleanupIntentAsync(
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var intents = await FindCleanupIntentsAsync(
            tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (intents.Count > 1)
            throw new CoordinationException("runtime_start_cleanup_ambiguous", StatusCodes.Status409Conflict);
        return intents.Count == 0 ? null : intents[0];
    }

    public async Task<RuntimeCleanupSummary?> ReadCleanupSummaryAsync(
        string tenantId, string projectId, string runId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Runtime start intents begin at migration 042; earlier schemas have no runtime cleanup to report.
        if (!await TableExistsAsync(connection, null, "runtime_run_start_intents", cancellationToken)
                .ConfigureAwait(false))
            return null;
        var intents = await FindCleanupIntentsAsync(
            tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (intents.Count == 0)
        {
            await using var run = new NpgsqlCommand($"""
                SELECT execution_state FROM {_schema}.accepted_runs
                WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                """, connection);
            run.Parameters.AddWithValue("tenant", tenantId);
            run.Parameters.AddWithValue("project", projectId);
            run.Parameters.AddWithValue("run", runId);
            if (await run.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is not ("completed" or "failed" or "indeterminate" or "cancelled"))
                return null;
        }
        var complete = intents.Count(intent => intent.State == "cleanup_complete");
        var pending = intents.Count - complete;
        var unknown = await HasUntrackedCleanupResourcesAsync(
            connection, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var unresolved = await HasUnresolvedNativeWorkAsync(
            connection, null, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (unknown || unresolved)
            pending++;
        if (intents.Count == 0 && pending == 0)
            return null;
        return new RuntimeCleanupSummary(
            pending == 0 ? "complete" : "pending", complete + pending, pending, complete);
    }

    private async Task<bool> HasIncompleteChildCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_schema}.coordination_sessions s
                JOIN {_schema}.runtime_run_start_intents i
                  ON i.project_id = s.project_id AND i.run_id = s.run_id
                 AND i.root_session_id = s.session_id AND i.receipt_links ? 'childStart'
                WHERE s.project_id = @project AND s.run_id = @run AND s.root_session_id = @root
                  AND s.node_kind = 'child_work'
                  AND NOT (i.cleanup_obligation AND i.state = 'cleanup_complete')
                UNION ALL
                SELECT 1 FROM {_schema}.runtime_registration_heads h
                JOIN {_schema}.runtime_registration_revisions v
                  ON v.runtime_instance_id = h.runtime_instance_id AND v.revision = h.current_revision
                WHERE v.state = 0 AND v.binding_json::jsonb ->> 'ProjectId' = @project
                  AND v.binding_json::jsonb ->> 'RunId' = @run
                  AND v.binding_json::jsonb ->> 'SessionId' IS DISTINCT FROM @root)
            """, connection, transaction);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("root", rootSessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<bool> HasUntrackedCleanupResourcesAsync(
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_schema}.runtime_run_start_intents i
                WHERE i.tenant_id = @tenant AND i.project_id = @project AND i.run_id = @run
                  AND i.receipt_links ? 'childStart' AND NOT i.cleanup_obligation
                UNION ALL
                SELECT 1 FROM {_schema}.runtime_registration_heads h
                JOIN {_schema}.runtime_registration_revisions v
                  ON v.runtime_instance_id = h.runtime_instance_id AND v.revision = h.current_revision
                WHERE v.state = 0 AND v.binding_json::jsonb ->> 'ProjectId' = @project
                  AND v.binding_json::jsonb ->> 'RunId' = @run
                  AND NOT EXISTS (
                      SELECT 1 FROM {_schema}.runtime_run_start_intents i
                      WHERE i.tenant_id = @tenant AND i.project_id = @project AND i.run_id = @run
                        AND i.root_session_id = v.binding_json::jsonb ->> 'SessionId'
                        AND i.cleanup_obligation AND i.state <> 'cleanup_complete'))
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<RuntimeRunStartIntent>> FindCleanupIntentsAsync(
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, null, "runtime_run_start_intents", cancellationToken)
                .ConfigureAwait(false))
            return [];
        await using var command = new NpgsqlCommand($"""
            SELECT root_session_id, accepted_selection_hash, execution_fence, intent_id,
                   bootstrap_operation_id, consume_operation_id, exchange_operation_id, state,
                   current_step, failure_code, environment_id, runtime_instance_id,
                   receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND cleanup_obligation
            ORDER BY root_session_id
            LIMIT 102
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var intents = new List<RuntimeRunStartIntent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            intents.Add(new RuntimeRunStartIntent(
                tenantId, projectId, runId,
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6),
                reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.GetString(12), reader.GetBoolean(13), reader.GetInt64(14)));
        }
        if (intents.Count > 101)
            throw new CoordinationException("runtime_start_cleanup_limit_exceeded", StatusCodes.Status409Conflict);
        return intents;
    }

    public async Task RequireRegistrationCleanupOwnerAsync(
        RuntimeRunStartIntent intent,
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_by_issuer, r.accepted_by_subject, s.writer_issuer, s.writer_subject
            FROM {_schema}.accepted_runs r
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @session
            """, connection);
        command.Parameters.AddWithValue("project", intent.ProjectId);
        command.Parameters.AddWithValue("run", intent.RunId);
        command.Parameters.AddWithValue("session", intent.RootSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != registration.Binding.ActorIssuer ||
            reader.GetString(1) != registration.Binding.ActorId ||
            reader.GetString(2) != registration.Binding.ActorIssuer ||
            reader.GetString(3) != registration.Binding.ActorId)
            throw new CoordinationException("runtime_start_cleanup_registration_stale", StatusCodes.Status409Conflict);
    }

    public Task<RuntimeRunStartIntent> RecordCleanupPendingAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        RuntimeRunStartCleanupReceipt receipt,
        RuntimeRunStartCleanupProjection projection,
        CancellationToken cancellationToken) =>
        UpdateCleanupAsync(intent, expectedVersion, "cleanup_pending", receipt, projection, cancellationToken);

    public Task<RuntimeRunStartIntent> MarkCleanupCompleteAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        RuntimeRunStartCleanupReceipt receipt,
        RuntimeRunStartCleanupProjection projection,
        CancellationToken cancellationToken) =>
        UpdateCleanupAsync(intent, expectedVersion, "cleanup_complete", receipt, projection, cancellationToken);

    private async Task<RuntimeRunStartIntent> UpdateCleanupAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        string state,
        RuntimeRunStartCleanupReceipt receipt,
        RuntimeRunStartCleanupProjection projection,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        projection.Validate();
        if (receipt.IntentId != intent.IntentId ||
            (state == "cleanup_complete") != (receipt.CleanupDisposition == "cleanup_complete"))
            throw new CoordinationException("runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
        using var originalLinks = JsonDocument.Parse(intent.ReceiptLinks);
        if (projection.IntentId != intent.IntentId || projection.IntentVersion != expectedVersion ||
            projection.TenantId != intent.TenantId || projection.ProjectId != intent.ProjectId ||
            projection.RunId != intent.RunId || projection.RootSessionId != intent.RootSessionId ||
            projection.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            projection.ExecutionFence != intent.ExecutionFence ||
            projection.BootstrapOperationId != intent.BootstrapOperationId ||
            projection.EnvironmentId != intent.EnvironmentId ||
            !JsonElement.DeepEquals(projection.ReceiptLinks, originalLinks.RootElement))
            throw new CoordinationException("runtime_start_cleanup_stale", StatusCodes.Status409Conflict);
        var links = ReadReceiptLinks(intent);
        if (receipt.LeaseState == "absent")
        {
            if (links.ContainsKey("cleanupAttempt"))
                throw new CoordinationException("runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
        }
        else
        {
            var expectedKey = RuntimeRunStartKeys.SandboxIdempotencyKey(
                intent.TenantId, intent.ProjectId, intent.RunId, intent.RootSessionId,
                intent.AcceptedSelectionHash, intent.BootstrapOperationId);
            if (receipt.SandboxIdempotencyKey != expectedKey)
                throw new CoordinationException("runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
            var attempt = new RuntimeRunStartCleanupAttempt(
                receipt.SandboxOperationId,
                receipt.ResourceGeneration,
                receipt.ProviderFencingGeneration,
                receipt.SandboxIdempotencyKey!)
            {
                SandboxResource = receipt.SandboxResource
            }.Validate();
            if (links.TryGetValue("cleanupAttempt", out var savedAttemptElement))
            {
                var savedAttempt = savedAttemptElement.Deserialize<RuntimeRunStartCleanupAttempt>(JsonOptions)
                    ?? throw new CoordinationException(
                        "runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
                if (!savedAttempt.Matches(attempt))
                    throw new CoordinationException(
                        "runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
            }
            else
                links["cleanupAttempt"] = JsonSerializer.SerializeToElement(attempt, JsonOptions);

            if (links.TryGetValue("placement", out var savedPlacementElement))
            {
                var placement = savedPlacementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                    ?? throw new CoordinationException(
                        "runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
                if (attempt.SandboxOperationId != placement.SandboxOperationId ||
                    attempt.ResourceGeneration != placement.SandboxResource.Generation ||
                    attempt.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
                    attempt.SandboxIdempotencyKey != placement.SandboxIdempotencyKey)
                    throw new CoordinationException(
                        "runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
            }
        }
        links["cleanup"] = JsonSerializer.SerializeToElement(receipt, JsonOptions);
        var json = JsonSerializer.Serialize(links, JsonOptions);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string parentRootSessionId;
        await using (var run = new NpgsqlCommand($"""
            SELECT r.execution_state, r.execution_fence, r.tenant_id, r.accepted_selection_hash,
                   s.root_session_id
            FROM {_schema}.accepted_runs r
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = r.project_id AND s.run_id = r.run_id AND s.session_id = @session
            WHERE r.project_id = @project AND r.run_id = @run
            FOR UPDATE OF r
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", intent.ProjectId);
            run.Parameters.AddWithValue("run", intent.RunId);
            run.Parameters.AddWithValue("session", intent.RootSessionId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != projection.RunState ||
                reader.GetInt64(1) != (projection.TerminalFence ?? projection.CancellationFence) ||
                reader.GetString(2) != projection.TenantId ||
                !string.Equals(reader.GetString(3).Trim(), projection.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                projection.ContractVersion == 2 && reader.GetString(4) != projection.ParentRootSessionId)
                throw new CoordinationException("runtime_start_cleanup_fence_stale", StatusCodes.Status409Conflict);
            parentRootSessionId = reader.GetString(4);
        }
        if (projection.ContractVersion == 2 &&
            (!await IsTerminalCleanupGuardInstalledAsync(connection, transaction, _schema, cancellationToken)
                .ConfigureAwait(false) ||
             projection.RunState != "cancelled" &&
             !await HasTerminalCleanupMarkerAsync(
                 connection, transaction, _schema, intent.TenantId, intent.ProjectId, intent.RunId,
                 projection.RunState, projection.TerminalFence!.Value, cancellationToken).ConfigureAwait(false)))
            throw new CoordinationException("runtime_start_cleanup_guard_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (await HasUnresolvedNativeWorkAsync(
                connection, transaction, intent.ProjectId, intent.RunId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_work_pending", StatusCodes.Status409Conflict);
        if (state == "cleanup_complete")
        {
            if (intent.RootSessionId == parentRootSessionId && await HasIncompleteChildCleanupAsync(
                    connection, transaction, intent.ProjectId, intent.RunId, parentRootSessionId, cancellationToken)
                .ConfigureAwait(false))
                throw new CoordinationException("runtime_start_child_cleanup_pending", StatusCodes.Status409Conflict);
            await using var active = new NpgsqlCommand($"""
                SELECT EXISTS (
                    SELECT 1 FROM {_schema}.runtime_registration_heads h
                    JOIN {_schema}.runtime_registration_revisions v
                      ON v.runtime_instance_id = h.runtime_instance_id AND v.revision = h.current_revision
                    WHERE v.state = 0 AND v.binding_json::jsonb ->> 'ProjectId' = @project
                      AND v.binding_json::jsonb ->> 'RunId' = @run
                      AND v.binding_json::jsonb ->> 'SessionId' = @session)
                """, connection, transaction);
            active.Parameters.AddWithValue("project", intent.ProjectId);
            active.Parameters.AddWithValue("run", intent.RunId);
            active.Parameters.AddWithValue("session", intent.RootSessionId);
            if ((bool)(await active.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new CoordinationException("runtime_start_registration_cleanup_pending", StatusCodes.Status409Conflict);
        }
        await using var update = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET state = @state, current_step = 'cleanup', failure_code = NULL,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND row_version = @version AND cleanup_obligation
            RETURNING row_version
            """, connection, transaction);
        AddKey(update, intent);
        update.Parameters.AddWithValue("state", state);
        update.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, json);
        update.Parameters.AddWithValue("version", expectedVersion);
        var nextVersion = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (nextVersion is not long version)
            throw new CoordinationException("runtime_start_cleanup_stale", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent with { State = state, CurrentStep = "cleanup", FailureCode = null,
            ReceiptLinks = json, RowVersion = version };
    }

    public async Task<RuntimeRunStartIntent> UpdateAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        string state,
        string step,
        string? failureCode,
        string? environmentId,
        Guid? runtimeInstanceId,
        object receiptLinks,
        CancellationToken cancellationToken)
    {
        if (state is not ("pending" or "ready" or "blocked" or "cleanup_pending" or "cleanup_complete") ||
            string.IsNullOrWhiteSpace(step) || step.Length > 48 ||
            failureCode is { Length: > 96 })
            throw new ArgumentException("The runtime start update is invalid.");
        var receiptJson = JsonSerializer.Serialize(receiptLinks, JsonOptions);
        using var document = JsonDocument.Parse(receiptJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Runtime start receipt links must be an object.", nameof(receiptLinks));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET state = @state, current_step = @step, failure_code = @failure,
                environment_id = @environment, runtime_instance_id = @runtime,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND row_version = @version
              AND NOT cleanup_obligation
            RETURNING row_version
            """, connection, transaction);
        AddKey(command, intent);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("step", step);
        command.Parameters.AddWithValue("failure", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("environment", NpgsqlDbType.Varchar, (object?)environmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, (object?)runtimeInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, receiptJson);
        command.Parameters.AddWithValue("version", expectedVersion);
        var nextVersion = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (nextVersion is not long version)
            throw new CoordinationException("runtime_start_intent_stale", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent with
        {
            State = state,
            CurrentStep = step,
            FailureCode = failureCode,
            EnvironmentId = environmentId,
            RuntimeInstanceId = runtimeInstanceId,
            ReceiptLinks = receiptJson,
            RowVersion = version
        };
    }

    public async Task<RuntimeRunStartIntent> ReplaceFailedBootstrapAttemptAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        RuntimeRunStartRetirementReceipt retirement,
        CancellationToken cancellationToken)
    {
        var links = ReadReceiptLinks(intent);
        if (intent.State != "pending" ||
            intent.CurrentStep != "retirement" ||
            intent.FailureCode is null ||
            !links.TryGetValue("placement", out var placementElement) ||
            !links.TryGetValue("registration", out var registrationElement))
            throw new CoordinationException(
                "runtime_start_retirement_receipt_invalid", StatusCodes.Status409Conflict);
        var placement = placementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
            ?? throw new CoordinationException(
                "runtime_start_retirement_receipt_invalid", StatusCodes.Status409Conflict);
        var registration = registrationElement.Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new CoordinationException(
                "runtime_start_retirement_receipt_invalid", StatusCodes.Status409Conflict);
        var request = new RuntimeRunStartRetirementRequest(
            1,
            new RuntimeOwnerContext(
                1,
                registration.Binding.ActorIssuer,
                registration.Binding.ActorId,
                intent.TenantId,
                intent.ProjectId,
                intent.RunId,
                intent.RootSessionId,
                registration.Binding.AgentId,
                registration.Binding.ModelSelectionReference!,
                registration.Binding.TurnId,
                registration.Binding.ProjectRevision,
                registration.Binding.ProjectConfigurationRevision,
                registration.Binding.PlatformRuntimeRevision,
                registration.Binding.ContextRevision,
                intent.AcceptedSelectionHash,
                intent.ExecutionFence,
                0,
                1,
                1),
            intent.BootstrapOperationId,
            registration.RuntimeInstanceId,
            registration.Revision,
            placement).Validate();
        _ = retirement.ValidateFor(request);
        if (retirement.State != "released")
            throw new CoordinationException(
                "runtime_start_retirement_receipt_invalid", StatusCodes.Status409Conflict);

        var retired = new
        {
            intent.BootstrapOperationId,
            intent.ConsumeOperationId,
            intent.ExchangeOperationId,
            Placement = placement,
            Registration = registration,
            Retirement = retirement
        };
        links["retiredBootstrap"] = JsonSerializer.SerializeToElement(retired, JsonOptions);
        foreach (var key in new[] { "placement", "registration", "bootstrap", "activation", "readiness" })
            links.Remove(key);
        var receiptJson = JsonSerializer.Serialize(links, JsonOptions);
        var nextBootstrapOperationId = Guid.NewGuid();
        var nextConsumeOperationId = Guid.NewGuid();
        var nextExchangeOperationId = Guid.NewGuid();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET bootstrap_operation_id = @bootstrap,
                consume_operation_id = @consume,
                exchange_operation_id = @exchange,
                state = 'pending', current_step = 'accepted', failure_code = NULL,
                environment_id = NULL, runtime_instance_id = NULL,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND row_version = @version
              AND state = 'pending' AND current_step = 'retirement'
              AND NOT cleanup_obligation
            RETURNING row_version
            """, connection);
        AddKey(command, intent);
        command.Parameters.AddWithValue("bootstrap", nextBootstrapOperationId);
        command.Parameters.AddWithValue("consume", nextConsumeOperationId);
        command.Parameters.AddWithValue("exchange", nextExchangeOperationId);
        command.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, receiptJson);
        command.Parameters.AddWithValue("version", expectedVersion);
        var nextVersion = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (nextVersion is not long version)
            throw new CoordinationException("runtime_start_intent_stale", StatusCodes.Status409Conflict);
        return intent with
        {
            BootstrapOperationId = nextBootstrapOperationId,
            ConsumeOperationId = nextConsumeOperationId,
            ExchangeOperationId = nextExchangeOperationId,
            State = "pending",
            CurrentStep = "accepted",
            FailureCode = null,
            EnvironmentId = null,
            RuntimeInstanceId = null,
            ReceiptLinks = receiptJson,
            RowVersion = version
        };
    }

    public async Task<RuntimeRunStartIntent> CommitReadyAsync(
        RuntimeRunStartIntent intent, RuntimeOwnerContext owner, long expectedVersion, object receiptLinks,
        CancellationToken cancellationToken, bool isChild = false)
    {
        if (owner.TenantId != intent.TenantId || owner.ProjectId != intent.ProjectId ||
            owner.RunId != intent.RunId || owner.SessionId != intent.RootSessionId ||
            owner.ExecutionFence != intent.ExecutionFence ||
            owner.AcceptedSelectionHash != intent.AcceptedSelectionHash)
            throw new CoordinationException("runtime_start_owner_stale", StatusCodes.Status409Conflict);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var run = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR UPDATE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", intent.ProjectId);
            run.Parameters.AddWithValue("run", intent.RunId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(reader.GetString(0).Trim(), intent.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(1) != intent.ExecutionFence ||
                reader.GetString(2) is "completed" or "failed" or "indeterminate" or "cancelled")
                throw new CoordinationException("runtime_start_run_terminal", StatusCodes.Status409Conflict);
        }
        if (isChild)
            await RequireCurrentChildAsync(connection, transaction, intent, owner, cancellationToken)
                .ConfigureAwait(false);
        else
            await RequireCurrentRootAsync(connection, transaction, owner, cancellationToken)
                .ConfigureAwait(false);
        var updated = await UpdateInTransactionAsync(
            connection, transaction, intent, expectedVersion, "ready", "ready", null,
            intent.EnvironmentId, intent.RuntimeInstanceId, receiptLinks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    internal static async Task CaptureCleanupInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema,
        string projectId, string runId, CancellationToken cancellationToken)
    {
        await using (var available = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction))
        {
            available.Parameters.AddWithValue("table", $"{schema}.runtime_run_start_intents");
            if (!(bool)(await available.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return;
        }
        await using var update = new NpgsqlCommand($"""
            UPDATE {schema}.runtime_run_start_intents
            SET state = CASE WHEN state = 'cleanup_complete' THEN state ELSE 'cleanup_pending' END,
                current_step = CASE WHEN state = 'cleanup_complete' THEN current_step ELSE 'cleanup' END,
                cleanup_obligation = true, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
              AND state <> 'cleanup_complete' AND NOT cleanup_obligation
            """, connection, transaction);
        update.Parameters.AddWithValue("project", projectId);
        update.Parameters.AddWithValue("run", runId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeRunStartIntent?> ReadIntentAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        RuntimeRunStartIntent key, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                   exchange_operation_id, state, current_step, failure_code, environment_id,
                   runtime_instance_id, receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
            {(forUpdate ? "FOR UPDATE" : "")}
            """, connection, transaction);
        AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return key with
        {
            ExecutionFence = reader.GetInt64(0),
            IntentId = reader.GetGuid(1),
            BootstrapOperationId = reader.GetGuid(2),
            ConsumeOperationId = reader.GetGuid(3),
            ExchangeOperationId = reader.GetGuid(4),
            State = reader.GetString(5),
            CurrentStep = reader.GetString(6),
            FailureCode = reader.IsDBNull(7) ? null : reader.GetString(7),
            EnvironmentId = reader.IsDBNull(8) ? null : reader.GetString(8),
            RuntimeInstanceId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
            ReceiptLinks = reader.GetString(10),
            CleanupObligation = reader.GetBoolean(11),
            RowVersion = reader.GetInt64(12)
        };
    }

    private async Task<RuntimeReplacementOperation?> ReadReplacementOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimeRunStartIntent intent,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_hash, expected_runtime_instance_id, expected_registration_revision,
                   accepted_selection_hash, execution_fence, state, current_step, failure_code,
                   receipt_links::text, row_version
            FROM {_schema}.runtime_replacement_operations
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND operation_id = @operation
            """, connection, transaction);
        AddKey(command, intent);
        command.Parameters.AddWithValue("operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var operation = new RuntimeReplacementOperation(
            intent.TenantId,
            intent.ProjectId,
            intent.RunId,
            intent.RootSessionId,
            operationId,
            reader.GetString(0).Trim(),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetString(3).Trim(),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "runtime_replacement_operation_ambiguous", StatusCodes.Status409Conflict);
        return operation;
    }

    private async Task<RuntimeRunStartIntent> UpdateInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRunStartIntent intent,
        long expectedVersion, string state, string step, string? failureCode, string? environmentId,
        Guid? runtimeInstanceId, object receiptLinks, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(receiptLinks, JsonOptions);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET state = @state, current_step = @step, failure_code = @failure,
                environment_id = @environment, runtime_instance_id = @runtime,
                receipt_links = @receipts, row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND row_version = @version AND NOT cleanup_obligation
            RETURNING row_version
            """, connection, transaction);
        AddKey(command, intent);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("step", step);
        command.Parameters.AddWithValue("failure", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("environment", NpgsqlDbType.Varchar, (object?)environmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, (object?)runtimeInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, json);
        command.Parameters.AddWithValue("version", expectedVersion);
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?
            ?? throw new CoordinationException("runtime_start_intent_stale", StatusCodes.Status409Conflict);
        return intent with
        {
            State = state, CurrentStep = step, FailureCode = failureCode,
            EnvironmentId = environmentId, RuntimeInstanceId = runtimeInstanceId,
            ReceiptLinks = json, RowVersion = version
        };
    }

    private async Task RequireCurrentRootAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeOwnerContext owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_selection_hash, r.execution_fence, r.execution_state,
                   r.accepted_by_issuer, r.accepted_by_subject,
                   s.parent_session_id, s.lifecycle_state, s.execution_fence
            FROM {_schema}.accepted_runs r
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @root
            FOR UPDATE OF r, s
            """, connection, transaction);
        command.Parameters.AddWithValue("project", owner.ProjectId);
        command.Parameters.AddWithValue("run", owner.RunId);
        command.Parameters.AddWithValue("root", owner.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0).Trim(), owner.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt64(1) != owner.ExecutionFence ||
            reader.GetString(2) is "completed" or "failed" or "indeterminate" or "cancelled" ||
            reader.GetString(3) != owner.ActorIssuer || reader.GetString(4) != owner.ActorId ||
            !reader.IsDBNull(5) || reader.GetString(6) != "active" || reader.GetInt64(7) != owner.ExecutionFence)
            throw new CoordinationException("runtime_start_owner_stale", StatusCodes.Status409Conflict);
    }

    private static void AddKey(NpgsqlCommand command, RuntimeRunStartIntent key)
    {
        command.Parameters.AddWithValue("tenant", key.TenantId);
        command.Parameters.AddWithValue("project", key.ProjectId);
        command.Parameters.AddWithValue("run", key.RunId);
        command.Parameters.AddWithValue("root", key.RootSessionId);
        command.Parameters.AddWithValue("selection", key.AcceptedSelectionHash);
        command.Parameters.AddWithValue("fence", key.ExecutionFence);
    }

    private static Dictionary<string, JsonElement> ReadReceiptLinks(RuntimeRunStartIntent intent) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(intent.ReceiptLinks, JsonOptions)
        ?? new(StringComparer.Ordinal);

    private static bool HasPath(
        Guid source,
        Guid target,
        IReadOnlyList<(RuntimeRegistration Previous, RuntimeRegistration Current)> edges)
    {
        var visited = new HashSet<Guid> { source };
        var pending = new Queue<Guid>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out var current))
        {
            if (current == target)
                return true;
            foreach (var edge in edges.Where(edge => edge.Previous.RuntimeInstanceId == current))
            {
                if (visited.Add(edge.Current.RuntimeInstanceId))
                    pending.Enqueue(edge.Current.RuntimeInstanceId);
            }
        }
        return false;
    }

    private static string LockKey(RuntimeRunStartIntent intent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            intent.TenantId, intent.ProjectId, intent.RunId, intent.RootSessionId,
            intent.AcceptedSelectionHash))));

    internal sealed class RuntimeRunStartLock(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
