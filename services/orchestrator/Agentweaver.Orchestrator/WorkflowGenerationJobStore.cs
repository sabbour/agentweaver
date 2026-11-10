using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class WorkflowGenerationJobStore(NpgsqlDataSource dataSource, string schema)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private readonly string _schema = $"\"{schema}\"";
    private readonly string _table = $"\"{schema}\".workflow_generation_jobs";
    private readonly string _evidenceTable = $"\"{schema}\".maf_runtime_evidence_receipts";

    internal async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_schema}.coordination_schema_migrations WHERE version = 46)
                AND to_regclass(@table) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Text, $"{schema}.workflow_generation_jobs");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal async Task<WorkflowGenerationCreateResult> CreateOrGetAsync(
        WorkflowGenerationJobCreate create,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (job_id, native_request_id, project_id, run_id, session_id, tenant_id,
                 actor_issuer, actor_id, idempotency_key, request_fingerprint, prompt,
                 prompt_sha256, expected_decision_state_version, configuration_revision,
                 accepted_selection_hash, execution_fence, runtime_instance_id,
                 registration_revision, model_selection_reference)
            VALUES
                (@job, @nativeRequest, @project, @run, @session, @tenant,
                 @issuer, @actor, @key, @fingerprint, @prompt,
                 @promptHash, @decisionVersion, @configurationRevision,
                 @selectionHash, @fence, @runtime, @registrationRevision, @model)
            ON CONFLICT (tenant_id, project_id, actor_issuer, actor_id, idempotency_key)
            DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, create.JobId);
        insert.Parameters.AddWithValue("nativeRequest", NpgsqlDbType.Uuid, create.NativeRequestId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, create.ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, create.RunId);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, create.SessionId);
        insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, create.TenantId);
        insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, create.ActorIssuer);
        insert.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, create.ActorId);
        insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, create.IdempotencyKey);
        insert.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Char, create.RequestFingerprint);
        insert.Parameters.AddWithValue("prompt", NpgsqlDbType.Text, create.Prompt);
        insert.Parameters.AddWithValue("promptHash", NpgsqlDbType.Char, create.PromptSha256);
        insert.Parameters.AddWithValue("decisionVersion", NpgsqlDbType.Bigint, create.ExpectedDecisionStateVersion);
        insert.Parameters.AddWithValue("configurationRevision", NpgsqlDbType.Bigint, create.ConfigurationRevision);
        insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, create.AcceptedSelectionHash);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, create.ExecutionFence);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, create.RuntimeInstanceId);
        insert.Parameters.AddWithValue("registrationRevision", NpgsqlDbType.Bigint, create.RegistrationRevision);
        insert.Parameters.AddWithValue("model", NpgsqlDbType.Varchar, create.ModelSelectionReference);
        bool inserted;
        try
        {
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (PostgresException exception) when (
            exception.SqlState == "55000" && exception.MessageText == "workflow_generation_run_unavailable")
        {
            throw new CoordinationException(
                "workflow_generation_run_unavailable", StatusCodes.Status409Conflict, exception);
        }
        var job = await ReadByKeyAsync(
            connection,
            transaction,
            create.TenantId,
            create.ProjectId,
            create.ActorIssuer,
            create.ActorId,
            create.IdempotencyKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow-generation job was not stored.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (inserted)
            return new(job, WorkflowGenerationCreateDisposition.Created);
        if (job.RequestFingerprint == create.RequestFingerprint)
            return new(job, WorkflowGenerationCreateDisposition.Existing);
        var bindingChanged =
            job.RunId != create.RunId ||
            job.SessionId != create.SessionId ||
            job.ConfigurationRevision != create.ConfigurationRevision ||
            job.AcceptedSelectionHash != create.AcceptedSelectionHash ||
            job.ExecutionFence != create.ExecutionFence ||
            job.RuntimeInstanceId != create.RuntimeInstanceId ||
            job.RegistrationRevision != create.RegistrationRevision ||
            job.ModelSelectionReference != create.ModelSelectionReference;
        return new(job, bindingChanged
            ? WorkflowGenerationCreateDisposition.BindingStale
            : WorkflowGenerationCreateDisposition.Conflict);
    }

    internal async Task<WorkflowGenerationJob?> ReadAsync(
        string projectId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = CreateReadCommand(connection, null, "project_id = @project AND job_id = @job");
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "workflow_generation_result_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }

    }

    internal async Task<WorkflowGenerationJob?> ReadByIdempotencyKeyAsync(
        string tenantId,
        string projectId,
        string actorIssuer,
        string actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = CreateReadCommand(
            connection,
            null,
            "tenant_id = @tenant AND project_id = @project AND actor_issuer = @issuer " +
            "AND actor_id = @actor AND idempotency_key = @key");
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actorId);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "workflow_generation_result_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<WorkflowGenerationJob?> TryBeginExecutionAsync(
        string projectId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_table}
            SET status = 'running', execution_phase = 'pre-send', updated_at = clock_timestamp()
            WHERE project_id = @project AND job_id = @job AND status = 'queued'
            """, connection))
        {
            update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            update.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = CreateReadCommand(
            connection, null, "project_id = @project AND job_id = @job");
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
        return job is { Status: "running", ExecutionPhase: "pre-send" or "send-admitted" } ||
            job is { Status: "failed", ExecutionPhase: "send-admitted",
                FailureCode: "workflow_generation_outcome_indeterminate" }
            ? job
            : null;
    }

    internal async Task<bool> MarkSendAdmittedAsync(
        string projectId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_table}
            SET execution_phase = 'send-admitted', updated_at = clock_timestamp()
            WHERE project_id = @project AND job_id = @job AND status = 'running'
              AND execution_phase = 'pre-send'
            """, connection))
        {
            update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            update.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                return true;
        }

        var current = await ReadAsync(projectId, jobId, cancellationToken).ConfigureAwait(false);
        return current is { Status: "running" or "failed", ExecutionPhase: "send-admitted" };
    }

    internal async Task<bool> CompleteAsync(
        string projectId,
        Guid jobId,
        WorkflowGenerationJobResult result,
        CancellationToken cancellationToken)
    {
        var job = await ReadAsync(projectId, jobId, cancellationToken).ConfigureAwait(false);
        if (job is null ||
            !HasValidResult(job, result) ||
            !await HasPersistedCompletionAsync(job, result, cancellationToken).ConfigureAwait(false))
            return false;
        var serialized = JsonSerializer.Serialize(result, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET status = 'completed', execution_phase = 'quiesced',
                failure_code = NULL, failure_message = NULL,
                result_json = @result::jsonb, updated_at = clock_timestamp()
            WHERE project_id = @project AND job_id = @job
              AND (status = 'running' OR
                   (status = 'failed' AND failure_code = 'workflow_generation_outcome_indeterminate'))
              AND execution_phase = 'send-admitted'
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, serialized);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task FailAsync(
        string projectId,
        Guid jobId,
        string code,
        string message,
        bool quiesced,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET status = 'failed',
                execution_phase = CASE WHEN @quiesced THEN 'quiesced' ELSE execution_phase END,
                failure_code = @code, failure_message = @message, updated_at = clock_timestamp()
            WHERE project_id = @project AND job_id = @job
              AND (status IN ('queued', 'running') OR
                   (status = 'failed' AND execution_phase = 'send-admitted' AND
                    failure_code = 'workflow_generation_outcome_indeterminate'))
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Varchar, message);
        command.Parameters.AddWithValue("quiesced", NpgsqlDbType.Boolean, quiesced);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task MarkQuiescedAsync(
        string projectId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET execution_phase = 'quiesced', updated_at = clock_timestamp()
            WHERE project_id = @project AND job_id = @job
              AND status IN ('running', 'cancelled', 'failed')
              AND execution_phase IN ('pre-send', 'send-admitted')
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> HasUnresolvedNativeWorkAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_table}
                WHERE project_id = @project AND run_id = @run
                  AND execution_phase = 'send-admitted'
                  AND status IN ('running', 'cancelled', 'failed'))
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal async Task<bool> WithExecutionLockAsync(
        string projectId,
        Guid jobId,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var lockKey = $"workflow-generation:{projectId}:{jobId:D}";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", connection))
        {
            acquire.Parameters.AddWithValue("key", NpgsqlDbType.Text, lockKey);
            if (!(bool)(await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return false;
        }

        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection);
            release.Parameters.AddWithValue("key", NpgsqlDbType.Text, lockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static bool HasSameCompletionEvidence(
        RuntimeRootTurnCompletionEvidence left,
        RuntimeRootTurnCompletionEvidence right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    internal static bool HasValidResult(
        WorkflowGenerationJob job,
        WorkflowGenerationJobResult? result)
    {
        if (result is null || result.WorkflowDefinition is null ||
            result.JobId != job.JobId ||
            result.ArtifactId == Guid.Empty ||
            result.NativeCompletion is null ||
            result.NativeCompletion.RequestId != job.NativeRequestId ||
            result.NativeCompletion.PromptSha256 != job.PromptSha256 ||
            result.NativeCompletion.RuntimeInstanceId != job.RuntimeInstanceId ||
            result.NativeCompletion.RegistrationRevision != job.RegistrationRevision ||
            result.NativeCompletion.ExecutionFence != job.ExecutionFence ||
            result.NativeCompletion.AcceptedSelectionHash != job.AcceptedSelectionHash ||
            result.NativeCompletion.ModelSelectionReference != job.ModelSelectionReference ||
            result.ConfigurationRevision != job.ConfigurationRevision ||
            result.AcceptedSelectionHash != job.AcceptedSelectionHash ||
            result.WorkflowDefinition.Origin != WorkflowDefinitionOrigin.Generated ||
            result.DefinitionSha256 != Hash(JsonSerializer.Serialize(result.WorkflowDefinition, JsonOptions)) ||
            !WorkflowDefinitionValidator.ValidateAndSnapshot(result.WorkflowDefinition).IsValid)
            return false;
        var artifactHash = Hash(
            $"{job.JobId:D}\0{result.WorkflowDefinition.Id}\0{result.WorkflowDefinition.Revision}\0{result.DefinitionSha256}");
        return result.ArtifactId == Guid.ParseExact(artifactHash[..32], "N");
    }

    internal async Task<bool> HasPersistedCompletionAsync(
        WorkflowGenerationJob job,
        WorkflowGenerationJobResult? result,
        CancellationToken cancellationToken)
    {
        if (result?.NativeCompletion is null)
            return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json::text
            FROM {_evidenceTable}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'native-root-turn-completion'
              AND attempt_id = @request
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, job.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, job.RunId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Uuid, job.NativeRequestId);
        var storedJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (storedJson is null)
            return false;
        try
        {
            var stored = JsonSerializer.Deserialize<RuntimeRootTurnCompletionLedgerReceipt>(storedJson, JsonOptions);
            return stored is not null &&
                HasSameCompletionEvidence(stored.Validate().Completion.ToEvidence(), result.NativeCompletion);
        }
        catch (Exception exception) when (exception is JsonException or RuntimeAuthorizationException)
        {
            return false;
        }
    }

    private NpgsqlCommand CreateReadCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string where) =>
        new($"""
            SELECT job_id, native_request_id, project_id, run_id, session_id, tenant_id,
                   actor_issuer, actor_id, idempotency_key, request_fingerprint, prompt,
                   prompt_sha256, expected_decision_state_version, configuration_revision,
                   accepted_selection_hash, execution_fence, runtime_instance_id,
                   registration_revision, model_selection_reference, status, execution_phase,
                   failure_code, failure_message, result_json::text, created_at, updated_at
            FROM {_table}
            WHERE {where}
            """, connection, transaction);

    private async Task<WorkflowGenerationJob?> ReadByKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string actorIssuer,
        string actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateReadCommand(
            connection,
            transaction,
            "tenant_id = @tenant AND project_id = @project AND actor_issuer = @issuer " +
            "AND actor_id = @actor AND idempotency_key = @key FOR UPDATE");
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actorId);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static WorkflowGenerationJob ReadJob(NpgsqlDataReader reader)
    {
        WorkflowGenerationJobResult? result = null;
        if (!reader.IsDBNull(23))
            result = JsonSerializer.Deserialize<WorkflowGenerationJobResult>(reader.GetString(23), JsonOptions);
        return new WorkflowGenerationJob(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetInt64(15),
            reader.GetGuid(16),
            reader.GetInt64(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            result,
            reader.GetFieldValue<DateTimeOffset>(24),
            reader.GetFieldValue<DateTimeOffset>(25));
    }
}
