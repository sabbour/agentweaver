using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Identifies the durable task claim and the accepted run state that owns its execution.
/// </summary>
internal sealed record BacklogTaskClaimReceipt(
    BacklogTaskReference Task,
    long TaskRevision,
    Guid ClaimId,
    string Phase,
    string RunId,
    string RootSessionId,
    string SelectionHash,
    long ExecutionFence,
    long DecisionStateVersion);

/// <summary>
/// Stores project backlog graphs and performs task claims in the Orchestrator PostgreSQL schema.
/// </summary>
internal sealed class BacklogOwnerStore
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _projects;
    private readonly string _tasks;
    private readonly string _dependencies;

    /// <summary>
    /// Creates a backlog store for a validated, non-reserved PostgreSQL schema.
    /// </summary>
    /// <exception cref="ArgumentException">The schema is not a safe application schema name.</exception>
    public BacklogOwnerStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (schema is null ||
            !SchemaPattern.IsMatch(schema) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));

        _dataSource = dataSource;
        var quotedSchema = $"\"{schema}\"";
        _projects = $"{quotedSchema}.backlog_projects";
        _tasks = $"{quotedSchema}.backlog_tasks";
        _dependencies = $"{quotedSchema}.backlog_dependencies";
    }

    /// <summary>
    /// Reads and validates a consistent project graph from PostgreSQL.
    /// </summary>
    public async Task<BacklogCoreResult<BacklogDependencyGraph>> ReadGraphAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var projectValidation = BacklogDependencyGraph.Create(
            new BacklogDependencyGraphSnapshot(projectId, 0, [], []));
        if (!projectValidation.IsSuccess)
            return projectValidation;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, projectId, lockRows: false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return graph;
    }

    /// <summary>
    /// Adds a task if the project graph still has the caller's expected revision.
    /// </summary>
    /// <remarks>The graph row is locked while the graph and task are written atomically.</remarks>
    public Task<BacklogCoreResult<BacklogDependencyGraph>> AddTaskAsync(
        BacklogTaskReference task,
        long expectedGraphRevision,
        CancellationToken cancellationToken) =>
        AddTaskCoreAsync(task, null, expectedGraphRevision, cancellationToken, null);

    /// <summary>Captures project task content without requiring an accepted run.</summary>
    public Task<BacklogCoreResult<BacklogDependencyGraph>> AddTaskAsync(
        BacklogTaskReference task,
        BacklogTaskContent content,
        long expectedGraphRevision,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null) =>
        AddTaskCoreAsync(task, content ?? throw new ArgumentNullException(nameof(content)),
            expectedGraphRevision, cancellationToken, revalidateCurrentAuthority);

    /// <summary>Serializes capture with claims and other graph writers.</summary>
    private async Task<BacklogCoreResult<BacklogDependencyGraph>> AddTaskCoreAsync(
        BacklogTaskReference task,
        BacklogTaskContent? content,
        long expectedGraphRevision,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority)
    {
        var taskValidation = ValidateTaskReference(task);
        if (!taskValidation.IsSuccess)
            return taskValidation;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProjectAsync(connection, transaction, task.ProjectId, cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return RollbackFailure<BacklogDependencyGraph>(transaction, graph.Issues);

        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var edit = content is null
            ? graph.Value!.AddTask(task, expectedGraphRevision)
            : graph.Value!.AddTask(task, content, expectedGraphRevision);
        if (!edit.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, edit.Issues).ConfigureAwait(false);
        if (edit.Value!.Changed)
        {
            await UpdateGraphRevisionAsync(connection, transaction, edit.Value.Graph, cancellationToken)
                .ConfigureAwait(false);
            await InsertTaskAsync(connection, transaction,
                edit.Value.Graph.Tasks.Single(item => item.Reference.TaskId == task.TaskId), cancellationToken)
                .ConfigureAwait(false);
        }
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogDependencyGraph.Create(edit.Value.Graph.Snapshot);
    }

    /// <summary>Replaces content and lifecycle under the same project lock used by execution claims.</summary>
    public async Task<BacklogCoreResult<BacklogDependencyGraph>> SetTaskContentAsync(
        BacklogTaskReference task,
        BacklogTaskContent content,
        long expectedGraphRevision,
        long expectedTaskRevision,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        var validation = ValidateTaskReference(task);
        if (!validation.IsSuccess)
            return validation;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(transaction, graph.Issues).ConfigureAwait(false);
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var edit = graph.Value!.SetTaskContent(task, content, expectedGraphRevision, expectedTaskRevision);
        if (!edit.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(transaction, edit.Issues).ConfigureAwait(false);
        if (edit.Value!.Changed)
        {
            await UpdateGraphRevisionAsync(connection, transaction, edit.Value.Graph, cancellationToken)
                .ConfigureAwait(false);
            await UpdateTaskAsync(connection, transaction, edit.Value.Graph, task.TaskId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogDependencyGraph.Create(edit.Value.Graph.Snapshot);
    }

    /// <summary>
    /// Adds a dependency after the core graph validates the edge and expected revision.
    /// </summary>
    public Task<BacklogCoreResult<BacklogDependencyGraph>> AddDependencyAsync(
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        long expectedGraphRevision,
        CancellationToken cancellationToken) =>
        MutateDependencyAsync(task, prerequisite, expectedGraphRevision, add: true, cancellationToken);

    /// <summary>
    /// Removes a dependency after the core graph validates the edge and expected revision.
    /// </summary>
    public Task<BacklogCoreResult<BacklogDependencyGraph>> RemoveDependencyAsync(
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        long expectedGraphRevision,
        CancellationToken cancellationToken) =>
        MutateDependencyAsync(task, prerequisite, expectedGraphRevision, add: false, cancellationToken);

    /// <summary>
    /// Updates a task state using graph and task revisions, persisting only a validated change.
    /// </summary>
    public async Task<BacklogCoreResult<BacklogDependencyGraph>> SetTaskStateAsync(
        BacklogTaskReference task,
        BacklogTaskState state,
        long expectedGraphRevision,
        long expectedTaskRevision,
        CancellationToken cancellationToken)
    {
        var taskValidation = ValidateTaskReference(task);
        if (!taskValidation.IsSuccess)
            return taskValidation;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, graph.Issues).ConfigureAwait(false);

        var edit = graph.Value!.SetTaskState(
            task, state, expectedGraphRevision, expectedTaskRevision);
        if (!edit.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, edit.Issues).ConfigureAwait(false);
        if (edit.Value!.Changed)
        {
            await UpdateGraphRevisionAsync(connection, transaction, edit.Value.Graph, cancellationToken)
                .ConfigureAwait(false);
            await UpdateTaskAsync(connection, transaction, edit.Value.Graph, task.TaskId, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogDependencyGraph.Create(edit.Value.Graph.Snapshot);
    }

    /// <summary>
    /// Archives a task using graph and task revisions, persisting only a validated change.
    /// </summary>
    public async Task<BacklogCoreResult<BacklogDependencyGraph>> ArchiveTaskAsync(
        BacklogTaskReference task,
        long expectedGraphRevision,
        long expectedTaskRevision,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, graph.Issues).ConfigureAwait(false);

        var edit = graph.Value!.ArchiveTask(task, expectedGraphRevision, expectedTaskRevision);
        if (!edit.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, edit.Issues).ConfigureAwait(false);
        if (edit.Value!.Changed)
        {
            await UpdateGraphRevisionAsync(connection, transaction, edit.Value.Graph, cancellationToken)
                .ConfigureAwait(false);
            await UpdateTaskAsync(connection, transaction, edit.Value.Graph, task.TaskId, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogDependencyGraph.Create(edit.Value.Graph.Snapshot);
    }

    /// <summary>
    /// Atomically verifies readiness, accepts the run root, initializes its decision state, and records the task claim.
    /// </summary>
    /// <remarks>
    /// Evidence that may require a remote read is prepared before the transaction, then re-read from local owner state
    /// while graph rows are locked. The idempotency key is bound to the full claim request and accepted selection.
    /// </remarks>
    /// <exception cref="CoordinationException">The request conflicts with an existing claim or the accepted run binding.</exception>
    public async Task<BacklogCoreResult<BacklogTaskClaimReceipt>> ClaimTaskAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogTaskReference task,
        long expectedGraphRevision,
        long expectedTaskRevision,
        string idempotencyKey,
        IBacklogPrerequisiteEvidenceReader evidenceReader,
        CoordinationOwnerStore coordination,
        CoordinatorDecisionOwnerStore decisions,
        Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<Agentweaver.Identity.RuntimeRunAdmissionReceipt>>? readRunAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(evidenceReader);
        ArgumentNullException.ThrowIfNull(coordination);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(revalidateCurrentAuthority);
        var taskValidation = ValidateTaskReference(task);
        if (!taskValidation.IsSuccess)
            return BacklogCoreResult<BacklogTaskClaimReceipt>.Failure(taskValidation.Issues);
        if (!string.Equals(task.ProjectId, selection.Selection.ProjectId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128 ||
            idempotencyKey.Any(char.IsControl))
            throw new CoordinationException("backlog_claim_invalid", StatusCodes.Status400BadRequest);

        var selectionHash = HashSelection(selection.Selection);
        var requestHash = CoordinatorDecisionOwnerStore.ComputeCommandHash(new
        {
            task.ProjectId,
            task.TaskId,
            expectedGraphRevision,
            expectedTaskRevision,
            selection.Selection.RunId,
            selectionHash,
            actor.Issuer,
            actor.Subject,
            selection.Authorization.TenantId
        });
        var claimId = Guid.ParseExact(requestHash[..32], "N");
        var rootSessionId = claimId.ToString("N");
        var selectionContext = CoordinatorWorkflowCatalog.CreateRunSelectionContext(
            selection.Selection.Snapshot);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingClaimPreview = await ReadClaimByIdempotencyKeyAsync(
            connection, transaction: null, task.ProjectId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingClaimPreview is not null && existingClaimPreview.RequestHash != requestHash)
            throw new CoordinationException(
                "backlog_claim_idempotency_conflict", StatusCodes.Status409Conflict);

        BacklogPrerequisiteEvidencePreparation? preparedEvidence = null;
        if (existingClaimPreview is null)
        {
            var preflightGraph = await ReadGraphAsync(task.ProjectId, cancellationToken).ConfigureAwait(false);
            if (!preflightGraph.IsSuccess)
                return BacklogCoreResult<BacklogTaskClaimReceipt>.Failure(preflightGraph.Issues);
            var preflight = BacklogReadinessEvaluator.Evaluate(
                preflightGraph.Value,
                task,
                expectedGraphRevision,
                expectedTaskRevision,
                []);
            if (!preflight.IsSuccess)
                return BacklogCoreResult<BacklogTaskClaimReceipt>.Failure(preflight.Issues);
            var blockingIssues = preflight.Value!.Blockers
                .Where(issue => issue.Code != BacklogIssueCode.MissingPrerequisiteSnapshot)
                .ToImmutableArray();
            if (!blockingIssues.IsEmpty)
                return BacklogCoreResult<BacklogTaskClaimReceipt>.Failure(blockingIssues);
            preparedEvidence = await evidenceReader.PrepareForClaimAsync(
                actor, selection, preflightGraph.Value!, task, cancellationToken).ConfigureAwait(false);
        }

        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogTaskClaimReceipt>(
                transaction, graph.Issues).ConfigureAwait(false);

        var acceptedRoot = await coordination.AcceptRootInTransactionAsync(
            connection, transaction, actor, selection, rootSessionId, cancellationToken,
            readRunAdmission, revalidateCurrentAuthority)
            .ConfigureAwait(false);
        var initializedRoot = await decisions.InitializeRootInTransactionAsync(
            connection,
            transaction,
            actor,
            new SessionIdentity(task.ProjectId, selection.Selection.RunId, rootSessionId),
            selection,
            selectionContext,
            cancellationToken).ConfigureAwait(false);
        if (initializedRoot.SelectionHash != selectionHash ||
            initializedRoot.State.Fence != acceptedRoot.ExecutionFence)
            throw new CoordinationException("backlog_claim_conflict", StatusCodes.Status409Conflict);

        var existingClaim = await ReadClaimByIdempotencyKeyAsync(
            connection, transaction, task.ProjectId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existingClaim is not null)
        {
            if (existingClaim.RequestHash != requestHash ||
                existingClaim.ClaimId != claimId ||
                existingClaim.RunId != selection.Selection.RunId ||
                existingClaim.RootSessionId != rootSessionId ||
                existingClaim.SelectionHash != selectionHash ||
                existingClaim.ExecutionFence != acceptedRoot.ExecutionFence)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CoordinationException(
                    "backlog_claim_idempotency_conflict", StatusCodes.Status409Conflict);
            }

            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BacklogCoreResult<BacklogTaskClaimReceipt>.Success(existingClaim.ToReceipt(task));
        }

        var prerequisites = await evidenceReader.ReadCurrentForClaimAsync(
            connection,
            transaction,
            actor,
            selection,
            graph.Value!,
            task,
            preparedEvidence ?? throw new InvalidOperationException(
                "A new backlog claim requires prepared prerequisite evidence."),
            initializedRoot.StateVersion,
            acceptedRoot.ExecutionFence,
            cancellationToken).ConfigureAwait(false);
        var claim = graph.Value!.ClaimTask(
            task,
            expectedGraphRevision,
            expectedTaskRevision,
            prerequisites);
        if (!claim.IsSuccess)
            return await RollbackFailureAsync<BacklogTaskClaimReceipt>(
                transaction, claim.Issues).ConfigureAwait(false);

        var claimedTask = claim.Value!.Graph.Tasks.Single(
            item => item.Reference.TaskId == task.TaskId);
        await UpdateGraphRevisionAsync(
            connection, transaction, claim.Value.Graph, cancellationToken).ConfigureAwait(false);
        await UpdateClaimedTaskAsync(
            connection,
            transaction,
            claimedTask,
            claimId,
            idempotencyKey,
            requestHash,
            selection.Selection.RunId,
            rootSessionId,
            selectionHash,
            acceptedRoot.ExecutionFence,
            initializedRoot.StateVersion,
            expectedTaskRevision,
            cancellationToken).ConfigureAwait(false);

        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogCoreResult<BacklogTaskClaimReceipt>.Success(new BacklogTaskClaimReceipt(
            task,
            claimedTask.Revision,
            claimId,
            "preparing",
            selection.Selection.RunId,
            rootSessionId,
            selectionHash,
            acceptedRoot.ExecutionFence,
            initializedRoot.StateVersion));
    }

    /// <summary>
    /// Reads persisted claim tuples for prerequisite tasks in the caller's transaction.
    /// </summary>
    /// <exception cref="ArgumentException">The transaction does not belong to the connection or prerequisite references are invalid.</exception>
    /// <exception cref="CoordinationException">A stored claim tuple is incomplete or inconsistent.</exception>
    internal async Task<ImmutableArray<MafBacklogTaskClaimTuple>> ReadPrerequisiteClaimTuplesInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        ImmutableArray<BacklogTaskReference> prerequisites,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (prerequisites.IsDefault ||
            prerequisites.Any(prerequisite =>
                prerequisite is null ||
                !ValidateTaskReference(prerequisite).IsSuccess ||
                prerequisite!.ProjectId != projectId) ||
            prerequisites.Select(prerequisite => prerequisite.TaskId)
                .Distinct(StringComparer.Ordinal).Count() != prerequisites.Length)
            throw new ArgumentException(
                "Prerequisites must be unique valid task references in the supplied project.",
                nameof(prerequisites));
        if (prerequisites.IsEmpty)
            return [];

        var taskIds = prerequisites.Select(prerequisite => prerequisite.TaskId).ToArray();
        var claims = ImmutableArray.CreateBuilder<MafBacklogTaskClaimTuple>();
        await using var command = new NpgsqlCommand($"""
            SELECT task_id, task_revision, claim_task_revision, task_state,
                   is_archived, automation_invocation_pending, claim_id, claim_phase,
                   claim_idempotency_key, claim_request_hash, claim_run_id, claim_root_session_id,
                   claim_selection_hash, claim_execution_fence, claim_decision_state_version
            FROM {_tasks}
            WHERE project_id = @project AND task_id = ANY(@tasks) AND claim_id IS NOT NULL
            ORDER BY task_id
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue(
            "tasks", NpgsqlDbType.Array | NpgsqlDbType.Varchar, taskIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<BacklogTaskState>(reader.GetString(3), ignoreCase: true, out var state) ||
                !Enum.IsDefined(state) ||
                reader.IsDBNull(2) ||
                reader.IsDBNull(6) ||
                reader.IsDBNull(7) ||
                reader.IsDBNull(8) ||
                reader.IsDBNull(9) ||
                reader.IsDBNull(10) ||
                reader.IsDBNull(11) ||
                reader.IsDBNull(12) ||
                reader.IsDBNull(13) ||
                reader.IsDBNull(14))
                throw new CoordinationException(
                    "backlog_claim_corrupt", StatusCodes.Status503ServiceUnavailable);

            var phase = reader.GetString(7);
            var taskId = reader.GetString(0);
            var claimId = reader.GetGuid(6);
            var claimTaskRevision = reader.GetInt64(2);
            var requestHash = reader.GetString(9).TrimEnd();
            var selectionHash = reader.GetString(12).TrimEnd();
            if (phase is not ("preparing" or "confirmed") ||
                claimTaskRevision < 0 ||
                claimTaskRevision > reader.GetInt64(1) ||
                requestHash.Length != 64 ||
                selectionHash.Length != 64 ||
                reader.GetInt64(13) <= 0 ||
                reader.GetInt64(14) <= 0)
                throw new CoordinationException(
                    "backlog_claim_corrupt", StatusCodes.Status503ServiceUnavailable);

            claims.Add(new MafBacklogTaskClaimTuple(
                new BacklogTaskReference(projectId, taskId),
                reader.GetInt64(1),
                claimTaskRevision,
                state,
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                claimId,
                phase,
                reader.GetString(8),
                requestHash,
                reader.GetString(10),
                reader.GetString(11),
                selectionHash,
                reader.GetInt64(13),
                reader.GetInt64(14)));
        }
        return claims.ToImmutable();
    }

    /// <summary>
    /// Applies an add or remove edge through core graph validation and a single database transaction.
    /// </summary>
    private async Task<BacklogCoreResult<BacklogDependencyGraph>> MutateDependencyAsync(
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        long expectedGraphRevision,
        bool add,
        CancellationToken cancellationToken)
    {
        var taskValidation = ValidateTaskReference(task);
        if (!taskValidation.IsSuccess)
            return taskValidation;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, task.ProjectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, graph.Issues).ConfigureAwait(false);

        var edit = add
            ? graph.Value!.AddDependency(task, prerequisite, expectedGraphRevision)
            : graph.Value!.RemoveDependency(task, prerequisite, expectedGraphRevision);
        if (!edit.IsSuccess)
            return await RollbackFailureAsync<BacklogDependencyGraph>(
                transaction, edit.Issues).ConfigureAwait(false);
        if (edit.Value!.Changed)
        {
            await UpdateGraphRevisionAsync(connection, transaction, edit.Value.Graph, cancellationToken)
                .ConfigureAwait(false);
            await PersistDependencyAsync(
                connection, transaction, task, prerequisite, add, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogDependencyGraph.Create(edit.Value.Graph.Snapshot);
    }

    /// <summary>
    /// Loads the graph and optionally locks its project, task, and dependency rows for a mutation.
    /// </summary>
    /// <returns>A validated graph, or a core issue when persisted rows cannot form a valid graph.</returns>
    private async Task<BacklogCoreResult<BacklogDependencyGraph>> ReadGraphInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        bool lockRows,
        CancellationToken cancellationToken)
    {
        long revision;
        await using (var readProject = new NpgsqlCommand($"""
            SELECT graph_revision
            FROM {_projects}
            WHERE project_id = @project
            {(lockRows ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction))
        {
            readProject.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            var result = await readProject.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull)
                return BacklogDependencyGraph.Create(new BacklogDependencyGraphSnapshot(
                    projectId, 0, [], []));
            revision = (long)result;
        }

        var tasks = ImmutableArray.CreateBuilder<BacklogTaskSnapshot>();
        await using (var readTasks = new NpgsqlCommand($"""
            SELECT task_id, task_revision, task_state, is_archived, automation_invocation_pending,
                   title, description, lifecycle, issue_provider_id, issue_repository_owner,
                   issue_repository_name, issue_number
            FROM {_tasks}
            WHERE project_id = @project
            ORDER BY task_id
            {(lockRows ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction))
        {
            readTasks.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            await using var reader = await readTasks.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Enum.TryParse<BacklogTaskState>(reader.GetString(2), ignoreCase: true, out var state))
                    return InvalidPersistedGraph("tasks.task_state");
                if (!Enum.TryParse<BacklogTaskLifecycle>(reader.GetString(7), ignoreCase: true, out var lifecycle))
                    return InvalidPersistedGraph("tasks.lifecycle");
                tasks.Add(new BacklogTaskSnapshot(
                    new BacklogTaskReference(projectId, reader.GetString(0)),
                    reader.GetInt64(1),
                    state,
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    lifecycle,
                    reader.IsDBNull(8) ? null : new BacklogRepositoryIssueLink(
                        reader.GetString(8),
                        new SourceControlRepositoryIdentity(reader.GetString(9), reader.GetString(10)),
                        reader.GetInt64(11))));
            }
        }

        var dependencies = ImmutableArray.CreateBuilder<BacklogDependencyEdge>();
        await using (var readDependencies = new NpgsqlCommand($"""
            SELECT task_id, prerequisite_task_id
            FROM {_dependencies}
            WHERE project_id = @project
            ORDER BY task_id, prerequisite_task_id
            {(lockRows ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction))
        {
            readDependencies.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            await using var reader = await readDependencies.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                dependencies.Add(new BacklogDependencyEdge(
                    new BacklogTaskReference(projectId, reader.GetString(0)),
                    new BacklogTaskReference(projectId, reader.GetString(1))));
        }
        return BacklogDependencyGraph.Create(new BacklogDependencyGraphSnapshot(
            projectId, revision, tasks.ToImmutable(), dependencies.ToImmutable()));
    }

    /// <summary>
    /// Finds an earlier task claim by its project-scoped idempotency key.
    /// </summary>
    private async Task<StoredBacklogClaim?> ReadClaimByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT claim_id, claim_phase, claim_task_revision, claim_request_hash,
                   claim_run_id, claim_root_session_id, claim_selection_hash,
                   claim_execution_fence, claim_decision_state_version
            FROM {_tasks}
            WHERE project_id = @project AND claim_idempotency_key = @idempotency
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new StoredBacklogClaim(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    /// <summary>
    /// Persists the claim's preparing phase only if the task remains claimable at the expected revision.
    /// </summary>
    /// <exception cref="CoordinationException">The guarded update did not affect exactly one task row.</exception>
    private async Task UpdateClaimedTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogTaskSnapshot claimedTask,
        Guid claimId,
        string idempotencyKey,
        string requestHash,
        string runId,
        string rootSessionId,
        string selectionHash,
        long executionFence,
        long decisionStateVersion,
        long expectedTaskRevision,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand($"""
            UPDATE {_tasks}
            SET task_revision = @revision,
                task_state = 'claimed',
                claim_id = @claim,
                claim_phase = 'preparing',
                claim_task_revision = @revision,
                claim_idempotency_key = @idempotency,
                claim_request_hash = @request_hash,
                claim_run_id = @run,
                claim_root_session_id = @root_session,
                claim_selection_hash = @selection_hash,
                claim_execution_fence = @fence,
                claim_decision_state_version = @decision_version,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND task_id = @task
              AND task_revision = @expected_task_revision
              AND claim_id IS NULL AND is_archived = false
              AND lifecycle = 'open'
              AND automation_invocation_pending = false
              AND task_state IN ('backlog', 'ready')
            """, connection, transaction);
        update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, claimedTask.Revision);
        update.Parameters.AddWithValue("claim", NpgsqlDbType.Uuid, claimId);
        update.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        update.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, requestHash);
        update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        update.Parameters.AddWithValue("root_session", NpgsqlDbType.Varchar, rootSessionId);
        update.Parameters.AddWithValue("selection_hash", NpgsqlDbType.Char, selectionHash);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        update.Parameters.AddWithValue("decision_version", NpgsqlDbType.Bigint, decisionStateVersion);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, claimedTask.Reference.ProjectId);
        update.Parameters.AddWithValue("task", NpgsqlDbType.Varchar, claimedTask.Reference.TaskId);
        update.Parameters.AddWithValue(
            "expected_task_revision", NpgsqlDbType.Bigint, expectedTaskRevision);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException("backlog_claim_conflict", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Creates the project graph row if it has not been created yet.
    /// </summary>
    private async Task EnsureProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_projects} (project_id)
            VALUES (@project)
            ON CONFLICT (project_id) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the new graph revision inside the same transaction as the graph mutation.
    /// </summary>
    private async Task UpdateGraphRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogDependencyGraph graph,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand($"""
            INSERT INTO {_projects} (project_id, graph_revision, updated_at)
            VALUES (@project, @revision, clock_timestamp())
            ON CONFLICT (project_id) DO UPDATE SET
                graph_revision = EXCLUDED.graph_revision,
                updated_at = clock_timestamp()
            """, connection, transaction);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, graph.ProjectId);
        update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, graph.Revision);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a newly validated task snapshot.
    /// </summary>
    private async Task InsertTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogTaskSnapshot task,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_tasks}
                (project_id, task_id, task_revision, task_state, is_archived, automation_invocation_pending,
                 title, description, lifecycle, issue_provider_id, issue_repository_owner,
                 issue_repository_name, issue_number)
            VALUES (@project, @task, @revision, @state, @archived, @pending,
                    @title, @description, @lifecycle, @issue_provider, @issue_owner, @issue_repository, @issue_number)
            """, connection, transaction);
        AddTaskParameters(insert, task);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the validated snapshot for an existing task.
    /// </summary>
    /// <exception cref="CoordinationException">The task row disappeared before it could be updated.</exception>
    private async Task UpdateTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogDependencyGraph graph,
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = graph.Tasks.Single(item => item.Reference.TaskId == taskId);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_tasks}
            SET task_revision = @revision, task_state = @state, is_archived = @archived,
                automation_invocation_pending = @pending, title = @title, description = @description,
                lifecycle = @lifecycle, issue_provider_id = @issue_provider,
                issue_repository_owner = @issue_owner, issue_repository_name = @issue_repository,
                issue_number = @issue_number, updated_at = clock_timestamp()
            WHERE project_id = @project AND task_id = @task
            """, connection, transaction);
        AddTaskParameters(update, task);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException("backlog_task_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Inserts or deletes one dependency row to match the validated graph edit.
    /// </summary>
    /// <exception cref="CoordinationException">The persisted edge did not change exactly once.</exception>
    private async Task PersistDependencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        bool add,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(add
            ? $"""
              INSERT INTO {_dependencies} (project_id, task_id, prerequisite_task_id)
              VALUES (@project, @task, @prerequisite)
              """
            : $"""
              DELETE FROM {_dependencies}
              WHERE project_id = @project AND task_id = @task AND prerequisite_task_id = @prerequisite
              """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, task.ProjectId);
        command.Parameters.AddWithValue("task", NpgsqlDbType.Varchar, task.TaskId);
        command.Parameters.AddWithValue("prerequisite", NpgsqlDbType.Varchar, prerequisite.TaskId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException("backlog_graph_conflict", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Adds the task snapshot fields using the database types expected by the backlog schema.
    /// </summary>
    private static void AddTaskParameters(NpgsqlCommand command, BacklogTaskSnapshot task)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, task.Reference.ProjectId);
        command.Parameters.AddWithValue("task", NpgsqlDbType.Varchar, task.Reference.TaskId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, task.Revision);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, task.State.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("archived", NpgsqlDbType.Boolean, task.IsArchived);
        command.Parameters.AddWithValue(
            "pending", NpgsqlDbType.Boolean, task.IsAutomationInvocationPending);
        command.Parameters.AddWithValue("title", NpgsqlDbType.Text, task.Title);
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, (object?)task.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("lifecycle", NpgsqlDbType.Varchar, task.Lifecycle.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("issue_provider", NpgsqlDbType.Varchar,
            (object?)task.RepositoryIssue?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("issue_owner", NpgsqlDbType.Varchar,
            (object?)task.RepositoryIssue?.Repository.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("issue_repository", NpgsqlDbType.Varchar,
            (object?)task.RepositoryIssue?.Repository.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("issue_number", NpgsqlDbType.Bigint,
            (object?)task.RepositoryIssue?.IssueNumber ?? DBNull.Value);
    }

    /// <summary>
    /// Hashes the accepted selection snapshot used to bind a task claim to its run inputs.
    /// </summary>
    private static string HashSelection(EffectiveRunSelection selection)
    {
        var snapshot = selection.Snapshot.GetRawText();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    /// <summary>
    /// Returns a core validation issue for persisted rows that cannot be represented as a graph.
    /// </summary>
    private static BacklogCoreResult<BacklogDependencyGraph> InvalidPersistedGraph(string path) =>
        BacklogCoreResult<BacklogDependencyGraph>.Failure(
        [
            new BacklogIssue(
                BacklogIssueCode.InvalidTaskState,
                path,
                "Persisted backlog data is not a valid graph.")
        ]);

    /// <summary>
    /// Validates a task reference using the same graph rules used for persisted backlog operations.
    /// </summary>
    private static BacklogCoreResult<BacklogDependencyGraph> ValidateTaskReference(
        BacklogTaskReference? task)
    {
        var graph = BacklogDependencyGraph.Create(
            new BacklogDependencyGraphSnapshot(task?.ProjectId ?? string.Empty, 0, [], []));
        if (!graph.IsSuccess)
            return graph;

        var taskValidation = graph.Value!.AddTask(task, expectedGraphRevision: 0);
        return taskValidation.IsSuccess
            ? graph
            : BacklogCoreResult<BacklogDependencyGraph>.Failure(taskValidation.Issues);
    }

    /// <summary>
    /// Rolls back a failed graph operation and returns its core validation issues.
    /// </summary>
    private static BacklogCoreResult<TOut> RollbackFailure<TOut>(
        NpgsqlTransaction transaction,
        ImmutableArray<BacklogIssue> issues) where TOut : class
    {
        transaction.Rollback();
        return BacklogCoreResult<TOut>.Failure(issues);
    }

    /// <summary>
    /// Asynchronously rolls back a failed graph operation and returns its core validation issues.
    /// </summary>
    private static async Task<BacklogCoreResult<TOut>> RollbackFailureAsync<TOut>(
        NpgsqlTransaction transaction,
        ImmutableArray<BacklogIssue> issues) where TOut : class
    {
        await transaction.RollbackAsync().ConfigureAwait(false);
        return BacklogCoreResult<TOut>.Failure(issues);
    }

    /// <summary>
    /// Holds a stored idempotent claim and projects it into the public claim receipt.
    /// </summary>
    private sealed record StoredBacklogClaim(
        Guid ClaimId,
        string Phase,
        long TaskRevision,
        string RequestHash,
        string RunId,
        string RootSessionId,
        string SelectionHash,
        long ExecutionFence,
        long DecisionStateVersion)
    {
        /// <summary>
        /// Returns the persisted claim details with the task reference supplied by the caller.
        /// </summary>
        public BacklogTaskClaimReceipt ToReceipt(BacklogTaskReference task) =>
            new(
                task,
                TaskRevision,
                ClaimId,
                Phase,
                RunId,
                RootSessionId,
                SelectionHash,
                ExecutionFence,
                DecisionStateVersion);
    }
}
