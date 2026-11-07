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

    public CoordinationRuntimeRunStartStore(NpgsqlDataSource dataSource, string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 63 ||
            schema.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !char.IsAsciiLetter(schema[0]))
            throw new ArgumentException("The Orchestrator schema name is invalid.", nameof(schema));
        _dataSource = dataSource;
        _schema = schema;
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

    public async Task<RuntimeRunStartCleanupProjection> ReadCleanupProjectionAsync(
        string projectId,
        string runId,
        string rootSessionId,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string tenantId;
        long cancellationFence;
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
                reader.GetString(3) != "cancelled" ||
                !string.Equals(reader.GetString(1).Trim(), selectionHash, StringComparison.OrdinalIgnoreCase))
                throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
            tenantId = reader.GetString(0);
            cancellationFence = reader.GetInt64(2);
        }

        await using var command = new NpgsqlCommand($"""
            SELECT execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                   exchange_operation_id, state, current_step, failure_code, environment_id,
                   runtime_instance_id, receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root AND accepted_selection_hash = @selection
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("root", rootSessionId);
        command.Parameters.AddWithValue("selection", selectionHash);
        await using var intentReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await intentReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !intentReader.GetBoolean(11) ||
            intentReader.GetString(5) is not ("cleanup_pending" or "cleanup_complete"))
            throw new CoordinationException("runtime_start_cleanup_unavailable", StatusCodes.Status409Conflict);
        var executionFence = intentReader.GetInt64(0);
        if (cancellationFence <= executionFence)
            throw new CoordinationException("runtime_start_cleanup_fence_stale", StatusCodes.Status409Conflict);
        using var links = JsonDocument.Parse(intentReader.GetString(10));
        return new RuntimeRunStartCleanupProjection(
            1,
            tenantId,
            projectId,
            runId,
            rootSessionId,
            selectionHash,
            "cancelled",
            cancellationFence,
            intentReader.GetGuid(1),
            intentReader.GetInt64(12),
            executionFence,
            intentReader.GetGuid(2),
            intentReader.IsDBNull(8) ? null : intentReader.GetString(8),
            intentReader.GetString(5),
            links.RootElement.Clone()).Validate();
    }

    public async Task<RuntimeRunStartIntent?> FindCleanupIntentAsync(
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT root_session_id, accepted_selection_hash, execution_fence, intent_id,
                   bootstrap_operation_id, consume_operation_id, exchange_operation_id, state,
                   current_step, failure_code, environment_id, runtime_instance_id,
                   receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND cleanup_obligation
            LIMIT 2
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var intent = new RuntimeRunStartIntent(
            tenantId, projectId, runId,
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6),
            reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11),
            reader.GetString(12), reader.GetBoolean(13), reader.GetInt64(14));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_ambiguous", StatusCodes.Status409Conflict);
        return intent;
    }

    public Task<RuntimeRunStartIntent> RecordCleanupPendingAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        RuntimeRunStartCleanupReceipt receipt,
        CancellationToken cancellationToken) =>
        UpdateCleanupAsync(intent, expectedVersion, "cleanup_pending", receipt, cancellationToken);

    public Task<RuntimeRunStartIntent> MarkCleanupCompleteAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        RuntimeRunStartCleanupReceipt receipt,
        CancellationToken cancellationToken) =>
        UpdateCleanupAsync(intent, expectedVersion, "cleanup_complete", receipt, cancellationToken);

    private async Task<RuntimeRunStartIntent> UpdateCleanupAsync(
        RuntimeRunStartIntent intent,
        long expectedVersion,
        string state,
        RuntimeRunStartCleanupReceipt receipt,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        if (receipt.IntentId != intent.IntentId ||
            (state == "cleanup_complete") != (receipt.CleanupDisposition == "cleanup_complete"))
            throw new CoordinationException("runtime_start_cleanup_receipt_invalid", StatusCodes.Status409Conflict);
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
                receipt.SandboxIdempotencyKey!).Validate();
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
        await using (var run = new NpgsqlCommand($"""
            SELECT execution_state, execution_fence
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR UPDATE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", intent.ProjectId);
            run.Parameters.AddWithValue("run", intent.RunId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != "cancelled" || reader.GetInt64(1) <= intent.ExecutionFence)
                throw new CoordinationException("runtime_start_cleanup_fence_stale", StatusCodes.Status409Conflict);
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
        CancellationToken cancellationToken)
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
