using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Tools;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// Integration tests for <see cref="SqliteToPostgresMigrator"/>.
/// Seeds a small in-disk SQLite pair (agentweaver.db + memory.db),
/// runs the migrator against the Testcontainers Postgres instance,
/// and asserts that row counts match and that a second run is a no-op.
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class DataMigratorTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly string _tempDir;
    private readonly string _agentweaverDbPath;
    private readonly string _memoryDbPath;

    public DataMigratorTests(PostgresFixture pg)
    {
        _pg = pg;
        _tempDir = Path.Combine(Path.GetTempPath(), "aw-migtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _agentweaverDbPath = Path.Combine(_tempDir, "agentweaver.db");
        _memoryDbPath = Path.Combine(_tempDir, "memory.db");

        SeedSqliteDb(_agentweaverDbPath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3a. Migration utility: row counts match source
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migrator_RowCounts_MatchSourceDatabase()
    {
        var migrator = BuildMigrator();
        await migrator.RunAsync();

        await using var db = await _pg.CreateDbContextAsync();
        var projects = await db.Projects.CountAsync();
        var runs = await db.Runs.CountAsync();
        var revisions = await db.RunRevisions.CountAsync();
        var workflowRuns = await db.WorkflowRuns.CountAsync();
        var backlogTasks = await db.BacklogTasks.CountAsync();

        // We seeded 2 projects, 3 runs, 2 revisions, 1 workflow run, 2 backlog tasks
        projects.Should().BeGreaterThanOrEqualTo(2, "all seeded projects must be migrated");
        runs.Should().BeGreaterThanOrEqualTo(3, "all seeded runs must be migrated");
        revisions.Should().BeGreaterThanOrEqualTo(2, "all seeded run_revisions must be migrated");
        workflowRuns.Should().BeGreaterThanOrEqualTo(1, "all seeded workflow_runs must be migrated");
        backlogTasks.Should().BeGreaterThanOrEqualTo(2, "all seeded backlog_tasks must be migrated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3b. Idempotency: second run is a no-op (no duplicate rows, no exception)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migrator_SecondRun_IsNoOp()
    {
        var migrator = BuildMigrator();
        await migrator.RunAsync(); // first run

        await using var db1 = await _pg.CreateDbContextAsync();
        var projectsAfterFirst = await db1.Projects.CountAsync();
        var runsAfterFirst = await db1.Runs.CountAsync();

        await migrator.RunAsync(); // second run — must be idempotent

        await using var db2 = await _pg.CreateDbContextAsync();
        var projectsAfterSecond = await db2.Projects.CountAsync();
        var runsAfterSecond = await db2.Runs.CountAsync();

        projectsAfterSecond.Should().Be(projectsAfterFirst,
            "second migration run must not insert duplicate projects");
        runsAfterSecond.Should().Be(runsAfterFirst,
            "second migration run must not insert duplicate runs");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private SqliteToPostgresMigrator BuildMigrator()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _agentweaverDbPath,
                ["Database:MemoryPath"] = _memoryDbPath,
            })
            .Build();

        return new SqliteToPostgresMigrator(
            _pg.Factory,
            config,
            NullLogger<SqliteToPostgresMigrator>.Instance);
    }

    /// <summary>Seeds a minimal agentweaver.db schema + test data via raw ADO.NET.</summary>
    private static void SeedSqliteDb(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using var schema = conn.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS projects (
                project_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                origin_kind TEXT NOT NULL DEFAULT 'blank',
                source_repository TEXT,
                working_directory TEXT NOT NULL DEFAULT '',
                default_branch TEXT NOT NULL DEFAULT 'main',
                owner TEXT NOT NULL DEFAULT '',
                default_provider TEXT NOT NULL DEFAULT 'github_copilot',
                default_model_copilot TEXT,
                default_model_foundry TEXT,
                state TEXT NOT NULL DEFAULT 'active',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                max_ready_per_heartbeat INTEGER NOT NULL DEFAULT 3,
                pickup_autopilot INTEGER NOT NULL DEFAULT 1,
                pickup_auto_approve_tools INTEGER NOT NULL DEFAULT 0,
                default_workflow_id TEXT,
                active_review_policy_name TEXT,
                sandbox_profile TEXT,
                source_blueprint_id TEXT,
                source_blueprint_type TEXT,
                allowed_workflow_ids TEXT
            );

            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                repository_path TEXT NOT NULL,
                originating_branch TEXT NOT NULL,
                model_source TEXT NOT NULL DEFAULT 'github_copilot',
                task TEXT NOT NULL,
                submitting_user TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                result TEXT,
                worktree_path TEXT,
                worktree_branch TEXT,
                tree_hash TEXT,
                step_count INTEGER NOT NULL DEFAULT 0,
                diff TEXT,
                merge_conflicts TEXT,
                project_id TEXT,
                model_id TEXT,
                agent_name TEXT,
                agent_charter TEXT,
                reviewed_by TEXT,
                workflow_run_id TEXT,
                merged_commit_hash TEXT,
                parent_run_id TEXT,
                subtask_id TEXT,
                origin TEXT NOT NULL DEFAULT 'interactive',
                retried_from TEXT,
                review_ready_at TEXT,
                review_wait_ms INTEGER NOT NULL DEFAULT 0,
                archived_at TEXT
            );

            CREATE TABLE IF NOT EXISTS run_revisions (
                run_id TEXT NOT NULL,
                revision_number INTEGER NOT NULL,
                reviewer_user TEXT NOT NULL,
                created_at TEXT NOT NULL,
                raw_comment TEXT NOT NULL,
                sanitized_comment TEXT NOT NULL,
                previous_tree_hash TEXT NOT NULL,
                PRIMARY KEY (run_id, revision_number)
            );

            CREATE TABLE IF NOT EXISTS workflow_runs (
                workflow_run_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                task TEXT NOT NULL,
                submitting_user TEXT NOT NULL,
                started_at TEXT NOT NULL,
                orchestration_worktree_path TEXT
            );

            CREATE TABLE IF NOT EXISTS backlog_tasks (
                task_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT,
                state TEXT NOT NULL DEFAULT 'backlog',
                order_key TEXT NOT NULL,
                captured_by TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                committed_at TEXT,
                claimed_at TEXT,
                run_id TEXT,
                workflow_override_id TEXT,
                archived_at TEXT,
                source_file_path TEXT
            );

            CREATE TABLE IF NOT EXISTS cast_proposals (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                owner TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                proposal_json TEXT NOT NULL
            );
            """;
        schema.ExecuteNonQuery();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var pid1 = "proj-" + Guid.NewGuid().ToString("N")[..8];
        var pid2 = "proj-" + Guid.NewGuid().ToString("N")[..8];
        var rid1 = "run-" + Guid.NewGuid().ToString("N")[..8];
        var rid2 = "run-" + Guid.NewGuid().ToString("N")[..8];
        var rid3 = "run-" + Guid.NewGuid().ToString("N")[..8];
        var wid1 = "wf-" + Guid.NewGuid().ToString("N")[..8];
        var tid1 = "task-" + Guid.NewGuid().ToString("N")[..8];
        var tid2 = "task-" + Guid.NewGuid().ToString("N")[..8];

        using var data = conn.CreateCommand();
        data.CommandText = $"""
            INSERT INTO projects VALUES ('{pid1}','Project A','blank',NULL,'/a','main','alice','github_copilot',NULL,NULL,'active','{now}','{now}',3,1,0,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO projects VALUES ('{pid2}','Project B','blank',NULL,'/b','main','bob','github_copilot',NULL,NULL,'active','{now}','{now}',3,1,0,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO runs VALUES ('{rid1}','/repo','main','github_copilot','task1','alice','completed','{now}','{now}','ok',NULL,NULL,NULL,0,NULL,NULL,'{pid1}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'interactive',NULL,NULL,0,NULL);
            INSERT INTO runs VALUES ('{rid2}','/repo','main','github_copilot','task2','bob','in_progress','{now}',NULL,NULL,NULL,NULL,NULL,0,NULL,NULL,'{pid1}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'interactive',NULL,NULL,0,NULL);
            INSERT INTO runs VALUES ('{rid3}','/repo','main','github_copilot','task3','alice','failed','{now}','{now}','err',NULL,NULL,NULL,0,NULL,NULL,'{pid2}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'interactive',NULL,NULL,0,NULL);
            INSERT INTO run_revisions VALUES ('{rid1}',1,'alice','{now}','raw1','sanitized1','hash0');
            INSERT INTO run_revisions VALUES ('{rid1}',2,'alice','{now}','raw2','sanitized2','hash1');
            INSERT INTO workflow_runs VALUES ('{wid1}','{pid1}','wf task','alice','{now}',NULL);
            INSERT INTO backlog_tasks VALUES ('{tid1}','{pid1}','Task A',NULL,'ready','key-a','alice','{now}','{now}',NULL,NULL,NULL,NULL,NULL);
            INSERT INTO backlog_tasks VALUES ('{tid2}','{pid1}','Task B',NULL,'backlog','key-b','alice','{now}',NULL,NULL,NULL,NULL,NULL,NULL);
            """;
        data.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
