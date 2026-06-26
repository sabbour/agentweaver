using Microsoft.Data.Sqlite;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Owns the SQLite database file and schema for the run event log, operational
/// records, and run records. Enables WAL mode for concurrent readers, creates
/// all tables on startup, and installs triggers that make the event log strictly
/// append-only (no UPDATE or DELETE).
/// </summary>
public sealed class SqliteDb
{
    private readonly string _connectionString;

    public SqliteDb(IConfiguration configuration)
    {
        var configuredPath = configuration["Database:Path"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppPaths.DataDirectory, "agentweaver.db")
            : configuredPath;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    /// <summary>Opens a new connection with WAL and a busy timeout applied.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Idempotent migrations for columns added after initial release.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN result TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN worktree_path TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN worktree_branch TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN tree_hash TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN step_count INTEGER NOT NULL DEFAULT 0;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN diff TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN merge_conflicts TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN project_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN model_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN state TEXT NOT NULL DEFAULT 'active';", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN default_branch TEXT NOT NULL DEFAULT 'main';", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_name TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_charter TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN reviewed_by TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN workflow_run_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN merged_commit_hash TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN parent_run_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN subtask_id TEXT;", ct);

        // Human-review dwell accounting (Feature: exclude human-review time from measured duration).
        // review_ready_at = timestamp the run MOST RECENTLY entered awaiting_review (NULL when not
        // currently parked in review). review_wait_ms = cumulative human-review dwell in ms, accrued
        // on every exit from awaiting_review so revise loops sum correctly.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN review_ready_at TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN review_wait_ms INTEGER NOT NULL DEFAULT 0;", ct);

        // Durable run-origin marker for backlog-pickup coordinator runs (Feature 009). Existing rows
        // default to 'interactive'; only the claim+reserve transaction writes 'backlog_pickup'.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN origin TEXT NOT NULL DEFAULT 'interactive';", ct);
        await TryAlterAsync(connection, "CREATE INDEX IF NOT EXISTS idx_runs_origin_status ON runs (origin, status);", ct);

        // Retry provenance (POST /api/runs/{id}/retry): the run_id of the failed run a fresh run was
        // retriggered from. Existing rows default to NULL (not produced by a retry).
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN retried_from TEXT;", ct);

        // Per-project backlog pickup configuration (Feature 009, FR-008a + unattended seeding).
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN max_ready_per_heartbeat INTEGER NOT NULL DEFAULT 3;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN pickup_autopilot INTEGER NOT NULL DEFAULT 1;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN pickup_auto_approve_tools INTEGER NOT NULL DEFAULT 0;", ct);

        // Per-project default workflow + per-task workflow override (Feature 010, FR-041/FR-042).
        // YAML/predefined workflows are loaded from .agentweaver/workflows/ and referenced here by id.
        // NULL means "use the built-in default" (project) / "use the project default" (task).
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN default_workflow_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN workflow_override_id TEXT;", ct);

        // Per-project active review policy (Feature 010, FR-027/033). Named review policies are loaded
        // from .agentweaver/review-policies/ and referenced here BY NAME. NULL means "use the built-in
        // default policy" (RAI + human-review, absorbed by the built-in workflow).
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN active_review_policy_name TEXT;", ct);

        // Per-project sandbox profile applied when a blueprint is selected at creation (Feature 012).
        // A named preset (e.g. 'default' | 'restricted'). NULL means "use the built-in default posture".
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN sandbox_profile TEXT;", ct);

        // Blueprint provenance — track which blueprint was applied at project creation (Feature 012).
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN source_blueprint_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN source_blueprint_type TEXT;", ct);

        // Per-project allowed workflow set declared by the applied blueprint's 'workflows' set
        // (Feature 015 US3). Stored as a JSON array of workflow ids. NULL/empty means "all catalog
        // workflows allowed" (backward compatible); a non-empty set restricts the workflow registry to
        // those ids (plus the built-in default).
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN allowed_workflow_ids TEXT;", ct);

        // Off-board archiving for runs/backlog tasks. NULL means active/non-archived, preserving all
        // existing rows. Archived Ready tasks are excluded from heartbeat pickup and board queries.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN archived_at TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN archived_at TEXT;", ct);

        await RecreateBacklogOrderKeyIndexAsync(connection, ct).ConfigureAwait(false);

        // Shared orchestration worktree for multi-agent coordinator runs (sandbox-cross-worktree-access).
        // One shared worktree per orchestration: all child runs share the coordinator's worktree path
        // as their sandbox root so Agent B can read files produced by Agent A.
        await TryAlterAsync(connection, "ALTER TABLE workflow_runs ADD COLUMN orchestration_worktree_path TEXT;", ct);

        // Cast proposals persistence (proposal store backed by SQLite so proposals survive restarts).
        await TryAlterAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS cast_proposals (
                id           TEXT PRIMARY KEY,
                project_id   TEXT NOT NULL,
                owner        TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                expires_at   TEXT NOT NULL,
                proposal_json TEXT NOT NULL
            );
            """, ct);
        await TryAlterAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_cast_proposals_project ON cast_proposals (project_id);", ct);

        // Source file path for spec-to-backlog decomposition (Feature 014). Records the workspace-
        // relative path from which a task was imported; used for idempotency by (project_id,
        // source_file_path, title). NULL for tasks captured manually or through other methods.
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN source_file_path TEXT;", ct);
    }

    private static async Task TryAlterAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
        {
            // Column already exists — ignore.
        }
    }

    private static async Task RecreateBacklogOrderKeyIndexAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText =
            """
            DROP INDEX IF EXISTS idx_backlog_tasks_orderkey_unique;
            CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_orderkey_unique
                ON backlog_tasks (project_id, state, order_key)
                WHERE state IN ('backlog','ready') AND archived_at IS NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS runs (
            run_id             TEXT PRIMARY KEY,
            repository_path    TEXT NOT NULL,
            originating_branch TEXT NOT NULL,
            model_source       TEXT NOT NULL,
            task               TEXT NOT NULL,
            submitting_user    TEXT NOT NULL,
            status             TEXT NOT NULL,
            started_at         TEXT NOT NULL,
            ended_at           TEXT,
            result             TEXT,
            worktree_path      TEXT,
            worktree_branch    TEXT,
            tree_hash          TEXT,
            step_count         INTEGER NOT NULL DEFAULT 0,
            diff               TEXT,
            review_ready_at    TEXT,
            review_wait_ms     INTEGER NOT NULL DEFAULT 0,
            archived_at        TEXT
        );

        CREATE TABLE IF NOT EXISTS run_revisions (
            run_id              TEXT NOT NULL,
            revision_number     INTEGER NOT NULL,
            reviewer_user       TEXT NOT NULL,
            created_at          TEXT NOT NULL,
            raw_comment         TEXT NOT NULL,
            sanitized_comment   TEXT NOT NULL,
            previous_tree_hash  TEXT NOT NULL,
            PRIMARY KEY (run_id, revision_number)
        );

        CREATE TRIGGER IF NOT EXISTS trg_run_revisions_no_update
            BEFORE UPDATE ON run_revisions
        BEGIN
            SELECT RAISE(ABORT, 'run_revisions is append-only: UPDATE is not permitted');
        END;

        CREATE TRIGGER IF NOT EXISTS trg_run_revisions_no_delete
            BEFORE DELETE ON run_revisions
        BEGIN
            SELECT RAISE(ABORT, 'run_revisions is append-only: DELETE is not permitted');
        END;

        CREATE TABLE IF NOT EXISTS projects (
            project_id              TEXT PRIMARY KEY,
            name                    TEXT NOT NULL,
            origin_kind             TEXT NOT NULL,
            source_repository       TEXT,
            working_directory       TEXT NOT NULL,
            default_branch          TEXT NOT NULL,
            owner                   TEXT NOT NULL,
            default_provider        TEXT NOT NULL,
            default_model_copilot   TEXT,
            default_model_foundry   TEXT,
            state                   TEXT NOT NULL DEFAULT 'active',
            created_at              TEXT NOT NULL,
            updated_at              TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_projects_state ON projects (state);

        CREATE TABLE IF NOT EXISTS workflow_runs (
            workflow_run_id  TEXT PRIMARY KEY,
            project_id       TEXT NOT NULL,
            task             TEXT NOT NULL,
            submitting_user  TEXT NOT NULL,
            started_at       TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_workflow_runs_project ON workflow_runs (project_id);

        CREATE TABLE IF NOT EXISTS backlog_tasks (
            task_id       TEXT PRIMARY KEY,
            project_id    TEXT NOT NULL,
            title         TEXT NOT NULL,
            description   TEXT,
            state         TEXT NOT NULL,            -- 'backlog' | 'ready' | 'claimed'
            order_key     TEXT NOT NULL,
            captured_by   TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            committed_at  TEXT,
            claimed_at    TEXT,
            run_id        TEXT,                      -- non-null iff state = 'claimed'
            archived_at   TEXT,
            FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE
        );

        -- Project scoping + ordered top-N reads.
        CREATE INDEX IF NOT EXISTS idx_backlog_tasks_project_state
            ON backlog_tasks (project_id, state, order_key);

        -- order_key uniqueness per (project_id, state) for the UNCLAIMED buckets only. Claimed rows
        -- are excluded so a stale claimed order_key never blocks a future insert.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_orderkey_unique
            ON backlog_tasks (project_id, state, order_key)
            WHERE state IN ('backlog','ready') AND archived_at IS NULL;

        -- One-task-to-at-most-one-run invariant at the storage layer.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_run
            ON backlog_tasks (run_id) WHERE run_id IS NOT NULL;

        """;
}
