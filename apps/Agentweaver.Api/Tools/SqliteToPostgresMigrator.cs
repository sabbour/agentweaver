using System.Globalization;
using Agentweaver.Api.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Tools;

/// <summary>
/// Migrates data from the two SQLite databases (agentweaver.db and memory.db) into Postgres
/// (or any non-SQLite MemoryDbContext provider) via EF Core.
///
/// Invoke via the <c>--migrate-data</c> CLI flag or call <see cref="RunAsync"/> directly.
/// Operations are idempotent: rows that already exist (by primary key) are skipped.
/// </summary>
public sealed class SqliteToPostgresMigrator
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqliteToPostgresMigrator> _logger;

    public SqliteToPostgresMigrator(
        IDbContextFactory<MemoryDbContext> factory,
        IConfiguration configuration,
        ILogger<SqliteToPostgresMigrator> logger)
    {
        _factory = factory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var agentweaverDbPath = GetSqlitePath("Database:Path", "agentweaver.db");
        var memoryDbPath = GetSqlitePath("Database:MemoryPath", "memory.db");

        _logger.LogInformation("Starting SQLite → Postgres migration");
        _logger.LogInformation("  agentweaver.db: {Path}", agentweaverDbPath);
        _logger.LogInformation("  memory.db:      {Path}", memoryDbPath);

        await using var db = await _factory.CreateDbContextAsync(ct);

        // Migrate agentweaver.db tables
        await MigrateAgentweaverDbAsync(agentweaverDbPath, db, ct);

        // Note: memory.db EF entities (AgentMemory, Decisions, etc.) are managed by EF migrations
        // and don't need data migration for fresh Postgres deployments. If existing memory.db data
        // must be preserved, extend this migrator with table-by-table reads from memory.db.

        _logger.LogInformation("Migration complete.");
    }

    private async Task MigrateAgentweaverDbAsync(string dbPath, MemoryDbContext db, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("agentweaver.db not found at {Path}; skipping agentweaver.db migration.", dbPath);
            return;
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        var projects = await ReadProjectsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} projects...", projects.Count);
        var projMigrated = 0;
        foreach (var rec in projects)
        {
            if (!await db.Projects.AnyAsync(p => p.ProjectId == rec.ProjectId, ct))
            {
                db.Projects.Add(rec);
                projMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  Projects: {Migrated}/{Total} migrated, {Skipped} skipped.",
            projMigrated, projects.Count, projects.Count - projMigrated);

        var runs = await ReadRunsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} runs...", runs.Count);
        var runsMigrated = 0;
        foreach (var rec in runs)
        {
            if (!await db.Runs.AnyAsync(r => r.RunId == rec.RunId, ct))
            {
                db.Runs.Add(rec);
                runsMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  Runs: {Migrated}/{Total} migrated, {Skipped} skipped.",
            runsMigrated, runs.Count, runs.Count - runsMigrated);

        var revisions = await ReadRunRevisionsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} run revisions...", revisions.Count);
        var revMigrated = 0;
        foreach (var rec in revisions)
        {
            if (!await db.RunRevisions.AnyAsync(r => r.RunId == rec.RunId && r.RevisionNumber == rec.RevisionNumber, ct))
            {
                db.RunRevisions.Add(rec);
                revMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  RunRevisions: {Migrated}/{Total} migrated, {Skipped} skipped.",
            revMigrated, revisions.Count, revisions.Count - revMigrated);

        var workflowRuns = await ReadWorkflowRunsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} workflow runs...", workflowRuns.Count);
        var wfMigrated = 0;
        foreach (var rec in workflowRuns)
        {
            if (!await db.WorkflowRuns.AnyAsync(w => w.WorkflowRunId == rec.WorkflowRunId, ct))
            {
                db.WorkflowRuns.Add(rec);
                wfMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  WorkflowRuns: {Migrated}/{Total} migrated, {Skipped} skipped.",
            wfMigrated, workflowRuns.Count, workflowRuns.Count - wfMigrated);

        var backlogTasks = await ReadBacklogTasksAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} backlog tasks...", backlogTasks.Count);
        var btMigrated = 0;
        foreach (var rec in backlogTasks)
        {
            if (!await db.BacklogTasks.AnyAsync(t => t.TaskId == rec.TaskId, ct))
            {
                db.BacklogTasks.Add(rec);
                btMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  BacklogTasks: {Migrated}/{Total} migrated, {Skipped} skipped.",
            btMigrated, backlogTasks.Count, backlogTasks.Count - btMigrated);

        // cast_proposals might not exist on older databases
        try
        {
            var castProposals = await ReadCastProposalsAsync(conn, ct);
            _logger.LogInformation("Migrating {Count} cast proposals...", castProposals.Count);
            var cpMigrated = 0;
            foreach (var rec in castProposals)
            {
                if (!await db.CastProposals.AnyAsync(p => p.Id == rec.Id, ct))
                {
                    db.CastProposals.Add(rec);
                    cpMigrated++;
                }
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("  CastProposals: {Migrated}/{Total} migrated, {Skipped} skipped.",
                cpMigrated, castProposals.Count, castProposals.Count - cpMigrated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not migrate cast_proposals (table may not exist in older database).");
        }
    }

    private static async Task<List<ProjectRecord>> ReadProjectsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<ProjectRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, name, origin_kind, source_repository, working_directory,
                   COALESCE(default_branch,'main'), owner, default_provider,
                   default_model_copilot, default_model_foundry,
                   COALESCE(state,'active'), created_at, updated_at,
                   COALESCE(max_ready_per_heartbeat,3), COALESCE(pickup_autopilot,1),
                   COALESCE(pickup_auto_approve_tools,0),
                   default_workflow_id, active_review_policy_name, sandbox_profile,
                   source_blueprint_id, source_blueprint_type, allowed_workflow_ids
              FROM projects;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProjectRecord
            {
                ProjectId = reader.GetString(0),
                Name = reader.GetString(1),
                OriginKind = reader.GetString(2),
                SourceRepository = reader.IsDBNull(3) ? null : reader.GetString(3),
                WorkingDirectory = reader.GetString(4),
                DefaultBranch = reader.GetString(5),
                Owner = reader.GetString(6),
                DefaultProvider = reader.GetString(7),
                DefaultModelCopilot = reader.IsDBNull(8) ? null : reader.GetString(8),
                DefaultModelFoundry = reader.IsDBNull(9) ? null : reader.GetString(9),
                State = reader.GetString(10),
                CreatedAt = ParseTs(reader.GetString(11)),
                UpdatedAt = ParseTs(reader.GetString(12)),
                MaxReadyPerHeartbeat = reader.GetInt32(13),
                PickupAutopilot = reader.GetInt32(14) != 0,
                PickupAutoApproveTools = reader.GetInt32(15) != 0,
                DefaultWorkflowId = reader.IsDBNull(16) ? null : reader.GetString(16),
                ActiveReviewPolicyName = reader.IsDBNull(17) ? null : reader.GetString(17),
                SandboxProfile = reader.IsDBNull(18) ? null : reader.GetString(18),
                SourceBlueprintId = reader.IsDBNull(19) ? null : reader.GetString(19),
                SourceBlueprintType = reader.IsDBNull(20) ? null : reader.GetString(20),
                AllowedWorkflowIds = reader.IsDBNull(21) ? null : reader.GetString(21),
            });
        }
        return results;
    }

    private static async Task<List<RunRecord>> ReadRunsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<RunRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT run_id, repository_path, originating_branch, model_source, task,
                  submitting_user, status, started_at, ended_at, result,
                  worktree_path, worktree_branch, tree_hash, diff,
                  merge_conflicts, project_id, model_id, agent_name, agent_charter,
                  reviewed_by, workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
                  COALESCE(origin,'interactive'), retried_from, review_ready_at, archived_at
              FROM runs;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunRecord
            {
                RunId = reader.GetString(0),
                RepositoryPath = reader.GetString(1),
                OriginatingBranch = reader.GetString(2),
                ModelSource = reader.GetString(3),
                Task = reader.GetString(4),
                SubmittingUser = reader.GetString(5),
                Status = reader.GetString(6),
                StartedAt = ParseTs(reader.GetString(7)),
                EndedAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                Result = reader.IsDBNull(9) ? null : reader.GetString(9),
                WorktreePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                WorktreeBranch = reader.IsDBNull(11) ? null : reader.GetString(11),
                TreeHash = reader.IsDBNull(12) ? null : reader.GetString(12),
                Diff = reader.IsDBNull(13) ? null : reader.GetString(13),
                MergeConflicts = reader.IsDBNull(14) ? null : reader.GetString(14),
                ProjectId = reader.IsDBNull(15) ? null : reader.GetString(15),
                ModelId = reader.IsDBNull(16) ? null : reader.GetString(16),
                AgentName = reader.IsDBNull(17) ? null : reader.GetString(17),
                AgentCharter = reader.IsDBNull(18) ? null : reader.GetString(18),
                ReviewedBy = reader.IsDBNull(19) ? null : reader.GetString(19),
                WorkflowRunId = reader.IsDBNull(20) ? null : reader.GetString(20),
                MergedCommitHash = reader.IsDBNull(21) ? null : reader.GetString(21),
                ParentRunId = reader.IsDBNull(22) ? null : reader.GetString(22),
                SubtaskId = reader.IsDBNull(23) ? null : reader.GetString(23),
                Origin = reader.GetString(24),
                RetriedFrom = reader.IsDBNull(25) ? null : reader.GetString(25),
                ReviewReadyAt = reader.IsDBNull(26) ? null : ParseTs(reader.GetString(26)),
                ArchivedAt = reader.IsDBNull(27) ? null : ParseTs(reader.GetString(27)),
            });
        }
        return results;
    }

    private static async Task<List<RunRevisionRecord>> ReadRunRevisionsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<RunRevisionRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT run_id, revision_number, reviewer_user, created_at, raw_comment, sanitized_comment, previous_tree_hash FROM run_revisions;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunRevisionRecord
            {
                RunId = reader.GetString(0),
                RevisionNumber = reader.GetInt32(1),
                ReviewerUser = reader.GetString(2),
                CreatedAt = ParseTs(reader.GetString(3)),
                RawComment = reader.GetString(4),
                SanitizedComment = reader.GetString(5),
                PreviousTreeHash = reader.GetString(6),
            });
        }
        return results;
    }

    private static async Task<List<WorkflowRunRecord>> ReadWorkflowRunsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<WorkflowRunRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT workflow_run_id, project_id, task, submitting_user, started_at, orchestration_worktree_path FROM workflow_runs;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WorkflowRunRecord
            {
                WorkflowRunId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Task = reader.GetString(2),
                SubmittingUser = reader.GetString(3),
                StartedAt = ParseTs(reader.GetString(4)),
                OrchestrationWorktreePath = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return results;
    }

    private static async Task<List<BacklogTaskRecord>> ReadBacklogTasksAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<BacklogTaskRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT task_id, project_id, title, description, state, order_key,
                   captured_by, created_at, committed_at, claimed_at, run_id,
                   workflow_override_id, archived_at, source_file_path
              FROM backlog_tasks;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BacklogTaskRecord
            {
                TaskId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Title = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                State = reader.GetString(4),
                OrderKey = reader.GetString(5),
                CapturedBy = reader.GetString(6),
                CreatedAt = ParseTs(reader.GetString(7)),
                CommittedAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                ClaimedAt = reader.IsDBNull(9) ? null : ParseTs(reader.GetString(9)),
                RunId = reader.IsDBNull(10) ? null : reader.GetString(10),
                WorkflowOverrideId = reader.IsDBNull(11) ? null : reader.GetString(11),
                ArchivedAt = reader.IsDBNull(12) ? null : ParseTs(reader.GetString(12)),
                SourceFilePath = reader.IsDBNull(13) ? null : reader.GetString(13),
            });
        }
        return results;
    }

    private static async Task<List<CastProposalRecord>> ReadCastProposalsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<CastProposalRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, project_id, owner, created_at, expires_at, proposal_json FROM cast_proposals;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CastProposalRecord
            {
                Id = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Owner = reader.GetString(2),
                CreatedAt = ParseTs(reader.GetString(3)),
                ExpiresAt = ParseTs(reader.GetString(4)),
                ProposalJson = reader.GetString(5),
            });
        }
        return results;
    }

    private string GetSqlitePath(string configKey, string defaultFilename)
    {
        var configured = _configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var baseDir = _configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
            ? Path.GetDirectoryName(Path.GetFullPath(p))!
            : Infrastructure.AppPaths.DataDirectory;

        return Path.Combine(baseDir, defaultFilename);
    }

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
