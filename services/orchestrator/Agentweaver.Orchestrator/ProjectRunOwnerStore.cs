using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>Persists interactive input and serializes start and cancel across service replicas.</summary>
internal sealed partial class CoordinationOwnerStore
{
    /// <summary>Creates one durable run identity per actor, tenant, project, and idempotency key.</summary>
    internal async Task<ProjectRunRequestRow> CreateProjectRunAsync(
        CoordinationActor actor, string tenantId, string projectId, string key,
        CreateProjectRunRequest request, CancellationToken cancellationToken,
        P1ProjectSetupReceiptSnapshot? setupReceipt = null)
    {
        ValidateProjectRunRequest(request);
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        ValidateP1SetupReceiptForRequest(actor, tenantId, projectId, request, setupReceipt);
        var hash = HashProjectRunInput(key);
        var fingerprint = HashProjectRunInput(JsonSerializer.Serialize(request, JsonOptions));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var p1LocatorColumn = request.P1SetupInvocationId is null ? string.Empty : ", p1_setup_invocation_id";
        var p1LocatorValue = request.P1SetupInvocationId is null ? string.Empty : ", @p1_invocation";
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.project_run_requests
                (tenant_id, project_id, run_id, root_session_id, title, first_message, workflow_id,
                 expected_project_config_revision, started_by_issuer, started_by_subject,
                 idempotency_key_hash, request_fingerprint{p1LocatorColumn})
            VALUES (@tenant, @project, @run, @root, @title, @message, @workflow,
                    @revision, @issuer, @subject, @key, @fingerprint{p1LocatorValue})
            ON CONFLICT (tenant_id, project_id, started_by_issuer, started_by_subject, idempotency_key_hash)
                DO NOTHING
            """, connection, transaction))
        {
            AddRunScope(insert, projectId, Guid.NewGuid().ToString("N"));
            AddProjectRunActor(insert, actor, tenantId);
            insert.Parameters.AddWithValue("root", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("title", request.Title ?? DefaultRunTitle(request.FirstMessage));
            insert.Parameters.AddWithValue("message", request.FirstMessage);
            insert.Parameters.AddWithValue("workflow", NpgsqlDbType.Varchar, (object?)request.WorkflowId ?? DBNull.Value);
            insert.Parameters.AddWithValue("revision", request.ExpectedProjectConfigRevision);
            insert.Parameters.AddWithValue("key", hash);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            if (request.P1SetupInvocationId is { } p1InvocationId)
                insert.Parameters.AddWithValue("p1_invocation", NpgsqlDbType.Uuid, p1InvocationId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var query = new NpgsqlCommand($"""
            SELECT run_id, root_session_id, first_message, workflow_id,
                   expected_project_config_revision, state,
                   NULLIF(to_jsonb(project_run_requests)->>'p1_setup_invocation_id', '')::uuid,
                   request_fingerprint
            FROM {_schema}.project_run_requests AS project_run_requests
            WHERE tenant_id = @tenant AND project_id = @project
              AND started_by_issuer = @issuer AND started_by_subject = @subject
              AND idempotency_key_hash = @key
            """, connection, transaction);
        query.Parameters.AddWithValue("project", projectId);
        AddProjectRunActor(query, actor, tenantId);
        query.Parameters.AddWithValue("key", hash);
        ProjectRunRequestRow row;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(7) != fingerprint)
                throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
            row = ReadProjectRunRequest(reader);
        }
        if (row.P1SetupInvocationId is { } invocationId && setupReceipt is not null)
            await PersistP1RunRequestProvenanceAsync(
                connection, transaction, actor, tenantId, projectId, row, setupReceipt, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    /// <summary>Bounds connection and request-lock ownership, including downstream calls, to thirty seconds.</summary>
    internal async Task<StartProjectRunResponse> StartProjectRunAsync(
        CoordinationActor actor, string tenantId, string projectId, string runId,
        Func<ProjectRunRequestRow, CancellationToken, Task> deliverFirstMessage,
        Func<CancellationToken, Task> revalidateAuthority, CancellationToken cancellationToken,
        Action<ProjectRunRequestRow>? validateRequest = null,
        Func<NpgsqlConnection, NpgsqlTransaction, ProjectRunRequestRow, CancellationToken, Task>?
            acceptRootInTransaction = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await StartProjectRunWithinDeadlineAsync(actor, tenantId, projectId, runId,
                deliverFirstMessage, revalidateAuthority, validateRequest, acceptRootInTransaction, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new CoordinationException("run_start_timeout", StatusCodes.Status504GatewayTimeout);
        }
    }

    /// <summary>Holds the durable request lock while idempotent admission and delivery finish within the shared deadline.</summary>
    private async Task<StartProjectRunResponse> StartProjectRunWithinDeadlineAsync(
        CoordinationActor actor, string tenantId, string projectId, string runId,
        Func<ProjectRunRequestRow, CancellationToken, Task> deliverFirstMessage,
        Func<CancellationToken, Task> revalidateAuthority,
        Action<ProjectRunRequestRow>? validateRequest,
        Func<NpgsqlConnection, NpgsqlTransaction, ProjectRunRequestRow, CancellationToken, Task>?
            acceptRootInTransaction,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LockProjectRunRequestAsync(
            connection, transaction, actor, tenantId, projectId, runId, cancellationToken)
            ?? throw new CoordinationException("run_request_not_found", StatusCodes.Status404NotFound);
        if (row.State == "cancelled")
            throw new CoordinationException("run_terminal", StatusCodes.Status409Conflict);
        // Replays skip first-message delivery, but must still match the durable request.
        validateRequest?.Invoke(row);
        if (row.P1SetupInvocationId is not null)
        {
            if (acceptRootInTransaction is null)
                throw new CoordinationException(
                    "p1_invocation_binding_unavailable", StatusCodes.Status503ServiceUnavailable);
            await acceptRootInTransaction(connection, transaction, row, cancellationToken)
                .ConfigureAwait(false);
            await revalidateAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (row.State != "started")
            {
                await deliverFirstMessage(row, cancellationToken).ConfigureAwait(false);
                await MarkProjectRunStartedAsync(
                    actor, tenantId, projectId, runId, row, revalidateAuthority, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new StartProjectRunResponse(runId, row.RootSessionId, "started");
        }
        if (row.State != "started")
        {
            await deliverFirstMessage(row, cancellationToken);
            await SetProjectRunRequestStateAsync(connection, transaction, projectId, runId, "started", cancellationToken);
        }
        await revalidateAuthority(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StartProjectRunResponse(runId, row.RootSessionId, "started");
    }

    /// <summary>Marks an atomically accepted P1 request started only after retry-safe external delivery succeeds.</summary>
    private async Task MarkProjectRunStartedAsync(
        CoordinationActor actor,
        string tenantId,
        string projectId,
        string runId,
        ProjectRunRequestRow expected,
        Func<CancellationToken, Task> revalidateAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await LockProjectRunRequestAsync(
            connection, transaction, actor, tenantId, projectId, runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException("run_request_not_found", StatusCodes.Status404NotFound);
        if (current.State == "cancelled" ||
            current.P1SetupInvocationId != expected.P1SetupInvocationId ||
            current.RootSessionId != expected.RootSessionId)
            throw new CoordinationException("run_terminal", StatusCodes.Status409Conflict);
        if (current.State != "started")
            await SetProjectRunRequestStateAsync(
                connection, transaction, projectId, runId, "started", cancellationToken).ConfigureAwait(false);
        await revalidateAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels project runs atomically; unstarted requests need no root admission or first-message delivery.</summary>
    internal async Task<CancelProjectRunResponse> CancelProjectRunAsync(
        CoordinationActor actor, AuthorizedRunSelection selection, string key, CancelProjectRunRequest request,
        Func<CancellationToken, Task> revalidateAuthority, CancellationToken cancellationToken)
    {
        RequireProjectRunCancellationAuthority(actor, selection);
        if (request.Reason is { Length: > 2048 })
            throw new CoordinationException("cancel_reason_invalid", StatusCodes.Status400BadRequest);
        var projectId = selection.Selection.ProjectId;
        var runId = selection.Selection.RunId;
        var tenantId = selection.Authorization.TenantId;
        var keyHash = HashProjectRunInput(key);
        var fingerprint = HashProjectRunInput(JsonSerializer.Serialize(request, JsonOptions));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var pending = await LockProjectRunRequestAsync(
            connection, transaction, actor, tenantId, projectId, runId, cancellationToken, requireCreator: false);
        await using var exists = new NpgsqlCommand($"""
            SELECT EXISTS (SELECT 1 FROM {_runs} WHERE project_id = @project AND run_id = @run)
            """, connection, transaction);
        AddRunScope(exists, projectId, runId);
        var run = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!
            ? await ReadAcceptedRunAsync(connection, transaction, projectId, runId, forUpdate: true, cancellationToken)
            : null;
        if (run is null && pending is null)
            throw new CoordinationException("run_request_not_found", StatusCodes.Status404NotFound);
        if (run is not null &&
            (run.TenantId != tenantId || run.SelectionHash != HashSelection(selection.Selection)))
            throw new CoordinationException("accepted_run_conflict", StatusCodes.Status409Conflict);
        await using (var replay = new NpgsqlCommand($"""
            SELECT request_fingerprint, result::text FROM {_schema}.project_run_cancellations
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND actor_issuer = @issuer AND actor_subject = @subject AND idempotency_key_hash = @key
            """, connection, transaction))
        {
            AddRunScope(replay, projectId, runId);
            AddProjectRunActor(replay, actor, tenantId);
            replay.Parameters.AddWithValue("key", keyHash);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != fingerprint)
                    throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
                var receipt = JsonSerializer.Deserialize<CancelProjectRunResponse>(reader.GetString(1), JsonOptions)
                    ?? throw new CoordinationException("cancel_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
                await reader.DisposeAsync();
                await revalidateAuthority(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return receipt;
            }
        }
        var alreadyTerminal = pending?.State == "cancelled" ||
            run?.ExecutionState is "completed" or "failed" or "indeterminate" or "cancelled";
        // Use the database clock shared by accepted_runs.created_at and its completion constraint.
        await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
        var cancelledAt = new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!);
        var result = new CancelProjectRunResponse(
            runId, alreadyTerminal ? run?.ExecutionState ?? "cancelled" : "cancelled",
            alreadyTerminal ? null : cancelledAt, alreadyTerminal);
        var operationId = Guid.NewGuid();
        var nextFence = run is null ? 1 : alreadyTerminal ? run.Fence : checked(run.Fence + 1);
        if (!alreadyTerminal)
        {
            if (run is not null)
            {
                await using (var update = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET execution_state = 'cancelled', cancelled_at = @now, completed_at = @now,
                execution_fence = @fence, state_version = state_version + 1, updated_at = @now
            WHERE project_id = @project AND run_id = @run;
            UPDATE {_sessions}
            SET execution_fence = @fence, state_version = state_version + 1,
                lifecycle_state = CASE WHEN lifecycle_state = 'active' THEN 'cancelled' ELSE lifecycle_state END,
                turn_boundary_request_version = NULL, turn_boundary_result = NULL
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction))
                {
                    AddRunScope(update, projectId, runId);
                    update.Parameters.AddWithValue("now", cancelledAt);
                    update.Parameters.AddWithValue("fence", nextFence);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
                var root = await ReadRootSessionAsync(connection, transaction, projectId, runId, forUpdate: true, cancellationToken)
                    ?? throw new CoordinationException("run_root_unavailable", StatusCodes.Status503ServiceUnavailable);
                await AdvanceCurrentDecisionFenceAsync(connection, transaction, actor,
                    new SessionIdentity(projectId, runId, root.SessionId), selection,
                    run.Fence, nextFence, operationId, "execution.cancelled", cancellationToken);
            }
            if (pending is not null)
                await SetProjectRunRequestStateAsync(connection, transaction, projectId, runId, "cancelled", cancellationToken);
        }
        await using (var save = new NpgsqlCommand($"""
            INSERT INTO {_schema}.project_run_cancellations
                (tenant_id, project_id, run_id, actor_issuer, actor_subject,
                 idempotency_key_hash, request_fingerprint, result)
            VALUES (@tenant, @project, @run, @issuer, @subject, @key, @fingerprint, @result)
            """, connection, transaction))
        {
            AddRunScope(save, projectId, runId);
            AddProjectRunActor(save, actor, tenantId);
            save.Parameters.AddWithValue("key", keyHash);
            save.Parameters.AddWithValue("fingerprint", fingerprint);
            save.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, JsonOptions));
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!alreadyTerminal)
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            operationId, $"coordination/{projectId}/{runId}/execution", $"cancel:{keyHash}",
            "orchestrator.run.cancelled", EventVersion,
            JsonSerializer.SerializeToElement(new { result, request.Reason, executionFence = nextFence }, JsonOptions),
            cancelledAt), cancellationToken);
        await revalidateAuthority(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Locks request state before accepted-run locks; only start is restricted to its creator.</summary>
    private async Task<ProjectRunRequestRow?> LockProjectRunRequestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CoordinationActor actor,
        string tenantId, string projectId, string runId, CancellationToken cancellationToken, bool requireCreator = true)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT run_id, root_session_id, first_message, workflow_id,
                   expected_project_config_revision, state,
                   NULLIF(to_jsonb(project_run_requests)->>'p1_setup_invocation_id', '')::uuid,
                   started_by_issuer, started_by_subject
            FROM {_schema}.project_run_requests AS project_run_requests
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(query, projectId, runId);
        query.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        if (requireCreator && (reader.GetString(7) != actor.Issuer || reader.GetString(8) != actor.Subject))
            throw new CoordinationException("run_request_permission_denied", StatusCodes.Status403Forbidden);
        return ReadProjectRunRequest(reader);
    }

    /// <summary>Requires fresh unbound Projects cancellation evidence without impersonating the run creator.</summary>
    internal static void RequireProjectRunCancellationAuthority(
        CoordinationActor actor, AuthorizedRunSelection selection)
    {
        var authority = selection.Authorization;
        if (authority.ContractVersion != 1 || authority.Issuer != actor.Issuer ||
            authority.ActorId != actor.Subject || authority.MembershipRevision < 1 ||
            string.IsNullOrWhiteSpace(authority.TenantId) || authority.BoundRunId is not null ||
            authority.BoundProjectId is not null && authority.BoundProjectId != selection.Selection.ProjectId ||
            authority.EffectiveAuthority.IsDefault ||
            !authority.EffectiveAuthority.Any(item =>
                (item.ResourceType == "project" && item.ResourceId == selection.Selection.ProjectId ||
                 item.ResourceType == "tenant" && item.ResourceId == authority.TenantId) &&
                !item.Permissions.IsDefault && item.Permissions.Any(permission =>
                    permission.Permission == "cancelRun" && permission.RoleRevision > 0)))
            throw new CoordinationException("run_cancel_permission_denied", StatusCodes.Status403Forbidden);
    }

    /// <summary>Updates request state within its start or cancellation transaction.</summary>
    private async Task SetProjectRunRequestStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string projectId, string runId, string state, CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand($"""
            UPDATE {_schema}.project_run_requests SET state = @state, updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction);
        AddRunScope(update, projectId, runId);
        update.Parameters.AddWithValue("state", state);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Validates byte bounds before durable input is written.</summary>
    internal static void ValidateProjectRunRequest(CreateProjectRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FirstMessage) ||
            Encoding.UTF8.GetByteCount(request.FirstMessage) > 32768 ||
            request.ExpectedProjectConfigRevision < 1 ||
            request.P1SetupInvocationId == Guid.Empty ||
            request.Title is not null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256))
            throw new CoordinationException("run_request_invalid", StatusCodes.Status400BadRequest);
        if (request.WorkflowId is not null)
            CoordinationIdentity.ValidateIdentity(request.WorkflowId, "workflow_id");
    }

    /// <summary>Validates a bounded run follow-up and prevents reuse of the persisted first-message key.</summary>
    internal static void ValidateProjectRunMessageRequest(
        ProjectRunMessageRequest request,
        string runId,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text) ||
            Encoding.UTF8.GetByteCount(request.Text) > 32_768)
            throw new CoordinationException("run_message_invalid", StatusCodes.Status400BadRequest);
        if (request.TargetSessionId is not null)
            CoordinationIdentity.ValidateIdentity(request.TargetSessionId, nameof(request.TargetSessionId));
        if (idempotencyKey == $"{runId}:first")
            throw new CoordinationException(
                "run_message_idempotency_key_reserved", StatusCodes.Status409Conflict);
    }

    /// <summary>Uses the first 80 Unicode characters without splitting a surrogate pair.</summary>
    private static string DefaultRunTitle(string message) =>
        string.Concat(message.EnumerateRunes().Take(80).Select(rune => rune.ToString()));

    /// <summary>Hashes opaque retry keys and canonical request bodies without retaining raw retry keys.</summary>
    private static string HashProjectRunInput(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Adds actor and tenant parameters shared by request and cancellation receipts.</summary>
    private static void AddProjectRunActor(NpgsqlCommand command, CoordinationActor actor, string tenantId)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("issuer", actor.Issuer);
        command.Parameters.AddWithValue("subject", actor.Subject);
    }

    /// <summary>Prevents generic messaging callers from forging the reserved first-input route or content.</summary>
    private async Task RequirePersistedFirstMessageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CoordinationActor actor, string? tenantId,
        SessionIdentity identity, CoordinationMessageRequest request, CancellationToken cancellationToken)
    {
        if (request.IdempotencyKey != $"{identity.RunId}:first")
            throw new CoordinationException("first_message_invalid", StatusCodes.Status409Conflict);
        await using var query = new NpgsqlCommand($"""
            SELECT first_message, workflow_id FROM {_schema}.project_run_requests
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @session AND started_by_issuer = @issuer AND started_by_subject = @subject
              AND state <> 'cancelled'
            """, connection, transaction);
        AddRunScope(query, identity.ProjectId, identity.RunId);
        AddProjectRunActor(query, actor, tenantId ?? string.Empty);
        query.Parameters.AddWithValue("session", identity.SessionId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            request.Payload.ValueKind != JsonValueKind.Object ||
            request.Payload.EnumerateObject().Count() != 2 ||
            !request.Payload.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            text.GetString() != reader.GetString(0) ||
            !request.Payload.TryGetProperty("workflowId", out var workflow) ||
            (reader.IsDBNull(1) ? workflow.ValueKind != JsonValueKind.Null :
                workflow.ValueKind != JsonValueKind.String || workflow.GetString() != reader.GetString(1)))
            throw new CoordinationException("first_message_invalid", StatusCodes.Status409Conflict);
    }

    /// <summary>Reads the persisted input used by both start and cancellation.</summary>
    private static ProjectRunRequestRow ReadProjectRunRequest(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetGuid(6));

    /// <summary>Internal durable input; the first message is never returned by create or start.</summary>
    internal sealed record ProjectRunRequestRow(
        string RunId, string RootSessionId, string FirstMessage, string? WorkflowId,
        long ExpectedProjectConfigRevision, string State, Guid? P1SetupInvocationId = null);
}
