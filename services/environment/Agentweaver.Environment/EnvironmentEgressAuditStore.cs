using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Npgsql;

namespace Agentweaver.Environment;

public sealed record EnvironmentEgressAuditWriteResult(string FlowId, bool Attributed);

public interface IEnvironmentEgressAuditStore
{
    Task<string> RegisterOwnerAsync(EnvironmentOwnerIdentity owner, CancellationToken cancellationToken);

    /// <summary>
    /// Commits the flow before returning. A reader must advance its source cursor only after this call succeeds.
    /// </summary>
    Task<EnvironmentEgressAuditWriteResult> AppendFlowAsync(
        EgressFlowObservation flow,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentEgressAuditConflictException(string message) : InvalidOperationException(message);

public sealed class EnvironmentEgressAuditStore(NpgsqlDataSource dataSource) : IEnvironmentEgressAuditStore
{
    public async Task<string> RegisterOwnerAsync(
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var fingerprint = AgentSandboxProvider.GetOwnerFingerprint(owner);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO environment.sandbox_owner_mappings
                (owner_fingerprint, tenant_id, project_id, run_id, environment_id)
            VALUES
                (@fingerprint, @tenant_id, @project_id, @run_id, @environment_id)
            ON CONFLICT (owner_fingerprint) DO NOTHING
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("tenant_id", owner.TenantId);
            insert.Parameters.AddWithValue("project_id", owner.ProjectId);
            insert.Parameters.AddWithValue("run_id", owner.RunId);
            insert.Parameters.AddWithValue("environment_id", owner.EnvironmentId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var read = new NpgsqlCommand(
            """
            SELECT tenant_id, project_id, run_id, environment_id
            FROM environment.sandbox_owner_mappings
            WHERE owner_fingerprint = @fingerprint
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("fingerprint", fingerprint);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != owner.TenantId ||
                reader.GetString(1) != owner.ProjectId ||
                reader.GetString(2) != owner.RunId ||
                reader.GetString(3) != owner.EnvironmentId)
                throw new EnvironmentEgressAuditConflictException(
                    "The sandbox owner fingerprint is already bound to another Environment owner.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return fingerprint;
    }

    public async Task<EnvironmentEgressAuditWriteResult> AppendFlowAsync(
        EgressFlowObservation flow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        flow = flow with
        {
            FirstSeen = flow.FirstSeen.ToUniversalTime(),
            LastSeen = flow.LastSeen.ToUniversalTime()
        };
        ValidateFlow(flow);
        var flowId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(flow)))
            .ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var flowLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@flow_id, 0))",
            connection,
            transaction))
        {
            flowLock.Parameters.AddWithValue("flow_id", flowId);
            await flowLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var existingFlow = false;
        var attributed = false;
        await using (var existing = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM environment.sandbox_egress_flows_attributed
                    WHERE flow_id = @flow_id),
                EXISTS (
                    SELECT 1
                    FROM environment.sandbox_egress_flows_unattributed
                    WHERE flow_id = @flow_id)
            """,
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("flow_id", flowId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The egress audit flow lookup returned no result.");
            attributed = reader.GetBoolean(0);
            existingFlow = attributed || reader.GetBoolean(1);
        }

        if (existingFlow)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(flowId, attributed);
        }

        if (flow.WorkloadRef is not null)
        {
            await using var lookup = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM environment.sandbox_owner_mappings
                    WHERE owner_fingerprint = @owner_fingerprint)
                """,
                connection,
                transaction);
            lookup.Parameters.AddWithValue("owner_fingerprint", flow.WorkloadRef);
            attributed = (bool)(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var table = attributed
            ? "sandbox_egress_flows_attributed"
            : "sandbox_egress_flows_unattributed";
        var columns = attributed
            ? "(flow_id, owner_fingerprint, host, address, port, verdict, first_seen, last_seen, flow_count, workload_ref)"
            : "(flow_id, host, address, port, verdict, first_seen, last_seen, flow_count, workload_ref)";
        var values = attributed
            ? "(@flow_id, @owner_fingerprint, @host, @address, @port, @verdict, @first_seen, @last_seen, @flow_count, @workload_ref)"
            : "(@flow_id, @host, @address, @port, @verdict, @first_seen, @last_seen, @flow_count, @workload_ref)";
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO environment.{table} {columns} VALUES {values} ON CONFLICT (flow_id) DO NOTHING",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("flow_id", flowId);
            if (attributed)
                insert.Parameters.AddWithValue("owner_fingerprint", flow.WorkloadRef!);
            insert.Parameters.AddWithValue("host", (object?)flow.Host ?? DBNull.Value);
            insert.Parameters.AddWithValue("address", (object?)flow.Address ?? DBNull.Value);
            insert.Parameters.AddWithValue("port", flow.Port);
            insert.Parameters.AddWithValue("verdict", flow.Verdict.ToString());
            insert.Parameters.AddWithValue("first_seen", flow.FirstSeen);
            insert.Parameters.AddWithValue("last_seen", flow.LastSeen);
            insert.Parameters.AddWithValue("flow_count", flow.Count);
            insert.Parameters.AddWithValue("workload_ref", (object?)flow.WorkloadRef ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(flowId, attributed);
    }

    private static void ValidateFlow(EgressFlowObservation flow)
    {
        if (flow.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(flow), "The egress flow port must be between 1 and 65535.");
        if (!Enum.IsDefined(flow.Verdict))
            throw new ArgumentOutOfRangeException(nameof(flow), "The egress flow verdict is not supported.");
        if (flow.LastSeen < flow.FirstSeen)
            throw new ArgumentException("Egress flow timestamps must be ordered.", nameof(flow));
        if (flow.Count < 1)
            throw new ArgumentOutOfRangeException(nameof(flow), "The egress flow count must be positive.");
        if (flow.Host is null && flow.Address is null)
            throw new ArgumentException("An egress flow must include a host or address.", nameof(flow));
        ValidateText(flow.Host, nameof(flow.Host));
        ValidateText(flow.Address, nameof(flow.Address));
        ValidateText(flow.WorkloadRef, nameof(flow.WorkloadRef));
    }

    private static void ValidateText(string? value, string parameterName)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
            throw new ArgumentException("Egress flow text fields must be non-empty, bounded, and contain no control characters.",
                parameterName);
    }
}
