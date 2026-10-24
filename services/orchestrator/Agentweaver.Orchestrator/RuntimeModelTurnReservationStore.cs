using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal static class RuntimeModelTurnReservationStore
{
    internal static async Task<long> CountRunWideAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string projectId,
        string runId,
        string rootSessionId,
        Guid excludedEventId,
        CancellationToken cancellationToken)
    {
        // Count workflow dispatches and root action grants together; excluding the same event in both
        // sources keeps exact retries from consuming another model-turn reservation.
        await using var count = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM {schema}.maf_execution_dispatches
                 WHERE project_id = @project AND run_id = @run
                   AND is_cost_producing AND dispatch_id <> @event)
                +
                (SELECT count(*) FROM {schema}.executable_action_grants
                 WHERE project_id = @project AND run_id = @run AND session_id = @root
                   AND step_id = @rootStep AND purpose = @purpose
                   AND action_ids @> '["model.turn"]'::jsonb AND grant_id <> @grant)
            """, connection, transaction);
        count.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        count.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        count.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        count.Parameters.AddWithValue(
            "rootStep", NpgsqlDbType.Varchar, Agentweaver.Identity.RuntimeActionContract.CoordinatorRootStepId);
        count.Parameters.AddWithValue(
            "purpose", NpgsqlDbType.Varchar, Agentweaver.Identity.RuntimeActionContract.Purpose);
        count.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, excludedEventId);
        count.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, excludedEventId.ToString("N"));
        return (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
