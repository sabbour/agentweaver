using System.Globalization;
using Microsoft.Data.Sqlite;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

public sealed class SqliteProjectStore : IProjectStore
{
    private readonly SqliteDb _db;

    public SqliteProjectStore(SqliteDb db) => _db = db;

    public async Task InsertAsync(Project project, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (project_id, name, origin_kind, source_repository,
                                  working_directory, default_branch, owner,
                                  default_provider, default_model_copilot, default_model_foundry,
                                  state, created_at, updated_at)
            VALUES ($projectId, $name, $originKind, $sourceRepository,
                    $workingDirectory, $defaultBranch, $owner,
                    $defaultProvider, $defaultModelCopilot, $defaultModelFoundry,
                    $state, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$projectId", project.Id.ToString());
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$originKind", project.Origin.ToApiString());
        command.Parameters.AddWithValue("$sourceRepository", (object?)project.Origin.SourceRepository ?? DBNull.Value);
        command.Parameters.AddWithValue("$workingDirectory", project.WorkingDirectory);
        command.Parameters.AddWithValue("$defaultBranch", project.DefaultBranch);
        command.Parameters.AddWithValue("$owner", project.Owner);
        command.Parameters.AddWithValue("$defaultProvider", project.ProviderSettings.DefaultProvider.ToApiString());
        command.Parameters.AddWithValue("$defaultModelCopilot", (object?)project.ProviderSettings.GitHubCopilotModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$defaultModelFoundry", (object?)project.ProviderSettings.MicrosoftFoundryModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", StateToString(project.State));
        command.Parameters.AddWithValue("$createdAt", Ts(project.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Ts(project.UpdatedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY created_at DESC;";
        var results = new List<Project>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE projects SET name = $name, updated_at = $updatedAt WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET default_provider = $defaultProvider,
                   default_model_copilot = $defaultModelCopilot,
                   default_model_foundry = $defaultModelFoundry,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$defaultProvider", settings.DefaultProvider.ToApiString());
        command.Parameters.AddWithValue("$defaultModelCopilot", (object?)settings.GitHubCopilotModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$defaultModelFoundry", (object?)settings.MicrosoftFoundryModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateWorkingDirectoryAsync(ProjectId id, string workingDirectory, string defaultBranch, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET working_directory = $workingDirectory,
                   default_branch = $defaultBranch,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$workingDirectory", workingDirectory);
        command.Parameters.AddWithValue("$defaultBranch", defaultBranch);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE projects SET state = 'deleting' WHERE project_id = $projectId AND state = 'active';";
        command.Parameters.AddWithValue("$projectId", id.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task DeleteAsync(ProjectId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Ordinals: 0=project_id 1=name 2=origin_kind 3=source_repository 4=working_directory
    //           5=default_branch 6=owner 7=default_provider 8=default_model_copilot
    //           9=default_model_foundry 10=state 11=created_at 12=updated_at
    private const string SelectSql =
        """
        SELECT project_id, name, origin_kind, source_repository, working_directory,
               default_branch, owner, default_provider, default_model_copilot,
               default_model_foundry, state, created_at, updated_at
          FROM projects
        """;

    private static Project Map(SqliteDataReader r)
    {
        var originKind = ProjectOrigin.KindFromApiString(r.GetString(2));
        var origin = originKind == ProjectOriginKind.FromGitHub
            ? ProjectOrigin.FromGitHub(r.GetString(3))
            : ProjectOrigin.Blank();

        return new Project
        {
            Id               = ProjectId.Parse(r.GetString(0)),
            Name             = r.GetString(1),
            Origin           = origin,
            WorkingDirectory = r.GetString(4),
            DefaultBranch    = r.GetString(5),
            Owner            = r.GetString(6),
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider      = ModelSourceExtensions.FromApiString(r.GetString(7)),
                GitHubCopilotModel   = r.IsDBNull(8)  ? null : r.GetString(8),
                MicrosoftFoundryModel = r.IsDBNull(9) ? null : r.GetString(9),
            },
            State     = StateFromString(r.GetString(10)),
            CreatedAt = DateTimeOffset.Parse(r.GetString(11), null, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(r.GetString(12), null, DateTimeStyles.RoundtripKind),
        };
    }

    private static string StateToString(ProjectState state) => state switch
    {
        ProjectState.Active   => "active",
        ProjectState.Deleting => "deleting",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static ProjectState StateFromString(string s) => s switch
    {
        "active"   => ProjectState.Active,
        "deleting" => ProjectState.Deleting,
        _ => throw new ArgumentException($"Unknown project state: {s}")
    };

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
}
