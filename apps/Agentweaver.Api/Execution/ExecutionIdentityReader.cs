using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Api.Sandbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Execution;

public sealed class ExecutionIdentityReader(
    MemoryDbContext db,
    SqliteDb sqliteDb,
    IConfiguration configuration,
    IRunEventStream eventStream,
    IEffectivePermissionBindingProvider permissionBindings)
{
    public async Task<ExecutionIdentityProjection> GetAsync(
        Run run,
        CancellationToken ct)
    {
        var provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        var descriptorRecord = provider is "postgres" or "postgresql"
            ? await ReadPostgresAsync(run, ct).ConfigureAwait(false)
            : await ReadSqliteAsync(run, ct).ConfigureAwait(false);
        var descriptor = descriptorRecord is null
            ? null
            : ExecutionIdentityDescriptor.FromRecord(descriptorRecord);
        var events = await eventStream
            .GetPersistedEventsAsync(run.Id.ToString(), 0, ct)
            .ConfigureAwait(false);
        EffectivePermissionBinding? currentBinding = null;
        try
        {
            currentBinding = await permissionBindings.ResolveForInspectionAsync(
                run.Id.ToString(),
                run.WorktreePath ?? run.RepositoryPath,
                ct).ConfigureAwait(false);
        }
        catch (EffectivePermissionBindingException)
        {
            // The projection reports partial evidence rather than manufacturing authority.
        }
        return ExecutionIdentityProjector.Project(run, descriptor, events, currentBinding);
    }

    private async Task<ExecutionIdentityRecord?> ReadPostgresAsync(Run run, CancellationToken ct) =>
        await db.ExecutionIdentities.AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RunId == run.Id.ToString()
                    && record.Attempt == run.LifecycleGeneration,
                ct)
            .ConfigureAwait(false);

    private async Task<ExecutionIdentityRecord?> ReadSqliteAsync(Run run, CancellationToken ct)
    {
        await using var connection = await sqliteDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT descriptor_id, schema_version, run_id, attempt, project_id,
                   initiating_principal_id, executing_service_id, agent_assignment_id,
                   agent_role, agent_display_name, parent_run_id, parent_descriptor_id,
                   retry_of_run_id, retry_of_descriptor_id, workflow_run_id, subtask_id,
                   approval_policy_snapshot_id, executable_workflow_content_digest, created_at
              FROM execution_identities
             WHERE run_id = $runId AND attempt = $attempt;
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$attempt", run.LifecycleGeneration);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ExecutionIdentityRecord
        {
            DescriptorId = reader.GetString(0),
            SchemaVersion = reader.GetInt32(1),
            RunId = reader.GetString(2),
            Attempt = reader.GetInt32(3),
            ProjectId = NullableString(reader, 4),
            InitiatingPrincipalId = reader.GetString(5),
            ExecutingServiceId = reader.GetString(6),
            AgentAssignmentId = reader.GetString(7),
            AgentRole = NullableString(reader, 8),
            AgentDisplayName = NullableString(reader, 9),
            ParentRunId = NullableString(reader, 10),
            ParentDescriptorId = NullableString(reader, 11),
            RetryOfRunId = NullableString(reader, 12),
            RetryOfDescriptorId = NullableString(reader, 13),
            WorkflowRunId = NullableString(reader, 14),
            SubtaskId = NullableString(reader, 15),
            ApprovalPolicySnapshotId = NullableString(reader, 16),
            ExecutableWorkflowContentDigest = NullableString(reader, 17),
            CreatedAt = DateTimeOffset.Parse(
                reader.GetString(18),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
        };
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
