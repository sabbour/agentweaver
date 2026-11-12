using System.Text.Json;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal static class CheckpointRecoveryHold
{
    internal static async Task<bool> IsHeldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string projectId,
        string runId,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        var lockClause = forUpdate ? "FOR UPDATE" : "FOR SHARE";
        long fence;
        await using (var run = new NpgsqlCommand($"""
            SELECT execution_fence
            FROM {schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            {lockClause}
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", projectId);
            run.Parameters.AddWithValue("run", runId);
            var value = await run.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not long currentFence)
                throw new CoordinationException(
                    "checkpoint_recovery_hold_unavailable", StatusCodes.Status503ServiceUnavailable);
            fence = currentFence;
        }

        await using var operations = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {schema}.coordination_execution_operations
            WHERE project_id = @project AND run_id = @run AND operation_kind = 'recovery'
              AND (result -> 'checkpointRecovery' ->> 'boundaryKind' = 'childJoin'
                   OR progress ->> 'boundaryKind' = 'childJoin'
                   OR progress ? 'childManifest' OR progress ? 'childManifestSha256'
                   OR progress ? 'holdFence')
            ORDER BY operation_id
            FOR SHARE
            """, connection, transaction);
        operations.Parameters.AddWithValue("project", projectId);
        operations.Parameters.AddWithValue("run", runId);
        await using var reader = await operations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                using var result = JsonDocument.Parse(reader.GetString(0));
                using var progress = JsonDocument.Parse(reader.GetString(1));
                if (IsHeldPayload(result.RootElement, progress.RootElement, fence))
                    return true;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                               KeyNotFoundException or FormatException)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsHeldPayload(JsonElement resultRoot, JsonElement progressRoot, long fence)
    {
        var resultIsChildJoin = resultRoot.ValueKind == JsonValueKind.Object &&
            resultRoot.TryGetProperty("checkpointRecovery", out var recovery) &&
            IsChildJoinBoundary(recovery, "boundaryKind");
        if (!resultIsChildJoin && !IsChildJoinBoundary(progressRoot, "boundaryKind"))
            return false;

        try
        {
            recovery = resultRoot.GetProperty("checkpointRecovery");
            var operationFence = resultRoot.GetProperty("executionFence").GetInt64();
            if (operationFence < fence)
                return false;
            if (operationFence > fence)
                return true;

            var progressBoundary = progressRoot.GetProperty("boundaryKind");
            var progressFence = progressRoot.TryGetProperty("holdFence", out var holdFence) &&
                holdFence.TryGetInt64(out var storedFence)
                ? storedFence
                : -1;
            var held = progressRoot.TryGetProperty("dispatchHeld", out var dispatchHeld) &&
                dispatchHeld.ValueKind == JsonValueKind.True && dispatchHeld.GetBoolean();
            var statusHeld = recovery.TryGetProperty("dispatchHeld", out var storedHeld) &&
                storedHeld.ValueKind == JsonValueKind.True && storedHeld.GetBoolean();
            var hasManifest = progressRoot.TryGetProperty("childManifest", out var manifest) &&
                manifest.ValueKind == JsonValueKind.String &&
                progressRoot.TryGetProperty("childManifestSha256", out var digest) &&
                digest.ValueKind == JsonValueKind.String &&
                digest.GetString() is { Length: 64 } hash &&
                hash.All(Uri.IsHexDigit) &&
                EncodingUtf8ByteCount(manifest.GetString()!) <= ChildCheckpointRecoveryManifestCodec.MaximumBytes;

            if (!IsChildJoinBoundary(recovery, "boundaryKind") ||
                progressBoundary.ValueKind != JsonValueKind.String ||
                progressBoundary.GetString() != "childJoin" ||
                progressFence != fence || !held || !statusHeld || !hasManifest)
                return true;
            // This version has no child-join continuation that can release the hold.
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           KeyNotFoundException or FormatException)
        {
            return true;
        }
    }

    private static bool IsChildJoinBoundary(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var boundaryKind) &&
        boundaryKind.ValueKind == JsonValueKind.String &&
        boundaryKind.GetString() == "childJoin";

    private static int EncodingUtf8ByteCount(string value) =>
        System.Text.Encoding.UTF8.GetByteCount(value);
}
