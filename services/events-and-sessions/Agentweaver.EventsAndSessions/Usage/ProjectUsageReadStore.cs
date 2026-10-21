using System.Text.Json;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

/// <summary>Reads persisted project usage without pricing, budgeting, or updating accounting state.</summary>
public sealed class ProjectUsageReadStore(NpgsqlDataSource dataSource, PostgresSessionsProviderOptions options)
{
    /// <summary>Aggregates model and agent groups in one statement snapshot, retaining unknown measurements.</summary>
    public async Task<ProjectUsage> ReadAsync(
        string tenantId, string projectId, string? boundRunId, ProjectMetricsRange range,
        CancellationToken cancellationToken)
    {
        options.Validate();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH scoped AS MATERIALIZED (
                SELECT l.*, dimension.kind, dimension.id
                FROM "{options.Schema}".usage_ledger l
                CROSS JOIN LATERAL (VALUES ('model', l.model_id), ('agent', l.agent_id)) dimension(kind, id)
                WHERE tenant_id = @tenant AND project_id = @project
                    AND (@run::varchar IS NULL OR run_id = @run)
                    AND occurred_at >= @from AND occurred_at < @to
            ), costs AS (
                SELECT kind, id, price_unit, price_disposition,
                    CASE WHEN count(price_amount) = count(*) THEN sum(price_amount) END AS amount
                FROM scoped GROUP BY kind, id, price_unit, price_disposition
            )
            SELECT kind, id,
                CASE WHEN count(request_count) = count(*) THEN sum(request_count)::bigint END,
                CASE WHEN count(input_tokens) = count(*) THEN sum(input_tokens) END,
                CASE WHEN count(output_tokens) = count(*) THEN sum(output_tokens) END,
                CASE WHEN count(cached_tokens) = count(*) THEN sum(cached_tokens) END,
                CASE WHEN count(cache_write_tokens) = count(*) THEN sum(cache_write_tokens) END,
                CASE WHEN count(reasoning_tokens) = count(*) THEN sum(reasoning_tokens) END,
                CASE WHEN count(duration_milliseconds) = count(*) THEN avg(duration_milliseconds) END,
                count(DISTINCT run_id),
                CASE WHEN count(*) FILTER (WHERE price_disposition = 'Unpriced' AND request_count IS NULL) = 0
                    THEN COALESCE(sum(request_count) FILTER (WHERE price_disposition = 'Unpriced'), 0)::bigint END,
                (SELECT jsonb_agg(jsonb_build_object('priceUnit', price_unit,
                    'disposition', lower(price_disposition), 'amount', amount)
                    ORDER BY price_unit NULLS FIRST, price_disposition)
                 FROM costs c WHERE c.kind = s.kind AND c.id = s.id)::text
            FROM scoped s GROUP BY kind, id ORDER BY kind, id COLLATE "C"
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)boundRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("from", range.From);
        command.Parameters.AddWithValue("to", range.To);
        var models = new List<ProjectUsageItem>();
        var agents = new List<ProjectUsageItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new ProjectUsageItem(reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                ReadNullableDecimal(reader, 3), ReadNullableDecimal(reader, 4),
                ReadNullableDecimal(reader, 5), ReadNullableDecimal(reader, 6),
                ReadNullableDecimal(reader, 7), ReadNullableDecimal(reader, 8),
                reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                JsonSerializer.Deserialize<ProjectUsageCost[]>(reader.GetString(11),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            (reader.GetString(0) == "model" ? models : agents).Add(item);
        }
        return new(projectId, range.From, range.To, models, agents);
    }

    /// <summary>Reads a nullable PostgreSQL numeric without converting unknown values to zero.</summary>
    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetDecimal(column);
}
