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
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN diff TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN merge_conflicts TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN project_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN model_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN state TEXT NOT NULL DEFAULT 'active';", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN default_branch TEXT NOT NULL DEFAULT 'main';", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN team_revision INTEGER NOT NULL DEFAULT 0;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_name TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_charter TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN reviewed_by TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN workflow_run_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN merged_commit_hash TEXT;", ct);
        // Coordinator workflow-selection reasoning (#167): short human-readable explanation of why the
        // coordinator selected the workflow it planned this run against. NULL for runs with no captured reason.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN workflow_selection_reason TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN parent_run_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN subtask_id TEXT;", ct);

        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN review_ready_at TEXT;", ct);

        // Durable run-origin marker for backlog-pickup coordinator runs (Feature 009). Existing rows
        // default to 'interactive'; only the claim+reserve transaction writes 'backlog_pickup'.
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN origin TEXT NOT NULL DEFAULT 'interactive';", ct);
        await TryAlterAsync(connection, "CREATE INDEX IF NOT EXISTS idx_runs_origin_status ON runs (origin, status);", ct);

        // Retry provenance (POST /api/runs/{id}/retry): the run_id of the failed run a fresh run was
        // retriggered from. Existing rows default to NULL (not produced by a retry).
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN retried_from TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN sandbox_backend TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN sandbox_claim_name TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN sandbox_pod_name TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN sandbox_namespace TEXT;", ct);

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

        // Project-scoped model overrides for server-authored generation flows. NULL means "use global
        // Generation fallback"; these do not alter runtime agent/run model defaults.
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN blueprint_generation_model TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN workflow_generation_model TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN outcome_spec_generation_model TEXT;", ct);
        // The secret itself lives in ISecretStore (Key Vault in production); this nullable value is
        // its per-project lookup key. Existing projects have no configured webhook until rotated.
        await TryAlterAsync(connection, "ALTER TABLE projects ADD COLUMN webhook_secret TEXT;", ct);

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
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN parent_prd_run_id TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN promotion_key TEXT;", ct);
        await TryAlterAsync(connection, "ALTER TABLE backlog_tasks ADD COLUMN promotion_reason TEXT;", ct);
        await TryAlterAsync(connection,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_parent_promotion_key
                ON backlog_tasks (parent_prd_run_id, promotion_key)
                WHERE parent_prd_run_id IS NOT NULL AND promotion_key IS NOT NULL;
            """, ct);
        await TryAlterAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS backlog_task_dependencies (
                project_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                depends_on_task_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (task_id, depends_on_task_id),
                FOREIGN KEY (task_id) REFERENCES backlog_tasks (task_id) ON DELETE CASCADE,
                FOREIGN KEY (depends_on_task_id) REFERENCES backlog_tasks (task_id) ON DELETE RESTRICT,
                CHECK (task_id <> depends_on_task_id)
            );
            """, ct);
        await TryAlterAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_backlog_task_dependencies_project_task ON backlog_task_dependencies (project_id, task_id);", ct);
        await TryAlterAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_backlog_task_dependencies_prerequisite ON backlog_task_dependencies (depends_on_task_id);", ct);

        // Per-project skill catalog + skill→agent assignments (issues #51/#56). Skills are
        // standards-compatible SKILL.md modules acquired via repo import, file upload, or
        // connected-repo sync, then assigned to specific agents for progressive-disclosure prompting.
        await TryAlterAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS skills (
                skill_id          TEXT PRIMARY KEY,
                project_id        TEXT NOT NULL,
                name              TEXT NOT NULL,
                description       TEXT NOT NULL,
                instructions      TEXT NOT NULL,
                resources         TEXT,
                provenance        TEXT NOT NULL,
                source_repository TEXT,
                source_location   TEXT,
                marketplace_name  TEXT,
                content_hash      TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'active',
                created_at        TEXT NOT NULL,
                updated_at        TEXT NOT NULL,
                UNIQUE (project_id, skill_id),
                FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE
            );
            """, ct);
        await TryAlterAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS skill_assignments (
                project_id  TEXT NOT NULL,
                skill_id    TEXT NOT NULL,
                agent_name  TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                PRIMARY KEY (project_id, skill_id, agent_name),
                FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE,
                FOREIGN KEY (project_id, skill_id)
                    REFERENCES skills (project_id, skill_id) ON DELETE CASCADE
            );
            """, ct);
        await TryAlterAsync(connection, "ALTER TABLE skills ADD COLUMN marketplace_name TEXT;", ct);
        await EnsureSkillOwnershipConstraintsAsync(connection, ct).ConfigureAwait(false);
        await TryAlterAsync(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_skills_project_name ON skills (project_id, name COLLATE NOCASE);", ct);
        await TryAlterAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_skill_assignments_agent ON skill_assignments (project_id, agent_name);", ct);

        // Owner-private immutable Blueprint package library. Package payloads, raw manifests and
        // descriptive acquisition records share the same owner/package/version key; stores write
        // these rows in one transaction and never update a version after insertion.
        await TryAlterAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS blueprint_package_library (
                owner_id TEXT NOT NULL, package_id TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY (owner_id, package_id)
            );
            CREATE TABLE IF NOT EXISTS blueprint_package_versions (
                owner_id TEXT NOT NULL, package_id TEXT NOT NULL, canonical_version TEXT NOT NULL,
                content_digest TEXT NOT NULL, payload_set_digest TEXT NOT NULL,
                raw_manifest_sha256 TEXT NOT NULL, container_sha256 TEXT, raw_manifest BLOB NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (owner_id, package_id, canonical_version),
                FOREIGN KEY (owner_id, package_id) REFERENCES blueprint_package_library(owner_id, package_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS blueprint_package_payloads (
                owner_id TEXT NOT NULL, package_id TEXT NOT NULL, canonical_version TEXT NOT NULL,
                path TEXT NOT NULL, bytes BLOB NOT NULL,
                PRIMARY KEY (owner_id, package_id, canonical_version, path),
                FOREIGN KEY (owner_id, package_id, canonical_version) REFERENCES blueprint_package_versions(owner_id, package_id, canonical_version) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS blueprint_package_acquisitions (
                owner_id TEXT NOT NULL, package_id TEXT NOT NULL, canonical_version TEXT NOT NULL,
                ordinal INTEGER NOT NULL, source TEXT NOT NULL, producer TEXT, repository TEXT,
                revision TEXT, acquired_at TEXT, requested_ref TEXT,
                PRIMARY KEY (owner_id, package_id, canonical_version, ordinal),
                FOREIGN KEY (owner_id, package_id, canonical_version) REFERENCES blueprint_package_versions(owner_id, package_id, canonical_version) ON DELETE CASCADE
            );
            CREATE TRIGGER IF NOT EXISTS trg_blueprint_package_versions_no_update
            BEFORE UPDATE ON blueprint_package_versions
            BEGIN SELECT RAISE(ABORT, 'blueprint package versions are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS trg_blueprint_package_payloads_no_update
            BEFORE UPDATE ON blueprint_package_payloads
            BEGIN SELECT RAISE(ABORT, 'blueprint package payloads are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS trg_blueprint_package_acquisitions_no_update
            BEFORE UPDATE ON blueprint_package_acquisitions
            BEGIN SELECT RAISE(ABORT, 'blueprint package acquisitions are immutable'); END;
            """, ct);
        await TryAlterAsync(connection,
            "ALTER TABLE blueprint_package_acquisitions ADD COLUMN requested_ref TEXT;", ct);

        await MigrateLegacyMetricsSchemaAsync(connection, ct).ConfigureAwait(false);
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

    private static async Task EnsureSkillOwnershipConstraintsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        if (await HasCascadeForeignKeyAsync(
                connection, "skills", "projects", ["project_id"], ct).ConfigureAwait(false)
            && await HasCascadeForeignKeyAsync(
                connection, "skill_assignments", "projects", ["project_id"], ct).ConfigureAwait(false)
            && await HasCascadeForeignKeyAsync(
                connection, "skill_assignments", "skills", ["project_id", "skill_id"], ct).ConfigureAwait(false))
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM skill_assignments
             WHERE NOT EXISTS (
                       SELECT 1
                         FROM projects AS p
                        WHERE p.project_id = skill_assignments.project_id)
                OR NOT EXISTS (
                       SELECT 1
                         FROM skills AS s
                        WHERE s.project_id = skill_assignments.project_id
                          AND s.skill_id = skill_assignments.skill_id);

            DELETE FROM skills
             WHERE NOT EXISTS (
                       SELECT 1
                         FROM projects AS p
                        WHERE p.project_id = skills.project_id);

            CREATE TABLE skills__ownership_migration (
                skill_id          TEXT PRIMARY KEY,
                project_id        TEXT NOT NULL,
                name              TEXT NOT NULL,
                description       TEXT NOT NULL,
                instructions      TEXT NOT NULL,
                resources         TEXT,
                provenance        TEXT NOT NULL,
                source_repository TEXT,
                source_location   TEXT,
                marketplace_name  TEXT,
                content_hash      TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'active',
                created_at        TEXT NOT NULL,
                updated_at        TEXT NOT NULL,
                UNIQUE (project_id, skill_id),
                FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE
            );

            INSERT INTO skills__ownership_migration (
                skill_id, project_id, name, description, instructions, resources,
                provenance, source_repository, source_location, content_hash, status,
                created_at, updated_at)
            SELECT skill_id, project_id, name, description, instructions, resources,
                   provenance, source_repository, source_location, content_hash, status,
                   created_at, updated_at
              FROM skills;

            CREATE TABLE skill_assignments__ownership_migration (
                project_id  TEXT NOT NULL,
                skill_id    TEXT NOT NULL,
                agent_name  TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                PRIMARY KEY (project_id, skill_id, agent_name),
                FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE,
                FOREIGN KEY (project_id, skill_id)
                    REFERENCES skills__ownership_migration (project_id, skill_id) ON DELETE CASCADE
            );

            INSERT INTO skill_assignments__ownership_migration (
                project_id, skill_id, agent_name, created_at)
            SELECT project_id, skill_id, agent_name, created_at
              FROM skill_assignments;

            DROP TABLE skill_assignments;
            DROP TABLE skills;
            ALTER TABLE skills__ownership_migration RENAME TO skills;
            ALTER TABLE skill_assignments__ownership_migration RENAME TO skill_assignments;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasCascadeForeignKeyAsync(
        SqliteConnection connection,
        string table,
        string principalTable,
        IReadOnlyList<string> columns,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}');";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var candidates = new Dictionary<long, List<(long Sequence, string Column)>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(2), principalTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(6), "CASCADE", StringComparison.OrdinalIgnoreCase))
            {
                var id = reader.GetInt64(0);
                if (!candidates.TryGetValue(id, out var candidate))
                {
                    candidate = [];
                    candidates.Add(id, candidate);
                }
                candidate.Add((reader.GetInt64(1), reader.GetString(3)));
            }
        }
        return candidates.Values.Any(candidate =>
            candidate.OrderBy(part => part.Sequence)
                .Select(part => part.Column)
                .SequenceEqual(columns, StringComparer.OrdinalIgnoreCase));
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

    private static async Task MigrateLegacyMetricsSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await TryDropTableAsync(connection, "token_usage_records", ct).ConfigureAwait(false);

        var columns = await GetRunColumnsAsync(connection, ct).ConfigureAwait(false);
        if (!columns.Contains("step_count") && !columns.Contains("review_wait_ms"))
            return;

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText =
            """
            CREATE TABLE runs__new (
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
                diff               TEXT,
                review_ready_at    TEXT,
                merge_conflicts    TEXT,
                project_id         TEXT,
                model_id           TEXT,
                agent_name         TEXT,
                agent_charter      TEXT,
                reviewed_by        TEXT,
                workflow_run_id    TEXT,
                merged_commit_hash TEXT,
                parent_run_id      TEXT,
                subtask_id         TEXT,
                origin             TEXT NOT NULL DEFAULT 'interactive',
                retried_from       TEXT,
                archived_at        TEXT,
                sandbox_backend    TEXT,
                sandbox_claim_name TEXT,
                sandbox_pod_name   TEXT,
                sandbox_namespace  TEXT,
                workflow_selection_reason TEXT
            );

            INSERT INTO runs__new (
                run_id, repository_path, originating_branch, model_source, task,
                submitting_user, status, started_at, ended_at, result,
                worktree_path, worktree_branch, tree_hash, diff, review_ready_at,
                merge_conflicts, project_id, model_id, agent_name, agent_charter,
                reviewed_by, workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
                origin, retried_from, archived_at, sandbox_backend, sandbox_claim_name,
                sandbox_pod_name, sandbox_namespace, workflow_selection_reason
            )
            SELECT
                run_id, repository_path, originating_branch, model_source, task,
                submitting_user, status, started_at, ended_at, result,
                worktree_path, worktree_branch, tree_hash, diff, review_ready_at,
                merge_conflicts, project_id, model_id, agent_name, agent_charter,
                reviewed_by, workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
                COALESCE(origin, 'interactive'), retried_from, archived_at,
                sandbox_backend, sandbox_claim_name, sandbox_pod_name, sandbox_namespace,
                workflow_selection_reason
            FROM runs;

            DROP TABLE runs;
            ALTER TABLE runs__new RENAME TO runs;
            CREATE INDEX IF NOT EXISTS idx_runs_origin_status ON runs (origin, status);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> GetRunColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(runs);";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task TryDropTableAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {tableName};";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
            diff               TEXT,
            review_ready_at    TEXT,
            archived_at        TEXT,
            sandbox_backend    TEXT,
            sandbox_claim_name TEXT,
            sandbox_pod_name   TEXT,
            sandbox_namespace  TEXT
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
            updated_at              TEXT NOT NULL,
            webhook_secret          TEXT,
            team_revision           INTEGER NOT NULL DEFAULT 0
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
            source_file_path TEXT,
            parent_prd_run_id TEXT,
            promotion_key TEXT,
            promotion_reason TEXT,
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

        CREATE TABLE IF NOT EXISTS backlog_task_dependencies (
            project_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            depends_on_task_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (task_id, depends_on_task_id),
            FOREIGN KEY (task_id) REFERENCES backlog_tasks (task_id) ON DELETE CASCADE,
            FOREIGN KEY (depends_on_task_id) REFERENCES backlog_tasks (task_id) ON DELETE RESTRICT,
            CHECK (task_id <> depends_on_task_id)
        );

        CREATE INDEX IF NOT EXISTS idx_backlog_task_dependencies_project_task
            ON backlog_task_dependencies (project_id, task_id);

        CREATE INDEX IF NOT EXISTS idx_backlog_task_dependencies_prerequisite
            ON backlog_task_dependencies (depends_on_task_id);

        """;
}
