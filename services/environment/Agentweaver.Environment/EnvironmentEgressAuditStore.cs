using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using NpgsqlTypes;

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

    Task<RunEgressPage> ReadRunEgressAsync(
        string tenantId,
        string projectId,
        string runId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentEgressAuditConflictException(string message) : InvalidOperationException(message);

public sealed class EnvironmentEgressQueryException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed class EnvironmentEgressAuditStore(NpgsqlDataSource dataSource) : IEnvironmentEgressAuditStore
{
    private const int CursorVersion = 1;
    private const string CursorOrder = "firstSeen-desc,flowId-desc";
    private static readonly JsonSerializerOptions CursorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private sealed record RunEgressCursor(
        int Version,
        string Order,
        string TenantId,
        string ProjectId,
        string RunId,
        int PageSize,
        DateTimeOffset FirstSeen,
        string FlowId);

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

    public async Task<RunEgressPage> ReadRunEgressAsync(
        string tenantId,
        string projectId,
        string runId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ValidateScopeId(tenantId, "run_egress_query_invalid");
        ValidateScopeId(projectId, "run_egress_query_invalid");
        ValidateScopeId(runId, "run_egress_query_invalid");
        if (pageSize is < 1 or > RunEgressContract.MaximumPageSize)
            throw new EnvironmentEgressQueryException(
                "run_egress_query_invalid",
                $"Page size must be between 1 and {RunEgressContract.MaximumPageSize}.");

        var anchor = cursor is null
            ? null
            : DecodeCursor(tenantId, projectId, runId, pageSize, cursor);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (anchor is not null)
        {
            await using var anchorCommand = new NpgsqlCommand(
                """
                SELECT 1
                FROM environment.sandbox_egress_flows_attributed AS f
                INNER JOIN environment.sandbox_owner_mappings AS m
                    ON m.owner_fingerprint = f.owner_fingerprint
                WHERE m.tenant_id = @tenant_id
                  AND m.project_id = @project_id
                  AND m.run_id = @run_id
                  AND f.flow_id = @anchor_flow_id
                  AND f.first_seen = @anchor_first_seen
                LIMIT 1
                """,
                connection);
            anchorCommand.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, tenantId);
            anchorCommand.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
            anchorCommand.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);
            anchorCommand.Parameters.AddWithValue("anchor_flow_id", NpgsqlDbType.Varchar, anchor.FlowId);
            anchorCommand.Parameters.AddWithValue(
                "anchor_first_seen",
                NpgsqlDbType.TimestampTz,
                anchor.FirstSeen.ToUniversalTime());
            if (await anchorCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw InvalidCursor();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT f.flow_id, f.host, f.address, f.port, f.verdict, f.first_seen, f.last_seen, f.flow_count
            FROM environment.sandbox_egress_flows_attributed AS f
            INNER JOIN environment.sandbox_owner_mappings AS m
                ON m.owner_fingerprint = f.owner_fingerprint
            WHERE m.tenant_id = @tenant_id
              AND m.project_id = @project_id
              AND m.run_id = @run_id
              AND (@after_first_seen::timestamptz IS NULL
                   OR (f.first_seen, f.flow_id) < (@after_first_seen, @after_flow_id::varchar))
            ORDER BY f.first_seen DESC, f.flow_id DESC
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue(
            "after_first_seen",
            NpgsqlDbType.TimestampTz,
            (object?)anchor?.FirstSeen.ToUniversalTime() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "after_flow_id",
            NpgsqlDbType.Varchar,
            (object?)anchor?.FlowId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = ImmutableArray.CreateBuilder<RunEgressRecord>(pageSize);
        string? lastFlowId = null;
        var hasMore = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count == pageSize)
                {
                    hasMore = true;
                    break;
                }

                lastFlowId = reader.GetString(0);
                var verdict = Enum.Parse<EgressFlowVerdict>(reader.GetString(4), ignoreCase: false);
                items.Add(new RunEgressRecord(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    verdict,
                    reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime(),
                    reader.GetFieldValue<DateTimeOffset>(6).ToUniversalTime(),
                    reader.GetInt64(7)));
            }
        }

        var nextCursor = hasMore && items.Count > 0 && lastFlowId is not null
            ? EncodeCursor(new RunEgressCursor(
                CursorVersion,
                CursorOrder,
                tenantId,
                projectId,
                runId,
                pageSize,
                items[^1].FirstSeen,
                lastFlowId))
            : null;

        return new RunEgressPage(
            runId,
            RunEgressContract.CoverageUnavailable,
            null,
            items.ToImmutable(),
            nextCursor);
    }

    private static string EncodeCursor(RunEgressCursor cursor) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(cursor, CursorJsonOptions));

    private static RunEgressCursor DecodeCursor(
        string tenantId,
        string projectId,
        string runId,
        int pageSize,
        string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > RunEgressContract.MaximumCursorLength)
            throw InvalidCursor();
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(cursor);
            var decoded = JsonSerializer.Deserialize<RunEgressCursor>(bytes, CursorJsonOptions);
            if (decoded is null ||
                decoded.Version != CursorVersion ||
                !string.Equals(decoded.Order, CursorOrder, StringComparison.Ordinal) ||
                !string.Equals(decoded.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(decoded.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(decoded.RunId, runId, StringComparison.Ordinal) ||
                decoded.PageSize != pageSize ||
                decoded.FirstSeen == default ||
                !IsFlowId(decoded.FlowId))
                throw InvalidCursor();
            return decoded;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw InvalidCursor();
        }
    }

    private static EnvironmentEgressQueryException InvalidCursor() =>
        new("run_egress_cursor_invalid", "The run egress pagination cursor is invalid.");

    private static void ValidateScopeId(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new EnvironmentEgressQueryException(code, "The run egress scope identifier is invalid.");
    }

    private static bool IsFlowId(string? value) =>
        value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
