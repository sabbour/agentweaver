using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Tools;
using Agentweaver.Domain.BlueprintPackages;
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
    private readonly string _seededProjectId;
    private readonly string _seededPackageId;
    private readonly string _seededPackageVersion;

    public DataMigratorTests(PostgresFixture pg)
    {
        _pg = pg;
        _tempDir = Path.Combine(Path.GetTempPath(), "aw-migtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _agentweaverDbPath = Path.Combine(_tempDir, "agentweaver.db");
        _memoryDbPath = Path.Combine(_tempDir, "memory.db");

        (_seededProjectId, _seededPackageId, _seededPackageVersion) = SeedSqliteDb(_agentweaverDbPath);
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
        var seededProject = await db.Projects.SingleAsync(project => project.ProjectId == _seededProjectId);
        var recoveredRun = await db.Runs.SingleAsync(run => run.Task == "task2");
        var packageVersions = await db.BlueprintPackageVersions
            .Where(version => version.PackageId == _seededPackageId)
            .ToListAsync();
        var packagePayloads = await db.BlueprintPackagePayloads
            .CountAsync(payload => payload.PackageId == _seededPackageId);
        var packageAcquisitions = await db.BlueprintPackageAcquisitions
            .CountAsync(acquisition => acquisition.PackageId == _seededPackageId);

        // We seeded 2 projects, 3 runs, 2 revisions, 1 workflow run, 2 backlog tasks
        projects.Should().BeGreaterThanOrEqualTo(2, "all seeded projects must be migrated");
        runs.Should().BeGreaterThanOrEqualTo(3, "all seeded runs must be migrated");
        revisions.Should().BeGreaterThanOrEqualTo(2, "all seeded run_revisions must be migrated");
        workflowRuns.Should().BeGreaterThanOrEqualTo(1, "all seeded workflow_runs must be migrated");
        backlogTasks.Should().BeGreaterThanOrEqualTo(2, "all seeded backlog_tasks must be migrated");
        seededProject.TeamRevision.Should().Be(7, "team mutation concurrency state must survive provider migration");
        seededProject.WebhookSecret.Should().Be("github-webhook:seed",
            "the per-project webhook secret-store reference must survive provider migration");
        recoveredRun.ApprovalGeneration.Should().Be(2,
            "a recovered run must retain its lifecycle generation so pre-recovery approval policies cannot match");
        packageVersions.Should().ContainSingle();
        packageVersions.Single().CanonicalVersionKey.Should().Be(
            BlueprintPackageLibraryLimits.CanonicalVersionKey(packageVersions.Single().CanonicalVersion));
        packagePayloads.Should().Be(1);
        packageAcquisitions.Should().Be(1);
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
        var packageVersionsAfterFirst = await db1.BlueprintPackageVersions.CountAsync(
            version => version.PackageId == _seededPackageId);

        await migrator.RunAsync(); // second run — must be idempotent

        await using var db2 = await _pg.CreateDbContextAsync();
        var projectsAfterSecond = await db2.Projects.CountAsync();
        var runsAfterSecond = await db2.Runs.CountAsync();
        var packageVersionsAfterSecond = await db2.BlueprintPackageVersions.CountAsync(
            version => version.PackageId == _seededPackageId);

        projectsAfterSecond.Should().Be(projectsAfterFirst,
            "second migration run must not insert duplicate projects");
        runsAfterSecond.Should().Be(runsAfterFirst,
            "second migration run must not insert duplicate runs");
        packageVersionsAfterSecond.Should().Be(
            packageVersionsAfterFirst,
            "second migration run must not insert duplicate package versions");
    }

    [PostgresFact]
    public async Task Migrator_ConflictingPackageVersion_AbortsWithoutMergingChildren()
    {
        await using (var db = await _pg.CreateDbContextAsync())
        {
            db.BlueprintPackageLibrary.Add(new BlueprintPackageLibraryRecord
            {
                OwnerId = "alice",
                PackageId = _seededPackageId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.BlueprintPackageVersions.Add(new BlueprintPackageVersionRecord
            {
                OwnerId = "alice",
                PackageId = _seededPackageId,
                CanonicalVersionKey = BlueprintPackageLibraryLimits.CanonicalVersionKey(_seededPackageVersion),
                CanonicalVersion = _seededPackageVersion,
                ContentDigest = new string('d', 64),
                PayloadSetDigest = new string('e', 64),
                RawManifestSha256 = new string('f', 64),
                RawManifest = "{}"u8.ToArray(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var migrate = () => BuildMigrator().RunAsync();

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*immutable Blueprint package version conflicts*");
        await using var verify = await _pg.CreateDbContextAsync();
        (await verify.BlueprintPackagePayloads.CountAsync(
            payload => payload.PackageId == _seededPackageId)).Should().Be(0);
        (await verify.BlueprintPackageAcquisitions.CountAsync(
            acquisition => acquisition.PackageId == _seededPackageId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Migrator_TwoAppFailure_RollsBackEveryTwoAppRecord()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={_memoryDbPath}")
            .Options;
        await using (var source = new MemoryDbContext(options))
        {
            await source.Database.EnsureCreatedAsync();
            source.Projects.Add(new ProjectRecord
            {
                ProjectId = _seededProjectId,
                Name = "Source project",
                OriginKind = "blank",
                WorkingDirectory = "source-worktree",
                Owner = "owner",
                DefaultProvider = "github_copilot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            source.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
            {
                Id = "binding-" + Guid.NewGuid().ToString("N"),
                ProjectId = _seededProjectId,
                EntraObjectId = "entra",
                CredentialReference = "kv-copilot",
                CredentialVersion = "version-1",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await source.SaveChangesAsync();
        }

        var migrator = BuildMigrator(_ => Task.FromException(
            new InvalidOperationException("Injected two-App transfer failure.")));
        Func<Task> migrate = () => migrator.RunAsync();
        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected two-App transfer failure.");

        await using var verify = await _pg.CreateDbContextAsync();
        (await verify.ProjectCopilotBindings.CountAsync(x => x.ProjectId == _seededProjectId)).Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private SqliteToPostgresMigrator BuildMigrator(Func<CancellationToken, Task>? beforeTwoAppCommit = null)
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
            NullLogger<SqliteToPostgresMigrator>.Instance,
            beforeTwoAppCommit);
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
    private static (string ProjectId, string PackageId, string PackageVersion) SeedSqliteDb(string dbPath)
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
        var packageId = "package-" + Guid.NewGuid().ToString("N")[..8];
        var packageVersion = new string('9', 300) + ".0.0";

        using var data = conn.CreateCommand();
        data.CommandText = $"""
            INSERT INTO projects (project_id, name, origin_kind, working_directory, default_branch, owner, default_provider, state, created_at, updated_at, team_revision, webhook_secret)
                VALUES ('{pid1}','Project A','blank','/a','main','alice','github_copilot','active','{now}','{now}',7,'github-webhook:seed');
            INSERT INTO projects (project_id, name, origin_kind, working_directory, default_branch, owner, default_provider, state, created_at, updated_at)
                VALUES ('{pid2}','Project B','blank','/b','main','bob','github_copilot','active','{now}','{now}');

            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, ended_at, result, project_id)
                VALUES ('{rid1}','/repo','main','github_copilot','task1','alice','completed','{now}','{now}','ok','{pid1}');
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, project_id, approval_generation)
                VALUES ('{rid2}','/repo','main','github_copilot','task2','bob','in_progress','{now}','{pid1}',2);
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

            INSERT INTO blueprint_package_library (owner_id, package_id, created_at)
                VALUES ('alice','{packageId}','{now}');
            INSERT INTO blueprint_package_versions (
                owner_id, package_id, canonical_version, content_digest, payload_set_digest,
                raw_manifest_sha256, raw_manifest, created_at)
                VALUES (
                    'alice','{packageId}','{packageVersion}',
                    '{new string('a', 64)}','{new string('b', 64)}','{new string('c', 64)}',
                    X'7B7D','{now}');
            INSERT INTO blueprint_package_payloads (owner_id, package_id, canonical_version, path, bytes)
                VALUES ('alice','{packageId}','{packageVersion}','definitions/blueprints/example.json',X'7B7D');
            INSERT INTO blueprint_package_acquisitions (
                owner_id, package_id, canonical_version, ordinal, source, producer,
                repository, revision, acquired_at, requested_ref)
                VALUES (
                    'alice','{packageId}','{packageVersion}',0,'github','migration-test',
                    'https://github.com/octo/migration-test',
                    '1111111111111111111111111111111111111111','{now}','feature/migrate');
            """;
        data.ExecuteNonQuery();
        return (pid1, packageId, packageVersion);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
