using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ISandboxPolicyStore"/>. Stores one row per repository
/// path. List-valued fields are persisted as JSON. When no row exists for a
/// repository the default policy is returned.
/// </summary>
public sealed class SqliteSandboxPolicyStore : ISandboxPolicyStore
{
    private readonly SqliteDb _db;

    public SqliteSandboxPolicyStore(SqliteDb db) => _db = db;

    public async Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT shell_enabled, allowed_repository_roots, destructive_command_patterns,
                   require_approval_for_all_shell, redact_pii, max_output_bytes
              FROM sandbox_policies
             WHERE repository_path = $repo;
            """;
        command.Parameters.AddWithValue("$repo", repositoryPath);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return SandboxPolicy.Default(repositoryPath);

        return new SandboxPolicy
        {
            RepositoryPath = repositoryPath,
            ShellEnabled = reader.GetInt32(0) != 0,
            AllowedRepositoryRoots = Deserialize(reader.GetString(1)),
            DestructiveCommandPatterns = Deserialize(reader.GetString(2)),
            RequireApprovalForAllShell = reader.GetInt32(3) != 0,
            RedactPii = reader.GetInt32(4) != 0,
            MaxOutputBytes = reader.GetInt32(5),
        };
    }

    public async Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO sandbox_policies
                (repository_path, shell_enabled, allowed_repository_roots,
                 destructive_command_patterns, require_approval_for_all_shell,
                 redact_pii, max_output_bytes, updated_at)
            VALUES ($repo, $shell, $roots, $patterns, $requireApproval,
                    $redactPii, $maxOutput, $updatedAt);
            """;
        command.Parameters.AddWithValue("$repo", policy.RepositoryPath);
        command.Parameters.AddWithValue("$shell", policy.ShellEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(policy.AllowedRepositoryRoots));
        command.Parameters.AddWithValue("$patterns", JsonSerializer.Serialize(policy.DestructiveCommandPatterns));
        command.Parameters.AddWithValue("$requireApproval", policy.RequireApprovalForAllShell ? 1 : 0);
        command.Parameters.AddWithValue("$redactPii", policy.RedactPii ? 1 : 0);
        command.Parameters.AddWithValue("$maxOutput", policy.MaxOutputBytes);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
