using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class P1RuntimeRoleBacklogPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "orchestrator";
    private const string RuntimeRole = "agentweaver-id-orchestrator";
    private readonly string runtimeApplicationName = "runtime-backlog-" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource? runtimeDataSource;
    private Task[] outstandingWrites = [];
    private BacklogOwnerStore store = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = await fixture.DataSource.OpenConnectionAsync())
        await using (var setup = new NpgsqlCommand($"""
            DROP SCHEMA IF EXISTS "{Schema}" CASCADE;
            DROP ROLE IF EXISTS "{RuntimeRole}";
            CREATE ROLE "{RuntimeRole}" NOLOGIN;
            GRANT "{RuntimeRole}" TO CURRENT_USER;
            """, admin))
            await setup.ExecuteNonQueryAsync();

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, Schema);
        var plan = PlannedRuntimeGrants.Load(
            "orchestrator", Schema, RuntimeRole, EmbeddedPlanHash(), CoordinationOwnerMigrator.SupportedVersions);
        await using (var admin = await fixture.DataSource.OpenConnectionAsync())
            await PlannedRuntimeGrants.ApplyAndVerifyAsync(admin, plan);

        var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        runtimeConnection.Options = string.IsNullOrWhiteSpace(runtimeConnection.Options)
            ? $"-c role={RuntimeRole}"
            : $"{runtimeConnection.Options} -c role={RuntimeRole}";
        runtimeConnection.ApplicationName = runtimeApplicationName;
        runtimeConnection.CommandTimeout = 30;
        runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection.ConnectionString);
        store = new BacklogOwnerStore(runtimeDataSource, Schema);
    }

    public async Task DisposeAsync()
    {
        if (outstandingWrites.Any(write => !write.IsCompleted))
            throw new InvalidOperationException(
                "Refusing to dispose the runtime pool or drop the PostgreSQL role while backlog writes are still running.");

        Exception? lateWriteFailure = null;
        if (outstandingWrites.Length > 0)
        {
            try
            {
                await Task.WhenAll(outstandingWrites);
            }
            catch (Exception exception)
            {
                lateWriteFailure = exception;
            }
        }

        try
        {
            if (runtimeDataSource is not null)
                await runtimeDataSource.DisposeAsync();
        }
        finally
        {
            await using var admin = await fixture.DataSource.OpenConnectionAsync();
            await using (var dropSchema = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", admin))
                await dropSchema.ExecuteNonQueryAsync();
            await using var roleExists = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)", admin);
            roleExists.Parameters.AddWithValue("role", RuntimeRole);
            if ((bool)(await roleExists.ExecuteScalarAsync())!)
            {
                await using var cleanup = new NpgsqlCommand($"""
                    REVOKE "{RuntimeRole}" FROM CURRENT_USER;
                    DROP ROLE "{RuntimeRole}";
                    """, admin);
                await cleanup.ExecuteNonQueryAsync();
            }
        }

        if (lateWriteFailure is not null)
            throw new InvalidOperationException("A backlog write failed while the test fixture was draining it.", lateWriteFailure);
    }

    [Fact]
    public async Task CanonicalRuntimeRoleCapturesEditsDependenciesAndDeletesWithoutDependencyUpdate()
    {
        await AssertRuntimeIdentityAndDependencyUpdateDeniedAsync();

        string project = "runtime-backlog-" + Guid.NewGuid().ToString("N");
        var first = new BacklogTaskReference(project, "first");
        var eligible = new BacklogTaskReference(project, "eligible");
        var graph = (await store.AddTaskAsync(
            first, new BacklogTaskContent("First"), 0, CancellationToken.None)).Value!;
        graph = (await store.AddTaskAsync(
            eligible, new BacklogTaskContent("Eligible"), graph.Revision, CancellationToken.None)).Value!;

        graph = (await store.SetTaskContentAsync(
            first, new BacklogTaskContent("Edited"), graph.Revision,
            graph.Tasks.Single(task => task.Reference == first).Revision,
            CancellationToken.None)).Value!;
        Assert.Equal(3, graph.Revision);
        Assert.Equal(1, graph.Tasks.Single(task => task.Reference == first).Revision);

        graph = (await store.AddDependencyAsync(
            first, eligible, graph.Revision, CancellationToken.None)).Value!;
        Assert.Equal(4, graph.Revision);
        Assert.Single(graph.Dependencies);

        graph = (await store.RemoveDependencyAsync(
            first, eligible, graph.Revision, CancellationToken.None)).Value!;
        Assert.Equal(5, graph.Revision);
        Assert.Empty(graph.Dependencies);

        graph = (await store.DeleteTaskAsync(
            eligible, graph.Revision,
            graph.Tasks.Single(task => task.Reference == eligible).Revision,
            CancellationToken.None)).Value!;
        Assert.Equal(6, graph.Revision);

        var persisted = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(6, persisted.Revision);
        Assert.Equal("Edited", Assert.Single(persisted.Tasks).Title);
        Assert.Equal(1, persisted.Tasks[0].Revision);
        Assert.Empty(persisted.Dependencies);
    }

    [Fact]
    public async Task ConcurrentRuntimeCapturesBlockOnProjectMutexAndRejectTheStaleRevision()
    {
        string project = "runtime-capture-" + Guid.NewGuid().ToString("N");
        await CreateEmptyProjectAsync(project);
        var firstTask = new BacklogTaskReference(project, "first");
        var secondTask = new BacklogTaskReference(project, "second");
        var enteredAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int authorityChecks = 0;
        Task HoldAfterProjectLock(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref authorityChecks) == 1)
            {
                enteredAuthority.TrySetResult(true);
                return releaseAuthority.Task.WaitAsync(cancellationToken);
            }
            return Task.CompletedTask;
        }

        Task<BacklogCoreResult<BacklogDependencyGraph>>? firstWrite = null;
        Task<BacklogCoreResult<BacklogDependencyGraph>>? secondWrite = null;
        Exception? failure = null;
        try
        {
            firstWrite = store.AddTaskAsync(
                firstTask, new BacklogTaskContent("First"), 0, writeCancellation.Token, HoldAfterProjectLock);
            await enteredAuthority.Task.WaitAsync(TimeSpan.FromSeconds(10), writeCancellation.Token);
            secondWrite = store.AddTaskAsync(
                secondTask, new BacklogTaskContent("Second"), 0, writeCancellation.Token);
            Assert.True(
                await WaitForRuntimeLockWaitAsync(
                    "%backlog_projects%FOR UPDATE%", TimeSpan.FromSeconds(10)),
                "The competing runtime capture must be blocked by PostgreSQL on the project row.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            releaseAuthority.TrySetResult(true);
        }

        if (failure is not null)
            writeCancellation.Cancel();
        BacklogCoreResult<BacklogDependencyGraph>[]? results = null;
        try
        {
            results = await FinishStartedWritesAsync(writeCancellation, firstWrite, secondWrite);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        Assert.NotNull(results);
        Assert.Single(results, result => result.IsSuccess);
        var stale = Assert.Single(results, result => !result.IsSuccess);
        Assert.Contains(stale.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);

        var persisted = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(firstTask, Assert.Single(persisted.Tasks).Reference);
    }

    [Fact]
    public async Task ConcurrentFirstRuntimeCapturesSerializeInitializationOfAnAbsentProject()
    {
        await AssertRuntimeIdentityAndDependencyUpdateDeniedAsync();

        string project = "runtime-first-capture-" + Guid.NewGuid().ToString("N");
        var initial = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(0, initial.Revision);
        Assert.Empty(initial.Tasks);
        await AssertProjectAbsentAsync(project);

        var firstTask = new BacklogTaskReference(project, "first");
        var secondTask = new BacklogTaskReference(project, "second");
        var enteredAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int authorityChecks = 0;
        Task HoldAfterProjectLock(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref authorityChecks) == 1)
            {
                enteredAuthority.TrySetResult(true);
                return releaseAuthority.Task.WaitAsync(cancellationToken);
            }
            return Task.CompletedTask;
        }

        Task<BacklogCoreResult<BacklogDependencyGraph>>? firstWrite = null;
        Task<BacklogCoreResult<BacklogDependencyGraph>>? secondWrite = null;
        Exception? failure = null;
        try
        {
            firstWrite = store.AddTaskAsync(
                firstTask, new BacklogTaskContent("First"), 0, writeCancellation.Token, HoldAfterProjectLock);
            await enteredAuthority.Task.WaitAsync(TimeSpan.FromSeconds(10), writeCancellation.Token);
            secondWrite = store.AddTaskAsync(
                secondTask, new BacklogTaskContent("Second"), 0, writeCancellation.Token);
            Assert.True(
                await WaitForRuntimeLockWaitAsync(
                    "%INSERT INTO \"orchestrator\".backlog_projects%",
                    TimeSpan.FromSeconds(10)),
                "The competing first capture must block on EnsureProjectAsync's INSERT against the uncommitted project row.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            releaseAuthority.TrySetResult(true);
        }

        if (failure is not null)
            writeCancellation.Cancel();
        BacklogCoreResult<BacklogDependencyGraph>[]? results = null;
        try
        {
            results = await FinishStartedWritesAsync(writeCancellation, firstWrite, secondWrite);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        Assert.NotNull(results);
        Assert.Single(results, result => result.IsSuccess);
        var stale = Assert.Single(results, result => !result.IsSuccess);
        Assert.Contains(stale.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);

        var persisted = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(firstTask, Assert.Single(persisted.Tasks).Reference);
        Assert.Empty(persisted.Dependencies);
    }

    [Fact]
    public async Task CancellingBlockedRuntimeCapturesDrainsBothWritesBeforeTeardown()
    {
        string project = "runtime-capture-cancel-" + Guid.NewGuid().ToString("N");
        await CreateEmptyProjectAsync(project);
        var firstTask = new BacklogTaskReference(project, "first");
        var secondTask = new BacklogTaskReference(project, "second");
        var enteredAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int authorityChecks = 0;
        Task HoldAfterProjectLock(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref authorityChecks) == 1)
            {
                enteredAuthority.TrySetResult(true);
                return releaseAuthority.Task.WaitAsync(cancellationToken);
            }
            return Task.CompletedTask;
        }

        Task<BacklogCoreResult<BacklogDependencyGraph>>? firstWrite = null;
        Task<BacklogCoreResult<BacklogDependencyGraph>>? secondWrite = null;
        Exception? setupFailure = null;
        try
        {
            firstWrite = store.AddTaskAsync(
                firstTask, new BacklogTaskContent("First"), 0, writeCancellation.Token, HoldAfterProjectLock);
            await enteredAuthority.Task.WaitAsync(TimeSpan.FromSeconds(10), writeCancellation.Token);
            secondWrite = store.AddTaskAsync(
                secondTask, new BacklogTaskContent("Second"), 0, writeCancellation.Token);
            Assert.True(
                await WaitForRuntimeLockWaitAsync(
                    "%backlog_projects%FOR UPDATE%", TimeSpan.FromSeconds(10)),
                "The second runtime capture must be blocked on the project row before cancellation.");
            writeCancellation.Cancel();
        }
        catch (Exception exception)
        {
            setupFailure = exception;
        }
        finally
        {
            releaseAuthority.TrySetResult(true);
            writeCancellation.Cancel();
        }

        Exception? drainFailure = await Record.ExceptionAsync(
            () => FinishStartedWritesAsync(writeCancellation, firstWrite, secondWrite));
        if (setupFailure is not null)
        {
            if (drainFailure is not null)
                throw new AggregateException(setupFailure, drainFailure);
            ExceptionDispatchInfo.Capture(setupFailure).Throw();
        }

        Assert.IsAssignableFrom<OperationCanceledException>(drainFailure);
        Assert.All(
            new Task?[] { firstWrite, secondWrite }.OfType<Task>(),
            write => Assert.True(write.IsCompleted, "Cancellation must be observed before fixture teardown."));
        Assert.DoesNotContain(
            new Task?[] { firstWrite, secondWrite }.OfType<Task>(),
            write => write.IsCompletedSuccessfully);

        var persisted = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(0, persisted.Revision);
        Assert.Empty(persisted.Tasks);
        Assert.Empty(persisted.Dependencies);
    }

    [Fact]
    public async Task ConcurrentDependencyAdditionAndTaskDeletionPreserveTheCommittedEdge()
    {
        string project = "runtime-edge-delete-" + Guid.NewGuid().ToString("N");
        var task = new BacklogTaskReference(project, "task");
        var prerequisite = new BacklogTaskReference(project, "prerequisite");
        var eligible = new BacklogTaskReference(project, "eligible");
        var graph = (await store.AddTaskAsync(task, 0, CancellationToken.None)).Value!;
        graph = (await store.AddTaskAsync(prerequisite, graph.Revision, CancellationToken.None)).Value!;
        graph = (await store.AddTaskAsync(eligible, graph.Revision, CancellationToken.None)).Value!;

        var enteredAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int authorityChecks = 0;
        Task HoldAfterProjectLock(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref authorityChecks) == 1)
            {
                enteredAuthority.TrySetResult(true);
                return releaseAuthority.Task.WaitAsync(cancellationToken);
            }
            return Task.CompletedTask;
        }

        Task<BacklogCoreResult<BacklogDependencyGraph>>? addEdge = null;
        Task<BacklogCoreResult<BacklogDependencyGraph>>? deleteTask = null;
        Exception? failure = null;
        try
        {
            addEdge = store.AddDependencyAsync(
                task, prerequisite, graph.Revision, writeCancellation.Token, HoldAfterProjectLock);
            await enteredAuthority.Task.WaitAsync(TimeSpan.FromSeconds(10), writeCancellation.Token);
            deleteTask = store.DeleteTaskAsync(
                eligible, graph.Revision,
                graph.Tasks.Single(item => item.Reference == eligible).Revision,
                writeCancellation.Token);
            Assert.True(
                await WaitForRuntimeLockWaitAsync("%backlog_projects%FOR UPDATE%", TimeSpan.FromSeconds(10)),
                "The competing delete must be blocked by PostgreSQL on the project row.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            releaseAuthority.TrySetResult(true);
        }

        if (failure is not null)
            writeCancellation.Cancel();
        BacklogCoreResult<BacklogDependencyGraph>[]? results = null;
        try
        {
            results = await FinishStartedWritesAsync(writeCancellation, addEdge, deleteTask);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        Assert.NotNull(results);
        var added = results[0];
        var deleted = results[1];
        Assert.True(added.IsSuccess);
        Assert.Contains(deleted.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);

        var persisted = (await store.ReadGraphAsync(project, CancellationToken.None)).Value!;
        Assert.Equal(graph.Revision + 1, persisted.Revision);
        Assert.Equal(3, persisted.Tasks.Length);
        Assert.Equal(new BacklogDependencyEdge(task, prerequisite), Assert.Single(persisted.Dependencies));
    }

    private async Task AssertRuntimeIdentityAndDependencyUpdateDeniedAsync()
    {
        await using var connection = await runtimeDataSource!.OpenConnectionAsync();
        await using (var identity = new NpgsqlCommand($"""
            SELECT current_user,
                   pg_get_userbyid(relation.relowner),
                   has_column_privilege(current_user, '"{Schema}".backlog_dependencies', 'task_id', 'UPDATE')
            FROM pg_class relation
            JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema AND relation.relname = 'backlog_dependencies'
            """, connection))
        {
            identity.Parameters.AddWithValue("schema", Schema);
            await using var reader = await identity.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(RuntimeRole, reader.GetString(0));
            Assert.NotEqual(RuntimeRole, reader.GetString(1));
            Assert.False(reader.GetBoolean(2));
        }

        await using var deniedUpdate = new NpgsqlCommand($"""
            UPDATE "{Schema}".backlog_dependencies
            SET task_id = task_id
            WHERE project_id = @project
            """, connection);
        deniedUpdate.Parameters.AddWithValue("project", "no-rows-required");
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
            () => deniedUpdate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private async Task CreateEmptyProjectAsync(string project)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{Schema}".backlog_projects (project_id) VALUES (@project)
            """, connection);
        command.Parameters.AddWithValue("project", project);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertProjectAbsentAsync(string project)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM "{Schema}".backlog_projects WHERE project_id = @project)
            """, connection);
        command.Parameters.AddWithValue("project", project);
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task<bool> WaitForRuntimeLockWaitAsync(
        string waitingQueryPattern,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                await using var connection = await fixture.DataSource.OpenConnectionAsync(cancellation.Token);
                await using var command = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity waiter
                        WHERE waiter.datname = current_database()
                          AND waiter.application_name = @application
                          AND waiter.query ILIKE @waitingQuery
                          AND EXISTS (
                              SELECT 1
                              FROM pg_stat_activity blocker
                              WHERE blocker.pid = ANY(pg_blocking_pids(waiter.pid))
                                AND blocker.application_name = @application
                                AND blocker.state = 'idle in transaction'))
                """, connection);
                command.Parameters.AddWithValue("application", runtimeApplicationName);
                command.Parameters.AddWithValue("waitingQuery", waitingQueryPattern);
                if ((bool)(await command.ExecuteScalarAsync(cancellation.Token))!)
                    return true;
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<T[]> FinishStartedWritesAsync<T>(
        CancellationTokenSource cancellation,
        params Task<T>?[] writes)
    {
        Task<T>[] started = writes.OfType<Task<T>>().ToArray();
        if (started.Length == 0)
            return [];

        try
        {
            return await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException timeout)
        {
            cancellation.Cancel();
            try
            {
                return await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException drainTimeout)
            {
                outstandingWrites = started;
                throw new TimeoutException(
                    "Backlog writes did not complete after cancellation; PostgreSQL fixture teardown is blocked.",
                    new AggregateException(timeout, drainTimeout));
            }
        }
    }

    private static string EmbeddedPlanHash()
    {
        using var plan = JsonDocument.Parse(PlannedRuntimeGrants.ReadEmbeddedPlan());
        var canonical = JsonSerializer.SerializeToUtf8Bytes(plan.RootElement);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}
