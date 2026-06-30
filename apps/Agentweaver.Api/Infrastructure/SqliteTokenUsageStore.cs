using System.Globalization;
using Microsoft.Data.Sqlite;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteTokenUsageStore : ITokenUsageStore
{
    private readonly SqliteDb _db;

    public SqliteTokenUsageStore(SqliteDb db)
    {
        _db = db;
    }

    public async Task RecordAsync(TokenUsageRecord record, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO token_usage_records
                (id, run_id, workflow_run_id, project_id, model_id,
                 input_tokens, output_tokens, total_nano_aiu, recorded_at)
            VALUES
                ($id, $runId, $workflowRunId, $projectId, $modelId,
                 $inputTokens, $outputTokens, $totalNanoAiu, $recordedAt);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$runId", record.RunId);
        command.Parameters.AddWithValue("$workflowRunId", (object?)record.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)record.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", record.ModelId);
        command.Parameters.AddWithValue("$inputTokens", record.InputTokens);
        command.Parameters.AddWithValue("$outputTokens", record.OutputTokens);
        command.Parameters.AddWithValue("$totalNanoAiu", record.TotalNanoAiu);
        command.Parameters.AddWithValue("$recordedAt", record.RecordedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<TokenUsageSummary> GetRunUsageAsync(string runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT model_id,
                   SUM(input_tokens)   AS input_tokens,
                   SUM(output_tokens)  AS output_tokens,
                   SUM(total_nano_aiu) AS total_nano_aiu
              FROM token_usage_records
             WHERE run_id = $runId
             GROUP BY model_id;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        return await ReadSummaryAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<TokenUsageSummary> GetWorkflowRunUsageAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT model_id,
                   SUM(input_tokens)   AS input_tokens,
                   SUM(output_tokens)  AS output_tokens,
                   SUM(total_nano_aiu) AS total_nano_aiu
              FROM token_usage_records
             WHERE workflow_run_id = $workflowRunId
             GROUP BY model_id;
            """;
        command.Parameters.AddWithValue("$workflowRunId", workflowRunId);
        return await ReadSummaryAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<TokenUsageSummary> GetProjectUsageAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT model_id,
                   SUM(input_tokens)   AS input_tokens,
                   SUM(output_tokens)  AS output_tokens,
                   SUM(total_nano_aiu) AS total_nano_aiu
              FROM token_usage_records
             WHERE project_id = $projectId
               AND recorded_at >= $from
               AND recorded_at <= $to
             GROUP BY model_id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
        return await ReadSummaryAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TokenUsageByProject>> GetAppUsageAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.project_id,
                   COALESCE(p.name, t.project_id) AS project_name,
                   t.model_id,
                   SUM(t.input_tokens)   AS input_tokens,
                   SUM(t.output_tokens)  AS output_tokens,
                   SUM(t.total_nano_aiu) AS total_nano_aiu
              FROM token_usage_records t
              LEFT JOIN projects p ON p.project_id = t.project_id
             WHERE t.recorded_at >= $from
               AND t.recorded_at <= $to
               AND t.project_id IS NOT NULL
             GROUP BY t.project_id, t.model_id;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var byProject = new Dictionary<string, (string projectName, List<TokenUsageByModel> models)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var projectId = reader.GetString(0);
            var projectName = reader.GetString(1);
            var modelId = reader.GetString(2);
            var input = reader.GetInt64(3);
            var output = reader.GetInt64(4);
            var nanoAiu = reader.GetInt64(5);

            if (!byProject.TryGetValue(projectId, out var entry))
            {
                entry = (projectName, new List<TokenUsageByModel>());
                byProject[projectId] = entry;
            }
            entry.models.Add(new TokenUsageByModel
            {
                ModelId = modelId,
                InputTokens = input,
                OutputTokens = output,
                TotalNanoAiu = nanoAiu,
            });
        }

        return byProject
            .Select(kv =>
            {
                var totalTokens = kv.Value.models.Sum(m => m.InputTokens + m.OutputTokens);
                var totalNano = kv.Value.models.Sum(m => m.TotalNanoAiu);
                return new TokenUsageByProject
                {
                    ProjectId = kv.Key,
                    ProjectName = kv.Value.projectName,
                    TotalTokens = totalTokens,
                    TotalNanoAiu = totalNano,
                    ByModel = kv.Value.models,
                };
            })
            .OrderByDescending(p => p.TotalTokens)
            .ToList();
    }

    private static async Task<TokenUsageSummary> ReadSummaryAsync(SqliteCommand command, CancellationToken ct)
    {
        var byModel = new List<TokenUsageByModel>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            byModel.Add(new TokenUsageByModel
            {
                ModelId = reader.GetString(0),
                InputTokens = reader.GetInt64(1),
                OutputTokens = reader.GetInt64(2),
                TotalNanoAiu = reader.GetInt64(3),
            });
        }

        var totalInput = byModel.Sum(m => m.InputTokens);
        var totalOutput = byModel.Sum(m => m.OutputTokens);
        var totalNano = byModel.Sum(m => m.TotalNanoAiu);

        return new TokenUsageSummary
        {
            InputTokens = totalInput,
            OutputTokens = totalOutput,
            TotalTokens = totalInput + totalOutput,
            TotalNanoAiu = totalNano,
            ByModel = byModel,
        };
    }
}
