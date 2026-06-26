using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteProjectStore : IProjectStore
{
    private readonly SqliteDb _db;
    private readonly ILogger<SqliteProjectStore>? _logger;

    public SqliteProjectStore(SqliteDb db, ILogger<SqliteProjectStore>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task InsertAsync(Project project, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (project_id, name, origin_kind, source_repository,
                                  working_directory, default_branch, owner,
                                  default_provider, default_model_copilot, default_model_foundry,
                                  state, created_at, updated_at,
                                  max_ready_per_heartbeat, pickup_autopilot, pickup_auto_approve_tools,
                                  default_workflow_id, active_review_policy_name, sandbox_profile,
                                  source_blueprint_id, source_blueprint_type, allowed_workflow_ids)
            VALUES ($projectId, $name, $originKind, $sourceRepository,
                    $workingDirectory, $defaultBranch, $owner,
                    $defaultProvider, $defaultModelCopilot, $defaultModelFoundry,
                    $state, $createdAt, $updatedAt,
                    $maxReadyPerHeartbeat, $pickupAutopilot, $pickupAutoApproveTools,
                    $defaultWorkflowId, $activeReviewPolicyName, $sandboxProfile,
                    $sourceBlueprintId, $sourceBlueprintType, $allowedWorkflowIds);
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
        command.Parameters.AddWithValue("$maxReadyPerHeartbeat", project.MaxReadyPerHeartbeat);
        command.Parameters.AddWithValue("$pickupAutopilot", project.PickupAutopilot ? 1 : 0);
        command.Parameters.AddWithValue("$pickupAutoApproveTools", project.PickupAutoApproveTools ? 1 : 0);
        command.Parameters.AddWithValue("$defaultWorkflowId", (object?)project.DefaultWorkflowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$activeReviewPolicyName", (object?)project.ActiveReviewPolicyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxProfile", (object?)project.SandboxProfile ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceBlueprintId", (object?)project.SourceBlueprintId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceBlueprintType", (object?)project.SourceBlueprintType ?? DBNull.Value);
        command.Parameters.AddWithValue("$allowedWorkflowIds", (object?)SerializeWorkflowIds(project.AllowedWorkflowIds) ?? DBNull.Value);
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

    public async Task UpdatePickupSettingsAsync(
        ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET max_ready_per_heartbeat = $maxReadyPerHeartbeat,
                   pickup_autopilot = $pickupAutopilot,
                   pickup_auto_approve_tools = $pickupAutoApproveTools,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$maxReadyPerHeartbeat", maxReadyPerHeartbeat);
        command.Parameters.AddWithValue("$pickupAutopilot", autopilot ? 1 : 0);
        command.Parameters.AddWithValue("$pickupAutoApproveTools", autoApproveTools ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET default_workflow_id = $defaultWorkflowId,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$defaultWorkflowId", (object?)workflowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET active_review_policy_name = $activeReviewPolicyName,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$activeReviewPolicyName", (object?)policyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET sandbox_profile = $sandboxProfile,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$sandboxProfile", (object?)sandboxProfile ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET source_blueprint_id = $sourceBlueprintId,
                   source_blueprint_type = $sourceBlueprintType,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$sourceBlueprintId", (object?)blueprintId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceBlueprintType", (object?)blueprintType ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
               SET allowed_workflow_ids = $allowedWorkflowIds,
                   updated_at = $updatedAt
             WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$allowedWorkflowIds", (object?)SerializeWorkflowIds(allowedWorkflowIds) ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Ts(updatedAt));
        command.Parameters.AddWithValue("$projectId", id.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Ordinals: 0=project_id 1=name 2=origin_kind 3=source_repository 4=working_directory
    //           5=default_branch 6=owner 7=default_provider 8=default_model_copilot
    //           9=default_model_foundry 10=state 11=created_at 12=updated_at
    //           13=max_ready_per_heartbeat 14=pickup_autopilot 15=pickup_auto_approve_tools
    //           16=default_workflow_id 17=active_review_policy_name 18=sandbox_profile
    //           19=source_blueprint_id 20=source_blueprint_type 21=allowed_workflow_ids
    private const string SelectSql =
        """
        SELECT project_id, name, origin_kind, source_repository, working_directory,
               default_branch, owner, default_provider, default_model_copilot,
               default_model_foundry, state, created_at, updated_at,
               max_ready_per_heartbeat, pickup_autopilot, pickup_auto_approve_tools,
               default_workflow_id, active_review_policy_name, sandbox_profile,
               source_blueprint_id, source_blueprint_type, allowed_workflow_ids
          FROM projects
        """;

    private Project Map(SqliteDataReader r)
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
            MaxReadyPerHeartbeat   = r.IsDBNull(13) ? 3 : r.GetInt32(13),
            PickupAutopilot        = r.IsDBNull(14) ? true : r.GetInt32(14) != 0,
            PickupAutoApproveTools = r.IsDBNull(15) ? false : r.GetInt32(15) != 0,
            DefaultWorkflowId      = r.IsDBNull(16) ? null : r.GetString(16),
            ActiveReviewPolicyName = r.IsDBNull(17) ? null : r.GetString(17),
            SandboxProfile         = r.IsDBNull(18) ? null : r.GetString(18),
            SourceBlueprintId      = r.IsDBNull(19) ? null : r.GetString(19),
            SourceBlueprintType    = r.IsDBNull(20) ? null : r.GetString(20),
            AllowedWorkflowIds     = r.IsDBNull(21) ? null : DeserializeWorkflowIds(r.GetString(21), r.GetString(0)),
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

    /// <summary>Serializes the allowed workflow id set to a JSON array string. Null/empty -> null
    /// (stored as SQL NULL = "all workflows allowed").</summary>
    private static string? SerializeWorkflowIds(IReadOnlyList<string>? ids) =>
        ids is null || ids.Count == 0 ? null : JsonSerializer.Serialize(ids);

    /// <summary>Deserializes a JSON array string of workflow ids. Blank payloads retain "all workflows allowed".</summary>
    private IReadOnlyList<string>? DeserializeWorkflowIds(string? json, string projectId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            return ids is { Count: > 0 } ? ids : null;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Invalid allowed_workflow_ids JSON for project {ProjectId}", projectId);
            throw new InvalidDataException($"Invalid allowed_workflow_ids JSON for project {projectId}.", ex);
        }
    }
}
