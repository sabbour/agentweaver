using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>A persisted pending gate projected for the existing attention list.</summary>
public sealed record UserNotification(
    string NotificationId, string ProjectId, string RunId, string SessionId,
    string RequestId, CoordinationBlockerKind Kind, string? Prompt, DateTimeOffset CreatedAt,
    Agentweaver.Orchestrator.Core.CoordinatorGateKind GateKind, long StateVersion, DateTimeOffset UpdatedAt);

/// <summary>A newest-first attention page and the total pending count after caller dismissals.</summary>
public sealed record UserNotificationPage(
    ImmutableArray<UserNotification> Items, long Count, string? NextCursor);

/// <summary>Reads authoritative pending gates and stores caller-specific dismissals without resolving gates.</summary>
internal sealed class UserNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public UserNotificationStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (string.IsNullOrEmpty(schema) || schema.Length > 63 ||
            !char.IsAsciiLetterLower(schema[0]) ||
            schema.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_') ||
            schema is "public" or "information_schema" || schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
    }

    /// <summary>Returns project read authority using the same permission set as project run lists.</summary>
    internal static (bool AllProjects, string[] ProjectIds) ReadProjects(ProjectsAuthorizationContext authority)
    {
        var all = authority.EffectiveAuthority.Any(item =>
            item.ResourceType == "tenant" && item.ResourceId == authority.TenantId &&
            item.Permissions.Any(p => p.Permission == "readProjects" && p.RoleRevision > 0));
        var ids = authority.EffectiveAuthority.Where(item =>
            item.ResourceType == "project" && item.Permissions.Any(p =>
                p.Permission is "readProjects" or "readRunSelection" && p.RoleRevision > 0))
            .Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return (all, ids);
    }

    /// <summary>Reads one snapshot so badge count and page agree; defensively narrows any bound authority before paging.</summary>
    internal async Task<UserNotificationPage> ListAsync(
        ProjectsAuthorizationContext authority, int pageSize, string? cursor, CancellationToken ct,
        string? projectId = null, bool includeDismissed = false)
    {
        if (pageSize is < 1 or > 100)
            throw new CoordinationException("query_invalid", StatusCodes.Status400BadRequest);
        if (projectId is not null) CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        var binding = QueryBinding(authority, pageSize, projectId, includeDismissed);
        var after = cursor is null ? null : DecodeCursor(cursor, binding);
        var (all, projectIds) = ReadProjects(authority);
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var where = PendingWhere(!includeDismissed) +
            " AND (@filterProject::varchar IS NULL OR g.project_id = @filterProject)";
        long count;
        await using (var countCommand = new NpgsqlCommand(
            $"SELECT count(*) {where}", connection, transaction))
        {
            BindAuthority(countCommand, authority, all, projectIds);
            countCommand.Parameters.AddWithValue("filterProject", NpgsqlDbType.Varchar, (object?)projectId ?? DBNull.Value);
            count = (long)(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        await using var command = new NpgsqlCommand($"""
            SELECT g.project_id, g.run_id, g.session_id, g.request_id, g.gate, g.created_at,
                g.state_version, g.updated_at, g.execution_fence
            {where}
            AND (@afterAt::timestamptz IS NULL OR
                (g.created_at, g.project_id, g.run_id, g.request_id) <
                (@afterAt, @afterProject::varchar, @afterRun::varchar, @afterRequest::varchar))
            ORDER BY g.created_at DESC, g.project_id DESC, g.run_id DESC, g.request_id DESC
            LIMIT @limit
            """, connection, transaction);
        BindAuthority(command, authority, all, projectIds);
        command.Parameters.AddWithValue("filterProject", NpgsqlDbType.Varchar, (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterAt", NpgsqlDbType.TimestampTz, (object?)after?.CreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("afterProject", NpgsqlDbType.Varchar, (object?)after?.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterRun", NpgsqlDbType.Varchar, (object?)after?.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterRequest", NpgsqlDbType.Varchar, (object?)after?.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        var items = ImmutableArray.CreateBuilder<UserNotification>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var project = reader.GetString(0);
                var run = reader.GetString(1);
                var session = reader.GetString(2);
                var request = reader.GetString(3);
                var gate = ReadGate(reader.GetString(4), request, reader.GetInt64(8));
                var blocker = CoordinationEndpoints.ToStatusBlocker(gate);
                items.Add(new UserNotification(NotificationId(project, run, request),
                    project, run, session, request, blocker.Kind, blocker.Prompt,
                    reader.GetFieldValue<DateTimeOffset>(5), gate.Kind, reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }
        var more = items.Count > pageSize;
        if (more) items.RemoveAt(items.Count - 1);
        var last = more ? items[^1] : null;
        var next = last is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(1, binding, last.CreatedAt, last.ProjectId, last.RunId, last.RequestId), JsonOptions));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new UserNotificationPage(items.ToImmutable(), count, next);
    }

    /// <summary>Dismisses an authorized pending source idempotently; callers cannot mutate other users' state.</summary>
    internal async Task DismissAsync(
        ProjectsAuthorizationContext authority, string notificationId, CancellationToken ct,
        Func<CancellationToken, Task>? revalidateAuthority = null)
    {
        if (notificationId.Length != 64 || notificationId.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CoordinationException("notification_id_invalid", 400);
        var (all, projectIds) = ReadProjects(authority);
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.user_notification_dismissals
                (tenant_id, actor_issuer, actor_subject, project_id, run_id, request_id)
            SELECT @tenant, @issuer, @subject, g.project_id, g.run_id, g.request_id
            {PendingWhere(includeDismissals: false)}
            AND upper(encode(sha256(convert_to(
                g.project_id || chr(31) || g.run_id || chr(31) || g.request_id, 'UTF8')), 'hex')) = @id
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        BindAuthority(command, authority, all, projectIds);
        command.Parameters.AddWithValue("id", notificationId.ToUpperInvariant());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (revalidateAuthority is not null)
            await revalidateAuthority(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private string PendingWhere(bool includeDismissals = true) => $"""
        FROM {_schema}.coordinator_gates g
        JOIN {_schema}.accepted_runs r ON r.project_id = g.project_id AND r.run_id = g.run_id
        JOIN {_schema}.coordination_sessions s ON s.project_id = g.project_id
            AND s.run_id = g.run_id AND s.session_id = g.session_id
        JOIN {_schema}.coordination_sessions root ON root.project_id = s.project_id
            AND root.run_id = s.run_id AND root.session_id = s.root_session_id
        WHERE r.tenant_id = @tenant AND r.execution_state IN ('idle', 'active', 'blocked')
            AND g.gate_state = 'pending' AND g.execution_fence = s.execution_fence
            AND s.lifecycle_state = 'active'
            AND root.lifecycle_state = 'active'
            AND (@all OR g.project_id = ANY(@projects))
            AND (@project::varchar IS NULL OR g.project_id = @project)
            AND (@run::varchar IS NULL OR g.run_id = @run)
        """ + (includeDismissals ? $"""

            AND NOT EXISTS (
                SELECT 1 FROM {_schema}.user_notification_dismissals d
                WHERE d.tenant_id = @tenant AND d.actor_issuer = @issuer AND d.actor_subject = @subject
                    AND d.project_id = g.project_id AND d.run_id = g.run_id AND d.request_id = g.request_id)
        """ : "");

    private static void BindAuthority(
        NpgsqlCommand command, ProjectsAuthorizationContext authority, bool all, string[] projectIds)
    {
        command.Parameters.AddWithValue("tenant", authority.TenantId);
        command.Parameters.AddWithValue("issuer", authority.Issuer);
        command.Parameters.AddWithValue("subject", authority.ActorId);
        command.Parameters.AddWithValue("all", all);
        command.Parameters.AddWithValue("projects", NpgsqlDbType.Array | NpgsqlDbType.Varchar, projectIds);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, (object?)authority.BoundProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)authority.BoundRunId ?? DBNull.Value);
    }

    internal static string NotificationId(string project, string run, string request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project}\u001f{run}\u001f{request}")));

    internal static Agentweaver.Orchestrator.Core.CoordinatorGateRequest ReadGate(
        string json, string requestId, long executionFence)
    {
        try
        {
            var gate = JsonSerializer.Deserialize<Agentweaver.Orchestrator.Core.CoordinatorGateRequest>(
                json, JsonOptions);
            if (gate is null || gate.RequestId != requestId || gate.Fence != executionFence ||
                gate.AllowedChoices.IsDefault)
                throw new CoordinationException("coordinator_gate_state_unavailable", 503);
            return gate;
        }
        catch (JsonException)
        {
            throw new CoordinationException("coordinator_gate_state_unavailable", 503);
        }
    }

    private static string QueryBinding(
        ProjectsAuthorizationContext authority, int pageSize, string? projectId, bool includeDismissed)
    {
        var (all, ids) = ReadProjects(authority);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            authority.Issuer, authority.ActorId, authority.TenantId, authority.MembershipRevision,
            authority.BoundProjectId, authority.BoundRunId, All = all, Projects = ids,
            PageSize = pageSize, ProjectId = projectId, IncludeDismissed = includeDismissed
        })));
    }

    private static Cursor DecodeCursor(string value, string binding)
    {
        if (value.Length > 4096) throw new CoordinationException("query_invalid", 400);
        try
        {
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value), JsonOptions);
            if (cursor is null || cursor.Version != 1 || cursor.Binding != binding ||
                string.IsNullOrEmpty(cursor.ProjectId) || string.IsNullOrEmpty(cursor.RunId) ||
                string.IsNullOrEmpty(cursor.RequestId))
                throw new CoordinationException("query_invalid", 400);
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new CoordinationException("query_invalid", 400);
        }
    }

    private sealed record Cursor(
        int Version, string Binding, DateTimeOffset CreatedAt, string ProjectId, string RunId, string RequestId);
}
