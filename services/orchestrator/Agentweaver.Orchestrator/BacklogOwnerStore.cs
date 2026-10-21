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
    private readonly string _promotions;
    private readonly string _origins;

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
        _promotions = $"{quotedSchema}.backlog_plan_promotions";
        _origins = $"{quotedSchema}.backlog_task_origins";
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
    /// Promotes the current confirmed work plan of a coordinator run into project backlog tasks.
    /// </summary>
    /// <remarks>
    /// Each plan item becomes one backlog task with a deterministic ID, and each item dependency becomes a graph edge.
    /// The promotion record and one origin record per task are written in the same transaction.
    /// The project graph rows are locked first, then the run owner binding row, so promotion is serialized
    /// with other graph writers and with MAF dispatch for the run. The decision state is not changed;
    /// after promotion, dispatch rejects the promoted plan. A replay with the same idempotency key and request
    /// returns the stored promotion.
    /// </remarks>
    /// <exception cref="CoordinationException">
    /// The request is invalid, conflicts with a stored promotion, the plan is no longer current, or the run already started execution.
    /// </exception>
    public async Task<BacklogCoreResult<BacklogPlanPromotion>> PromoteWorkPlanAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string rootSessionId,
        string workPlanId,
        long expectedDecisionStateVersion,
        long expectedExecutionFence,
        long expectedGraphRevision,
        string idempotencyKey,
        WorkPlanRunSelectionContext? selectionContext,
        CoordinatorDecisionOwnerStore decisions,
        Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(revalidateCurrentAuthority);
        var projectId = selection.Selection.ProjectId;
        var runId = selection.Selection.RunId;
        // Validate the project ID with the same rules as an empty graph before any database access.
        var projectValidation = BacklogDependencyGraph.Create(
            new BacklogDependencyGraphSnapshot(projectId, 0, [], []));
        if (!projectValidation.IsSuccess)
            return BacklogCoreResult<BacklogPlanPromotion>.Failure(projectValidation.Issues);
        if (!IsBoundedText(rootSessionId, 256) ||
            !IsBoundedText(workPlanId, 256) ||
            !IsBoundedText(idempotencyKey, 128) ||
            expectedDecisionStateVersion < 1 ||
            expectedExecutionFence < 1 ||
            expectedGraphRevision < 0)
            throw new CoordinationException("backlog_promotion_invalid", StatusCodes.Status400BadRequest);

        // The request hash binds the idempotency key to the caller, the selection, and every expected value.
        // The promotion ID comes from the hash, so a replay of the same request gets the same ID.
        var selectionHash = HashSelection(selection.Selection);
        var requestHash = CoordinatorDecisionOwnerStore.ComputeCommandHash(new
        {
            projectId,
            runId,
            rootSessionId,
            workPlanId,
            expectedDecisionStateVersion,
            expectedExecutionFence,
            expectedGraphRevision,
            selectionHash,
            actor.Issuer,
            actor.Subject,
            selection.Authorization.TenantId
        });
        var promotionId = Guid.ParseExact(requestHash[..32], "N");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        // Reject a reused key with different values early, before taking any locks.
        // The locked read below repeats this check for concurrent requests.
        var preview = await ReadPromotionByIdempotencyKeyAsync(
            connection, transaction: null, projectId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (preview is not null && preview.RequestHash != requestHash)
            throw new CoordinationException(
                "backlog_promotion_idempotency_conflict", StatusCodes.Status409Conflict);

        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        // Lock the project graph rows first. Every other graph writer takes this lock, so it serializes
        // this promotion with task edits, claims, and other promotions in the project.
        await EnsureProjectAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphInTransactionAsync(
            connection, transaction, projectId, lockRows: true, cancellationToken).ConfigureAwait(false);
        if (!graph.IsSuccess)
            return await RollbackFailureAsync<BacklogPlanPromotion>(
                transaction, graph.Issues).ConfigureAwait(false);

        // A stored promotion with this key is a replay. Return it unchanged if the request matches.
        var existing = await ReadPromotionByIdempotencyKeyAsync(
            connection, transaction, projectId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash || existing.Promotion.PromotionId != promotionId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CoordinationException(
                    "backlog_promotion_idempotency_conflict", StatusCodes.Status409Conflict);
            }

            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BacklogCoreResult<BacklogPlanPromotion>.Success(existing.Promotion);
        }

        // A plan can be promoted only once. A different key for the same plan is a conflict, not a replay.
        if (await IsPlanPromotedAsync(connection, transaction, projectId, runId, workPlanId, cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CoordinationException("backlog_promotion_conflict", StatusCodes.Status409Conflict);
        }

        // Lock the run owner binding row and read the confirmed plan again inside this transaction.
        // MAF dispatch takes the same lock, so the plan can't be dispatched while it's promoted.
        var identity = new SessionIdentity(projectId, runId, rootSessionId);
        var state = await decisions.ReadConfirmedWorkPlanForPromotionInTransactionAsync(
            connection, transaction, actor, identity, selection, selectionContext,
            expectedDecisionStateVersion, expectedExecutionFence, workPlanId, cancellationToken)
            .ConfigureAwait(false);
        var plan = state.ConfirmedWorkPlan!.Plan;

        // Each task ID is derived from the project, run, plan, and item IDs, so it's stable across replays.
        var taskIds = plan.Items.ToImmutableDictionary(
            item => item.Id,
            item => BacklogPlanPromotionIds.CreateTaskId(projectId, runId, plan.Id, item.Id),
            StringComparer.Ordinal);
        // Apply each change to the in-memory graph first, so the graph rules check every task and edge.
        // The first change uses the caller's expected revision; a stale revision fails here.
        var current = graph.Value!;
        var revision = expectedGraphRevision;
        foreach (var item in plan.Items)
        {
            var added = current.AddTask(
                new BacklogTaskReference(projectId, taskIds[item.Id]),
                new BacklogTaskContent(item.Title, item.Task),
                revision);
            if (!added.IsSuccess)
                return await RollbackFailureAsync<BacklogPlanPromotion>(
                    transaction, added.Issues).ConfigureAwait(false);
            current = added.Value!.Graph;
            revision = current.Revision;
        }

        var edges = ImmutableArray.CreateBuilder<BacklogDependencyEdge>();
        foreach (var item in plan.Items)
        foreach (var prerequisite in item.DependsOn.Distinct(StringComparer.Ordinal))
        {
            // A dependency on an item that isn't in this plan means the plan isn't usable as stored.
            if (!taskIds.TryGetValue(prerequisite, out var prerequisiteTaskId))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CoordinationException("backlog_promotion_stale", StatusCodes.Status409Conflict);
            }

            var edge = new BacklogDependencyEdge(
                new BacklogTaskReference(projectId, taskIds[item.Id]),
                new BacklogTaskReference(projectId, prerequisiteTaskId));
            var linked = current.AddDependency(edge.Task, edge.Prerequisite, revision);
            if (!linked.IsSuccess)
                return await RollbackFailureAsync<BacklogPlanPromotion>(
                    transaction, linked.Issues).ConfigureAwait(false);
            current = linked.Value!.Graph;
            revision = current.Revision;
            edges.Add(edge);
        }

        // Persist the validated graph: the new revision, the tasks, and then the edges that reference them.
        await UpdateGraphRevisionAsync(connection, transaction, current, cancellationToken).ConfigureAwait(false);
        foreach (var item in plan.Items)
            await InsertTaskAsync(connection, transaction,
                current.Tasks.Single(task => task.Reference.TaskId == taskIds[item.Id]), cancellationToken)
                .ConfigureAwait(false);
        foreach (var edge in edges)
            await PersistDependencyAsync(
                connection, transaction, edge.Task, edge.Prerequisite, add: true, cancellationToken)
                .ConfigureAwait(false);

        // Record the promotion and one origin row per task. Only promoted tasks get an origin row.
        var promotedAt = await InsertPromotionAsync(
            connection,
            transaction,
            promotionId,
            projectId,
            runId,
            rootSessionId,
            plan,
            expectedDecisionStateVersion,
            expectedExecutionFence,
            current.Revision,
            idempotencyKey,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        foreach (var item in plan.Items)
            await InsertOriginAsync(
                connection,
                transaction,
                new BacklogTaskOrigin(
                    new BacklogTaskReference(projectId, taskIds[item.Id]),
                    promotionId,
                    runId,
                    rootSessionId,
                    plan.Id,
                    item.Id),
                cancellationToken).ConfigureAwait(false);

        // Check caller authority again after the lock waits, then commit everything at once.
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BacklogCoreResult<BacklogPlanPromotion>.Success(new BacklogPlanPromotion(
            promotionId,
            projectId,
            runId,
            rootSessionId,
            plan.Id,
            plan.WorkflowId,
            plan.DefinitionRevision,
            expectedDecisionStateVersion,
            expectedExecutionFence,
            current.Revision,
            promotedAt,
            [.. plan.Items
                .Select(item => new BacklogPromotedTask(taskIds[item.Id], item.Id))
                .OrderBy(task => task.WorkItemId, StringComparer.Ordinal)]));
    }

    /// <summary>
    /// Reads every plan promotion recorded for a project, oldest first.
    /// </summary>
    public async Task<ImmutableArray<BacklogPlanPromotion>> ReadPromotionsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // One snapshot for both queries, so a concurrent promotion can't appear without its tasks.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var promotions = await ReadPromotionsInTransactionAsync(
            connection, transaction, projectId, idempotencyKey: null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. promotions.Select(item => item.Promotion)];
    }

    /// <summary>
    /// Reads the origin of every promoted task in a project. Tasks captured directly have no origin.
    /// </summary>
    public async Task<ImmutableArray<BacklogTaskOrigin>> ReadTaskOriginsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Origin rows are insert-only, so a single query without a transaction is consistent.
        await using var command = new NpgsqlCommand($"""
            SELECT task_id, promotion_id, run_id, root_session_id, work_plan_id, work_item_id
            FROM {_origins}
            WHERE project_id = @project
            ORDER BY task_id
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var origins = ImmutableArray.CreateBuilder<BacklogTaskOrigin>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            origins.Add(new BacklogTaskOrigin(
                new BacklogTaskReference(projectId, reader.GetString(0)),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        return origins.ToImmutable();
    }

    // Accepts nonblank text within the column length that contains no control characters.
    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private async Task<StoredBacklogPromotion?> ReadPromotionByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        (await ReadPromotionsInTransactionAsync(
            connection, transaction, projectId, idempotencyKey, cancellationToken).ConfigureAwait(false))
        .SingleOrDefault();

    private async Task<ImmutableArray<StoredBacklogPromotion>> ReadPromotionsInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Read the promotion rows first. A null key reads every promotion in the project.
        var rows = new List<(BacklogPlanPromotion Promotion, string RequestHash, int TaskCount)>();
        await using (var command = new NpgsqlCommand($"""
            SELECT promotion_id, run_id, root_session_id, work_plan_id, workflow_id, workflow_revision,
                   decision_state_version, execution_fence, graph_revision, created_at, request_hash, task_count
            FROM {_promotions}
            WHERE project_id = @project AND (@key::varchar IS NULL OR idempotency_key = @key)
            ORDER BY created_at, promotion_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, (object?)idempotencyKey ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((new BacklogPlanPromotion(
                    reader.GetGuid(0),
                    projectId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    []), reader.GetString(10), reader.GetInt32(11)));
        }
        if (rows.Count == 0)
            return [];

        // Then read the promoted tasks for those promotions and group them by promotion ID.
        var tasks = new Dictionary<Guid, ImmutableArray<BacklogPromotedTask>.Builder>();
        await using (var command = new NpgsqlCommand($"""
            SELECT promotion_id, task_id, work_item_id
            FROM {_origins}
            WHERE project_id = @project AND promotion_id = ANY(@promotions)
            ORDER BY promotion_id, work_item_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            command.Parameters.AddWithValue(
                "promotions", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                rows.Select(row => row.Promotion.PromotionId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var promotion = reader.GetGuid(0);
                if (!tasks.TryGetValue(promotion, out var builder))
                    tasks[promotion] = builder = ImmutableArray.CreateBuilder<BacklogPromotedTask>();
                builder.Add(new BacklogPromotedTask(reader.GetString(1), reader.GetString(2)));
            }
        }

        return [.. rows.Select(row =>
        {
            var promoted = tasks.TryGetValue(row.Promotion.PromotionId, out var builder)
                ? [.. builder.OrderBy(task => task.WorkItemId, StringComparer.Ordinal)]
                : ImmutableArray<BacklogPromotedTask>.Empty;
            // The stored task count must match the origin rows. A mismatch means the data is
            // incomplete, so report the store as unavailable instead of returning a partial promotion.
            if (promoted.Length != row.TaskCount)
                throw new CoordinationException("backlog_promotion_unavailable", StatusCodes.Status503ServiceUnavailable);
            return new StoredBacklogPromotion(row.Promotion with { Tasks = promoted }, row.RequestHash);
        })];
    }

    // Returns true when any promotion already exists for this plan in the run, whatever its key.
    private async Task<bool> IsPlanPromotedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string workPlanId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_promotions}
                WHERE project_id = @project AND run_id = @run AND work_plan_id = @plan)
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, workPlanId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    // Inserts the promotion row and returns the database timestamp, so replays report the same time.
    private async Task<DateTimeOffset> InsertPromotionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid promotionId,
        string projectId,
        string runId,
        string rootSessionId,
        WorkPlan plan,
        long decisionStateVersion,
        long executionFence,
        long graphRevision,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_promotions}
                (promotion_id, project_id, run_id, root_session_id, work_plan_id, workflow_id, workflow_revision,
                 decision_state_version, execution_fence, graph_revision, idempotency_key, request_hash, task_count)
            VALUES (@promotion, @project, @run, @root, @plan, @workflow, @workflow_revision,
                    @decision_version, @fence, @graph_revision, @key, @hash, @task_count)
            RETURNING created_at
            """, connection, transaction);
        insert.Parameters.AddWithValue("promotion", NpgsqlDbType.Uuid, promotionId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        insert.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        insert.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, plan.Id);
        insert.Parameters.AddWithValue("workflow", NpgsqlDbType.Varchar, plan.WorkflowId);
        insert.Parameters.AddWithValue("workflow_revision", NpgsqlDbType.Varchar, plan.DefinitionRevision);
        insert.Parameters.AddWithValue("decision_version", NpgsqlDbType.Bigint, decisionStateVersion);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        insert.Parameters.AddWithValue("graph_revision", NpgsqlDbType.Bigint, graphRevision);
        insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
        insert.Parameters.AddWithValue("task_count", NpgsqlDbType.Integer, plan.Items.Length);
        // Npgsql returns timestamptz as a UTC DateTime; mark it as UTC explicitly.
        var createdAt = (DateTime)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
    }

    // Inserts one origin row. The unique indexes allow one origin per task and per plan item.
    private async Task InsertOriginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BacklogTaskOrigin origin,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_origins}
                (project_id, task_id, promotion_id, run_id, root_session_id, work_plan_id, work_item_id)
            VALUES (@project, @task, @promotion, @run, @root, @plan, @item)
            """, connection, transaction);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, origin.Task.ProjectId);
        insert.Parameters.AddWithValue("task", NpgsqlDbType.Varchar, origin.Task.TaskId);
        insert.Parameters.AddWithValue("promotion", NpgsqlDbType.Uuid, origin.PromotionId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, origin.RunId);
        insert.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, origin.RootSessionId);
        insert.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, origin.WorkPlanId);
        insert.Parameters.AddWithValue("item", NpgsqlDbType.Varchar, origin.WorkItemId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record StoredBacklogPromotion(BacklogPlanPromotion Promotion, string RequestHash);

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
