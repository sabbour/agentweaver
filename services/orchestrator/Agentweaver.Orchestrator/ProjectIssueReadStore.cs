using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Names a provider repository as Agentweaver stored it.
/// </summary>
/// <param name="Owner">The repository owner, with the stored casing.</param>
/// <param name="Name">The repository name, with the stored casing.</param>
public sealed record ProjectSourceControlRepository(string Owner, string Name);

/// <summary>
/// Summarizes one synced repository issue from the project backlog.
/// </summary>
/// <param name="TaskId">The backlog task that holds the issue.</param>
/// <param name="TaskState">The backlog execution state: <c>backlog</c>, <c>ready</c>, or <c>claimed</c>.</param>
/// <param name="ProviderId">The source control provider that owns the issue.</param>
/// <param name="Repository">The repository that contains the issue.</param>
/// <param name="Number">The provider issue number.</param>
/// <param name="Title">The stored issue title.</param>
/// <param name="State">The stored issue state: <c>open</c> or <c>closed</c>.</param>
/// <param name="CreatedAt">When the backlog task was created.</param>
/// <param name="UpdatedAt">When the backlog task last changed.</param>
public sealed record ProjectIssueSummary(
    string TaskId,
    string TaskState,
    string ProviderId,
    ProjectSourceControlRepository Repository,
    long Number,
    string Title,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Holds one page of synced project issues and the opaque cursor for the next page.
/// </summary>
public sealed record ProjectIssuePage(
    string ProjectId,
    ImmutableArray<ProjectIssueSummary> Items,
    string? NextCursor);

/// <summary>
/// Reads keyset-paged issue summaries that periodic issue sync stored in the project backlog.
/// </summary>
/// <remarks>
/// This read makes no source control provider call. It returns only backlog tasks that link a
/// repository issue in a project with an issue sync configuration for the caller's tenant.
/// Archived tasks are not returned. The backlog doesn't store an issue URL or author, so the
/// summary doesn't include them.
/// </remarks>
public sealed class ProjectIssueReadStore
{
    private const int CursorVersion = 1;
    private const string CursorOrder = "number-desc,taskId-desc";

    /// <summary>The accepted <c>state</c> values. The default is <c>open</c>.</summary>
    public static readonly ImmutableArray<string> StateFilters = ["open", "closed", "all"];

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _tasks;
    private readonly string _syncProjects;

    /// <summary>
    /// Creates a reader over the coordination owner schema.
    /// </summary>
    public ProjectIssueReadStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _tasks = $"\"{schema}\".backlog_tasks";
        _syncProjects = $"\"{schema}\".issue_sync_projects";
    }

    /// <summary>
    /// Parses the issue state filter.
    /// </summary>
    /// <param name="value"><c>open</c>, <c>closed</c>, or <c>all</c>. Null means <c>open</c>.</param>
    /// <returns>The canonical filter value.</returns>
    /// <exception cref="CoordinationException">The value is not an accepted filter.</exception>
    public static string ParseStateFilter(string? value)
    {
        // A missing filter means open issues, the usual picker case.
        if (value is null)
            return "open";
        // Matching is exact and case-sensitive, so the cursor can compare the stored filter directly.
        if (!StateFilters.Contains(value))
            throw new CoordinationException("issue_state_filter_invalid", StatusCodes.Status400BadRequest);
        return value;
    }

    /// <summary>
    /// Reads one page of synced issues for a project, highest issue number first.
    /// </summary>
    /// <param name="tenantId">The caller's tenant from Projects authorization. Issue sync in another tenant is invisible.</param>
    /// <param name="projectId">The project to list.</param>
    /// <param name="state">The canonical filter from <see cref="ParseStateFilter"/>.</param>
    /// <param name="cursor">The opaque cursor from a previous page, or null for the first page.</param>
    /// <param name="pageSize">The page size, from 1 to <see cref="ProjectRunReadStore.MaximumPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <remarks>
    /// Order is issue number descending, then task ID descending. Both keys are stable, so a later
    /// sync doesn't move rows between pages. Pages are not a snapshot.
    /// </remarks>
    /// <exception cref="CoordinationException">The filter, page size, or cursor is invalid for this query.</exception>
    public async Task<ProjectIssuePage> ListAsync(
        string tenantId,
        string projectId,
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

        // The join with issue_sync_projects limits rows to projects that have issue sync in the
        // caller's tenant. Rows without an issue link and archived tasks are skipped. The keyset
        // condition compares (number, task) as a tuple, so it matches the ORDER BY exactly and
        // no row is skipped or repeated between pages.

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT t.task_id, t.task_state, t.issue_provider_id, t.issue_repository_owner,
                t.issue_repository_name, t.issue_number, t.title, t.lifecycle, t.created_at, t.updated_at
            FROM {_tasks} AS t
            JOIN {_syncProjects} AS s ON s.project_id = t.project_id
            WHERE t.project_id = @project AND s.tenant_id = @tenant
                AND t.issue_provider_id IS NOT NULL AND NOT t.is_archived
                AND (@lifecycle::varchar IS NULL OR t.lifecycle = @lifecycle)
                AND (@afterNumber::bigint IS NULL
                    OR (t.issue_number, t.task_id) < (@afterNumber, @afterTask::varchar))
            ORDER BY t.issue_number DESC, t.task_id DESC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        // The filter value matches the stored lifecycle column. "all" is passed as NULL to turn the filter off.
        command.Parameters.AddWithValue(
            "lifecycle", NpgsqlDbType.Varchar, state == "all" ? DBNull.Value : state);
        command.Parameters.AddWithValue(
            "afterNumber", NpgsqlDbType.Bigint, (object?)after?.IssueNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("afterTask", NpgsqlDbType.Varchar, (object?)after?.TaskId ?? DBNull.Value);
        // Read one extra row. If it exists there is a next page; it is not returned.
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var items = ImmutableArray.CreateBuilder<ProjectIssueSummary>(pageSize);
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
                items.Add(new ProjectIssueSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    new ProjectSourceControlRepository(reader.GetString(3), reader.GetString(4)),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9)));
            }
        }
        // The next cursor points after the last returned row and records the query shape,
        // so it can't be replayed against a different filter or page size.
        var last = items.Count > 0 ? items[^1] : null;
        return new ProjectIssuePage(
            projectId,
            items.ToImmutable(),
            hasMore && last is not null
                ? ProjectListCursor.Encode(new IssueListCursor(
                    CursorVersion, CursorOrder, projectId, pageSize, state, last.Number, last.TaskId))
                : null);
    }

    /// <summary>
    /// Decodes a cursor and rejects it unless it was issued for the same project, page size, and state filter.
    /// </summary>
    private static IssueListCursor DecodeCursor(string projectId, int pageSize, string state, string value)
    {
        var cursor = ProjectListCursor.Decode<IssueListCursor>(value);
        // Reject cursors from another version, ordering, project, page size, or filter, and
        // reject key values that no stored row can have.
        if (cursor.Version != CursorVersion ||
            cursor.Order != CursorOrder ||
            cursor.ProjectId != projectId ||
            cursor.PageSize != pageSize ||
            cursor.State != state ||
            cursor.IssueNumber < 1 ||
            string.IsNullOrEmpty(cursor.TaskId) ||
            cursor.TaskId.Length > 256)
            throw ProjectListCursor.Invalid();
        return cursor;
    }

    /// <summary>
    /// The decoded keyset position and the query shape that issued it.
    /// </summary>
    private sealed record IssueListCursor(
        int Version,
        string Order,
        string ProjectId,
        int PageSize,
        string State,
        long IssueNumber,
        string TaskId);
}
