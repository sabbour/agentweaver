using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed record IssueSyncProject(string ProjectId, string TenantId, long Revision,
    IssueSyncSettings Settings, IssueSyncGrantReceipt GrantProof, DateTimeOffset NextCycleAt,
    DateTimeOffset? LastCycleAt, string Status, string? ErrorCode, int ImportPage, string TaskCursor);
internal sealed record IssueSyncReceipt(string TaskId, string Repository, long? IssueNumber,
    SourceControlIssueContent? Baseline, bool CreatePending);

internal sealed class IssueSyncStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _data;
    private readonly string _schema;
    internal IssueSyncStore(NpgsqlDataSource data, string schema)
    {
        if (!Regex.IsMatch(schema, "^[a-z][a-z0-9_]{0,62}$") ||
            schema is "public" or "information_schema" || schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("Invalid issue sync schema.", nameof(schema));
        _data = data;
        _schema = "\"" + schema + "\"";
    }

    internal async Task<NpgsqlConnection?> TryLockAsync(string projectId, CancellationToken ct)
    {
        var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtext('agentweaver.issue-sync'), hashtext(@project))", connection);
        command.Parameters.AddWithValue("project", projectId);
        if (await command.ExecuteScalarAsync(ct) is true)
            return connection;
        await connection.DisposeAsync();
        return null;
    }

    internal static async Task UnlockAsync(NpgsqlConnection connection, string projectId)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('agentweaver.issue-sync'), hashtext(@project))", connection);
            command.Parameters.AddWithValue("project", projectId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally { await connection.DisposeAsync(); }
    }

    internal async Task<IssueSyncProject?> ReadAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT project_id,tenant_id,revision,settings::text,grant_proof::text,next_cycle_at,
                last_cycle_at,status,error_code,import_page,task_cursor
            FROM {_schema}.issue_sync_projects WHERE project_id=@project
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            JsonSerializer.Deserialize<IssueSyncSettings>(reader.GetString(3), Json)!,
            JsonSerializer.Deserialize<IssueSyncGrantReceipt>(reader.GetString(4), Json)!,
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetString(10)) : null;
    }

    internal async Task ConfigureAsync(string projectId, string tenantId, IssueSyncSettings settings,
        IssueSyncGrantReceipt proof, long expectedRevision, Func<CancellationToken, Task> revalidate, CancellationToken ct)
    {
        settings.Validate();
        var connection = await TryLockAsync(projectId, ct)
            ?? throw new CoordinationException("issue_sync_cycle_in_progress", 409);
        try
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var initialize = new NpgsqlCommand($"""
                INSERT INTO {_schema}.backlog_projects(project_id,graph_revision) VALUES (@project,0)
                ON CONFLICT DO NOTHING
                """, connection, tx))
            {
                initialize.Parameters.AddWithValue("project", projectId);
                await initialize.ExecuteNonQueryAsync(ct);
            }
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.issue_sync_projects
                    (project_id,tenant_id,revision,settings,grant_proof,next_cycle_at,status)
                SELECT @project,@tenant,1,@settings,@proof,clock_timestamp(),@status
                WHERE @expected=0 OR EXISTS (
                    SELECT 1 FROM {_schema}.issue_sync_projects WHERE project_id=@project AND revision=@expected)
                ON CONFLICT(project_id) DO UPDATE SET revision=issue_sync_projects.revision+1,
                    settings=@settings,grant_proof=@proof,next_cycle_at=clock_timestamp(),status=@status,error_code=NULL
                WHERE issue_sync_projects.revision=@expected AND issue_sync_projects.tenant_id=@tenant
                    AND issue_sync_projects.grant_proof->'project'->'repository' = @proof->'project'->'repository'
                """, connection, tx);
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(settings, Json));
            command.Parameters.AddWithValue("proof", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(proof, Json));
            command.Parameters.AddWithValue("status", settings.Enabled ? "configured" : "disabled");
            command.Parameters.AddWithValue("expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new CoordinationException("issue_sync_settings_revision_conflict", 409);
            await revalidate(ct);
            await tx.CommitAsync(ct);
        }
        finally { await UnlockAsync(connection, projectId); }
    }

    internal async Task<IReadOnlyList<string>> ReadDueAsync(CancellationToken ct)
    {
        await using var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT project_id FROM {_schema}.issue_sync_projects
            WHERE (settings->>'enabled')::boolean AND next_cycle_at<=clock_timestamp()
            ORDER BY next_cycle_at,project_id LIMIT 16
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    internal async Task SetStatusAsync(IssueSyncProject project, string status, string? error,
        int page, string cursor, CancellationToken ct)
    {
        await using var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.issue_sync_projects SET status=@status,error_code=@error,import_page=@page,
                task_cursor=@cursor,last_cycle_at=clock_timestamp(),
                next_cycle_at=clock_timestamp()+make_interval(secs=>@seconds)
            WHERE project_id=@project AND revision=@revision
            """, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("page", page);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("seconds", project.Settings.IntervalSeconds);
        command.Parameters.AddWithValue("project", project.ProjectId);
        command.Parameters.AddWithValue("revision", project.Revision);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new CoordinationException("issue_sync_settings_changed", 409);
    }

    internal async Task<IssueSyncReceipt?> ReadReceiptAsync(string project, string task, CancellationToken ct)
    {
        await using var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT repository,issue_number,baseline::text,create_pending FROM {_schema}.issue_sync_receipts
            WHERE project_id=@project AND task_id=@task
            """, connection);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("task", task);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(task, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<SourceControlIssueContent>(reader.GetString(2), Json),
            reader.GetBoolean(3)) : null;
    }

    internal async Task WriteReceiptAsync(string project, IssueSyncReceipt receipt, CancellationToken ct)
    {
        await using var connection = await _data.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.issue_sync_receipts(project_id,task_id,repository,issue_number,baseline,create_pending)
            VALUES (@project,@task,@repository,@issue,@baseline,@pending)
            ON CONFLICT(project_id,task_id) DO UPDATE SET issue_number=@issue,baseline=@baseline,create_pending=@pending
            WHERE issue_sync_receipts.repository=@repository
                AND (issue_sync_receipts.issue_number IS NULL OR issue_sync_receipts.issue_number=@issue)
            """, connection);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("task", receipt.TaskId);
        command.Parameters.AddWithValue("repository", receipt.Repository);
        command.Parameters.AddWithValue("issue", NpgsqlDbType.Bigint, (object?)receipt.IssueNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("baseline", NpgsqlDbType.Jsonb, receipt.Baseline is null
            ? DBNull.Value : JsonSerializer.Serialize(receipt.Baseline, Json));
        command.Parameters.AddWithValue("pending", receipt.CreatePending);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new CoordinationException("issue_sync_mapping_conflict", 409);
    }
}
