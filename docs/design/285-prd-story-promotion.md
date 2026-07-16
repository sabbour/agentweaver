# PRD story promotion to coordinated backlog runs

**Issue:** [#285](https://github.com/sabbour/agentweaver/issues/285)  
**Related:** [#284](https://github.com/sabbour/agentweaver/issues/284)  
**Status:** Implementation-ready design

## Summary

Agentweaver will keep non-independent stories as subtasks in the current coordinator run and
promote genuinely separate deliverables to independent backlog tasks. The promotion rule is a
model judgment after decomposition:

1. the decomposition agent proposes stories plus dependency edges and optional `[run]` /
   `[inline]` overrides;
2. the server groups stories into weakly connected dependency components;
3. each component receives one tool-less Copilot classification deciding whether it is a genuinely
   independent deliverable from the rest of the initiative; and
4. explicit `[run]` / `[inline]` overrides still win over the classification.

Promoted stories are created in **Backlog**, not Ready. A human can commit any or all of them to
Ready. A Ready story with unmet dependencies remains visibly Ready but is not claimable. It becomes
claimable automatically after every prerequisite backlog task's linked coordinator run reaches
`RunStatus.Merged`.

Dependencies are normalized rows between backlog tasks. `BlockedReason` is a derived read-model
value, not persisted state.

## Current behavior and insertion points

- `CoordinatorOrchestratorExecutor.OrchestrateAsync` decomposes every confirmed `OutcomeSpec` into
  `SubtaskDraft` values, breaks cycles, assigns roster members, and persists one run-local
  `WorkPlan`, `Subtask` rows, and `SubtaskDependency` rows.
- Its model schema already includes `complexity`, `isolation`, and run-local `depends_on`, but
  complexity is transient and every accepted draft is persisted as an inline subtask.
- `CoordinatorWorkflowFactory` calls that executor immediately after outcome-spec confirmation.
  `CoordinatorRunService` subsequently dispatches the persisted inline frontier.
- `CoordinatorPickupService` turns a Ready `BacklogTask` into a top-level coordinator run.
- `BacklogDecomposeEndpoints` and `BacklogDecomposeService` provide a separate, user-confirmed,
  flat markdown-to-backlog import path. They do not preserve dependencies.
- `BacklogTools` exposes the external MCP backlog surface. The in-process
  `AgentweaverApiTools` surface already includes native Coordinator equivalents for
  `backlog_capture_task` and `backlog_get_board`.
- `WorkflowSelector` selects the current run's workflow. `CollectiveAssemblyPipeline` assembles
  child runs belonging to the current work plan. Neither is a cross-run dependency mechanism.

## Goals

1. Give one decomposition a fail-closed inline-versus-independent-run decision.
2. Preserve the originating PRD run and the reason for each promotion.
3. Persist hard dependency edges between promoted backlog tasks.
4. Prevent pickup of a Ready task until all hard dependencies are merged.
5. Surface dependency and blocking information consistently through API, board, and MCP.
6. Make promotion idempotent and safe under coordinator retry.

## Promotion contract

### Decomposition schema

Extend the JSON element parsed into `SubtaskDraft` with:

```csharp
string StoryKey                 // required, unique kebab-case key within this decomposition
string? PromotionOverride       // null | "run" | "inline"
```

The decomposition prompt must:

- preserve a case-insensitive `[run]` or `[inline]` token found in a story heading/title as
  `promotion_override`, then remove the token from the resulting title;
- require a stable semantic `story_key`; and
- forbid both override tokens on one story.

Parsing rejects values outside the contract and falls back to the existing deterministic
decomposition. The deterministic fallback uses `PromotionOverride = "inline"` so an unavailable
model does not unexpectedly fan out runs.

### Exact decision algorithm

Construct an undirected graph from the existing `depends_on` edges, then decide once per weakly
connected component in this order:

```text
if component contains both "run" and "inline" overrides:
    fail with conflicting_promotion_overrides
else if component contains a "run" override:
    promote the component
else if component contains an "inline" override:
    keep the component inline
else:
    classify the whole component with a tool-less Copilot turn
    if classification succeeds and says the component is an independent deliverable:
        promote the component
    else:
        keep the component inline
```

The component rule prevents unsupported mixed edges:

- dependencies among inline stories remain `SubtaskDependency` rows;
- dependencies among promoted stories become `BacklogTaskDependency` rows; and
- there can be no dependency edge from an inline story to a promoted story or vice versa.

The classification is semantic, not size-based. The model must judge whether the component is a
coherent, separately shippable deliverable — for example, "storefront" plus "frontend/backend"
layers remains one inline deliverable, while "storefront" plus "pipeline service" may be promoted as
two separate deliverables within the larger initiative.

Follow the existing fail-closed classifier pattern used elsewhere in the coordinator:
`OutcomeSpecReplyClassifier` / `CopilotOutcomeSpecReplyClassifier` run one empty-tools Copilot
completion, require structured JSON output, and default to the safe non-advancing decision when the
model fails or the response is unparseable. Promotion classification should mirror that pattern and
default to inline on any failure.

The server, not the model, generates the persisted reason. Use `Explicit [run] override.`,
`LLM judged this dependency component to be an independent deliverable.`, or, for another node
pulled across the boundary by component closure, `Promoted with dependency component rooted at
{StoryKey}.`

### Initial state and parent-run behavior

- Every promoted story is created with `BacklogTaskState.Backlog`.
- Promotion never silently commits or starts a story. Existing move-to-Ready and pickup behavior
  remains the execution gate.
- Promoted stories are excluded from the current run's `WorkPlan`; the remaining inline stories
  continue through normal dispatch and collective assembly.
- The parent PRD run does not wait for promoted runs and is not an implicit prerequisite.
- If every story is promoted, persist a zero-subtask `WorkPlan` with `Status = "delegated"`.
  `CoordinatorWorkflowFactory` must return a delegated orchestration result, and
  `CoordinatorRunService` must terminalize that planning-only coordinator run as
  `RunStatus.Completed` with result `delegated_to_backlog`, skipping dispatch and collective
  assembly. This is the sole new-run use of the backward-compatible `Completed` status.

`OrchestrateAsync` therefore changes from:

```csharp
Task OrchestrateAsync(CoordinatorDraftInput input, CancellationToken ct)
```

to:

```csharp
Task<CoordinatorOrchestrationResult> OrchestrateAsync(
    CoordinatorDraftInput input,
    CancellationToken ct)

public sealed record CoordinatorOrchestrationResult(
    int WorkPlanId,
    int InlineSubtaskCount,
    IReadOnlyList<BacklogTaskId> PromotedTaskIds)
{
    public bool IsDelegated => InlineSubtaskCount == 0 && PromotedTaskIds.Count > 0;
}
```

## Domain and persistence model

### `BacklogTask` additions

Add these nullable properties to `packages/Agentweaver.Domain/BacklogTask.cs`:

```csharp
/// Top-level Coordinator run whose confirmed outcome spec produced this story.
public RunId? ParentPrdRunId { get; init; }

/// Stable story key within ParentPrdRunId; used for idempotent promotion retries.
public string? PromotionKey { get; init; }

/// Human-readable explanation of the override/classification decision, max 500 characters.
public string? PromotionReason { get; init; }
```

For a promoted task all three values are non-null. For manual and pre-existing tasks they are null.
`ParentPrdRunId` is provenance only; it is deliberately not `Run.ParentRunId`, which represents a
worker child inside one coordinator topology.

### Dependency entity

Add `packages/Agentweaver.Domain/BacklogTaskDependency.cs`:

```csharp
public sealed record BacklogTaskDependency
{
    public required ProjectId ProjectId { get; init; }
    public required BacklogTaskId TaskId { get; init; }
    public required BacklogTaskId DependsOnTaskId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

The first implementation supports only hard, same-project dependencies with one satisfaction mode:
the prerequisite's linked run is Merged. Do not add type or satisfaction-mode columns until another
mode is implemented.

### Database shape

Add nullable columns to `backlog_tasks`:

```sql
parent_prd_run_id TEXT NULL
promotion_key TEXT NULL
promotion_reason TEXT NULL
```

Add:

```sql
CREATE UNIQUE INDEX idx_backlog_tasks_parent_promotion_key
    ON backlog_tasks(parent_prd_run_id, promotion_key)
    WHERE parent_prd_run_id IS NOT NULL AND promotion_key IS NOT NULL;

CREATE TABLE backlog_task_dependencies (
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    depends_on_task_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (task_id, depends_on_task_id),
    FOREIGN KEY (task_id) REFERENCES backlog_tasks(task_id) ON DELETE CASCADE,
    FOREIGN KEY (depends_on_task_id) REFERENCES backlog_tasks(task_id) ON DELETE RESTRICT,
    CHECK (task_id <> depends_on_task_id)
);

CREATE INDEX idx_backlog_task_dependencies_project_task
    ON backlog_task_dependencies(project_id, task_id);
CREATE INDEX idx_backlog_task_dependencies_prerequisite
    ON backlog_task_dependencies(depends_on_task_id);
```

The service validates that both ends belong to `project_id`; the duplicated project id keeps every
query project-scoped. Apply equivalent EF mappings and a PostgreSQL migration.

### Store/service signatures

Add a transaction-owning promotion operation rather than issuing one insert per story:

```csharp
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
```

`PromoteAsync` validates the parent as a top-level Coordinator run in the same project, validates
unique keys/titles and an acyclic in-batch graph, appends all tasks to Backlog, and inserts all edges
in one transaction. It is idempotent on `(parent_prd_run_id, promotion_key)`:

- an identical retry returns the existing task and does not duplicate edges;
- a reused key with different title, description, or dependencies fails with
  `promotion_key_conflict`;
- no partial task/edge batch is committed.

The coordinator calls this service before persisting the inline plan. A retry can safely repeat the
promotion batch. If a retry's newly generated payload conflicts with an already persisted key, the
coordinator fails visibly rather than creating duplicate stories.

Add batched dependency reads to `IBacklogTaskStore`:

```csharp
Task<IReadOnlyList<BacklogTaskDependency>> ListDependenciesAsync(
    ProjectId projectId,
    IReadOnlyCollection<BacklogTaskId> taskIds,
    CancellationToken ct = default);

Task<IReadOnlyList<BacklogDependencyStatus>> ListDependencyStatusesAsync(
    ProjectId projectId,
    IReadOnlyCollection<BacklogTaskId> taskIds,
    CancellationToken ct = default);
```

`BacklogDependencyStatus` contains `TaskId`, `DependsOnTaskId`, prerequisite title, optional
prerequisite `RunId`, optional `RunStatus`, and `IsSatisfied`. Board reads must use the batched
method, not an N+1 query.

```csharp
public sealed record BacklogDependencyStatus(
    BacklogTaskId TaskId,
    BacklogTaskId DependsOnTaskId,
    string DependsOnTitle,
    RunId? DependsOnRunId,
    RunStatus? DependsOnRunStatus,
    bool IsSatisfied);
```

## Dependency coordination semantics

A dependency is satisfied if and only if:

```text
prerequisite.BacklogTask.RunId is not null
AND prerequisite linked Run.Status == RunStatus.Merged
```

`Completed`, `AwaitingReview`, `Declined`, `Failed`, `MergeFailed`, and an archived task do not
satisfy the edge. Merged is terminal, so eligibility cannot regress after satisfaction.

Backlog state and execution eligibility remain separate:

- `State == Backlog`: not committed, regardless of dependencies.
- `State == Ready` with an unresolved edge: committed but blocked.
- `State == Ready` with every edge satisfied: ready to start and claimable.
- `State == Claimed`: already has a run; dependency edits are not supported.

No background "unblock" write is required. Eligibility is derived from current task/run rows. When
the last prerequisite becomes Merged, the next board read reports the task unblocked and the next
heartbeat may claim it.

All three pickup paths must use the same predicate:

```sql
NOT EXISTS (
    SELECT 1
    FROM backlog_task_dependencies d
    JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
    LEFT JOIN runs r ON r.run_id = prerequisite.run_id
    WHERE d.task_id = candidate.task_id
      AND (
          prerequisite.archived_at IS NOT NULL
          OR prerequisite.run_id IS NULL
          OR r.run_id IS NULL
          OR r.status <> 'merged'
      )
)
```

Apply it to:

1. `ListReadyForClaimAsync`;
2. `CountReadyForPickupAsync`; and
3. the conditional claim inside `TryClaimAndReserveCoordinatorRunAsync`.

The third check is mandatory even when listing filtered candidates; it closes the claim race.

## API and UI contract

### Promotion endpoint

Add:

```http
POST /api/projects/{projectId}/backlog/promotions
```

Request:

```json
{
  "parent_prd_run_id": "run-id",
  "stories": [
    {
      "key": "define-storage-contract",
      "title": "Define the storage contract",
      "description": "Exact scope passed to the story coordinator.",
      "promotion_reason": "llm_judged_independent_deliverable",
      "depends_on_keys": []
    },
    {
      "key": "implement-api",
      "title": "Implement the API",
      "description": "Exact scope passed to the story coordinator.",
      "promotion_reason": "same dependency component as define-storage-contract",
      "depends_on_keys": ["define-storage-contract"]
    }
  ]
}
```

The endpoint accepts 1..50 stories, returns `200 OK` for both first creation and idempotent retry,
and returns:

```json
{ "tasks": [/* BacklogTaskDto */], "created_count": 2 }
```

Return `400 invalid_promotion_graph` for missing keys, duplicate keys, self-edges, unknown dependency
keys, cycles, or an out-of-range batch; `404` for an unknown/wrong-project parent run; and
`409 promotion_key_conflict` for a non-identical idempotency-key reuse.

Add:

```http
GET /api/projects/{projectId}/backlog/tasks/{taskId}
```

It returns the enriched `BacklogTaskDto` below. Existing authorization and project-scoping rules
apply to both endpoints.

### Read model

Add to both `BacklogTaskDto` and `TaskCardDto`:

```json
{
  "parent_prd_run_id": "run-id-or-null",
  "promotion_key": "implement-api-or-null",
  "promotion_reason": "text-or-null",
  "depends_on_task_ids": ["task-id"],
  "is_blocked": true,
  "blocked_reason": "Waiting for 1 prerequisite task to merge.",
  "is_ready_to_start": false,
  "blocking_dependencies": [
    {
      "task_id": "prerequisite-task-id",
      "title": "Define the storage contract",
      "run_id": "run-id-or-null",
      "run_status": "awaiting_review-or-null"
    }
  ]
}
```

Definitions:

- `is_blocked` is true when at least one dependency is unsatisfied, in any task state;
- `blocked_reason` is null when unblocked and otherwise uses the exact pluralized form
  `Waiting for {N} prerequisite task(s) to merge.`;
- `is_ready_to_start` is true only when state is Ready, the task is unarchived/unclaimed, and
  `is_blocked` is false; and
- `blocking_dependencies` contains only unsatisfied edges, ordered by task id.

The board keeps the card in the Ready column and renders a blocked badge plus `blocked_reason`.
There is no new Blocked column and no new `BacklogTaskState`.

## Coordinator implementation changes

In `CoordinatorOrchestratorExecutor.OrchestrateAsync`:

1. extend the decomposition prompt/parser with `story_key` and `promotion_override`;
2. run existing cycle breaking before partitioning;
3. apply the exact override + component-closure algorithm, classifying only components that have no
   explicit override;
4. convert promoted drafts to `PromotedStoryInput` values. Their description must include the
   draft scope and declared output paths so the future story coordinator has complete context;
5. call `IBacklogPromotionService.PromoteAsync` once for the promoted partition;
6. assign roster/model and call `PersistPlanAsync` only for the inline partition;
7. translate `depends_on` indices separately into backlog edges or `SubtaskDependency` edges; and
8. emit `coordinator.stories_promoted` with parent run id, task ids, keys, and reasons, then emit the
   existing work-plan event for inline work.

`CoordinatorWorkflowFactory` consumes `CoordinatorOrchestrationResult`. `CoordinatorRunService`
handles `IsDelegated` as described above. Mixed plans continue through the current dispatch path.
`CoordinatorDispatchService` and `CollectiveAssemblyPipeline` see only inline subtasks and require
no dependency logic changes. `WorkflowSelector` continues to select only the parent run's workflow;
each promoted task selects its own workflow when pickup creates its coordinator run.

`BacklogDecomposeEndpoints` remains the manual flat-import feature in v1. It does not apply the
promotion classifier or create dependency edges. This avoids silently changing confirmed file-import
behavior; it may adopt the shared batch contract in a later issue.

## MCP and native coordinator tools

Add two external tools to `BacklogTools`:

```csharp
public sealed record PromotedStoryToolInput(
    string Key,
    string Title,
    string Description,
    string PromotionReason,
    IReadOnlyList<string> DependsOnKeys);

Task<string> BacklogPromoteStoriesAsync(
    string project_id,
    string parent_prd_run_id,
    IReadOnlyList<PromotedStoryToolInput> stories,
    CancellationToken ct = default);

Task<string> BacklogGetTaskAsync(
    string project_id,
    string task_id,
    CancellationToken ct = default);
```

They call the promotion POST and task GET endpoints respectively. `backlog_get_board` automatically
receives the enriched card fields and is the bulk "what is blocked?" query.

For #284 compatibility, register matching `backlog_promote_stories` and `backlog_get_task`
functions in `AgentweaverApiTools.ToolNames`/`Build` for the Coordinator's native loopback surface.
The atomic batch tool supersedes asking the model to call `backlog_capture_task` N times: individual
calls cannot safely resolve forward references or commit a DAG atomically. Both the direct
coordinator path and MCP/native adapters must invoke the same API/service contract; they must not
implement separate promotion rules.

## Migration and backward compatibility

Implement both storage paths:

- SQLite startup migration in `SqliteDb`, plus mappings/selects in `SqliteBacklogTaskStore`;
- EF `BacklogTaskRecord`, a dependency record/`DbSet`, model mappings, and a new PostgreSQL
  migration;
- `EfBacklogTaskStore`; and
- `SqliteToPostgresMigrator` for all three new columns and dependency rows.

Existing tasks receive null promotion fields and have no dependency rows. Therefore:

- their API fields are null/empty;
- `is_blocked` is false;
- Ready tasks remain claimable under the new `NOT EXISTS` predicate; and
- existing task state, ordering, claim, archive, and run linkage are unchanged.

Existing API request bodies remain valid because all new capture/read fields are additive. Existing
MCP tool names and signatures remain valid.

## Failure behavior

- Promotion validation or persistence failure fails the parent orchestration before inline dispatch;
  it must not silently rewrite an explicit `[run]` override.
- If the independence classifier is unavailable, times out, or returns unparseable JSON, the server
  must fail closed to inline for that component.
- A failed/declined prerequisite leaves dependents Ready-but-blocked. There is no automatic cascade
  failure or cancellation.
- Deleting a prerequisite referenced by another task returns `409 task_is_dependency`. Archiving it
  is allowed but does not satisfy the edge.
- A promoted task may be deleted while unclaimed only when no task depends on it.
- Dependencies are immutable after batch creation in v1.

## Explicitly out of scope

1. Soft, optional, artifact, branch, or "completed without merge" dependency modes.
2. Cross-project dependencies.
3. Adding or editing arbitrary dependency edges after promotion.
4. Automatically moving promoted stories from Backlog to Ready.
5. Automatically prioritizing, assigning owners, or selecting workflows for promoted stories.
6. Making the parent PRD run wait for, aggregate, or merge promoted story runs.
7. Propagating cancellation/failure from a prerequisite to dependents.
8. Stacked-branch inclusion or reuse of `DependencyBranchInclusion`.
9. Changing manual markdown decomposition in `BacklogDecomposeEndpoints`.
10. A new epic entity or a new Backlog/Ready/Blocked state/column.

## Acceptance criteria

1. A component that is only multiple technical layers of one feature remains inline even if it is
   large or complex.
2. A component judged to be its own separately shippable deliverable is promoted.
3. `[run]` promotes a component the classifier would otherwise inline, and `[inline]` keeps a
   classifier-positive component inline.
4. Promotion creates one Backlog task per promoted story, with parent run, key, reason, and exact
   dependency edges; repeating the same batch creates nothing.
5. A Ready task with an unclaimed, in-progress, failed, declined, or AwaitingReview prerequisite is
   excluded from list, metric, and atomic claim paths.
6. After the prerequisite run becomes Merged, no unblock write is needed: the dependent reads as
   `is_blocked=false`, `is_ready_to_start=true`, and is picked up by the next heartbeat.
7. Board, task GET, `backlog_get_board`, and `backlog_get_task` expose the same blocker set.
8. A mixed decomposition dispatches only inline subtasks; promoted stories are not child runs of the
   parent coordinator.
9. An all-promoted decomposition completes the parent as `Completed/delegated_to_backlog` without
   dispatching or entering collective assembly.
10. Legacy Ready tasks with no dependency rows are still claimed exactly as before on SQLite and
    PostgreSQL.
