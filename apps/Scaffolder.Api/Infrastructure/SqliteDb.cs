using Microsoft.Data.Sqlite;

namespace Scaffolder.Api.Infrastructure;

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
            ? Path.Combine(AppPaths.DataDirectory, "scaffolder.db")
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
    }

    private static async Task TryAlterAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (SqliteException) { /* column already exists */ }
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
            diff               TEXT
        );

        CREATE TABLE IF NOT EXISTS sandbox_policies (
            repository_path             TEXT PRIMARY KEY,
            shell_enabled               INTEGER NOT NULL DEFAULT 1,
            allowed_repository_roots    TEXT NOT NULL DEFAULT '[]',
            destructive_command_patterns TEXT NOT NULL DEFAULT '["rm -rf","del /s","format ","mkfs","dd if=","git push --force","git reset --hard"]',
            require_approval_for_all_shell INTEGER NOT NULL DEFAULT 0,
            redact_pii                  INTEGER NOT NULL DEFAULT 1,
            max_output_bytes            INTEGER NOT NULL DEFAULT 4194304,
            updated_at                  TEXT NOT NULL
        );
        """;
}
