using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Execution;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteRunStore : IRunStore
{
    private readonly SqliteDb _db;
    private readonly ILogger<SqliteRunStore>? _logger;

    public SqliteRunStore(SqliteDb db, ILogger<SqliteRunStore>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetChildRunIdsAsync(string parentRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM runs WHERE parent_run_id = $parent ORDER BY run_id;";
        command.Parameters.AddWithValue("$parent", parentRunId);
        return await ReadRunIdsAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetTerminalCoordinatorRunIdsAsync(
        int offset, int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id FROM runs
            WHERE parent_run_id IS NULL AND agent_name = 'Coordinator'
              AND status IN ('failed', 'completed', 'merged', 'declined', 'merge_failed')
            ORDER BY ended_at DESC, run_id LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return await ReadRunIdsAsync(command, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ReadRunIdsAsync(SqliteCommand command, CancellationToken ct)
    {
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText =
            """
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                              submitting_user, status, approval_generation, started_at, ended_at, result,
                              worktree_path, worktree_branch, project_id, model_id,
                              agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id,
                              origin, retried_from, archived_at, sandbox_backend, sandbox_claim_name,
                              sandbox_pod_name, sandbox_namespace, workflow_selection_reason,
                              executable_workflow_pin_required,
                              executable_workflow_manifest_schema_version,
                              executable_workflow_definition_id, executable_workflow_definition_version,
                              executable_workflow_source, executable_workflow_content_digest,
                              executable_workflow_definition_yaml, executable_workflow_pinned_at,
                              launch_auto_approve_tools, launch_autopilot, approval_policy_snapshot_id,
                              approval_policy_source,
                              approval_policy_captured_at, approval_policy_settings_updated_at,
                              approval_policy_inherited_from_run_id,
                              execution_input_required, execution_input_source_commit_hash,
                              execution_input_commit_hash, execution_input_composite_id)
            VALUES ($runId, $repo, $branch, $modelSource, $task,
                    $user, $status, $approvalGeneration, $startedAt, $endedAt, $result,
                    $worktreePath, $worktreeBranch, $projectId, $modelId,
                    $agentName, $agentCharter, $workflowRunId, $parentRunId, $subtaskId,
                    $origin, $retriedFrom, $archivedAt, $sandboxBackend, $sandboxClaimName,
                    $sandboxPodName, $sandboxNamespace, $workflowSelectionReason,
                    $executableWorkflowPinRequired,
                    $executableWorkflowManifestSchemaVersion,
                    $executableWorkflowDefinitionId, $executableWorkflowDefinitionVersion,
                    $executableWorkflowSource, $executableWorkflowContentDigest,
                    $executableWorkflowDefinitionYaml, $executableWorkflowPinnedAt,
                    $launchAutoApproveTools, $launchAutopilot, $approvalPolicySnapshotId,
                    $approvalPolicySource,
                    $approvalPolicyCapturedAt, $approvalPolicySettingsUpdatedAt,
                    $approvalPolicyInheritedFromRunId,
                    $executionInputRequired, $executionInputSourceCommitHash,
                    $executionInputCommitHash, $executionInputCompositeId);
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", run.Status.ToApiString());
        command.Parameters.AddWithValue("$approvalGeneration", run.ApprovalGeneration);
        command.Parameters.AddWithValue("$startedAt", Ts(run.StartedAt));
        command.Parameters.AddWithValue("$endedAt", NullableTs(run.EndedAt));
        command.Parameters.AddWithValue("$result", (object?)run.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreePath", (object?)run.WorktreePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreeBranch", (object?)run.WorktreeBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)run.ProjectId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentName", (object?)run.AgentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentCharter", (object?)run.AgentCharter ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowRunId", (object?)run.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", (object?)run.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtaskId", (object?)run.SubtaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", run.Origin.ToApiString());
        command.Parameters.AddWithValue("$retriedFrom", (object?)run.RetriedFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$archivedAt", NullableTs(run.ArchivedAt));
        command.Parameters.AddWithValue("$sandboxBackend", (object?)run.SandboxBackend ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxClaimName", (object?)run.SandboxClaimName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxPodName", (object?)run.SandboxPodName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxNamespace", (object?)run.SandboxNamespace ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowSelectionReason", (object?)run.WorkflowSelectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowPinRequired",
            run.ExecutableWorkflowPinRequired || (run.ParentRunId is null && run.ProjectId is not null) ? 1 : 0);
        AddExecutableWorkflowPinParameters(command, run);
        command.Parameters.AddWithValue("$launchAutoApproveTools", run.LaunchAutoApproveTools is { } autoApproveTools ? autoApproveTools ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$launchAutopilot", run.LaunchAutopilot is { } autopilot ? autopilot ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySnapshotId", (object?)run.ApprovalPolicySnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySource", (object?)run.ApprovalPolicySource ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicyCapturedAt", NullableTs(run.ApprovalPolicyCapturedAt));
        command.Parameters.AddWithValue("$approvalPolicySettingsUpdatedAt", NullableTs(run.ApprovalPolicySettingsUpdatedAt));
        command.Parameters.AddWithValue("$approvalPolicyInheritedFromRunId", (object?)run.ApprovalPolicyInheritedFromRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionInputRequired", run.ExecutionInputRequired ? 1 : 0);
        command.Parameters.AddWithValue("$executionInputSourceCommitHash", (object?)run.ExecutionInputSourceCommitHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionInputCommitHash", (object?)run.ExecutionInputCommitHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionInputCompositeId", (object?)run.ExecutionInputCompositeId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await CreateExecutionIdentityAsync(
            connection,
            (SqliteTransaction)tx,
            run,
            ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<bool> TryBindExecutionInputAsync(
        RunId runId,
        int expectedLifecycleGeneration,
        string sourceCommitHash,
        string executionCommitHash,
        string compositeId,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET execution_input_required = 1,
                   execution_input_source_commit_hash = $sourceCommitHash,
                   execution_input_commit_hash = $executionCommitHash,
                   execution_input_composite_id = $compositeId
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND execution_input_required = 1
               AND (
                    (execution_input_source_commit_hash IS NULL
                     AND execution_input_commit_hash IS NULL
                     AND execution_input_composite_id IS NULL)
                    OR
                    (execution_input_source_commit_hash = $sourceCommitHash
                     AND execution_input_commit_hash = $executionCommitHash
                     AND execution_input_composite_id = $compositeId)
               );
            """,
            command =>
            {
                command.Parameters.AddWithValue("$runId", runId.ToString());
                command.Parameters.AddWithValue("$generation", expectedLifecycleGeneration);
                command.Parameters.AddWithValue("$sourceCommitHash", sourceCommitHash);
                command.Parameters.AddWithValue("$executionCommitHash", executionCommitHash);
                command.Parameters.AddWithValue("$compositeId", compositeId);
            },
            ct).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status.ToApiString());

        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt,
                   current_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE current_output_revision_id END,
                   approved_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE approved_output_revision_id END,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' AND $status <> 'in_progress' THEN 1 ELSE 0 END,
                   lifecycle_generation = lifecycle_generation +
                       CASE WHEN $status = 'in_progress'
                                 AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                            THEN 1 ELSE 0 END
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", NullableTs(endedAt));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, $"update status to {status.ToApiString()}");
    }

    public async Task<bool> TryUpdateStatusAsync(
        RunId runId, RunStatus status, DateTimeOffset? endedAt, RunLeaseClaim requiredLease,
        CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        var now = DateTimeOffset.UtcNow;
        return await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt,
                   current_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE current_output_revision_id END,
                   approved_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE approved_output_revision_id END,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' AND $status <> 'in_progress' THEN 1 ELSE 0 END,
                   lifecycle_generation = lifecycle_generation +
                       CASE WHEN $status = 'in_progress'
                                 AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                            THEN 1 ELSE 0 END
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND EXISTS (
                   SELECT 1 FROM run_execution_leases lease
                    WHERE lease.run_id = runs.run_id
                      AND lease.owner_id = $leaseOwner
                      AND lease.fencing_token = $fencingToken
                      AND lease.lease_expires_at > $now);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", NullableTs(endedAt));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", requiredLease.LifecycleGeneration);
                cmd.Parameters.AddWithValue("$leaseOwner", requiredLease.OwnerId);
                cmd.Parameters.AddWithValue("$fencingToken", requiredLease.FencingToken);
                cmd.Parameters.AddWithValue("$now", Ts(now));
            }, ct).ConfigureAwait(false) == 1;
    }

    public async Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt, result = $result,
                   current_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE current_output_revision_id END,
                   approved_output_revision_id = CASE WHEN $status = 'in_progress'
                       AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                       THEN NULL ELSE approved_output_revision_id END,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' AND $status <> 'in_progress' THEN 1 ELSE 0 END,
                   lifecycle_generation = lifecycle_generation +
                       CASE WHEN $status = 'in_progress'
                                 AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready')
                            THEN 1 ELSE 0 END
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", Ts(endedAt));
                cmd.Parameters.AddWithValue("$result", result);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, $"update result to {status.ToApiString()}");
    }

    public async Task UpdateAssemblyArtifactsAsync(
        RunId runId, string treeHash, string diff, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET tree_hash = $treeHash, diff = $diff
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", treeHash);
                cmd.Parameters.AddWithValue("$diff", diff);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "persist assembly artifacts");
    }

    public async Task<bool> TryUpdateAssemblyArtifactsAsync(
        RunId runId, string treeHash, string diff, RunLeaseClaim requiredLease,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET tree_hash = $treeHash, diff = $diff
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND EXISTS (
                   SELECT 1 FROM run_execution_leases lease
                    WHERE lease.run_id = runs.run_id
                      AND lease.owner_id = $leaseOwner
                      AND lease.fencing_token = $fencingToken
                      AND lease.lease_expires_at > $now);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", treeHash);
                cmd.Parameters.AddWithValue("$diff", diff);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", requiredLease.LifecycleGeneration);
                cmd.Parameters.AddWithValue("$leaseOwner", requiredLease.OwnerId);
                cmd.Parameters.AddWithValue("$fencingToken", requiredLease.FencingToken);
                cmd.Parameters.AddWithValue("$now", Ts(now));
            }, ct).ConfigureAwait(false) == 1;
    }

    public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount,
        CancellationToken ct = default, DateTimeOffset? now = null) =>
        UpdateReviewReadyAsync(runId, treeHash, diff, stepCount, ct, now, null);

    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct,
        DateTimeOffset? now, byte[]? treeContent) =>
        await PublishReviewReadyCoreAsync(runId, null, treeHash, diff, stepCount, ct, now, treeContent).ConfigureAwait(false);

    public Task PublishReviewReadyAsync(RunId runId, int expectedLifecycleGeneration, string treeHash,
        string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) =>
        PublishReviewReadyAsync(runId, expectedLifecycleGeneration, treeHash, diff, stepCount, ct, now, null);

    public Task PublishReviewReadyAsync(
        RunId runId, int expectedLifecycleGeneration, string treeHash, string diff, int stepCount,
        CancellationToken ct, DateTimeOffset? now, byte[]? treeContent) =>
        PublishReviewReadyCoreAsync(runId, expectedLifecycleGeneration, treeHash, diff, stepCount, ct, now, treeContent);

    private async Task PublishReviewReadyCoreAsync(
        RunId runId, int? expectedLifecycleGeneration, string treeHash, string diff, int stepCount,
        CancellationToken ct, DateTimeOffset? now, byte[]? treeContent)
    {
        if (string.IsNullOrWhiteSpace(treeHash))
            throw new InvalidOperationException("Review-ready output requires a pinned tree.");
        if (treeContent is not null)
            RunOutputTree.Decode(treeContent);
        var ts = now ?? DateTimeOffset.UtcNow;
        var bytes = RunOutputRevision.EncodeDiff(diff);
        var digest = RunOutputRevision.Sha256(bytes);
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            UPDATE runs SET status = status
            WHERE run_id = $runId AND status IN ('in_progress', 'awaiting_review');
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Run cannot publish output from its current status.");

        command.CommandText =
            """
            SELECT lifecycle_generation, status, review_ready_at, executable_workflow_content_digest,
                   current_output_revision_id
            FROM runs WHERE run_id = $runId;
            """;
        int generation;
        string status;
        string? readyAt;
        string? workflowDigest;
        string? currentRevisionId;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            generation = reader.GetInt32(0);
            status = reader.GetString(1);
            readyAt = reader.IsDBNull(2) ? null : reader.GetString(2);
            workflowDigest = reader.IsDBNull(3) ? null : reader.GetString(3);
            currentRevisionId = reader.IsDBNull(4) ? null : reader.GetString(4);
        }
        if (expectedLifecycleGeneration is { } expected && generation != expected)
            throw new InvalidOperationException("Run lifecycle generation changed before output publication.");

        var existing = await GetGenerationRevisionAsync(connection, tx, runId, generation, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (status != "awaiting_review" || currentRevisionId != existing.RevisionId
                || existing.TreeHash != treeHash || existing.DiffSha256 != digest
                || existing.WorkflowDigest != workflowDigest || !existing.DiffBytes.AsSpan().SequenceEqual(bytes)
                || !(existing.TreeContent ?? []).AsSpan().SequenceEqual(treeContent ?? []))
                throw new InvalidOperationException("Output revision already published with different content or generation is no longer review-ready.");
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        if (currentRevisionId is not null || (status == "awaiting_review" && readyAt is not null))
            throw new InvalidOperationException("Review-ready run has no durable output revision.");

        command.CommandText =
            """
            SELECT revision_id FROM run_output_revisions
            WHERE run_id = $runId AND lifecycle_generation < $generation
            ORDER BY lifecycle_generation DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$generation", generation);
        var predecessor = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        command.CommandText =
            """
            INSERT INTO run_output_revisions
                (revision_id, schema_version, run_id, lifecycle_generation, workflow_digest,
                 manifest_incomplete, tree_hash, diff_sha256, predecessor_revision_id, diff_bytes, created_at,
                 tree_content, tree_content_sha256)
            VALUES ($revision, 1, $runId, $generation, $workflow, $incomplete, $tree, $digest,
                    $predecessor, $bytes, $created, $treeContent, $treeContentDigest);
            """;
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$workflow", (object?)workflowDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$incomplete", workflowDigest is null ? 1 : 0);
        command.Parameters.AddWithValue("$tree", treeHash);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$predecessor", (object?)predecessor ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$created", Ts(ts));
        command.Parameters.AddWithValue("$treeContent", (object?)treeContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$treeContentDigest", treeContent is null
            ? DBNull.Value : RunOutputRevision.Sha256(treeContent));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        command.CommandText =
            """
            UPDATE runs SET tree_hash = $tree, diff = $diff, status = 'awaiting_review', review_ready_at = $created,
                current_output_revision_id = $revision,
                approval_generation = approval_generation + CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END
            WHERE run_id = $runId AND lifecycle_generation = $generation
                AND (status = 'in_progress' OR (status = 'awaiting_review' AND review_ready_at IS NULL));
            """;
        command.Parameters.AddWithValue("$diff", diff);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Run generation changed during output publication.");
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<RunOutputRevision?> GetGenerationRevisionAsync(
        SqliteConnection connection, SqliteTransaction? tx, RunId runId, int generation, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = OutputRevisionSelect +
            " WHERE run_id = $runId AND lifecycle_generation = $generation;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapOutputRevision(reader) : null;
    }

    private const string OutputRevisionSelect =
        "SELECT revision_id, schema_version, run_id, lifecycle_generation, workflow_digest, " +
        "manifest_incomplete, tree_hash, diff_sha256, predecessor_revision_id, diff_bytes, created_at, " +
        "output_kind, merged_commit_hash, work_plan_id, merge_effect_id, accepted_no_change, " +
        "tree_content, tree_content_sha256 " +
        "FROM run_output_revisions";

    private static RunOutputRevision MapOutputRevision(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt32(1), new RunId(Guid.Parse(reader.GetString(2))),
            reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0,
            reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetInt32(15) != 0,
            reader.IsDBNull(16) ? null : reader.GetFieldValue<byte[]>(16),
            reader.IsDBNull(17) ? null : reader.GetString(17));

    public async Task<RunOutputRevision?> GetOutputRevisionAsync(
        RunId runId, string revisionId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OutputRevisionSelect + " WHERE run_id = $runId AND revision_id = $revision;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$revision", revisionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapOutputRevision(reader) : null;
    }

    public async Task<RunOutputRevision?> GetLatestOutputRevisionAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OutputRevisionSelect +
            " WHERE run_id = $runId ORDER BY lifecycle_generation DESC, rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapOutputRevision(reader) : null;
    }

    public async Task<IReadOnlyList<RunOutputRevision>> ListOutputRevisionsAsync(
        RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OutputRevisionSelect +
            " WHERE run_id = $runId ORDER BY lifecycle_generation DESC, rowid DESC;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var revisions = new List<RunOutputRevision>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            revisions.Add(MapOutputRevision(reader));
        return revisions;
    }

    public async Task<RunOutputRevision> PublishCollectiveCandidateAsync(
        RunId runId, int generation, string workPlanId, string treeHash, string diff,
        byte[] treeContent, CancellationToken ct = default) =>
        await PublishCollectiveCandidateCoreAsync(
            runId, generation, workPlanId, treeHash, diff, treeContent, ct, requiredLease: null)
            .ConfigureAwait(false);

    public async Task<RunOutputRevision> PublishCollectiveCandidateAsync(
        RunId runId, int generation, string workPlanId, string treeHash, string diff,
        byte[] treeContent, CancellationToken ct, RunLeaseClaim requiredLease) =>
        await PublishCollectiveCandidateCoreAsync(
            runId, generation, workPlanId, treeHash, diff, treeContent, ct, (RunLeaseClaim?)requiredLease)
            .ConfigureAwait(false);

    private async Task<RunOutputRevision> PublishCollectiveCandidateCoreAsync(
        RunId runId, int generation, string workPlanId, string treeHash, string diff,
        byte[] treeContent, CancellationToken ct, RunLeaseClaim? requiredLease)
    {
        if (string.IsNullOrWhiteSpace(treeHash) || string.IsNullOrWhiteSpace(workPlanId))
            throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
        RunOutputTree.Decode(treeContent);
        var bytes = RunOutputRevision.EncodeDiff(diff);
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE runs SET status=status
             WHERE run_id=$runId AND lifecycle_generation=$generation
               AND status IN ('in_progress','awaiting_review')
               AND ($leaseOwner IS NULL OR EXISTS (
                   SELECT 1 FROM run_execution_leases lease
                    WHERE lease.run_id=runs.run_id AND lease.owner_id=$leaseOwner
                      AND lease.fencing_token=$fencingToken AND lease.lease_expires_at>$now));
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$leaseOwner", (object?)requiredLease?.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fencingToken", (object?)requiredLease?.FencingToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        if (requiredLease is { } candidateLease && candidateLease.LifecycleGeneration != generation)
            throw new RunOutputRevisionUnavailableException("stale_collective_candidate");
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new RunOutputRevisionUnavailableException("stale_collective_candidate");
        command.CommandText = """
            SELECT tree_hash, diff, executable_workflow_content_digest, current_output_revision_id,
                   executable_workflow_pin_required
              FROM runs WHERE run_id=$runId;
            """;
        string? previousId;
        string? digest;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            if (reader.IsDBNull(0) || reader.GetString(0) != treeHash
                || reader.IsDBNull(1) || reader.GetString(1) != diff)
                throw new RunOutputRevisionUnavailableException("stale_collective_candidate");
            if (reader.GetInt32(4) != 0 && reader.IsDBNull(2))
                throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
            digest = reader.IsDBNull(2) ? null : reader.GetString(2);
            previousId = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        if (previousId is not null)
        {
            var previous = await GetOutputRevisionAsync(runId, previousId, ct).ConfigureAwait(false)
                ?? throw new RunOutputRevisionUnavailableException("missing_content");
            if (previous.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion
                && previous.LifecycleGeneration == generation && previous.WorkPlanId == workPlanId
                && !previous.ManifestIncomplete
                && previous.TreeHash == treeHash && previous.WorkflowDigest == digest
                && previous.DiffBytes.AsSpan().SequenceEqual(bytes)
                && (previous.TreeContent ?? []).AsSpan().SequenceEqual(treeContent))
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return previous;
            }
        }
        var revisionId = Guid.NewGuid().ToString("N");
        command.CommandText = """
            INSERT INTO run_output_revisions
                (revision_id,schema_version,run_id,lifecycle_generation,workflow_digest,
                 manifest_incomplete,tree_hash,diff_sha256,predecessor_revision_id,diff_bytes,
                 created_at,output_kind,work_plan_id,tree_content,tree_content_sha256)
            VALUES ($revision,4,$runId,$generation,$workflow,$incomplete,$tree,$sha,
                    $previous,$bytes,$created,'collective',$plan,$content,$contentSha);
            UPDATE runs SET current_output_revision_id=$revision,approved_output_revision_id=NULL
             WHERE run_id=$runId AND lifecycle_generation=$generation;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$workflow", (object?)digest ?? DBNull.Value);
        command.Parameters.AddWithValue("$incomplete", 0);
        command.Parameters.AddWithValue("$tree", treeHash);
        command.Parameters.AddWithValue("$sha", RunOutputRevision.Sha256(bytes));
        command.Parameters.AddWithValue("$previous", (object?)previousId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$created", Ts(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$plan", workPlanId);
        command.Parameters.AddWithValue("$content", treeContent);
        command.Parameters.AddWithValue("$contentSha", RunOutputRevision.Sha256(treeContent));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (await GetOutputRevisionAsync(runId, revisionId, ct).ConfigureAwait(false))!;
    }

    public async Task<bool> ApproveCollectiveCandidateAsync(
        RunId runId, int generation, string revisionId, CancellationToken ct = default) =>
        await ApproveCollectiveCandidateCoreAsync(
            runId, generation, revisionId, ct, requiredLease: null).ConfigureAwait(false);

    public async Task<bool> ApproveCollectiveCandidateAsync(
        RunId runId, int generation, string revisionId, CancellationToken ct,
        RunLeaseClaim requiredLease) =>
        await ApproveCollectiveCandidateCoreAsync(
            runId, generation, revisionId, ct, (RunLeaseClaim?)requiredLease).ConfigureAwait(false);

    private async Task<bool> ApproveCollectiveCandidateCoreAsync(
        RunId runId, int generation, string revisionId, CancellationToken ct,
        RunLeaseClaim? requiredLease)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET approved_output_revision_id=$revision
             WHERE run_id=$runId AND lifecycle_generation=$generation
               AND status IN ('in_progress','awaiting_review')
               AND current_output_revision_id=$revision
               AND ($leaseOwner IS NULL OR EXISTS (
                   SELECT 1 FROM run_execution_leases lease
                    WHERE lease.run_id=runs.run_id AND lease.owner_id=$leaseOwner
                      AND lease.fencing_token=$fencingToken AND lease.lease_expires_at>$now))
               AND EXISTS (
                   SELECT 1 FROM run_output_revisions v
                    WHERE v.revision_id=$revision AND v.run_id=$runId
                      AND v.lifecycle_generation=$generation AND v.schema_version=4
                      AND v.tree_hash=runs.tree_hash AND v.diff_sha256=$diffSha
                      AND v.workflow_digest IS runs.executable_workflow_content_digest);
            """;
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Diff is null) return false;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$diffSha", RunOutputRevision.Sha256(RunOutputRevision.EncodeDiff(run.Diff)));
        command.Parameters.AddWithValue("$leaseOwner", (object?)requiredLease?.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fencingToken", (object?)requiredLease?.FencingToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        if (requiredLease is { } approvalLease && approvalLease.LifecycleGeneration != generation)
            return false;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to InProgress.
    /// Returns true if the CAS succeeded (request-changes won the race),
    /// false if another request already moved the run out of AwaitingReview.
    /// Used by the request-changes endpoint (B3) to reclaim the run for a new revision.
    /// </summary>
    public async Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText =
            """
            UPDATE runs
               SET status = 'in_progress', ended_at = NULL, review_ready_at = NULL,
                   approved_output_revision_id = NULL, current_output_revision_id = NULL,
                   lifecycle_generation = lifecycle_generation + 1
             WHERE run_id = $runId AND status = 'awaiting_review';
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
            return false;
        await InsertCurrentExecutionIdentityAsync(connection, (SqliteTransaction)tx, runId, ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryParkForChildWorkAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = 'awaiting_review', ended_at = NULL, review_ready_at = NULL,
                   approval_generation = approval_generation + 1
             WHERE run_id = $runId
               AND status = 'in_progress'
               AND lifecycle_generation = $lifecycleGeneration;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$lifecycleGeneration", lifecycleGeneration);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryResumeFromChildWorkAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = 'in_progress', ended_at = NULL, review_ready_at = NULL
             WHERE run_id = $runId
               AND status = 'awaiting_review'
               AND lifecycle_generation = $lifecycleGeneration;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$lifecycleGeneration", lifecycleGeneration);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryReopenTerminalToInProgressAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText =
            """
            UPDATE runs
               SET status = 'in_progress', ended_at = NULL,
                   current_output_revision_id = NULL, approved_output_revision_id = NULL,
                   lifecycle_generation = lifecycle_generation + 1
             WHERE run_id = $runId AND status IN ('failed', 'merge_failed', 'assemble_ready')
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now);
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
            return false;
        await InsertCurrentExecutionIdentityAsync(connection, (SqliteTransaction)tx, runId, ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
    /// Returns true if the transition was applied (exactly one row updated), false if a
    /// concurrent request already changed the status. This single-row conditional UPDATE
    /// is the idempotency and concurrency guard for the review endpoint (design issue #4).
    /// </summary>
    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.AwaitingReview) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(toStatus, EventTypes.ReviewDeclined, new { result, reviewer }, endedAt, run.LifecycleGeneration),
            result, new HashSet<RunStatus> { RunStatus.AwaitingReview }, Reviewer: reviewer), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to Committing.
    /// Returns true if the CAS succeeded (this /commit request owns the run),
    /// false if another request already moved the run out of AwaitingReview.
    /// Must be called BEFORE CommitChanges to prevent TOCTOU races.
    /// </summary>
    public Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        TryTransitionToCommittingCoreAsync(runId, null, ct);

    public Task<bool> TryTransitionToCommittingRevisionAsync(
        RunId runId, string revisionId, CancellationToken ct = default) =>
        TryTransitionToCommittingCoreAsync(runId, revisionId, ct);

    private async Task<bool> TryTransitionToCommittingCoreAsync(
        RunId runId, string? revisionId, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var run = await ReadForRevisionFenceAsync(connection, tx, runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.AwaitingReview
            || !await MatchesReviewRevisionAsync(connection, tx, run, revisionId, ct).ConfigureAwait(false))
            return false;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            UPDATE runs SET status = 'committing', review_ready_at = NULL,
                approved_output_revision_id = $revision
            WHERE run_id = $runId AND status = 'awaiting_review'
              AND lifecycle_generation = $generation AND tree_hash IS $treeHash AND diff IS $diff;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$revision", (object?)revisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$generation", run.LifecycleGeneration);
        command.Parameters.AddWithValue("$treeHash", (object?)run.TreeHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$diff", (object?)run.Diff ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows != 1) return false;
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<Run?> ReadForRevisionFenceAsync(
        SqliteConnection connection, SqliteTransaction tx, RunId runId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = SelectSql + " WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static async Task<bool> MatchesReviewRevisionAsync(
        SqliteConnection connection, SqliteTransaction tx, Run run, string? revisionId, CancellationToken ct)
    {
        var revision = await GetGenerationRevisionAsync(connection, tx, run.Id, run.LifecycleGeneration, ct)
            .ConfigureAwait(false);
        return revision is null
            ? revisionId is null && run.CurrentOutputRevisionId is null
            : string.Equals(run.CurrentOutputRevisionId, revision.RevisionId, StringComparison.Ordinal)
              && revision.Matches(run, revisionId);
    }

    /// <summary>
    /// Reverts a run from Committing back to AwaitingReview.
    /// Optionally updates tree_hash (used by restart recovery to record the
    /// committed HEAD after a crash between CommitChanges and ExecuteMergeAsync).
    /// Returns true if a row was updated; false if the run was no longer in Committing.
    /// </summary>
    public async Task<bool> TryRevertCommittingAsync(
        RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review', tree_hash = COALESCE($treeHash, tree_hash), review_ready_at = $now, approved_output_revision_id = NULL WHERE run_id = $runId AND status = 'committing';";
        command.Parameters.AddWithValue("$treeHash", (object?)treeHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }


    /// <summary>
    /// Atomically transitions a run from AwaitingReview or Committing to Merging.
    /// Accepts both source states so the /commit flow (Committing → Merging) and
    /// the /review flow (AwaitingReview → Merging) share a single CAS guard.
    /// Returns true if the CAS succeeded (this request owns the merge slot),
    /// false if another request already moved the run out of the expected state (MF3).
    /// </summary>
    public Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        TryStartMergingCoreAsync(runId, null, reviewer, ct);

    public Task<bool> TryStartMergingRevisionAsync(
        RunId runId, string revisionId, string? reviewer = null, CancellationToken ct = default) =>
        TryStartMergingCoreAsync(runId, revisionId, reviewer, ct);

    private async Task<bool> TryStartMergingCoreAsync(
        RunId runId, string? revisionId, string? reviewer, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var run = await ReadForRevisionFenceAsync(connection, tx, runId, ct).ConfigureAwait(false);
        if (run is null || run.Status is not (RunStatus.AwaitingReview or RunStatus.Committing)
            || (run.Status == RunStatus.Committing
                && !string.Equals(run.ApprovedOutputRevisionId, revisionId, StringComparison.Ordinal))
            || !await MatchesReviewRevisionAsync(connection, tx, run, revisionId, ct).ConfigureAwait(false))
            return false;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            UPDATE runs
              SET status = 'merging', reviewed_by = $reviewer, approved_output_revision_id = $revision,
                  review_ready_at = NULL
             WHERE run_id = $runId AND status IN ('awaiting_review', 'committing')
               AND lifecycle_generation = $generation AND tree_hash IS $treeHash AND diff IS $diff;
            """;
        command.Parameters.AddWithValue("$reviewer", (object?)reviewer ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)revisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$generation", run.LifecycleGeneration);
        command.Parameters.AddWithValue("$treeHash", (object?)run.TreeHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$diff", (object?)run.Diff ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows != 1) return false;
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reverts a run from Merging back to AwaitingReview.
    /// Used on Blocked outcome or on exception fail-safe to keep the run recoverable (MF6).
    /// Returns true if a row was reverted; false if the run was no longer in Merging
    /// (a no-op the caller may log for observability).
    /// </summary>
    public async Task<bool> RevertMergingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review', review_ready_at = $now, approved_output_revision_id = NULL WHERE run_id = $runId AND status = 'merging';";
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Transitions a run from Merging to a terminal status (Merged or MergeFailed).
    /// Called after MergeWorktree returns a Merged or Conflict outcome.
    /// <paramref name="mergeConflicts"/> is a JSON array of conflicting file paths; pass null on success.
    /// <paramref name="mergedCommitHash"/> is the commit SHA produced by the merge; pass null for non-merge transitions.
    /// Returns true if the transition was applied (one row updated); false on a concurrency conflict.
    /// </summary>
    public async Task<bool> CompleteMergingAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.Merging) return false;
        var eventType = toStatus == RunStatus.Merged ? EventTypes.MergeCompleted : EventTypes.MergeFailed;
        var outcome = TerminalRunOutcome.Create(toStatus, eventType,
            new { result, mergeConflicts, mergedCommitHash }, endedAt, run.LifecycleGeneration);
        return await TryMutateTerminalOutcomeAsync(runId,
            new TerminalRunMutation(outcome, result, new HashSet<RunStatus> { RunStatus.Merging },
                MergeConflicts: mergeConflicts, MergedCommitHash: mergedCommitHash), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the tree_hash of a run that is in Committing status.
    /// Used by the /commit endpoint after staging and committing any remaining
    /// uncommitted changes on top of the agent's commit.
    /// </summary>
    public async Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET tree_hash = $treeHash WHERE run_id = $runId AND status = 'committing' AND (approved_output_revision_id IS NULL OR tree_hash = $treeHash);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", newTreeHash);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        if (rows == 0 && (await GetAsync(runId, ct).ConfigureAwait(false))?.ApprovedOutputRevisionId is not null)
            throw new InvalidOperationException("Committed tree differs from the approved output revision.");
        WarnIfNoRows(rows, runId, "update tree hash after commit");
    }

    public async Task<bool> TryRecordFanInputProjectionAsync(
        RunId runId, int generation, string expectedBaseTree, string projectedTree,
        string worktreeBranch, string? recoveredWorktreePath = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE runs SET tree_hash=$projected,
                worktree_path=COALESCE($recovered, worktree_path)
            WHERE run_id=$run AND lifecycle_generation=$generation
              AND status='awaiting_review' AND worktree_branch=$branch
              AND (tree_hash IS NULL OR tree_hash=$base OR tree_hash=$projected);
            """;
        update.Parameters.AddWithValue("$projected", projectedTree);
        update.Parameters.AddWithValue("$run", runId.ToString());
        update.Parameters.AddWithValue("$generation", generation);
        update.Parameters.AddWithValue("$branch", worktreeBranch);
        update.Parameters.AddWithValue("$base", expectedBaseTree);
        update.Parameters.AddWithValue("$recovered", (object?)recoveredWorktreePath ?? DBNull.Value);
        return await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> SetAssembleReadyAsync(
        RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount,
        DateTimeOffset endedAt, CancellationToken ct = default)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.AssembleReady, EventTypes.RunAssembleReady,
                new { treeHash, worktreeBranch, diff, stepCount }, endedAt, run.LifecycleGeneration),
            null, TreeHash: treeHash, WorktreeBranch: worktreeBranch, Diff: diff), ct).ConfigureAwait(false);
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Status-only terminal mutations are not supported; persist a typed TerminalRunOutcome instead.");
    }

    public async Task<bool> TrySetTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        string? result,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt, result = $result,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now)
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """;
        update.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        update.Parameters.AddWithValue("$endedAt", Ts(outcome.OccurredAt));
        update.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        update.Parameters.AddWithValue("$runId", runId.ToString());
        update.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        update.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            return false;

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO terminal_run_outcomes
                (run_id, lifecycle_generation, status, event_type, payload_json, occurred_at)
            VALUES ($runId, $generation, $status, $eventType, $payload, $occurredAt);
            """;
        insert.Parameters.AddWithValue("$runId", runId.ToString());
        insert.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        insert.Parameters.AddWithValue("$eventType", outcome.EventType);
        insert.Parameters.AddWithValue("$payload", outcome.Payload.GetRawText());
        insert.Parameters.AddWithValue("$occurredAt", Ts(outcome.OccurredAt));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryMutateTerminalOutcomeAsync(
        RunId runId, TerminalRunMutation mutation, CancellationToken ct = default)
    {
        if (mutation.RequiredLease is { } fence
            && fence.LifecycleGeneration != mutation.Outcome.ExpectedLifecycleGeneration)
            return false;
        // Specialized callers currently require one precise source status (merging or
        // awaiting_review). Keep that compare-and-swap in the same SQLite transaction as the outbox.
        var expected = mutation.ExpectedStatuses?.SingleOrDefault();
        if (mutation.ExpectedStatuses is { Count: > 1 })
            throw new NotSupportedException("Terminal mutations require one expected source status.");
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            """
            UPDATE runs SET status=$status, ended_at=$endedAt, result=$result,
              reviewed_by=COALESCE($reviewer, reviewed_by),
              merge_conflicts=COALESCE($mergeConflicts, merge_conflicts),
              merged_commit_hash=COALESCE($mergedCommitHash, merged_commit_hash),
              tree_hash=COALESCE($treeHash, tree_hash),
              worktree_branch=COALESCE($worktreeBranch, worktree_branch),
              diff=COALESCE($diff, diff)
             WHERE run_id=$runId AND lifecycle_generation=$generation
               AND ($collectiveTree IS NULL OR tree_hash=$collectiveTree)
               AND ($approved IS NULL OR
                    (current_output_revision_id=$approved AND approved_output_revision_id=$approved
                     AND tree_hash=$treeHash AND EXISTS (
                        SELECT 1 FROM run_output_revisions v WHERE v.revision_id=$approved
                          AND v.run_id=$runId AND v.lifecycle_generation=$generation
                          AND v.schema_version=4 AND v.tree_hash=runs.tree_hash
                          AND v.diff_sha256=$approvedDiffSha
                          AND v.workflow_digest IS runs.executable_workflow_content_digest)))
               AND ($expectedStatus IS NULL OR status=$expectedStatus)
               AND ($leaseOwner IS NULL OR EXISTS (
                   SELECT 1 FROM run_execution_leases lease
                    WHERE lease.run_id=$runId AND lease.owner_id=$leaseOwner
                      AND lease.fencing_token=$fencingToken AND lease.lease_expires_at>$now))
               AND ($parentGeneration IS NULL OR EXISTS (
                   SELECT 1 FROM runs parent WHERE parent.run_id=runs.parent_run_id
                     AND parent.lifecycle_generation=$parentGeneration
                     AND parent.status='in_progress'))
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now)
               AND status NOT IN ('merged','declined','failed','completed','merge_failed','assemble_ready','cancelled');
            """;
        update.Parameters.AddWithValue("$status", mutation.Outcome.Status.ToApiString());
        update.Parameters.AddWithValue("$endedAt", Ts(mutation.Outcome.OccurredAt));
        update.Parameters.AddWithValue("$result", (object?)mutation.Result ?? DBNull.Value);
        update.Parameters.AddWithValue("$reviewer", (object?)mutation.Reviewer ?? DBNull.Value);
        update.Parameters.AddWithValue("$mergeConflicts", (object?)mutation.MergeConflicts ?? DBNull.Value);
        update.Parameters.AddWithValue("$mergedCommitHash", (object?)mutation.MergedCommitHash ?? DBNull.Value);
        update.Parameters.AddWithValue("$treeHash", (object?)mutation.TreeHash ?? DBNull.Value);
        update.Parameters.AddWithValue("$worktreeBranch", (object?)mutation.WorktreeBranch ?? DBNull.Value);
        update.Parameters.AddWithValue("$diff", (object?)mutation.Diff ?? DBNull.Value);
        update.Parameters.AddWithValue("$runId", runId.ToString());
        update.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
        update.Parameters.AddWithValue("$collectiveTree", (object?)mutation.CollectiveOutput?.TreeHash ?? DBNull.Value);
        var approvedId = mutation.ApprovedCollectiveRevisionId;
        if (approvedId is not null
            && (mutation.CollectiveOutput is null || mutation.NoChangeOutput is not null
                || mutation.Outcome.Status != RunStatus.Completed || mutation.TreeHash is null
                || mutation.MergedCommitHash is null))
            throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
        var current = approvedId is null ? null : await GetAsync(runId, ct).ConfigureAwait(false);
        RunOutputRevision? approvedRevision = null;
        if (approvedId is not null)
        {
            approvedRevision = await GetOutputRevisionAsync(runId, approvedId, ct).ConfigureAwait(false);
            if (approvedRevision is null || approvedRevision.SchemaVersion != RunOutputRevision.CollectiveCandidateSchemaVersion
                || approvedRevision.TreeHash != mutation.TreeHash)
                return false;
            approvedRevision.ResolveFiles();
        }
        update.Parameters.AddWithValue("$approved", (object?)approvedId ?? DBNull.Value);
        update.Parameters.AddWithValue("$approvedDiffSha", current?.Diff is null
            ? DBNull.Value : RunOutputRevision.Sha256(RunOutputRevision.EncodeDiff(current.Diff)));
        update.Parameters.AddWithValue("$expectedStatus", expected is null ? DBNull.Value : expected.Value.ToApiString());
        update.Parameters.AddWithValue("$leaseOwner", (object?)mutation.RequiredLease?.OwnerId ?? DBNull.Value);
        update.Parameters.AddWithValue("$fencingToken", (object?)mutation.RequiredLease?.FencingToken ?? DBNull.Value);
        update.Parameters.AddWithValue("$parentGeneration", (object?)mutation.ExpectedParentLifecycleGeneration ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return false;
        if (mutation.CollectiveOutput is { } output)
        {
            if (output.TreeContent is null)
                throw new RunOutputRevisionUnavailableException("missing_content");
            RunOutputTree.Decode(output.TreeContent);
            if (approvedRevision is not null
                && (approvedRevision.WorkPlanId != output.WorkPlanId
                    || approvedRevision.TreeContent is null
                    || !approvedRevision.TreeContent.AsSpan().SequenceEqual(output.TreeContent)))
                throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
            if (mutation.Outcome.Status != RunStatus.Completed
                || string.IsNullOrWhiteSpace(output.CommitHash)
                || string.IsNullOrWhiteSpace(output.TreeHash)
                || string.IsNullOrWhiteSpace(output.WorkPlanId)
                || string.IsNullOrWhiteSpace(output.MergeEffectId)
                || (mutation.TreeHash is not null && mutation.TreeHash != output.TreeHash))
                throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
            await using var publish = connection.CreateCommand();
            publish.Transaction = tx;
            publish.CommandText =
                """
                SELECT diff, executable_workflow_content_digest, executable_workflow_pin_required,
                       current_output_revision_id, tree_hash
                FROM runs WHERE run_id = $runId AND lifecycle_generation = $generation;
                """;
            publish.Parameters.AddWithValue("$runId", runId.ToString());
            publish.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
            string diff;
            string? workflowDigest;
            await using (var reader = await publish.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                    || reader.IsDBNull(0)
                    || (approvedId is null ? !reader.IsDBNull(3) : reader.IsDBNull(3) || reader.GetString(3) != approvedId)
                    || reader.IsDBNull(4) || reader.GetString(4) != output.TreeHash
                    || (reader.GetInt32(2) != 0 && reader.IsDBNull(1)))
                    throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
                diff = reader.GetString(0);
                workflowDigest = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
            string? predecessor;
            if (approvedId is not null)
            {
                predecessor = approvedId;
            }
            else
            {
                publish.CommandText =
                    """
                    SELECT revision_id FROM run_output_revisions
                    WHERE run_id = $runId AND lifecycle_generation < $generation
                    ORDER BY lifecycle_generation DESC LIMIT 1;
                    """;
                predecessor = (string?)await publish.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
            var revisionId = Guid.NewGuid().ToString("N");
            var bytes = RunOutputRevision.EncodeDiff(diff);
            publish.CommandText =
                """
                INSERT INTO run_output_revisions
                    (revision_id, schema_version, run_id, lifecycle_generation, workflow_digest,
                     manifest_incomplete, tree_hash, diff_sha256, predecessor_revision_id, diff_bytes,
                     created_at, output_kind, merged_commit_hash, work_plan_id, merge_effect_id,
                     accepted_no_change, tree_content, tree_content_sha256)
                VALUES ($revision, 2, $runId, $generation, $workflow, $incomplete, $tree, $digest,
                        $predecessor, $bytes, $created, 'collective', $commit, $plan, $effect,
                        $noChange, $treeContent, $treeContentDigest);
                UPDATE runs SET current_output_revision_id = $revision
                WHERE run_id = $runId AND lifecycle_generation = $generation
                  AND (($approved IS NULL AND current_output_revision_id IS NULL)
                       OR current_output_revision_id = $approved);
                """;
            publish.Parameters.AddWithValue("$revision", revisionId);
            publish.Parameters.AddWithValue("$workflow", (object?)workflowDigest ?? DBNull.Value);
            publish.Parameters.AddWithValue("$incomplete", 0);
            publish.Parameters.AddWithValue("$tree", output.TreeHash);
            publish.Parameters.AddWithValue("$digest", RunOutputRevision.Sha256(bytes));
            publish.Parameters.AddWithValue("$predecessor", (object?)predecessor ?? DBNull.Value);
            publish.Parameters.AddWithValue("$bytes", bytes);
            publish.Parameters.AddWithValue("$created", Ts(mutation.Outcome.OccurredAt));
            publish.Parameters.AddWithValue("$commit", output.CommitHash);
            publish.Parameters.AddWithValue("$plan", output.WorkPlanId);
            publish.Parameters.AddWithValue("$effect", output.MergeEffectId);
            publish.Parameters.AddWithValue("$noChange", output.AcceptedNoChange ? 1 : 0);
            publish.Parameters.AddWithValue("$treeContent", output.TreeContent);
            publish.Parameters.AddWithValue("$treeContentDigest", RunOutputRevision.Sha256(output.TreeContent));
            publish.Parameters.AddWithValue("$approved", (object?)approvedId ?? DBNull.Value);
            await publish.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (mutation.FanDeclaredFiles is { } fan)
        {
            var files = RunOutputTree.Decode(fan.TreeContent);
            if (mutation.Outcome.Status != RunStatus.AssembleReady
                || mutation.TreeHash != fan.TreeHash
                || string.IsNullOrWhiteSpace(fan.CommitHash)
                || files.Count == 0 || files.Any(file => file.Mode is not (33188 or 33261)))
                throw new RunOutputRevisionUnavailableException("invalid_fan_manifest");
            await using var publish = connection.CreateCommand();
            publish.Transaction = tx;
            publish.CommandText = """
                SELECT parent_run_id, current_output_revision_id, diff,
                       executable_workflow_content_digest
                  FROM runs WHERE run_id=$runId AND lifecycle_generation=$generation;
                """;
            publish.Parameters.AddWithValue("$runId", runId.ToString());
            publish.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
            string? workflowDigest;
            byte[] diffBytes;
            await using (var reader = await publish.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                    || reader.IsDBNull(0) || !reader.IsDBNull(1))
                    throw new RunOutputRevisionUnavailableException("invalid_fan_manifest");
                diffBytes = [];
                workflowDigest = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
            var revisionId = Guid.NewGuid().ToString("N");
            publish.CommandText = """
                INSERT INTO run_output_revisions
                    (revision_id, schema_version, run_id, lifecycle_generation, workflow_digest,
                     manifest_incomplete, tree_hash, diff_sha256, diff_bytes, created_at,
                     output_kind, merged_commit_hash, work_plan_id, tree_content, tree_content_sha256)
                VALUES ($revision, 5, $runId, $generation, $workflow, 0, $tree, $digest, $bytes,
                        $created, 'fan_declared_files', $commit, $plan, $content, $contentDigest);
                UPDATE runs SET current_output_revision_id=$revision
                WHERE run_id=$runId AND lifecycle_generation=$generation
                  AND current_output_revision_id IS NULL;
                """;
            publish.Parameters.AddWithValue("$revision", revisionId);
            publish.Parameters.AddWithValue("$workflow", (object?)workflowDigest ?? DBNull.Value);
            publish.Parameters.AddWithValue("$tree", fan.TreeHash);
            publish.Parameters.AddWithValue("$digest", RunOutputRevision.Sha256(diffBytes));
            publish.Parameters.AddWithValue("$bytes", diffBytes);
            publish.Parameters.AddWithValue("$created", Ts(mutation.Outcome.OccurredAt));
            publish.Parameters.AddWithValue("$commit", fan.CommitHash);
            publish.Parameters.AddWithValue("$plan", fan.WorkPlanId);
            publish.Parameters.AddWithValue("$content", fan.TreeContent);
            publish.Parameters.AddWithValue("$contentDigest", RunOutputRevision.Sha256(fan.TreeContent));
            await publish.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (mutation.NoChangeOutput is { } receipt)
        {
            if (mutation.CollectiveOutput is not null || mutation.Outcome.Status != RunStatus.Completed
                || string.IsNullOrWhiteSpace(receipt.CommitHash) || string.IsNullOrWhiteSpace(receipt.TreeHash))
                throw new RunOutputRevisionUnavailableException("invalid_no_change_manifest");
            RunOutputTree.Decode(receipt.TreeContent);
            await using var publish = connection.CreateCommand();
            publish.Transaction = tx;
            publish.CommandText = """
                SELECT executable_workflow_content_digest, executable_workflow_pin_required,
                       current_output_revision_id, diff
                FROM runs WHERE run_id=$runId AND lifecycle_generation=$generation;
                """;
            publish.Parameters.AddWithValue("$runId", runId.ToString());
            publish.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
            string? workflowDigest;
            await using (var reader = await publish.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false) || !reader.IsDBNull(2)
                    || (!reader.IsDBNull(3) && reader.GetString(3).Length != 0)
                    || (reader.GetInt32(1) != 0 && reader.IsDBNull(0)))
                    throw new RunOutputRevisionUnavailableException("invalid_no_change_manifest");
                workflowDigest = reader.IsDBNull(0) ? null : reader.GetString(0);
            }
            publish.CommandText = """
                SELECT revision_id FROM run_output_revisions
                 WHERE run_id=$runId ORDER BY created_at DESC LIMIT 1;
                """;
            var predecessor = (string?)await publish.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var revisionId = Guid.NewGuid().ToString("N");
            publish.CommandText = """
                INSERT INTO run_output_revisions
                    (revision_id, schema_version, run_id, lifecycle_generation, workflow_digest,
                     manifest_incomplete, tree_hash, diff_sha256, predecessor_revision_id, diff_bytes,
                     created_at, output_kind, merged_commit_hash, accepted_no_change, tree_content, tree_content_sha256)
                VALUES ($revision, 3, $runId, $generation, $workflow, $incomplete, $tree, $digest,
                        $predecessor, $bytes, $created, 'no_change', $commit, 1, $treeContent, $treeDigest);
                UPDATE runs SET current_output_revision_id=$revision, tree_hash=$tree,
                                merged_commit_hash=$commit, diff=''
                 WHERE run_id=$runId AND lifecycle_generation=$generation AND current_output_revision_id IS NULL;
                """;
            publish.Parameters.AddWithValue("$revision", revisionId);
            publish.Parameters.AddWithValue("$workflow", (object?)workflowDigest ?? DBNull.Value);
            publish.Parameters.AddWithValue("$incomplete", workflowDigest is null ? 1 : 0);
            publish.Parameters.AddWithValue("$tree", receipt.TreeHash);
            publish.Parameters.AddWithValue("$digest", RunOutputRevision.Sha256([]));
            publish.Parameters.AddWithValue("$predecessor", (object?)predecessor ?? DBNull.Value);
            publish.Parameters.AddWithValue("$bytes", Array.Empty<byte>());
            publish.Parameters.AddWithValue("$created", Ts(mutation.Outcome.OccurredAt));
            publish.Parameters.AddWithValue("$commit", receipt.CommitHash);
            publish.Parameters.AddWithValue("$treeContent", receipt.TreeContent);
            publish.Parameters.AddWithValue("$treeDigest", RunOutputRevision.Sha256(receipt.TreeContent));
            await publish.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO terminal_run_outcomes (run_id,lifecycle_generation,status,event_type,payload_json,occurred_at) VALUES ($runId,$generation,$status,$eventType,$payload,$occurredAt);";
        insert.Parameters.AddWithValue("$runId", runId.ToString()); insert.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", mutation.Outcome.Status.ToApiString()); insert.Parameters.AddWithValue("$eventType", mutation.Outcome.EventType);
        insert.Parameters.AddWithValue("$payload", mutation.Outcome.Payload.GetRawText()); insert.Parameters.AddWithValue("$occurredAt", Ts(mutation.Outcome.OccurredAt));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<PendingTerminalRunOutcome>> GetUnprojectedTerminalOutcomesAsync(
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, lifecycle_generation, status, event_type, payload_json, occurred_at
              FROM terminal_run_outcomes
             WHERE projected_at IS NULL
             ORDER BY occurred_at;
            """;
        var result = new List<PendingTerminalRunOutcome>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            using var payload = JsonDocument.Parse(reader.GetString(4));
            result.Add(new PendingTerminalRunOutcome(
                RunId.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                new TerminalRunOutcome(
                    RunStatusExtensions.ParseStatus(reader.GetString(2)),
                    reader.GetString(3),
                    payload.RootElement.Clone(),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(1))));
        }
        return result;
    }

    public async Task MarkTerminalOutcomeProjectedAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE terminal_run_outcomes
               SET projected_at = $projectedAt
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND projected_at IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$projectedAt", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", lifecycleGeneration);
            }, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryAdoptLegacyTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO terminal_run_outcomes
                (run_id, lifecycle_generation, status, event_type, payload_json, occurred_at)
            SELECT $runId, $generation, $status, $eventType, $payload, $occurredAt
              WHERE EXISTS (
                  SELECT 1 FROM runs
                   WHERE run_id = $runId
                     AND lifecycle_generation = $generation
                     AND status = $status)
            ON CONFLICT (run_id, lifecycle_generation) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$runId", runId.ToString());
        insert.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        insert.Parameters.AddWithValue("$eventType", outcome.EventType);
        insert.Parameters.AddWithValue("$payload", outcome.Payload.GetRawText());
        insert.Parameters.AddWithValue("$occurredAt", Ts(outcome.OccurredAt));
        var rows = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryBeginPreviewPublicationAsync(
        RunId runId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryAcquirePreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, int expectedLifecycleGeneration,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = $ownerId,
                   preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled')
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", expectedLifecycleGeneration);
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryRenewPreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, int expectedLifecycleGeneration,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND preview_publication_lease_owner = $ownerId
               AND preview_publication_lease_until > $now
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", expectedLifecycleGeneration);
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryReserveTerminalPreviewCleanupAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, int expectedLifecycleGeneration,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = $ownerId,
                   preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND status IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled')
               AND (preview_publication_lease_owner IS NULL
                    OR preview_publication_lease_owner = $ownerId)
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", expectedLifecycleGeneration);
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryReserveUnclaimedPreviewCleanupAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, int expectedLifecycleGeneration,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = $ownerId,
                   preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled')
               AND preview_publication_lease_owner IS NULL
               AND preview_publication_lease_until IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", expectedLifecycleGeneration);
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task EndPreviewPublicationAsync(RunId runId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = NULL,
                   preview_publication_lease_until = NULL
             WHERE run_id = $runId;
            """,
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()),
            ct).ConfigureAwait(false);
    }

    public async Task EndPreviewPublicationAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = NULL,
                   preview_publication_lease_until = NULL
             WHERE run_id = $runId
               AND preview_publication_lease_owner = $ownerId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    public async Task<bool> IsPreviewPublicationOwnerAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
              FROM runs
             WHERE run_id = $runId
               AND preview_publication_lease_owner = $ownerId
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$ownerId", ownerId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task<DateTimeOffset?> GetPreviewPublicationLeaseAsync(
        RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_publication_lease_until FROM runs WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null || value is DBNull
            ? null
            : DateTimeOffset.TryParse(
                value.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }

    public async Task<bool> TryTransitionToIdleAsync(RunId runId, CancellationToken ct = default)
    {
        // CAS: only the replica that still sees this run as in_progress parks it dormant. Deliberately
        // does NOT set ended_at — an Idle run is paused, not ended (woken via TryWakeFromIdleAsync).
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $idle, approval_generation = approval_generation + 1
             WHERE run_id = $runId AND status = 'in_progress';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$idle", RunStatus.Idle.ToApiString());
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryWakeFromIdleAsync(RunId runId, CancellationToken ct = default)
    {
        // CAS: only the replica that still sees this run as idle wakes it back to in_progress.
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText =
            """
            UPDATE runs
               SET status = 'in_progress', current_output_revision_id = NULL,
                   approved_output_revision_id = NULL, lifecycle_generation = lifecycle_generation + 1
             WHERE run_id = $runId AND status = 'idle';
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
            return false;
        await InsertCurrentExecutionIdentityAsync(connection, (SqliteTransaction)tx, runId, ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Transitions a pre-inserted Pending run to InProgress, recording the worktree path, branch,
    /// and actual start time. Called by the project-run path after TryCreateProjectRunAsync reserves
    /// the row atomically.
    /// </summary>
    public async Task UpdateToInProgressAsync(
        RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = 'in_progress', worktree_path = $worktreePath,
                   worktree_branch = $worktreeBranch, started_at = $startedAt
             WHERE run_id = $runId AND status = 'pending';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$worktreePath", worktreePath);
                cmd.Parameters.AddWithValue("$worktreeBranch", worktreeBranch);
                cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "transition to in_progress");
    }

    public async Task DeleteAsync(RunId runId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            "DELETE FROM runs WHERE run_id = $runId;",
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the shared orchestration worktree path and branch on a coordinator run.
    /// Called once when the shared worktree is first provisioned for the orchestration.
    /// Idempotent: second call with same values is a no-op (WHERE guards against overwrite).
    /// </summary>
    public async Task UpdateWorktreeAsync(
        RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET worktree_path = $worktreePath, worktree_branch = $worktreeBranch
             WHERE run_id = $runId AND worktree_path IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$worktreePath", worktreePath);
                cmd.Parameters.AddWithValue("$worktreeBranch", worktreeBranch);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    public async Task SetSandboxInfoAsync(
        RunId runId,
        string? backend,
        string? claimName,
        string? podName,
        string? @namespace,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET sandbox_backend = COALESCE(sandbox_backend, $backend),
                   sandbox_claim_name = COALESCE(sandbox_claim_name, $claimName),
                   sandbox_pod_name = COALESCE(sandbox_pod_name, $podName),
                   sandbox_namespace = COALESCE(sandbox_namespace, $namespace)
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$backend", (object?)backend ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$claimName", (object?)claimName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$podName", (object?)podName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$namespace", (object?)@namespace ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "set sandbox info");
    }

    public async Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET archived_at = $archivedAt
             WHERE run_id = $runId AND archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("$archivedAt", Ts(archivedAt));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default)
    {
        // Returns the first child run for (parentRunId, subtaskId) whose status indicates it is
        // actively executing — states that mean a second dispatch would create a duplicate worker
        // for the same subtask. Delivered/terminal states such as assemble_ready are deliberately
        // excluded: when assembly review resets a subtask to pending, the next dispatch must create
        // a new child turn instead of reusing the old terminal output.
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            " WHERE parent_run_id = $parentRunId AND subtask_id = $subtaskId" +
            " AND status IN ('in_progress', 'awaiting_review', 'assembling', 'in_review')" +
            " ORDER BY started_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$subtaskId", subtaskId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<Run?> FindChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            " WHERE parent_run_id = $parentRunId AND subtask_id = $subtaskId" +
            " ORDER BY started_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$subtaskId", subtaskId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private async Task<int> ExecuteNonQueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void WarnIfNoRows(int rows, RunId runId, string operation)
    {
        if (rows == 0)
            _logger?.LogWarning("Run transition no-op while attempting to {Operation} for run {RunId}", operation, runId);
    }

    private static void RejectTerminalStatus(RunStatus status)
    {
        if (TerminalRunOutcome.IsTerminal(status))
            throw new InvalidOperationException(
                $"Terminal status '{status}' requires TrySetTerminalOutcomeAsync or a typed terminal mutation.");
    }

    /// <summary>
    /// Lists the runs for a project. Coordinator CHILD runs (those with a non-null
    /// <see cref="Run.ParentRunId"/>) are EXCLUDED by default: per the "children-as-nodes"
    /// directive a coordinator's children are nodes inside the single coordinator topology, not
    /// separate top-level workflows, so they must not surface in the project-wide runs list. The
    /// child rows themselves are retained (they still execute, stream, and are reachable by id for
    /// drill-down). Pass <paramref name="includeChildren"/> = true to include them (internal callers
    /// that genuinely need the full set).
    /// </summary>
    public async Task<IReadOnlyList<Run>> GetRunsByProjectAsync(
        ProjectId projectId, bool includeChildren = false, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var childFilter = includeChildren ? string.Empty : " AND parent_run_id IS NULL";
        command.CommandText = SelectSql + " WHERE project_id = $projectId AND archived_at IS NULL" + childFilter + " ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE parent_run_id = $parentRunId ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
        ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default)
    {
        var statusStrings = statuses.Select(s => s.ToApiString()).ToList();
        var paramNames = statusStrings.Select((_, i) => $"$s{i}").ToList();
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + $" WHERE project_id = $projectId AND status IN ({string.Join(", ", paramNames)});";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        for (int i = 0; i < statusStrings.Count; i++)
            command.Parameters.AddWithValue(paramNames[i], statusStrings[i]);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var agentFilter = agentName is null ? string.Empty : " AND agent_name = $agentName";
        command.CommandText = SelectSql +
            " WHERE submitting_user = $user AND archived_at IS NULL" + agentFilter +
            " ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$user", submittingUser);
        if (agentName is not null)
            command.Parameters.AddWithValue("$agentName", agentName);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// Atomically inserts a new run row with status Pending only when the
    /// referenced project is still Active. Returns true if the row was inserted
    /// (project was Active); returns false if the project is Deleting or missing
    /// (the run should be rejected with 409).
    /// </summary>
    public async Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                              submitting_user, status, started_at, ended_at, result,
                              worktree_path, worktree_branch, project_id, model_id,
                              agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id,
                              retried_from, launch_auto_approve_tools, launch_autopilot,
                              approval_policy_snapshot_id,
                              approval_policy_source, approval_policy_captured_at,
                              approval_policy_settings_updated_at, approval_policy_inherited_from_run_id,
                              executable_workflow_pin_required,
                              executable_workflow_manifest_schema_version,
                              executable_workflow_definition_id, executable_workflow_definition_version,
                              executable_workflow_source, executable_workflow_content_digest,
                              executable_workflow_definition_yaml, executable_workflow_pinned_at,
                              execution_input_required, execution_input_source_commit_hash,
                              execution_input_commit_hash, execution_input_composite_id)
            SELECT $runId, $repo, $branch, $modelSource, $task,
                   $user, $status, $startedAt, NULL, NULL,
                   NULL, NULL, $projectId, $modelId,
                   $agentName, $agentCharter, $workflowRunId, $parentRunId, $subtaskId,
                   $retriedFrom, $launchAutoApproveTools, $launchAutopilot,
                   $approvalPolicySnapshotId,
                   $approvalPolicySource, $approvalPolicyCapturedAt,
                   $approvalPolicySettingsUpdatedAt, $approvalPolicyInheritedFromRunId,
                   $executableWorkflowPinRequired,
                   $executableWorkflowManifestSchemaVersion,
                   $executableWorkflowDefinitionId, $executableWorkflowDefinitionVersion,
                   $executableWorkflowSource, $executableWorkflowContentDigest,
                   $executableWorkflowDefinitionYaml, $executableWorkflowPinnedAt,
                   $executionInputRequired, $executionInputSourceCommitHash,
                   $executionInputCommitHash, $executionInputCompositeId
            WHERE EXISTS (
                SELECT 1 FROM projects WHERE project_id = $projectId AND state = 'active'
            );
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", RunStatus.Pending.ToApiString());
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$projectId", run.ProjectId!.Value.ToString());
        command.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentName", (object?)run.AgentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentCharter", (object?)run.AgentCharter ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowRunId", (object?)run.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", (object?)run.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtaskId", (object?)run.SubtaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$retriedFrom", (object?)run.RetriedFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$launchAutoApproveTools", run.LaunchAutoApproveTools is { } autoApproveTools ? autoApproveTools ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$launchAutopilot", run.LaunchAutopilot is { } autopilot ? autopilot ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySnapshotId", (object?)run.ApprovalPolicySnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySource", (object?)run.ApprovalPolicySource ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicyCapturedAt", NullableTs(run.ApprovalPolicyCapturedAt));
        command.Parameters.AddWithValue("$approvalPolicySettingsUpdatedAt", NullableTs(run.ApprovalPolicySettingsUpdatedAt));
        command.Parameters.AddWithValue("$approvalPolicyInheritedFromRunId", (object?)run.ApprovalPolicyInheritedFromRunId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowPinRequired",
            run.ExecutableWorkflowPinRequired || (run.ParentRunId is null && run.ProjectId is not null) ? 1 : 0);
        AddExecutableWorkflowPinParameters(command, run);
        command.Parameters.AddWithValue("$executionInputRequired", run.ExecutionInputRequired ? 1 : 0);
        command.Parameters.AddWithValue("$executionInputSourceCommitHash", (object?)run.ExecutionInputSourceCommitHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionInputCommitHash", (object?)run.ExecutionInputCommitHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionInputCompositeId", (object?)run.ExecutionInputCompositeId ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0)
        {
            await CreateExecutionIdentityAsync(connection, tx, run, ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    internal static async Task InsertExecutionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionIdentityDescriptor descriptor,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO execution_identities (
                descriptor_id, schema_version, run_id, attempt, project_id,
                initiating_principal_id, executing_service_id, agent_assignment_id,
                agent_role, agent_display_name, parent_run_id, parent_descriptor_id,
                retry_of_run_id, retry_of_descriptor_id, workflow_run_id, subtask_id,
                approval_policy_snapshot_id, executable_workflow_content_digest, created_at)
            VALUES (
                $descriptorId, $schemaVersion, $runId, $attempt, $projectId,
                $initiatingPrincipalId, $executingServiceId, $agentAssignmentId,
                $agentRole, $agentDisplayName, $parentRunId, $parentDescriptorId,
                $retryOfRunId, $retryOfDescriptorId, $workflowRunId, $subtaskId,
                $approvalPolicySnapshotId, $executableWorkflowContentDigest, $createdAt);
            """;
        command.Parameters.AddWithValue("$descriptorId", descriptor.DescriptorId);
        command.Parameters.AddWithValue("$schemaVersion", descriptor.SchemaVersion);
        command.Parameters.AddWithValue("$runId", descriptor.RunId);
        command.Parameters.AddWithValue("$attempt", descriptor.Attempt);
        command.Parameters.AddWithValue("$projectId", (object?)descriptor.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$initiatingPrincipalId", descriptor.InitiatingPrincipalId);
        command.Parameters.AddWithValue("$executingServiceId", descriptor.ExecutingServiceId);
        command.Parameters.AddWithValue("$agentAssignmentId", descriptor.AgentAssignmentId);
        command.Parameters.AddWithValue("$agentRole", (object?)descriptor.AgentRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentDisplayName", (object?)descriptor.AgentDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", (object?)descriptor.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentDescriptorId", (object?)descriptor.ParentDescriptorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryOfRunId", (object?)descriptor.RetryOfRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryOfDescriptorId", (object?)descriptor.RetryOfDescriptorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowRunId", (object?)descriptor.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtaskId", (object?)descriptor.SubtaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySnapshotId", (object?)descriptor.ApprovalPolicySnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$executableWorkflowContentDigest", (object?)descriptor.ExecutableWorkflowContentDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", Ts(descriptor.CreatedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task CreateExecutionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Run run,
        CancellationToken ct)
    {
        async Task<string?> LatestDescriptorIdAsync(string? linkedRunId)
        {
            if (string.IsNullOrWhiteSpace(linkedRunId))
                return null;

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT descriptor_id
                  FROM execution_identities
                 WHERE run_id = $runId
                 ORDER BY attempt DESC
                 LIMIT 1;
                """;
            select.Parameters.AddWithValue("$runId", linkedRunId);
            return await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        var parentDescriptorId = await LatestDescriptorIdAsync(run.ParentRunId);
        var retryDescriptorId = await LatestDescriptorIdAsync(run.RetriedFrom);
        await InsertExecutionIdentityAsync(
            connection,
            transaction,
            ExecutionIdentityDescriptor.CreateWithResolvedLineage(
                run,
                parentDescriptorId,
                retryDescriptorId),
            ct).ConfigureAwait(false);
    }

    private static async Task InsertCurrentExecutionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = SelectSql + " WHERE run_id = $runId;";
        select.Parameters.AddWithValue("$runId", runId.ToString());
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Run '{runId}' disappeared while creating its execution identity.");
        var run = Map(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        await CreateExecutionIdentityAsync(connection, transaction, run, ct).ConfigureAwait(false);
    }

    private static void AddExecutableWorkflowPinParameters(SqliteCommand command, Run run)
    {
        command.Parameters.AddWithValue(
            "$executableWorkflowManifestSchemaVersion",
            (object?)run.ExecutableWorkflowManifestSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowDefinitionId",
            (object?)run.ExecutableWorkflowDefinitionId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowDefinitionVersion",
            (object?)run.ExecutableWorkflowDefinitionVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowSource",
            (object?)run.ExecutableWorkflowSource ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowContentDigest",
            (object?)run.ExecutableWorkflowContentDigest ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowDefinitionYaml",
            (object?)run.ExecutableWorkflowDefinitionYaml ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableWorkflowPinnedAt",
            NullableTs(run.ExecutableWorkflowPinnedAt));
    }

    // Ordinals: 0=run_id 1=repository_path 2=originating_branch 3=model_source 4=task
    //           5=submitting_user 6=status 7=approval_generation 8=lifecycle_generation 9=started_at 10=ended_at 11=result
    //           12=worktree_path 13=worktree_branch 14=tree_hash 15=diff 16=merge_conflicts
    //           17=project_id 18=model_id 19=agent_name 20=agent_charter 21=reviewed_by
    //           22=workflow_run_id 23=merged_commit_hash 24=parent_run_id 25=subtask_id
    //           26=origin 27=retried_from 28=archived_at 29=sandbox_backend 30=sandbox_claim_name
    //           31=sandbox_pod_name 32=sandbox_namespace 33=workflow_selection_reason
    //           34=launch_auto_approve_tools 35=launch_autopilot 36=approval_policy_snapshot_id
    //           37=approval_policy_source 38=approval_policy_captured_at
    //           39=approval_policy_settings_updated_at 40=approval_policy_inherited_from_run_id
    //           41=executable_workflow_pin_required 42=executable_workflow_manifest_schema_version
    //           43=executable_workflow_definition_id 44=executable_workflow_definition_version
    //           45=executable_workflow_source 46=executable_workflow_content_digest
    //           47=executable_workflow_definition_yaml 48=executable_workflow_pinned_at
    private const string SelectSql =
        """
        SELECT run_id, repository_path, originating_branch, model_source, task,
               submitting_user, status, approval_generation, lifecycle_generation, started_at, ended_at, result,
               worktree_path, worktree_branch, tree_hash, diff, merge_conflicts,
               project_id, model_id, agent_name, agent_charter, reviewed_by,
               workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
               origin, retried_from, archived_at, sandbox_backend, sandbox_claim_name,
               sandbox_pod_name, sandbox_namespace, workflow_selection_reason,
               launch_auto_approve_tools, launch_autopilot, approval_policy_snapshot_id,
               approval_policy_source,
               approval_policy_captured_at, approval_policy_settings_updated_at,
               approval_policy_inherited_from_run_id,
               executable_workflow_pin_required, executable_workflow_manifest_schema_version,
               executable_workflow_definition_id, executable_workflow_definition_version,
               executable_workflow_source, executable_workflow_content_digest,
               executable_workflow_definition_yaml, executable_workflow_pinned_at,
               approved_output_revision_id, current_output_revision_id,
               execution_input_required, execution_input_source_commit_hash,
               execution_input_commit_hash, execution_input_composite_id
          FROM runs
        """;

    private static Run Map(SqliteDataReader r) => new()
    {
        Id               = RunId.Parse(r.GetString(0)),
        RepositoryPath   = r.GetString(1),
        OriginatingBranch = r.GetString(2),
        ModelSource      = ModelSourceExtensions.FromApiString(r.GetString(3)),
        Task             = r.GetString(4),
        SubmittingUser   = r.GetString(5),
        Status           = RunStatusExtensions.ParseStatus(r.GetString(6)),
        ApprovalGeneration = r.GetInt32(7),
        LifecycleGeneration = r.GetInt32(8),
        StartedAt        = DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt          = r.IsDBNull(10)  ? null : DateTimeOffset.Parse(r.GetString(10),  CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Result           = r.IsDBNull(11) ? null : r.GetString(11),
        WorktreePath     = r.IsDBNull(12) ? null : r.GetString(12),
        WorktreeBranch   = r.IsDBNull(13) ? null : r.GetString(13),
        TreeHash         = r.IsDBNull(14) ? null : r.GetString(14),
        StepCount        = 0,
        Diff             = r.IsDBNull(15) ? null : r.GetString(15),
        MergeConflicts   = r.IsDBNull(16) ? null : r.GetString(16),
        ProjectId        = r.IsDBNull(17) ? null : ProjectId.Parse(r.GetString(17)),
        ModelId          = r.IsDBNull(18) ? null : r.GetString(18),
        AgentName        = r.IsDBNull(19) ? null : r.GetString(19),
        AgentCharter     = r.IsDBNull(20) ? null : r.GetString(20),
        ReviewedBy       = r.IsDBNull(21) ? null : r.GetString(21),
        WorkflowRunId    = r.IsDBNull(22) ? null : r.GetString(22),
        MergedCommitHash = r.IsDBNull(23) ? null : r.GetString(23),
        ParentRunId      = r.IsDBNull(24) ? null : r.GetString(24),
        SubtaskId        = r.IsDBNull(25) ? null : r.GetString(25),
        Origin           = RunOriginExtensions.ParseOrigin(r.IsDBNull(26) ? null : r.GetString(26)),
        RetriedFrom      = r.IsDBNull(27) ? null : r.GetString(27),
        ArchivedAt       = r.IsDBNull(28) ? null : DateTimeOffset.Parse(r.GetString(28), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SandboxBackend   = r.IsDBNull(29) ? null : r.GetString(29),
        SandboxClaimName = r.IsDBNull(30) ? null : r.GetString(30),
        SandboxPodName   = r.IsDBNull(31) ? null : r.GetString(31),
        SandboxNamespace = r.IsDBNull(32) ? null : r.GetString(32),
        WorkflowSelectionReason = r.IsDBNull(33) ? null : r.GetString(33),
        LaunchAutoApproveTools = r.IsDBNull(34) ? null : r.GetInt32(34) != 0,
        LaunchAutopilot = r.IsDBNull(35) ? null : r.GetInt32(35) != 0,
        ApprovalPolicySnapshotId = r.IsDBNull(36) ? null : r.GetString(36),
        ApprovalPolicySource = r.IsDBNull(37) ? null : r.GetString(37),
        ApprovalPolicyCapturedAt = r.IsDBNull(38) ? null : DateTimeOffset.Parse(r.GetString(38), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ApprovalPolicySettingsUpdatedAt = r.IsDBNull(39) ? null : DateTimeOffset.Parse(r.GetString(39), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ApprovalPolicyInheritedFromRunId = r.IsDBNull(40) ? null : r.GetString(40),
        ExecutableWorkflowPinRequired = !r.IsDBNull(41) && r.GetInt32(41) != 0,
        ExecutableWorkflowManifestSchemaVersion = r.IsDBNull(42) ? null : r.GetInt32(42),
        ExecutableWorkflowDefinitionId = r.IsDBNull(43) ? null : r.GetString(43),
        ExecutableWorkflowDefinitionVersion = r.IsDBNull(44) ? null : r.GetString(44),
        ExecutableWorkflowSource = r.IsDBNull(45) ? null : r.GetString(45),
        ExecutableWorkflowContentDigest = r.IsDBNull(46) ? null : r.GetString(46),
        ExecutableWorkflowDefinitionYaml = r.IsDBNull(47) ? null : r.GetString(47),
        ExecutableWorkflowPinnedAt = r.IsDBNull(48) ? null : DateTimeOffset.Parse(r.GetString(48), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ApprovedOutputRevisionId = r.IsDBNull(49) ? null : r.GetString(49),
        CurrentOutputRevisionId = r.IsDBNull(50) ? null : r.GetString(50),
        ExecutionInputRequired = !r.IsDBNull(51) && r.GetInt32(51) != 0,
        ExecutionInputSourceCommitHash = r.IsDBNull(52) ? null : r.GetString(52),
        ExecutionInputCommitHash = r.IsDBNull(53) ? null : r.GetString(53),
        ExecutionInputCompositeId = r.IsDBNull(54) ? null : r.GetString(54),
    };

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
    private static object NullableTs(DateTimeOffset? v) => v is null ? DBNull.Value : Ts(v.Value);

    /// <summary>
    /// Returns the run whose workflow_run_id matches, falling back to run_id for
    /// legacy runs that have no workflow_run_id (COALESCE behaviour).
    /// </summary>
    public async Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE COALESCE(workflow_run_id, run_id) = $workflowRunId;";
        command.Parameters.AddWithValue("$workflowRunId", workflowRunId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET workflow_selection_reason = $reason WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "update workflow selection reason");
    }

    public async Task UpdateExecutableWorkflowPinAsync(
        RunId runId,
        ExecutableWorkflowPin pin,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET executable_workflow_pin_required = 1,
                   executable_workflow_manifest_schema_version = $schemaVersion,
                   executable_workflow_definition_id = $definitionId,
                   executable_workflow_definition_version = $definitionVersion,
                   executable_workflow_source = $source,
                   executable_workflow_content_digest = $contentDigest,
                   executable_workflow_definition_yaml = $definitionYaml,
                   executable_workflow_pinned_at = $pinnedAt
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$schemaVersion", pin.ManifestSchemaVersion);
                cmd.Parameters.AddWithValue("$definitionId", pin.DefinitionId);
                cmd.Parameters.AddWithValue("$definitionVersion", (object?)pin.DefinitionVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$source", pin.Source);
                cmd.Parameters.AddWithValue("$contentDigest", pin.ContentDigest);
                cmd.Parameters.AddWithValue("$definitionYaml", pin.DefinitionYaml);
                cmd.Parameters.AddWithValue("$pinnedAt", Ts(pin.PinnedAt));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        if (rows == 0)
            throw new InvalidOperationException($"Cannot pin executable workflow because run {runId} does not exist.");
    }

    public async Task UpdateModelSourceAsync(RunId runId, ModelSource modelSource, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET model_source = $modelSource WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$modelSource", modelSource.ToApiString());
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "update model source");
    }
}
