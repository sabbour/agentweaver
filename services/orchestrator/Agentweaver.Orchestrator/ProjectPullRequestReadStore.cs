using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Summarizes one pull request that a project run created or reused through Source Control.
/// </summary>
/// <param name="ProviderId">The source control provider from the run's repository pin.</param>
/// <param name="Repository">The pinned repository that contains the pull request.</param>
/// <param name="Number">The provider pull request number.</param>
/// <param name="Title">The title when the run last recorded the pull request, or null when the provider returned none.</param>
/// <param name="HtmlUrl">The provider web URL of the pull request.</param>
/// <param name="State">The state when the run last recorded the pull request: <c>open</c>, <c>closed</c>, or <c>merged</c>. This is not the live provider state.</param>
/// <param name="HeadBranch">The recorded head branch.</param>
/// <param name="HeadSha">The recorded head commit.</param>
/// <param name="BaseBranch">The recorded base branch.</param>
/// <param name="BaseSha">The recorded base commit.</param>
/// <param name="RunId">The run that created or reused the pull request.</param>
/// <param name="Disposition">Whether the run <c>created</c> the pull request or <c>reused</c> an existing one.</param>
/// <param name="RecordedAt">When the run first recorded the pull request.</param>
/// <param name="UpdatedAt">When the run last recorded the pull request.</param>
public sealed record ProjectPullRequestSummary(
    string ProviderId,
    ProjectSourceControlRepository Repository,
    long Number,
    string? Title,
    Uri HtmlUrl,
    string State,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    string RunId,
    string Disposition,
    DateTimeOffset RecordedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Describes which pull requests the stored record can contain.
/// </summary>
/// <param name="RecordedSince">When pull request recording started: the time migration 032 was applied.</param>
/// <param name="IncludesEarlierPullRequests">Always false. Pull requests from before <paramref name="RecordedSince"/> were never recorded.</param>
public sealed record ProjectPullRequestCoverage(DateTimeOffset RecordedSince, bool IncludesEarlierPullRequests);

/// <summary>
/// Holds one page of recorded project pull requests, the record coverage, and the opaque cursor for the next page.
/// </summary>
public sealed record ProjectPullRequestPage(
    string ProjectId,
    ProjectPullRequestCoverage Coverage,
    ImmutableArray<ProjectPullRequestSummary> Items,
    string? NextCursor);

/// <summary>
/// Reads keyset-paged pull request records that project runs wrote when they created or reused a pull request.
/// </summary>
/// <remarks>
/// This read makes no source control provider call. It isn't a complete pull request history:
/// it contains only pull requests that a run created or reused after migration 032 was applied.
/// Before that migration is applied, the read fails with <c>pull_request_records_unavailable</c>
/// rather than returning an empty list.
/// </remarks>
public sealed class ProjectPullRequestReadStore
{
    private const int CursorVersion = 1;
    private const string CursorOrder = "recordedAt-desc,runId-desc,pinId-desc,number-desc";

    /// <summary>The accepted <c>state</c> values. The default is <c>open</c>.</summary>
    public static readonly ImmutableArray<string> StateFilters = ["open", "closed", "merged", "all"];

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _records;
    private readonly string _pins;

    /// <summary>
    /// Creates a reader over the coordination owner schema.
    /// </summary>
    public ProjectPullRequestReadStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
        _records = $"{_schema}.source_control_pull_requests";
        _pins = $"{_schema}.source_control_repository_pins";
    }

    /// <summary>
    /// Parses the pull request state filter.
    /// </summary>
    /// <param name="value">One of <c>open</c>, <c>closed</c>, <c>merged</c>, or <c>all</c>. Null means <c>open</c>.</param>
    /// <exception cref="CoordinationException">The value isn't an accepted state filter.</exception>
    public static string ParseStateFilter(string? value)
    {
        // A missing filter means open pull requests, the usual picker case.
        if (value is null)
            return "open";
        // Matching is exact and case-sensitive, so the cursor can compare the stored filter directly.
        if (!StateFilters.Contains(value))
            throw new CoordinationException("pull_request_state_filter_invalid", StatusCodes.Status400BadRequest);
        return value;
    }

    /// <summary>
    /// Reads one page of recorded pull requests for a project in a tenant, most recently recorded first.
    /// </summary>
    /// <param name="tenantId">The caller's tenant from Projects authorization. Rows in other tenants are invisible.</param>
    /// <param name="projectId">The project to list.</param>
    /// <param name="boundRunId">When set, narrows the list to this run's pull requests for a run-bound caller.</param>
    /// <param name="state">The filter from <see cref="ParseStateFilter"/>.</param>
    /// <param name="cursor">The opaque cursor from a previous page, or null for the first page.</param>
    /// <param name="pageSize">The page size, from 1 to <see cref="ProjectRunReadStore.MaximumPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <remarks>
    /// Order is recordedAt descending, then runId, pinId, and number descending. recordedAt never changes,
    /// so a later reuse doesn't move a row between pages. Pages are not a snapshot.
    /// </remarks>
    /// <exception cref="CoordinationException">
    /// The page size or cursor is invalid for this query, or migration 032 isn't applied (503
    /// <c>pull_request_records_unavailable</c>).
    /// </exception>
    public async Task<ProjectPullRequestPage> ListAsync(
        string tenantId,
        string projectId,
        string? boundRunId,
        string state,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Validate inputs again here, because the store can be called without the endpoint.
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        state = ParseStateFilter(state);
        if (pageSize is < 1 or > ProjectRunReadStore.MaximumPageSize)
            throw new CoordinationException("page_size_invalid", StatusCodes.Status400BadRequest);
        // A cursor is valid only for the same project, page size, and filter that issued it.
        var after = cursor is null ? null : DecodeCursor(projectId, pageSize, state, cursor);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Check for migration 032 before reading. Its ledger time is also the start of coverage.
        // Without it there are no records at all, so return 503 instead of an empty page that
        // would look like "this project has no pull requests". This check never applies 032.
        var recordedSince = await SourceControlPullRequestRecords.ReadCoverageStartAsync(
                connection, _schema, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                SourceControlPullRequestRecords.UnavailableCode, StatusCodes.Status503ServiceUnavailable);
        // Tenant comes from the run's resource pin, because the record table has no tenant column.
        // The inner join drops any record whose pin isn't in the caller's tenant. A bound run narrows
        // the list to that run. The keyset tuple matches the ORDER BY exactly, so paging never skips
        // or repeats a row; recorded_at doesn't change on reuse, so rows don't move between pages.
        await using var command = new NpgsqlCommand($"""
            SELECT r.provider_id, r.repository_owner, r.repository_name, r.pull_request_number, r.title,
                r.html_url, r.state, r.head_branch, r.head_sha, r.base_branch, r.base_sha, r.run_id,
                r.disposition, r.recorded_at, r.updated_at, r.pin_id
            FROM {_records} AS r
            JOIN {_pins} AS p
                ON p.project_id = r.project_id AND p.run_id = r.run_id AND p.pin_id = r.pin_id
            WHERE p.tenant_id = @tenant AND r.project_id = @project
                AND (@run::varchar IS NULL OR r.run_id = @run)
                AND (@state = 'all' OR r.state = @state)
                AND (@afterRecordedAt::timestamptz IS NULL
                    OR (r.recorded_at, r.run_id, r.pin_id, r.pull_request_number) <
                        (@afterRecordedAt, @afterRun::varchar, @afterPin::varchar, @afterNumber::bigint))
            ORDER BY r.recorded_at DESC, r.run_id DESC, r.pin_id DESC, r.pull_request_number DESC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, (object?)boundRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, state);
        command.Parameters.AddWithValue(
            "afterRecordedAt", NpgsqlDbType.TimestampTz, (object?)after?.RecordedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("afterRun", NpgsqlDbType.Varchar, (object?)after?.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("afterPin", NpgsqlDbType.Varchar, (object?)after?.PinId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "afterNumber", NpgsqlDbType.Bigint, (object?)after?.PullRequestNumber ?? DBNull.Value);
        // Read one extra row. If it exists there is a next page; it is not returned.
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = ImmutableArray.CreateBuilder<ProjectPullRequestSummary>(pageSize);
        // The pin ID is part of the sort key but not of the public summary, so keep the last one
        // separately for the next cursor.
        string? lastPinId = null;
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
                items.Add(new ProjectPullRequestSummary(
                    reader.GetString(0),
                    new ProjectSourceControlRepository(reader.GetString(1), reader.GetString(2)),
                    reader.GetInt64(3),
                    // Title is null when the provider didn't return one.
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    new Uri(reader.GetString(5), UriKind.Absolute),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetFieldValue<DateTimeOffset>(13),
                    reader.GetFieldValue<DateTimeOffset>(14)));
                lastPinId = reader.GetString(15);
            }
        }
        // Every page reports coverage, so a client never reads a page as complete history.
        var last = items.Count > 0 ? items[^1] : null;
        return new ProjectPullRequestPage(
            projectId,
            new ProjectPullRequestCoverage(recordedSince, IncludesEarlierPullRequests: false),
            items.ToImmutable(),
            hasMore && last is not null && lastPinId is not null
                ? ProjectListCursor.Encode(new PullRequestListCursor(
                    CursorVersion, CursorOrder, projectId, pageSize, state,
                    last.RecordedAt, last.RunId, lastPinId, last.Number))
                : null);
    }

    /// <summary>
    /// Decodes a cursor and rejects it unless it was issued for the same project, page size, and state filter.
    /// </summary>
    private static PullRequestListCursor DecodeCursor(string projectId, int pageSize, string state, string value)
    {
        var cursor = ProjectListCursor.Decode<PullRequestListCursor>(value);
        // Reject cursors from another version, ordering, project, page size, or filter, and
        // reject key values that no stored row can have.
        if (cursor.Version != CursorVersion ||
            cursor.Order != CursorOrder ||
            cursor.ProjectId != projectId ||
            cursor.PageSize != pageSize ||
            cursor.State != state ||
            cursor.PullRequestNumber < 1 ||
            string.IsNullOrEmpty(cursor.RunId) || cursor.RunId.Length > 256 ||
            string.IsNullOrEmpty(cursor.PinId) || cursor.PinId.Length > 128)
            throw ProjectListCursor.Invalid();
        return cursor;
    }

    /// <summary>
    /// The decoded keyset position and the query shape that issued it.
    /// </summary>
    private sealed record PullRequestListCursor(
        int Version,
        string Order,
        string ProjectId,
        int PageSize,
        string State,
        DateTimeOffset RecordedAt,
        string RunId,
        string PinId,
        long PullRequestNumber);
}
