using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Identifies the actor whose accepted run selection created a run.
/// </summary>
public sealed record ProjectRunStartedBy(string Issuer, string Subject);

/// <summary>
/// Summarizes one accepted run for a project run list.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="RootSessionId">The root coordinator session, or null before a root session is accepted.</param>
/// <param name="Title">The display title saved with the run request, or null when no request is stored.</param>
/// <param name="Goal">The first message saved with the run request, or null when no request is stored.</param>
/// <param name="WorkflowId">The workflow selected when the run was created, or null when none was selected.</param>
/// <param name="State">The derived run state. See <see cref="ProjectRunReadStore"/> for the derivation.</param>
/// <param name="NeedsInputCount">The number of durable pending decision gates in the run.</param>
/// <param name="CreatedAt">When the run was accepted.</param>
/// <param name="UpdatedAt">When run-level owner state last changed. Ordinary turns do not change it.</param>
/// <param name="CompletedAt">When the run reached a terminal state, or null.</param>
/// <param name="StartedBy">The actor that accepted the run selection.</param>
public sealed record ProjectRunSummary(
    string RunId,
    string? RootSessionId,
    string? Title,
    string? Goal,
    string? WorkflowId,
    string State,
    long NeedsInputCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    ProjectRunStartedBy StartedBy);

/// <summary>
/// Holds one newest-first page of project runs and the opaque cursor for the next page.
/// </summary>
public sealed record ProjectRunPage(
    string ProjectId,
    ImmutableArray<ProjectRunSummary> Items,
    string? NextCursor);

/// <summary>
/// Reads newest-first, keyset-paged run summaries for one project from the coordination owner tables.
/// </summary>
/// <remarks>
/// The run state is derived from persisted rows only. A failed, indeterminate, or cancelled run execution
/// state reports that state.
/// Otherwise the root session decides: completed or cancelled lifecycle wins, then an active or presenting
/// turn reports <c>active</c>, a blocked turn reports <c>blocked</c>, and anything else reports <c>idle</c>.
/// </remarks>
public sealed class ProjectRunReadStore
{
    /// <summary>The default number of runs on one page.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest accepted page size.</summary>
    public const int MaximumPageSize = 100;

    private const int CursorVersion = 2;
    private const string CursorOrder = "createdAt-desc,runId-desc";

    /// <summary>Every derived run state, in documentation order.</summary>
    public static readonly ImmutableArray<string> States =
        ["idle", "active", "blocked", "completed", "failed", "indeterminate", "cancelled"];

    private static readonly ImmutableArray<string> OpenStates = ["idle", "active", "blocked"];
    private static readonly ImmutableArray<string> TerminalStates =
        ["completed", "failed", "indeterminate", "cancelled"];

    private static readonly JsonSerializerOptions CursorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _runs;
    private readonly string _sessions;
    private readonly string _requests;
    private readonly string _gates;

    /// <summary>The canonical derived state shared by run lists and project metrics.</summary>
    private const string DerivedStateSql = """
        CASE
            WHEN r.execution_state IN ('failed', 'indeterminate', 'cancelled') THEN r.execution_state
            WHEN root.lifecycle_state = 'completed' THEN 'completed'
            WHEN root.lifecycle_state = 'cancelled' THEN 'cancelled'
            WHEN root.turn_state IN ('active', 'presenting') THEN 'active'
            WHEN root.turn_state = 'blocked' THEN 'blocked'
            ELSE 'idle'
        END
        """;

    /// <summary>
    /// Creates a reader over the coordination owner schema.
    /// </summary>
    public ProjectRunReadStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _runs = $"\"{schema}\".accepted_runs";
        _sessions = $"\"{schema}\".coordination_sessions";
        _requests = $"\"{schema}\".project_run_requests";
        _gates = $"\"{schema}\".coordinator_gates";
    }

    /// <summary>
    /// Parses a comma-separated state filter into a sorted, distinct set of derived states.
    /// </summary>
    /// <param name="value">States, plus the <c>open</c> and <c>terminal</c> aliases. Null or empty means all states.</param>
    /// <returns>The canonical state set, or an empty array when no filter applies.</returns>
    /// <exception cref="CoordinationException">A state is unknown or blank.</exception>
    public static ImmutableArray<string> ParseStateFilter(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        var states = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in value.Split(','))
        {
            if (item == "open")
                states.UnionWith(OpenStates);
            else if (item == "terminal")
                states.UnionWith(TerminalStates);
            else if (States.Contains(item))
                states.Add(item);
            else
                throw new CoordinationException("run_state_filter_invalid", StatusCodes.Status400BadRequest);
        }
        // The empty set represents no filter, including an explicit selection of every state.
        return states.Count == States.Length ? [] : [.. states];
    }

    /// <summary>
    /// Reads one page of runs for a project in a tenant, newest first.
    /// </summary>
    /// <param name="tenantId">The caller's tenant from Projects authorization. Rows in other tenants are invisible.</param>
    /// <param name="projectId">The project to list.</param>
    /// <param name="boundRunId">When set, narrows the list to this run for a run-bound caller.</param>
    /// <param name="states">The canonical state filter from <see cref="ParseStateFilter"/>; empty means all.</param>
    /// <param name="cursor">The opaque cursor from a previous page, or null for the first page.</param>
    /// <param name="pageSize">The page size, from 1 to <see cref="MaximumPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <param name="startedBy">When set, filters to this exact issuer and subject and binds the cursor to them.</param>
    /// <remarks>
    /// The state is derived per row, so the filter can't use an index. The keyset index bounds an
    /// unfiltered page to <paramref name="pageSize"/> + 1 rows; a selective state filter can read every
    /// project run older than the cursor before it fills a page.
    /// </remarks>
    /// <exception cref="CoordinationException">The page size or cursor is invalid for this query.</exception>
    public async Task<ProjectRunPage> ListAsync(
        string tenantId,
        string projectId,
        string? boundRunId,
        ImmutableArray<string> states,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        ProjectRunStartedBy? startedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        if (pageSize is < 1 or > MaximumPageSize)
            throw new CoordinationException("page_size_invalid", StatusCodes.Status400BadRequest);
        var filter = string.Join(',', states);
        var after = cursor is null ? null : DecodeCursor(projectId, pageSize, filter, startedBy, cursor);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT run_id, root_session_id, title, goal, workflow_id, state, needs_input_count,
                created_at, updated_at, completed_at,
                accepted_by_issuer, accepted_by_subject
            FROM (
                SELECT r.run_id, root.session_id AS root_session_id, run_request.title,
                    run_request.first_message AS goal, run_request.workflow_id,
                    pending_gates.needs_input_count, r.created_at, r.updated_at,
                    r.completed_at, r.accepted_by_issuer, r.accepted_by_subject,
                    {DerivedStateSql} AS state
                FROM {_runs} AS r
                LEFT JOIN {_sessions} AS root
                    ON root.project_id = r.project_id AND root.run_id = r.run_id
                    AND root.parent_session_id IS NULL
                LEFT JOIN {_requests} AS run_request
                    ON run_request.tenant_id = r.tenant_id AND run_request.project_id = r.project_id
                    AND run_request.run_id = r.run_id
                LEFT JOIN LATERAL (
                    SELECT count(*) AS needs_input_count
                    FROM {_gates} AS gate
                    WHERE gate.project_id = r.project_id AND gate.run_id = r.run_id
                        AND gate.gate_state = 'pending'
                ) AS pending_gates ON true
                WHERE r.tenant_id = @tenant AND r.project_id = @project
                    AND (@run::varchar IS NULL OR r.run_id = @run)
                    AND (@startedByIssuer::varchar IS NULL OR
                        (r.accepted_by_issuer = @startedByIssuer AND r.accepted_by_subject = @startedBySubject))
                    AND (@afterCreated::timestamptz IS NULL
                        OR (r.created_at, r.run_id) < (@afterCreated, @afterRun::varchar))
            ) AS runs
            WHERE cardinality(@states::text[]) = 0 OR state = ANY(@states)
            ORDER BY created_at DESC, run_id DESC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)boundRunId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "startedByIssuer", NpgsqlDbType.Varchar, (object?)startedBy?.Issuer ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "startedBySubject", NpgsqlDbType.Varchar, (object?)startedBy?.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "afterCreated", NpgsqlDbType.TimestampTz, (object?)after?.CreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("afterRun", NpgsqlDbType.Varchar, (object?)after?.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("states", NpgsqlDbType.Array | NpgsqlDbType.Text, states.ToArray());
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = ImmutableArray.CreateBuilder<ProjectRunSummary>(pageSize);
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
                items.Add(new ProjectRunSummary(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    new ProjectRunStartedBy(reader.GetString(10), reader.GetString(11))));
            }
        }
        var last = items.Count > 0 ? items[^1] : null;
        return new ProjectRunPage(
            projectId,
            items.ToImmutable(),
            hasMore && last is not null
                ? EncodeCursor(new RunListCursor(
                    CursorVersion, CursorOrder, projectId, pageSize, filter,
                    startedBy?.Issuer, startedBy?.Subject, last.CreatedAt, last.RunId))
                : null);
    }

    /// <summary>Aggregates one authorized project's persisted runs in a single statement snapshot.</summary>
    public async Task<ProjectMetrics> ReadMetricsAsync(
        string tenantId, string projectId, string? boundRunId, ProjectMetricsRange range,
        CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH runs AS MATERIALIZED (
                SELECT r.created_at, r.completed_at, {DerivedStateSql} AS state
                FROM {_runs} r
                LEFT JOIN {_sessions} root ON root.project_id = r.project_id AND root.run_id = r.run_id
                    AND root.parent_session_id IS NULL
                WHERE r.tenant_id = @tenant AND r.project_id = @project
                    AND (@run::varchar IS NULL OR r.run_id = @run)
            ), totals AS (
                SELECT count(*) FILTER (WHERE state = 'active') AS active,
                    count(*) FILTER (WHERE state = 'blocked') AS blocked,
                    count(*) FILTER (WHERE created_at >= @from AND created_at < @to) AS in_range,
                    count(*) AS total,
                    count(*) FILTER (WHERE state = ANY(@terminal) AND completed_at >= @from
                        AND completed_at < @to) AS completed,
                    count(*) FILTER (WHERE state = 'completed' AND completed_at >= @from
                        AND completed_at < @to) AS succeeded
                FROM runs
            ), daily AS (
                SELECT (created_at AT TIME ZONE 'UTC')::date AS day, count(*) AS created, 0::bigint AS completed
                FROM runs WHERE created_at >= @from AND created_at < @to GROUP BY 1
                UNION ALL
                SELECT (completed_at AT TIME ZONE 'UTC')::date, 0::bigint, count(*)
                FROM runs WHERE state = ANY(@terminal) AND completed_at >= @from AND completed_at < @to
                GROUP BY 1
            )
            SELECT active, blocked, in_range, total, totals.completed, succeeded,
                COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'date', to_char(day, 'YYYY-MM-DD'), 'created', created, 'completed', completed) ORDER BY day)
                    FROM (SELECT day, sum(created)::bigint AS created, sum(completed)::bigint AS completed
                        FROM daily GROUP BY day) d), '[]'::jsonb)::text
            FROM totals
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)boundRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("from", range.From);
        command.Parameters.AddWithValue("to", range.To);
        command.Parameters.AddWithValue("terminal", TerminalStates.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var days = JsonSerializer.Deserialize<ProjectMetricsDay[]>(reader.GetString(6),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var counts = days.ToDictionary(day => day.Date, StringComparer.Ordinal);
        var series = new List<ProjectMetricsDay>();
        var firstDay = range.From.UtcDateTime.Date;
        var dayCount = range.From == range.To ? 0 : (int)Math.Ceiling((range.To.UtcDateTime - firstDay).TotalDays);
        for (var index = 0; index < dayCount; index++)
        {
            var day = firstDay.AddDays(index);
            var date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            series.Add(counts.GetValueOrDefault(date) ?? new ProjectMetricsDay(date, 0, 0));
        }
        return new(projectId, range.From, range.To, reader.GetInt64(0), reader.GetInt64(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), series);
    }

    /// <summary>Encodes the cursor version, order, query shape, and last row into an opaque URL-safe string.</summary>
    private static string EncodeCursor(RunListCursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, CursorJsonOptions);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes a cursor and rejects it unless it was issued for the same project, page size, filters, and caller.
    /// </summary>
    /// <exception cref="CoordinationException">The cursor is malformed or belongs to another query.</exception>
    private static RunListCursor DecodeCursor(
        string projectId, int pageSize, string filter, ProjectRunStartedBy? startedBy, string value)
    {
        if (value.Length is 0 or > 2048)
            throw InvalidCursor();
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw InvalidCursor(),
            };
            var cursor = JsonSerializer.Deserialize<RunListCursor>(Convert.FromBase64String(base64), CursorJsonOptions);
            if (cursor is null ||
                cursor.Version != CursorVersion ||
                cursor.Order != CursorOrder ||
                cursor.ProjectId != projectId ||
                cursor.PageSize != pageSize ||
                cursor.States != filter ||
                cursor.StartedByIssuer != startedBy?.Issuer ||
                cursor.StartedBySubject != startedBy?.Subject ||
                string.IsNullOrEmpty(cursor.RunId))
                throw InvalidCursor();
            CoordinationIdentity.ValidateIdentity(cursor.RunId, "run_id");
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw InvalidCursor();
        }
        catch (CoordinationException exception) when (exception.Code == "run_id_invalid")
        {
            throw InvalidCursor();
        }
    }

    private static CoordinationException InvalidCursor() =>
        new("cursor_invalid", StatusCodes.Status400BadRequest);

    /// <summary>
    /// The decoded keyset position and the query shape that issued it.
    /// </summary>
    private sealed record RunListCursor(
        int Version,
        string Order,
        string ProjectId,
        int PageSize,
        string States,
        string? StartedByIssuer,
        string? StartedBySubject,
        DateTimeOffset CreatedAt,
        string RunId);
}
