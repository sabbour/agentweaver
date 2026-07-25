using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Backlog;

public sealed record PromotedStoryInput(
    string Key,
    string Title,
    string Description,
    string PromotionReason,
    IReadOnlyList<string> DependsOnKeys);

public sealed record BacklogPromotionResult(
    IReadOnlyList<BacklogTask> Tasks,
    int CreatedCount);

public interface IBacklogPromotionService
{
    Task<BacklogPromotionResult> PromoteAsync(
        ProjectId projectId,
        RunId parentPrdRunId,
        string capturedBy,
        IReadOnlyList<PromotedStoryInput> stories,
        CancellationToken ct = default);
}

public sealed class InvalidPromotionGraphException(string message) : InvalidOperationException(message);
public sealed class PromotionKeyConflictException(string message) : InvalidOperationException(message);

public sealed class BacklogPromotionService : IBacklogPromotionService
{
    private readonly string _provider;
    private readonly SqliteDb? _sqliteDb;
    private readonly IDbContextFactory<MemoryDbContext>? _dbFactory;
    private readonly IRunStore _runStore;

    public BacklogPromotionService(
        IConfiguration configuration,
        IRunStore runStore,
        SqliteDb? sqliteDb = null,
        IDbContextFactory<MemoryDbContext>? dbFactory = null)
    {
        _provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        _sqliteDb = sqliteDb;
        _dbFactory = dbFactory;
        _runStore = runStore;
    }

    public async Task<BacklogPromotionResult> PromoteAsync(
        ProjectId projectId,
        RunId parentPrdRunId,
        string capturedBy,
        IReadOnlyList<PromotedStoryInput> stories,
        CancellationToken ct = default)
    {
        await ValidateParentRunAsync(projectId, parentPrdRunId, ct).ConfigureAwait(false);
        ValidateStories(stories);

        return _provider is "postgres" or "postgresql"
            ? await PromotePostgresAsync(projectId, parentPrdRunId, capturedBy, stories, ct).ConfigureAwait(false)
            : await PromoteSqliteAsync(projectId, parentPrdRunId, capturedBy, stories, ct).ConfigureAwait(false);
    }

    private async Task ValidateParentRunAsync(ProjectId projectId, RunId parentPrdRunId, CancellationToken ct)
    {
        var parent = await _runStore.GetAsync(parentPrdRunId, ct).ConfigureAwait(false);
        if (parent is null
            || parent.ProjectId != projectId
            || parent.ParentRunId is not null
            || !string.Equals(parent.AgentName, "Coordinator", StringComparison.Ordinal))
            throw new KeyNotFoundException("parent_prd_run_not_found");
    }

    private static void ValidateStories(IReadOnlyList<PromotedStoryInput> stories)
    {
        if (stories.Count is < 1 or > 50)
            throw new InvalidPromotionGraphException("invalid_promotion_graph");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, PromotedStoryInput>(StringComparer.Ordinal);
        foreach (var story in stories)
        {
            if (string.IsNullOrWhiteSpace(story.Key)
                || string.IsNullOrWhiteSpace(story.Title)
                || string.IsNullOrWhiteSpace(story.Description)
                || string.IsNullOrWhiteSpace(story.PromotionReason)
                || story.PromotionReason.Length > 500)
                throw new InvalidPromotionGraphException("invalid_promotion_graph");

            if (!keys.Add(story.Key.Trim()) || !titles.Add(story.Title.Trim()))
                throw new InvalidPromotionGraphException("invalid_promotion_graph");

            byKey.Add(story.Key.Trim(), story with
            {
                DependsOnKeys = story.DependsOnKeys.Select(k => k.Trim()).ToList()
            });
        }

        foreach (var story in byKey.Values)
        {
            foreach (var dependencyKey in story.DependsOnKeys)
            {
                if (!byKey.ContainsKey(dependencyKey) || string.Equals(dependencyKey, story.Key, StringComparison.Ordinal))
                    throw new InvalidPromotionGraphException("invalid_promotion_graph");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string key)
        {
            if (!visiting.Add(key))
                return false;
            if (visited.Contains(key))
                return true;

            foreach (var dependencyKey in byKey[key].DependsOnKeys.Distinct(StringComparer.Ordinal))
                if (!visited.Contains(dependencyKey) && !Visit(dependencyKey))
                    return false;

            visiting.Remove(key);
            visited.Add(key);
            return true;
        }

        foreach (var key in byKey.Keys)
            if (!visited.Contains(key) && !Visit(key))
                throw new InvalidPromotionGraphException("invalid_promotion_graph");
    }

    private async Task<BacklogPromotionResult> PromoteSqliteAsync(
        ProjectId projectId,
        RunId parentPrdRunId,
        string capturedBy,
        IReadOnlyList<PromotedStoryInput> stories,
        CancellationToken ct)
    {
        if (_sqliteDb is null)
            throw new InvalidOperationException("SQLite promotion requested without SqliteDb.");

        await using var connection = await _sqliteDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();

        var existing = await ReadExistingSqliteAsync(connection, tx, projectId, parentPrdRunId, stories.Select(s => s.Key).ToList(), ct).ConfigureAwait(false);
        if (existing.Count > 0)
            return await ValidateExistingSqliteAsync(connection, tx, projectId, parentPrdRunId, stories, existing, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var lastKey = await ReadLastBacklogOrderKeySqliteAsync(connection, tx, projectId, ct).ConfigureAwait(false);
        var created = new List<BacklogTask>(stories.Count);
        var keyToTask = new Dictionary<string, BacklogTask>(StringComparer.Ordinal);
        foreach (var story in stories)
        {
            var orderKey = OrderKey.Between(lastKey, null);
            var task = new BacklogTask
            {
                Id = BacklogTaskId.New(),
                ProjectId = projectId,
                Title = story.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(story.Description) ? null : story.Description.Trim(),
                State = BacklogTaskState.Backlog,
                OrderKey = orderKey,
                CapturedBy = capturedBy,
                CreatedAt = now,
                ParentPrdRunId = parentPrdRunId,
                PromotionKey = story.Key.Trim(),
                PromotionReason = story.PromotionReason.Trim(),
            };
            await InsertSqliteTaskAsync(connection, tx, task, ct).ConfigureAwait(false);
            created.Add(task);
            keyToTask.Add(task.PromotionKey!, task);
            lastKey = orderKey;
        }

        foreach (var story in stories)
        {
            var task = keyToTask[story.Key.Trim()];
            foreach (var dependencyKey in story.DependsOnKeys.Distinct(StringComparer.Ordinal))
                await InsertSqliteDependencyAsync(connection, tx, projectId, task.Id, keyToTask[dependencyKey].Id, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new BacklogPromotionResult(created, created.Count);
    }

    private async Task<BacklogPromotionResult> PromotePostgresAsync(
        ProjectId projectId,
        RunId parentPrdRunId,
        string capturedBy,
        IReadOnlyList<PromotedStoryInput> stories,
        CancellationToken ct)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Postgres promotion requested without MemoryDbContext factory.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var parentRunId = parentPrdRunId.ToString();
        var keys = stories.Select(s => s.Key.Trim()).ToList();
        var existing = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId.ToString()
                && t.ParentPrdRunId == parentRunId
                && t.PromotionKey != null
                && keys.Contains(t.PromotionKey))
            .ToListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0)
            return await ValidateExistingPostgresAsync(db, projectId, parentPrdRunId, stories, existing, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var lastKey = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId.ToString() && t.State == "backlog" && t.ArchivedAt == null)
            .OrderByDescending(t => t.OrderKey)
            .Select(t => (string?)t.OrderKey)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var created = new List<BacklogTask>(stories.Count);
        var keyToTaskId = new Dictionary<string, BacklogTaskId>(StringComparer.Ordinal);
        foreach (var story in stories)
        {
            var orderKey = OrderKey.Between(lastKey, null);
            var task = new BacklogTask
            {
                Id = BacklogTaskId.New(),
                ProjectId = projectId,
                Title = story.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(story.Description) ? null : story.Description.Trim(),
                State = BacklogTaskState.Backlog,
                OrderKey = orderKey,
                CapturedBy = capturedBy,
                CreatedAt = now,
                ParentPrdRunId = parentPrdRunId,
                PromotionKey = story.Key.Trim(),
                PromotionReason = story.PromotionReason.Trim(),
            };
            db.BacklogTasks.Add(new BacklogTaskRecord
            {
                TaskId = task.Id.ToString(),
                ProjectId = task.ProjectId.ToString(),
                Title = task.Title,
                Description = task.Description,
                State = task.State.ToApiString(),
                OrderKey = task.OrderKey,
                CapturedBy = task.CapturedBy,
                CreatedAt = task.CreatedAt,
                CommittedAt = task.CommittedAt,
                ClaimedAt = task.ClaimedAt,
                RunId = task.RunId?.ToString(),
                WorkflowOverrideId = task.WorkflowOverrideId,
                ArchivedAt = task.ArchivedAt,
                SourceFilePath = task.SourceFilePath,
                ParentPrdRunId = parentRunId,
                PromotionKey = task.PromotionKey,
                PromotionReason = task.PromotionReason,
            });
            created.Add(task);
            keyToTaskId.Add(task.PromotionKey!, task.Id);
            lastKey = orderKey;
        }

        // Persist the tasks first, in their own SaveChangesAsync call. The dependency
        // junction rows below reference these tasks via two separate FKs to the same
        // self-referencing table (TaskId, DependsOnTaskId); EF Core/Npgsql's batched
        // command ordering does not reliably guarantee the parent-task INSERTs are
        // sent before the dependency INSERTs when both are tracked in a single
        // SaveChangesAsync call, which can trip the
        // FK_backlog_task_dependencies_backlog_tasks_depends_on_task_id constraint.
        // Splitting into two saves (still inside the same transaction) guarantees the
        // correct order.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var story in stories)
        {
            var taskId = keyToTaskId[story.Key.Trim()];
            foreach (var dependencyKey in story.DependsOnKeys.Distinct(StringComparer.Ordinal))
            {
                db.BacklogTaskDependencies.Add(new BacklogTaskDependencyRecord
                {
                    ProjectId = projectId.ToString(),
                    TaskId = taskId.ToString(),
                    DependsOnTaskId = keyToTaskId[dependencyKey].ToString(),
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new BacklogPromotionResult(created, created.Count);
    }

    private static async Task<Dictionary<string, BacklogTask>> ReadExistingSqliteAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ProjectId projectId,
        RunId parentPrdRunId,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BacklogTask>(StringComparer.Ordinal);
        if (keys.Count == 0)
            return results;

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        var placeholders = new List<string>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var param = $"$key{i}";
            placeholders.Add(param);
            command.Parameters.AddWithValue(param, keys[i]);
        }

        command.CommandText =
            $"""
            SELECT task_id, project_id, title, description, state, order_key,
                   captured_by, created_at, committed_at, claimed_at, run_id,
                   workflow_override_id, archived_at, source_file_path,
                   parent_prd_run_id, promotion_key, promotion_reason
              FROM backlog_tasks
             WHERE project_id = $projectId
               AND parent_prd_run_id = $parentPrdRunId
               AND promotion_key IN ({string.Join(", ", placeholders)});
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$parentPrdRunId", parentPrdRunId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var task = SqliteTask(reader);
            if (task.PromotionKey is not null)
                results[task.PromotionKey] = task;
        }

        return results;
    }

    private static async Task<BacklogPromotionResult> ValidateExistingSqliteAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ProjectId projectId,
        RunId parentPrdRunId,
        IReadOnlyList<PromotedStoryInput> stories,
        Dictionary<string, BacklogTask> existing,
        CancellationToken ct)
    {
        if (existing.Count != stories.Count)
            throw new PromotionKeyConflictException("promotion_key_conflict");

        var dependencyMap = await ReadExistingDependencyKeysSqliteAsync(connection, tx, projectId, existing.Values.Select(t => t.Id).ToList(), ct).ConfigureAwait(false);
        ValidateExistingStories(stories, existing, dependencyMap, parentPrdRunId);
        return new BacklogPromotionResult(stories.Select(story => existing[story.Key.Trim()]).ToList(), 0);
    }

    private static async Task<BacklogPromotionResult> ValidateExistingPostgresAsync(
        MemoryDbContext db,
        ProjectId projectId,
        RunId parentPrdRunId,
        IReadOnlyList<PromotedStoryInput> stories,
        List<BacklogTaskRecord> existingRecords,
        CancellationToken ct)
    {
        if (existingRecords.Count != stories.Count)
            throw new PromotionKeyConflictException("promotion_key_conflict");

        var existing = existingRecords
            .Select(record => new BacklogTask
            {
                Id = BacklogTaskId.Parse(record.TaskId),
                ProjectId = ProjectId.Parse(record.ProjectId),
                Title = record.Title,
                Description = record.Description,
                State = BacklogTaskStateExtensions.ParseState(record.State),
                OrderKey = record.OrderKey,
                CapturedBy = record.CapturedBy,
                CreatedAt = record.CreatedAt,
                CommittedAt = record.CommittedAt,
                ClaimedAt = record.ClaimedAt,
                RunId = record.RunId is null ? null : RunId.Parse(record.RunId),
                WorkflowOverrideId = record.WorkflowOverrideId,
                ArchivedAt = record.ArchivedAt,
                SourceFilePath = record.SourceFilePath,
                ParentPrdRunId = record.ParentPrdRunId is null ? null : RunId.Parse(record.ParentPrdRunId),
                PromotionKey = record.PromotionKey,
                PromotionReason = record.PromotionReason,
            })
            .ToDictionary(t => t.PromotionKey!, StringComparer.Ordinal);

        var taskIds = existing.Values.Select(t => t.Id).ToList();
        var edges = await db.BacklogTaskDependencies.AsNoTracking()
            .Where(d => d.ProjectId == projectId.ToString() && taskIds.Select(x => x.ToString()).Contains(d.TaskId))
            .ToListAsync(ct).ConfigureAwait(false);
        var reverse = existing.Values.ToDictionary(t => t.Id, t => t.PromotionKey!, EqualityComparer<BacklogTaskId>.Default);
        var dependencyMap = edges
            .GroupBy(e => BacklogTaskId.Parse(e.TaskId))
            .ToDictionary(
                g => reverse[g.Key],
                g => g.Select(edge => reverse[BacklogTaskId.Parse(edge.DependsOnTaskId)]).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        ValidateExistingStories(stories, existing, dependencyMap, parentPrdRunId);
        return new BacklogPromotionResult(stories.Select(story => existing[story.Key.Trim()]).ToList(), 0);
    }

    private static void ValidateExistingStories(
        IReadOnlyList<PromotedStoryInput> stories,
        IReadOnlyDictionary<string, BacklogTask> existing,
        IReadOnlyDictionary<string, List<string>> dependencyMap,
        RunId parentPrdRunId)
    {
        foreach (var story in stories)
        {
            if (!existing.TryGetValue(story.Key.Trim(), out var task)
                || task.ParentPrdRunId != parentPrdRunId
                || !string.Equals(task.Title, story.Title.Trim(), StringComparison.Ordinal)
                || !string.Equals(task.Description ?? string.Empty, story.Description.Trim(), StringComparison.Ordinal)
                || !string.Equals(task.PromotionReason ?? string.Empty, story.PromotionReason.Trim(), StringComparison.Ordinal))
                throw new PromotionKeyConflictException("promotion_key_conflict");

            var existingDeps = dependencyMap.TryGetValue(story.Key.Trim(), out var deps)
                ? deps
                : [];
            var requested = story.DependsOnKeys.Select(k => k.Trim()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (!existingDeps.SequenceEqual(requested, StringComparer.Ordinal))
                throw new PromotionKeyConflictException("promotion_key_conflict");
        }
    }

    private static async Task<Dictionary<string, List<string>>> ReadExistingDependencyKeysSqliteAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ProjectId projectId,
        IReadOnlyList<BacklogTaskId> taskIds,
        CancellationToken ct)
    {
        var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (taskIds.Count == 0)
            return results;

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        var placeholders = new List<string>(taskIds.Count);
        for (var i = 0; i < taskIds.Count; i++)
        {
            var param = $"$taskId{i}";
            placeholders.Add(param);
            command.Parameters.AddWithValue(param, taskIds[i].ToString());
        }

        command.CommandText =
            $"""
            SELECT child.promotion_key, prerequisite.promotion_key
              FROM backlog_task_dependencies d
              JOIN backlog_tasks child ON child.task_id = d.task_id
              JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
             WHERE d.project_id = $projectId
               AND d.task_id IN ({string.Join(", ", placeholders)})
             ORDER BY child.promotion_key, prerequisite.promotion_key;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            if (!results.TryGetValue(key, out var list))
            {
                list = [];
                results[key] = list;
            }
            list.Add(reader.GetString(1));
        }

        return results;
    }

    private static async Task<string?> ReadLastBacklogOrderKeySqliteAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ProjectId projectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT order_key
              FROM backlog_tasks
             WHERE project_id = $projectId
               AND state = 'backlog'
               AND archived_at IS NULL
             ORDER BY order_key DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static async Task InsertSqliteTaskAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        BacklogTask task,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            INSERT INTO backlog_tasks (
                task_id, project_id, title, description, state, order_key,
                captured_by, created_at, committed_at, claimed_at, run_id,
                workflow_override_id, archived_at, source_file_path,
                parent_prd_run_id, promotion_key, promotion_reason)
            VALUES (
                $taskId, $projectId, $title, $description, $state, $orderKey,
                $capturedBy, $createdAt, NULL, NULL, NULL,
                NULL, NULL, NULL,
                $parentPrdRunId, $promotionKey, $promotionReason);
            """;
        command.Parameters.AddWithValue("$taskId", task.Id.ToString());
        command.Parameters.AddWithValue("$projectId", task.ProjectId.ToString());
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", (object?)task.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", task.State.ToApiString());
        command.Parameters.AddWithValue("$orderKey", task.OrderKey);
        command.Parameters.AddWithValue("$capturedBy", task.CapturedBy);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$parentPrdRunId", task.ParentPrdRunId!.Value.ToString());
        command.Parameters.AddWithValue("$promotionKey", task.PromotionKey!);
        command.Parameters.AddWithValue("$promotionReason", task.PromotionReason!);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertSqliteDependencyAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ProjectId projectId,
        BacklogTaskId taskId,
        BacklogTaskId dependsOnTaskId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            INSERT INTO backlog_task_dependencies (project_id, task_id, depends_on_task_id, created_at)
            VALUES ($projectId, $taskId, $dependsOnTaskId, $createdAt);
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$taskId", taskId.ToString());
        command.Parameters.AddWithValue("$dependsOnTaskId", dependsOnTaskId.ToString());
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static BacklogTask SqliteTask(SqliteDataReader reader) => new()
    {
        Id = BacklogTaskId.Parse(reader.GetString(0)),
        ProjectId = ProjectId.Parse(reader.GetString(1)),
        Title = reader.GetString(2),
        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
        State = BacklogTaskStateExtensions.ParseState(reader.GetString(4)),
        OrderKey = reader.GetString(5),
        CapturedBy = reader.GetString(6),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
        CommittedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        ClaimedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
        RunId = reader.IsDBNull(10) ? null : RunId.Parse(reader.GetString(10)),
        WorkflowOverrideId = reader.IsDBNull(11) ? null : reader.GetString(11),
        ArchivedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
        SourceFilePath = reader.IsDBNull(13) ? null : reader.GetString(13),
        ParentPrdRunId = reader.IsDBNull(14) ? null : RunId.Parse(reader.GetString(14)),
        PromotionKey = reader.IsDBNull(15) ? null : reader.GetString(15),
        PromotionReason = reader.IsDBNull(16) ? null : reader.GetString(16),
    };
}
