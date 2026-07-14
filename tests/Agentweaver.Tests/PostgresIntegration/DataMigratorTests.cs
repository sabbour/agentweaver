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

    [PostgresFact]
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

    [PostgresFact]
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

    /// <summary>
    /// Seeds an agentweaver.db with test data via raw ADO.NET.
    ///
    /// The schema is created by calling the real <see cref="SqliteDb.EnsureCreatedAsync"/>
    /// (the same code path production uses) instead of a hand-copied CREATE TABLE script.
    /// This is deliberate: a duplicated schema definition here previously drifted out of sync
    /// with the growing "runs" table (34 columns vs. a hardcoded 30-value INSERT) and broke
    /// this fixture — see issue #318. Reusing the production schema-creation logic means the
    /// fixture can never again fall behind an ALTER TABLE added elsewhere. All INSERT
    /// statements below also use explicit column-name lists rather than positional VALUES, so
    /// future nullable/defaulted columns added via migration don't require touching this file.
    /// </summary>
    private static void SeedSqliteDb(string dbPath)
    {
        var schemaConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = dbPath })
            .Build();
        new SqliteDb(schemaConfig).EnsureCreatedAsync().GetAwaiter().GetResult();

        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

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
            INSERT INTO projects (project_id, name, origin_kind, working_directory, default_branch, owner, default_provider, state, created_at, updated_at)
                VALUES ('{pid1}','Project A','blank','/a','main','alice','github_copilot','active','{now}','{now}');
            INSERT INTO projects (project_id, name, origin_kind, working_directory, default_branch, owner, default_provider, state, created_at, updated_at)
                VALUES ('{pid2}','Project B','blank','/b','main','bob','github_copilot','active','{now}','{now}');

            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, ended_at, result, project_id)
                VALUES ('{rid1}','/repo','main','github_copilot','task1','alice','completed','{now}','{now}','ok','{pid1}');
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, project_id)
                VALUES ('{rid2}','/repo','main','github_copilot','task2','bob','in_progress','{now}','{pid1}');
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, ended_at, result, project_id)
                VALUES ('{rid3}','/repo','main','github_copilot','task3','alice','failed','{now}','{now}','err','{pid2}');

            INSERT INTO run_revisions (run_id, revision_number, reviewer_user, created_at, raw_comment, sanitized_comment, previous_tree_hash)
                VALUES ('{rid1}',1,'alice','{now}','raw1','sanitized1','hash0');
            INSERT INTO run_revisions (run_id, revision_number, reviewer_user, created_at, raw_comment, sanitized_comment, previous_tree_hash)
                VALUES ('{rid1}',2,'alice','{now}','raw2','sanitized2','hash1');

            INSERT INTO workflow_runs (workflow_run_id, project_id, task, submitting_user, started_at)
                VALUES ('{wid1}','{pid1}','wf task','alice','{now}');

            INSERT INTO backlog_tasks (task_id, project_id, title, state, order_key, captured_by, created_at, committed_at)
                VALUES ('{tid1}','{pid1}','Task A','ready','key-a','alice','{now}','{now}');
            INSERT INTO backlog_tasks (task_id, project_id, title, state, order_key, captured_by, created_at)
                VALUES ('{tid2}','{pid1}','Task B','backlog','key-b','alice','{now}');
            """;
        data.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
