# Implementation Design & Cross-Layer Contracts: Project Backlog & Workflow Kanban Board (Feature 009)

**Author**: Tank (Backend Engineer) — revision owner after reviewer rejection (Reviewer Rejection Protocol)
**Original author**: Morpheus (Runtime Engineer) — locked out for this revision
**Date**: 2026-06-19 (revised)
**Status**: Design — re-submitted for rubber-duck re-review of its rejected items. No product code written.
**Spec**: `specs/009-backlog-kanban-board/spec.md` (clarified 2026-06-19)
**Constitution**: `.specify/memory/constitution.md` v1.4.0

**Revision summary**: This revision resolves the seven blocking issues from the rejected review and encodes the user's product decision (PATH A — FULL COORDINATOR PICKUP): each claimed Ready item becomes a Squad Coordinator run (decompose into subtasks, dispatch child runs) reusing the existing coordinator (FR-021), running unattended while a named human (the task's `CapturedBy`) stays accountable. The previously proposed single-agent project-run path is removed.

This document is the binding contract every implementer follows literally: concrete names, signatures, routes, JSON shapes, DDL, and ordering. Where it says MUST, it is a hard requirement; where it says SHOULD, deviation requires a note in Complexity Tracking.

---

## 0. Architecture overview

The feature adds one new domain aggregate (`BacklogTask`), one persistence store, one hosted background service (the coordinator heartbeat), one read-model projector (board composition + workflow-stage columns), a set of API endpoints, mirrored MCP tools, and a Kanban board on the project homepage.

The pickup path reuses the existing Squad Coordinator (Feature 008) verbatim: a claimed Ready task starts a coordinator run through `CoordinatorRunService`, which drafts an outcome spec, decomposes it into subtasks, and dispatches child runs — exactly the path the interactive coordinator endpoints drive today. The board's dynamic columns are the coordinator topology's stages (`CoordinatorGraphDescriptor`), and each coordinator-run card is placed in the column for the stage its run currently occupies, computed from the persisted coordinator work-plan state.

Data and control flow:

```
Web / MCP --HTTP--> API endpoints --> IBacklogTaskStore (SqliteBacklogTaskStore)
                                   \-> BoardProjectionService (read model)
                                          |- workflow columns  <- WorkflowStageProjector(CoordinatorGraphDescriptor.BuildEmpty)
                                          \- run-backed cards   <- SqliteRunStore.GetRunsByProjectAsync
                                                                   + CoordinatorRunService work-plan stage state

CoordinatorHeartbeatService (BackgroundService, PeriodicTimer)
   every tick, per eligible project:
     IBacklogTaskStore.ListReadyForClaimAsync(projectId, top-N by priority)
       -> CoordinatorPickupService.TryPickupAsync(project, task)
            (1) build reserved coordinator Run (fresh RunId, AgentName "Coordinator")
            (2) IBacklogTaskStore.TryClaimAndReserveCoordinatorRunAsync(...)   [ONE SQLite transaction:
                    atomic claim UPDATE + coordinator run INSERT + workflow_run INSERT]
            (3) on commit: CoordinatorRunService.StartReservedCoordinatorRunAsync(...) [activate + unattended confirm]
            (4) on reserve rollback: task stays Ready (priority preserved); nothing persisted
            (5) on activation failure: run terminalized Failed; task stays Claimed (no silent re-queue)
```

The reservation (claim + coordinator run row + workflow_run row) is one atomic SQLite transaction, so the board only ever observes `(Ready, run_id IS NULL)` or `(Claimed, actual persisted run)` — never an orphan in between (blocking issue #1). Starting the coordinator workflow happens after the transaction commits; a start failure terminalizes the already-persisted run as `Failed` and the task stays `Claimed` (FR-012, no silent re-queue), whereas a reservation failure (project deleting/unavailable) rolls the whole transaction back and leaves the task `Ready` (blocking issue #2).

---

## 1. Data model

### 1.1 Value-object id — `BacklogTaskId`

New file `packages/Agentweaver.Domain/BacklogTaskId.cs`, mirroring `ProjectId`/`RunId`:

```csharp
namespace Agentweaver.Domain;

public readonly record struct BacklogTaskId(Guid Value)
{
    public static BacklogTaskId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static BacklogTaskId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out BacklogTaskId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new(g); return true; }
        id = default; return false;
    }
}
```

### 1.2 Bucket/state enum — `BacklogTaskState`

New file `packages/Agentweaver.Domain/BacklogTaskState.cs`:

```csharp
namespace Agentweaver.Domain;

public enum BacklogTaskState
{
    Backlog,   // captured-but-not-committed
    Ready,     // committed, awaiting coordinator pickup (claim gate open while run_id IS NULL)
    Claimed,   // claimed by a heartbeat; RunId set to a persisted coordinator run; card renders from run state
}
```

API string mapping (new `apps/Agentweaver.Api/Contracts/BacklogTaskStateExtensions.cs`, mirroring `RunStatusExtensions`): `Backlog -> "backlog"`, `Ready -> "ready"`, `Claimed -> "claimed"`.

### 1.3 Domain record — `BacklogTask`

New file `packages/Agentweaver.Domain/BacklogTask.cs`:

```csharp
namespace Agentweaver.Domain;

public sealed record BacklogTask
{
    public required BacklogTaskId Id { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required BacklogTaskState State { get; init; }
    /// <summary>
    /// Lexicographic fractional ordering key (e.g. "n", "u", "g3"). Sorts ascending = top of bucket
    /// first = highest pickup priority. Unique per (project_id, state) bucket for the unclaimed
    /// buckets (backlog/ready). See section 1.4.
    /// </summary>
    public required string OrderKey { get; init; }
    /// <summary>The accountable human (signed-in user) who captured the task (Principle IX). Becomes
    /// the coordinator run's SubmittingUser AND the confirmedBy on the unattended outcome-spec confirm.</summary>
    public required string CapturedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Set when the task is first moved Backlog -> Ready. Null while in Backlog. Also a
    /// pickup tie-breaker (section 1.4).</summary>
    public DateTimeOffset? CommittedAt { get; init; }
    /// <summary>Set atomically with the Ready -> Claimed transition.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }
    /// <summary>The 1:1 coordinator run this task produced. Non-null iff State == Claimed.</summary>
    public RunId? RunId { get; init; }
}
```

### 1.4 Ordering scheme (FR-016, FR-018a) + uniqueness and pickup determinism

Ordering is a **lexicographic fractional rank key** stored as `TEXT order_key`, **unique within a `(project_id, state)` bucket for the unclaimed buckets** (`backlog`, `ready`). Rationale: stable reordering of a single card needs only one row update (insert a key strictly between its new neighbours), avoiding a full re-number of the bucket and integer-gap exhaustion.

- The base alphabet is lowercase `a`-`z`. "Top of bucket" = smallest key = highest priority.
- Generation helper `OrderKey.Between(string? lo, string? hi)` returns a key strictly between `lo` and `hi` (either may be null to mean "before first" / "after last"). Implemented as base-26 midpoint generation with on-demand digit extension when neighbours are adjacent. Lives in `packages/Agentweaver.Domain/OrderKey.cs`, pure/unit-tested.
- On capture into Backlog, the new task gets `OrderKey.Between(currentMaxKeyInBucket, null)` (appended to the bottom).
- On move between buckets the task receives a fresh key generated for the destination bucket (default: appended to the bottom, unless a target index is supplied — see reorder/move endpoints).

**Uniqueness + retry-on-conflict (blocking issue #6).** A `UNIQUE` index covers `(project_id, state, order_key)` for the unclaimed buckets only (partial index, section 2.3). Two concurrent reorders that compute the same midpoint key would otherwise collide; the store's move/reorder `Try*` methods MUST treat a SQLite `UNIQUE` constraint violation (SQLITE_CONSTRAINT) as a soft conflict and retry: re-read the destination neighbours and regenerate `OrderKey.Between` with a freshly extended key, up to 5 attempts, then surface `409 order_conflict` if still colliding. `Claimed` rows are excluded from the unique index because a claimed task is removed from the priority buckets and its stale `order_key` must never block a future insert.

**Deterministic pickup tie-breaker (blocking issue #6).** `ListReadyForClaimAsync` orders by `order_key ASC, committed_at ASC, task_id ASC` and caps at `limit`. The two trailing keys make top-N selection fully deterministic even in the pathological case of two equal `order_key`s slipping past the unique index during a retry window, so repeated/overlapping heartbeats always see the same top-N in the same order (supports SC-002/SC-003).

`OrderKey` is opaque to clients; clients send a target neighbour index, never raw keys (section 5).

### 1.5 Atomic claim + coordinator-run reservation (FR-008, FR-009, FR-012, exactly-once)

Claim and coordinator-run reservation are a single atomic SQLite transaction so there is never a board-visible orphan (blocking issues #1, #2). The store method `TryClaimAndReserveCoordinatorRunAsync` (section 2.1) executes, on one connection/transaction:

```sql
-- (a) exactly-once, project-scoped claim gate
UPDATE backlog_tasks
   SET state      = 'claimed',
       run_id     = $runId,
       claimed_at = $claimedAt
 WHERE task_id    = $taskId
   AND project_id = $projectId        -- project scoping (blocking issue #7)
   AND state      = 'ready'
   AND run_id IS NULL;                -- the exactly-once gate
-- rows affected MUST be exactly 1, else ROLLBACK and return Lost.

-- (b) persist the coordinator run row, gated on the project still being active.
--     origin = 'backlog_pickup' is the DURABLE run-origin marker (blocking issue: unattended
--     recovery). It is written atomically with the claim so recovery never has to infer origin
--     from per-project pickup settings. Interactive coordinator runs are inserted elsewhere with
--     the schema default origin = 'interactive'.
INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                  submitting_user, status, started_at, ended_at, result,
                  worktree_path, worktree_branch, project_id, model_id,
                  agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id, origin)
SELECT $runId, $repo, $branch, $modelSource, $task,
       $user, 'in_progress', $startedAt, NULL, NULL,
       NULL, NULL, $projectId, $modelId,
       'Coordinator', NULL, $workflowRunId, NULL, NULL, 'backlog_pickup'
WHERE EXISTS (SELECT 1 FROM projects WHERE project_id = $projectId AND state = 'active');
-- rows affected MUST be exactly 1, else ROLLBACK and return ProjectUnavailable.

-- (c) persist the workflow_run envelope
INSERT INTO workflow_runs (workflow_run_id, project_id, task, submitting_user, started_at)
VALUES ($workflowRunId, $projectId, $task, $user, $startedAt);

COMMIT;   -- return Won
```

Result enum `ClaimReserveResult { Won, Lost, ProjectUnavailable }`. Properties:

- **No orphan**: the run row exists only if the claim committed in the same transaction; the board never sees a `Claimed` task whose `run_id` has no run row (resolves the original design's risk #7). While `Ready`, `run_id IS NULL`, so no run-backed card exists either.
- **Exactly-once (FR-009)**: a second/overlapping/delayed heartbeat sees `state <> 'ready'` (or `run_id IS NOT NULL`) → step (a) affects zero rows → `ROLLBACK` → `Lost`, no duplicate run. Covers "Coordinator misses or overlaps heartbeats."
- **Ready -> Backlog race (FR-007)**: a move-back is itself a conditional UPDATE gated on `state='ready' AND run_id IS NULL`; exactly one of {move-back, claim} wins. If claim wins, move-back returns `409 task_already_claimed`; if move-back wins, the claim's step (a) affects zero rows → `Lost`. Covers "moved to Ready then back to Backlog before a heartbeat."
- **Reservation failure (blocking issue #2)**: if the project went `Deleting`/inactive between the eligibility check and the transaction, step (b) affects zero rows → `ROLLBACK` → `ProjectUnavailable`. The task is untouched (`Ready`, priority position preserved) and is retried on a later heartbeat. The item is never consumed or dropped.
- **Activation failure after commit (FR-012)**: once the transaction commits, the run row and the 1:1 task->run link exist. If starting the coordinator workflow then throws (section 3.4), the run is terminalized `Failed` and the task stays `Claimed` pointing at the `Failed` run (shown in the terminal column). The task is NOT silently re-queued; a deliberate re-queue is a future user action, out of scope here.
- **Durable run origin (unattended-recovery flaw fix, FR-021)**: step (b) stamps `origin = 'backlog_pickup'` on the run row in the same transaction as the claim. This is the single source of truth for "this is a backlog-pickup coordinator run." Recovery (section 3.6) keys the unattended outcome-spec confirm on this persisted marker, never on inference from per-project pickup settings, so an interactive coordinator run that happens to be `awaiting_confirmation` during a restart can never be auto-confirmed (Principles IX/X/XI).

`$runId` and `$workflowRunId` are generated by `CoordinatorPickupService` before the transaction, binding the claim and the run identity atomically.

---

## 2. Persistence

### 2.1 Interface — `IBacklogTaskStore`

New file `packages/Agentweaver.Domain/IBacklogTaskStore.cs`. **Every read and mutation is project-scoped** (blocking issue #7): each method takes `ProjectId` and every SQL `WHERE` includes `project_id = $projectId`, so a task can never be read or mutated through the wrong project route.

```csharp
namespace Agentweaver.Domain;

public enum ClaimReserveResult { Won, Lost, ProjectUnavailable }

public interface IBacklogTaskStore
{
    Task InsertAsync(BacklogTask task, CancellationToken ct = default);

    /// <summary>Project-scoped get. Returns null if the id does not exist OR belongs to another project.</summary>
    Task<BacklogTask?> GetAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default);

    /// <summary>Durable run-id -> backlog task lookup over the unique idx_backlog_tasks_run index.
    /// Returns the (at most one) Claimed task whose run_id == <paramref name="runId"/>, or null.
    /// Used by RecoverInterruptedRunsAsync (section 3.6) to resolve the accountable CapturedBy for a
    /// backlog-pickup coordinator run after a restart, without per-project-settings inference.</summary>
    Task<BacklogTask?> GetByRunIdAsync(RunId runId, CancellationToken ct = default);

    /// <summary>All tasks for a project (Backlog + Ready + Claimed), ordered by (state, order_key).</summary>
    Task<IReadOnlyList<BacklogTask>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>Ready, unclaimed tasks for a project ordered by (order_key ASC, committed_at ASC,
    /// task_id ASC), capped at <paramref name="limit"/>. Deterministic top-N claim candidates
    /// (section 1.4).</summary>
    Task<IReadOnlyList<BacklogTask>> ListReadyForClaimAsync(
        ProjectId projectId, int limit, CancellationToken ct = default);

    /// <summary>Updates title/description only, gated on project_id. Returns false if not found in project.</summary>
    Task<bool> UpdateContentAsync(
        ProjectId projectId, BacklogTaskId id, string title, string? description, CancellationToken ct = default);

    /// <summary>Deletes a task, gated on project_id AND state IN ('backlog','ready') AND run_id IS NULL.
    /// Returns false if Claimed (cannot delete a run-backed task) or not found in project.</summary>
    Task<bool> TryDeleteAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default);

    /// <summary>Atomic Backlog -> Ready. Sets committed_at and the destination order_key. Gated on
    /// project_id AND state = 'backlog'. Retries on order_key UNIQUE conflict (section 1.4).</summary>
    Task<bool> TryMoveToReadyAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default);

    /// <summary>Atomic Ready -> Backlog, permitted only while unclaimed. Gated on project_id AND
    /// state = 'ready' AND run_id IS NULL. Returns false if already claimed or not found.</summary>
    Task<bool> TryMoveToBacklogAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, CancellationToken ct = default);

    /// <summary>Reorders a task within its CURRENT bucket by assigning a new order_key. Gated on
    /// project_id AND state = $expectedState AND run_id IS NULL. Retries on UNIQUE conflict.</summary>
    Task<bool> TryReorderAsync(
        ProjectId projectId, BacklogTaskId id, BacklogTaskState expectedState, string newOrderKey, CancellationToken ct = default);

    /// <summary>
    /// Atomic, exactly-once claim + coordinator-run reservation (section 1.5). In ONE transaction:
    /// (a) Ready -> Claimed gated on project_id AND state='ready' AND run_id IS NULL, binding run_id;
    /// (b) INSERT the coordinator <paramref name="coordinatorRun"/> row gated on the project being
    ///     active, stamping the durable run-origin marker origin='backlog_pickup';
    /// (c) INSERT the <paramref name="workflowRun"/> envelope. Returns Won/Lost/ProjectUnavailable.
    /// </summary>
    Task<ClaimReserveResult> TryClaimAndReserveCoordinatorRunAsync(
        ProjectId projectId,
        BacklogTaskId id,
        Run coordinatorRun,
        WorkflowRun workflowRun,
        DateTimeOffset claimedAt,
        CancellationToken ct = default);
}
```

Notes:
- The store crosses into the `runs`/`workflow_runs` tables only inside `TryClaimAndReserveCoordinatorRunAsync`, by design, because the atomic invariant (no orphan) requires the claim and the run insert to commit together. The INSERT SQL mirrors `SqliteRunStore.TryCreateProjectRunAsync` (`apps/Agentweaver.Api/Infrastructure/SqliteRunStore.cs:426`) and `WorkflowRun` insert (`ProjectEndpoints.cs:384`) so there is one source of truth for column lists; implementers MUST keep the column list in sync (covered by a store test that round-trips the reserved run via `SqliteRunStore.GetAsync`). The one intentional difference is that this insert sets `origin = 'backlog_pickup'` (the durable run-origin marker, sections 1.5 and 2.3), whereas the interactive run-insert paths rely on the schema default `origin = 'interactive'`; the round-trip test MUST assert the reserved run reads back with `Origin == RunOrigin.BacklogPickup`.
- Neighbour-key lookups to compute `newOrderKey` for move/reorder are done in the endpoint/service layer from the destination bucket's neighbour keys (read via `ListByProjectAsync`); the store accepts an already-computed `newOrderKey` and owns only the UNIQUE-conflict retry.

### 2.2 Implementation — `SqliteBacklogTaskStore`

New file `apps/Agentweaver.Api/Infrastructure/SqliteBacklogTaskStore.cs`, following `SqliteProjectStore`/`SqliteRunStore` conventions exactly (constructor takes `SqliteDb`, `OpenConnectionAsync`, `AddWithValue`, ISO-8601 `O` timestamps via a private `Ts` helper, `DateTimeStyles.RoundtripKind` on read, conditional UPDATEs returning `rows > 0`).

- Each single-row `Try*` method is a conditional `UPDATE ... WHERE ...` returning `ExecuteNonQueryAsync() > 0`, always including `project_id = $projectId`.
- `TryClaimAndReserveCoordinatorRunAsync` uses one `connection.BeginTransaction()` spanning the three statements in section 1.5, mirroring the transaction shape of `SqliteRunStore.TryCreateProjectRunAsync`. It inspects rows-affected after (a) and (b) and `ROLLBACK`s with the appropriate result; only an all-success path `COMMIT`s and returns `Won`.
- Move/reorder methods wrap their UPDATE in the retry-on-`SqliteException`(`SQLITE_CONSTRAINT`) loop described in section 1.4 (the caller passes a fresh `newOrderKey` per attempt via a `Func<string>` key factory, or the store re-reads neighbours itself — implementer's choice, but the retry MUST be inside the store so the conflict never leaks as a 500).

### 2.3 DDL — add to `SqliteDb.SchemaSql`

Append to the `SchemaSql` constant in `apps/Agentweaver.Api/Infrastructure/SqliteDb.cs`:

```sql
CREATE TABLE IF NOT EXISTS backlog_tasks (
    task_id       TEXT PRIMARY KEY,
    project_id    TEXT NOT NULL,
    title         TEXT NOT NULL,
    description   TEXT,
    state         TEXT NOT NULL,            -- 'backlog' | 'ready' | 'claimed'
    order_key     TEXT NOT NULL,
    captured_by   TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    committed_at  TEXT,
    claimed_at    TEXT,
    run_id        TEXT,                      -- non-null iff state = 'claimed'
    FOREIGN KEY (project_id) REFERENCES projects (project_id) ON DELETE CASCADE
);

-- Project scoping + ordered top-N reads (FR-003/FR-008/FR-016).
CREATE INDEX IF NOT EXISTS idx_backlog_tasks_project_state
    ON backlog_tasks (project_id, state, order_key);

-- order_key uniqueness per (project_id, state) for the UNCLAIMED buckets only (blocking issue #6).
-- Claimed rows are excluded so a stale claimed order_key never blocks a future insert.
CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_orderkey_unique
    ON backlog_tasks (project_id, state, order_key)
    WHERE state IN ('backlog','ready');

-- One-task-to-at-most-one-run invariant at the storage layer.
CREATE UNIQUE INDEX IF NOT EXISTS idx_backlog_tasks_run
    ON backlog_tasks (run_id) WHERE run_id IS NOT NULL;
```

`ON DELETE CASCADE` is honoured because `PRAGMA foreign_keys=ON` is already set in `OpenConnectionAsync`, so project deletion cleans up its tasks. The table is created via `SchemaSql` (`CREATE TABLE IF NOT EXISTS`), so no `TryAlterAsync` migration is needed for the fresh table.

**Durable run-origin column on `runs` (unattended-recovery flaw fix).** The `runs` table gains a persisted origin marker so recovery can identify backlog-pickup coordinator runs without inferring from per-project pickup settings. Because `runs` is a pre-existing table, it is added via the idempotent `TryAlterAsync` migration path in `SqliteDb.EnsureCreatedAsync` (alongside the section 2.4 migrations), so existing rows default to `interactive`:

```csharp
await TryAlterAsync(connection,
    "ALTER TABLE runs ADD COLUMN origin TEXT NOT NULL DEFAULT 'interactive';", ct);   // 'interactive' | 'backlog_pickup'

// Recovery scans interrupted coordinator runs by origin; index keeps the startup sweep cheap.
await TryAlterAsync(connection,
    "CREATE INDEX IF NOT EXISTS idx_runs_origin_status ON runs (origin, status);", ct);
```

Only the section 1.5 claim+reserve transaction writes `origin = 'backlog_pickup'`; every other run-insert path (interactive coordinator runs, project runs, child runs) leaves the `interactive` default. Domain change — add to `Run` (`packages/Agentweaver.Domain/Run.cs`) and a small enum:

```csharp
public enum RunOrigin { Interactive, BacklogPickup }   // packages/Agentweaver.Domain/RunOrigin.cs

// on the Run record:
public RunOrigin Origin { get; init; } = RunOrigin.Interactive;   // persisted as TEXT 'interactive'|'backlog_pickup'
```

`SqliteRunStore` maps the column: `Map` reads `origin` (defaulting to `Interactive` when absent), `SelectSql` includes it, and the round-trip test in section 2.1 asserts a reserved run reads back `Origin == RunOrigin.BacklogPickup`. The interactive run-insert paths are unchanged (they omit the column and inherit the default).

### 2.4 Per-project pickup configuration (FR-008a + unattended seeding)

Three project-scoped pickup settings stored as new columns on `projects` (cadence/automation is orthogonal to provider/model settings). Added via the existing idempotent `TryAlterAsync` migration path in `SqliteDb.EnsureCreatedAsync`, so existing rows get the defaults:

```csharp
await TryAlterAsync(connection,
    "ALTER TABLE projects ADD COLUMN max_ready_per_heartbeat INTEGER NOT NULL DEFAULT 3;", ct);   // FR-008a
await TryAlterAsync(connection,
    "ALTER TABLE projects ADD COLUMN pickup_autopilot INTEGER NOT NULL DEFAULT 1;", ct);           // unattended
await TryAlterAsync(connection,
    "ALTER TABLE projects ADD COLUMN pickup_auto_approve_tools INTEGER NOT NULL DEFAULT 0;", ct);   // unattended
```

Domain change — add to `Project` (`packages/Agentweaver.Domain/Project.cs`):

```csharp
public int MaxReadyPerHeartbeat { get; init; } = 3;        // FR-008a default
public bool PickupAutopilot { get; init; } = true;          // auto-answer child clarifying questions (Feature 008 autopilot)
public bool PickupAutoApproveTools { get; init; } = false;  // auto-approve allow-with-approval tools ONLY; safety floor still blocks destructive (Principle X)
```

Meaning and Principle X/IX boundary (this is the unattended-pickup seeding decision the user asked for):
- A heartbeat-created coordinator run runs with no human present, so the pickup path MUST seed `RunOptions(AutoApproveTools: project.PickupAutoApproveTools, Autopilot: project.PickupAutopilot)` and MUST perform the Phase 1 outcome-spec confirmation unattended (section 3.4) — because `Autopilot` only auto-answers child clarifying questions and does NOT bypass the confirmation gate (`CoordinatorWorkflowFactory.cs:103,116-129`; `CoordinatorAutopilot.cs:14-36`).
- `PickupAutopilot` defaults ON: child clarifying questions are auto-answered by the coordinator model and cascade to children (`CoordinatorDispatchService.CascadeOptionsToChild`).
- `PickupAutoApproveTools` defaults OFF and, even when ON, only auto-approves allow-with-approval tools; the sandbox/permission safety floor that blocks destructive/irreversible actions is NOT bypassed (`CoordinatorAutopilot` never auto-grants tool/permission gates — Principle X). The named human `CapturedBy` stays accountable (Principle IX): it is the run's `SubmittingUser` and the `confirmedBy` recorded on the unattended confirm, so the audit log attributes the run and its plan confirmation to a person.

`SqliteProjectStore` changes:
- `InsertAsync` / `SelectSql` / `Map` include the three new columns.
- New `IProjectStore.UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct)` + its `SqliteProjectStore` implementation (single UPDATE), used by the set endpoint (section 5.8).

Validation: `max_ready_per_heartbeat` MUST be an integer in `[1, 20]`; the set endpoint rejects out-of-range with 400. Floor of 1 guarantees the heartbeat always makes progress.

### 2.5 DI registrations (Program.cs)

Add alongside the other singleton stores (after `SqliteProjectStore` is registered):

```csharp
builder.Services.AddSingleton<SqliteBacklogTaskStore>();
builder.Services.AddSingleton<IBacklogTaskStore>(sp => sp.GetRequiredService<SqliteBacklogTaskStore>());
builder.Services.AddSingleton<WorkflowStageProjector>();
builder.Services.AddSingleton<BoardProjectionService>();
builder.Services.AddSingleton<CoordinatorPickupService>();
builder.Services.AddHostedService<CoordinatorHeartbeatService>();
```

Endpoint registration (after `app.MapProjectEndpoints();`):

```csharp
app.MapBacklogEndpoints();
```

---

## 3. Heartbeat + coordinator pickup (Path A)

### 3.1 Definition of "active coordinator for a project" (FR-011)

There is no per-project coordinator daemon today; the heartbeat is realized as one process-wide hosted scheduler that services every eligible project (consistent with the spec assumption that the heartbeat is "the Squad Coordinator Agent's existing periodic activation cycle"):

> A project is **eligible for heartbeat pickup** when, at tick time, `project.State == Active` AND its workspace is available (`IProjectWorkspaceProvider.IsAvailable(project.WorkingDirectory)`).

If a project is `Deleting` or its workspace is missing, its Ready tasks remain in Ready and are picked up on a later tick once it is active again (FR-011, "Coordinator inactive or not yet configured"). The move to Ready never fails or drops the task — it is a pure state write independent of the heartbeat.

### 3.2 Configuration

- Interval: `Coordinator:HeartbeatIntervalSeconds`, default **10**. Read once at construction; floor of 1 second.
- Master enable flag `Coordinator:HeartbeatEnabled`, default **true**. Hermetic web tests that must stay deterministic set it `false` (mirrors the existing `Coordinator:AutoDispatch` toggle).

### 3.3 `CoordinatorHeartbeatService` per-tick algorithm

New file `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs`, a `BackgroundService` driven by a `PeriodicTimer`.

```
ExecuteAsync(stoppingToken):
  if not HeartbeatEnabled: return
  timer = new PeriodicTimer(interval)
  while await timer.WaitForNextTickAsync(stoppingToken):
    using scope = scopeFactory.CreateScope()
    projects = projectStore.ListAsync()
    foreach project where project.State == Active:
      try:
        if not workspaceProvider.IsAvailable(project.WorkingDirectory): continue   // FR-011
        var limit = project.MaxReadyPerHeartbeat                                    // FR-008a
        var candidates = backlogStore.ListReadyForClaimAsync(project.Id, limit)     // deterministic top-N
        foreach task in candidates:
          try:
            await pickupService.TryPickupAsync(project, task, stoppingToken)
          catch (Exception exTask):
            logger.LogError(exTask, "Heartbeat: pickup failed for task {TaskId}", task.Id)
            // isolated; sibling tasks still processed
      catch (Exception exProject):
        logger.LogError(exProject, "Heartbeat: project {ProjectId} tick failed", project.Id)
        // isolated; next project still processed
```

Error isolation is two-level (per project, per task) so one bad task or project never stalls the tick. `OperationCanceledException` from `stoppingToken` propagates out to stop the service cleanly.

### 3.4 `CoordinatorPickupService.TryPickupAsync` — atomic reserve + unattended coordinator start

New file `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs`. This owns the section 1.5 flow and reuses `CoordinatorRunService` (FR-021).

```csharp
public async Task TryPickupAsync(Project project, BacklogTask task, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
    var runId = RunId.New();
    var workflowRunId = Guid.NewGuid().ToString();
    var goal = string.IsNullOrWhiteSpace(task.Description) ? task.Title : $"{task.Title}\n\n{task.Description}";

    // (1) Build the reserved coordinator run (mirrors CoordinatorRunService.StartCoordinatorRunAsync:112-127
    //     but with a caller-supplied RunId so the claim can bind it atomically).
    var run = new Run
    {
        Id = runId,
        RepositoryPath    = project.WorkingDirectory,
        OriginatingBranch = project.DefaultBranch,
        ModelSource       = ModelSource.GitHubCopilot,
        ModelId           = DefaultModelFor(project.ProviderSettings),
        Task              = goal,
        SubmittingUser    = task.CapturedBy,        // accountable human (Principle IX)
        Status            = RunStatus.InProgress,
        StartedAt         = now,
        ProjectId         = project.Id,
        AgentName         = "Coordinator",          // parent coordinator run
        ParentRunId       = null,
        SubtaskId         = null,
        WorkflowRunId     = workflowRunId,
        Origin            = RunOrigin.BacklogPickup, // durable origin marker; persisted atomically in step (b) of section 1.5
    };
    var workflowRun = new WorkflowRun
    {
        Id = workflowRunId, ProjectId = project.Id, Task = goal,
        SubmittingUser = task.CapturedBy, StartedAt = now,
    };

    // (2) Atomic claim + reserve (section 1.5).
    var result = await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
        project.Id, task.Id, run, workflowRun, now, ct);

    switch (result)
    {
        case ClaimReserveResult.Lost:
            return;                                  // another heartbeat won, or task moved back; nothing persisted
        case ClaimReserveResult.ProjectUnavailable:
            logger.LogInformation("Pickup: project {ProjectId} not active; task {TaskId} left Ready", project.Id, task.Id);
            return;                                  // task untouched in Ready (priority preserved) — blocking issue #2
    }

    // (3) Reservation committed. Activate the coordinator workflow + unattended confirm, post-commit.
    try
    {
        await coordinatorRunService.StartReservedCoordinatorRunAsync(
            run,
            autoApproveTools: project.PickupAutoApproveTools,
            autopilot:        project.PickupAutopilot,
            confirmedBy:      task.CapturedBy,       // named human accountable for the auto-confirm (Principle IX)
            ct: CancellationToken.None);             // run must outlive the heartbeat tick
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Pickup: coordinator start failed for run {RunId}", runId);
        await runStore.TrySetTerminalStatusAsync(runId, RunStatus.Failed, DateTimeOffset.UtcNow, "coordinator_start_failed", CancellationToken.None);
        // Task stays Claimed -> Failed coordinator run shown in the terminal column. No silent re-queue (FR-012).
    }
}
```

`DefaultModelFor(project.ProviderSettings)` reuses the same helper the project-run endpoint uses to choose the default model.

### 3.5 `CoordinatorRunService.StartReservedCoordinatorRunAsync` — required refactor

The existing `StartCoordinatorRunAsync` generates its own `RunId` and inserts the run row itself (`CoordinatorRunService.cs:98-154`), which is incompatible with the atomic claim+reserve (the run row is already inserted inside the section 1.5 transaction). Add a reserved variant that assumes the run row is already persisted (mirroring `RunOrchestrator.StartReservedProjectRunAsync`):

```csharp
/// <summary>
/// Activates a coordinator run whose row is ALREADY persisted (reserved) — used by unattended
/// heartbeat pickup. Seeds RunOptions, opens the stream, starts + supervises the coordinator
/// workflow, then performs the Phase 1 outcome-spec confirmation UNATTENDED on behalf of
/// <paramref name="confirmedBy"/> (the accountable human), because Autopilot does not bypass the
/// confirmation gate. Mirrors the interactive StartCoordinatorRunAsync activation steps.
/// </summary>
public async Task StartReservedCoordinatorRunAsync(
    Run reservedRun, bool autoApproveTools, bool autopilot, string confirmedBy, CancellationToken ct)
{
    var runId = reservedRun.Id.ToString();
    _runOptions.Set(runId, new RunOptions(AutoApproveTools: autoApproveTools, Autopilot: autopilot));

    var entry = _streamStore.Create(runId, reservedRun.SubmittingUser);
    entry.RecordNext(EventTypes.CoordinatorStarted, new { goal = reservedRun.Task });

    var input = new CoordinatorDraftInput(
        runId, reservedRun.ProjectId!.Value.ToString(), reservedRun.Task,
        reservedRun.SubmittingUser, reservedRun.RepositoryPath, reservedRun.ModelId);

    var runCts = new CancellationTokenSource();
    var streamingRun = await _factory.StartAsync(input, runId, runCts.Token).ConfigureAwait(false);
    var runCt = _registry.Register(runId, streamingRun, runCts);
    StartWatching(runId, streamingRun, entry, reservedRun.SubmittingUser, runCt);

    ScheduleUnattendedConfirm(runId, confirmedBy);   // fire-and-forget; see below
}
```

The interactive `StartCoordinatorRunAsync` is refactored to share the same activation body (extract a private `Activate(Run, RunOptions, ct)`), but it does NOT schedule the unattended confirm — interactive runs wait for a human to confirm/revise via the existing HTTP endpoints.

`ScheduleUnattendedConfirm` is a bounded background loop (no human present):

```csharp
private void ScheduleUnattendedConfirm(string runId, string confirmedBy)
{
    _ = Task.Run(async () =>
    {
        // The draft phase (an LLM turn) persists the spec as 'awaiting_confirmation' before the gate arms.
        // Poll until the spec is awaiting_confirmation, then confirm. ConfirmOutcomeSpecAsync itself
        // bounded-waits for the request port to arm (WaitForGateToArmAsync).
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline && !_appStopping.IsCancellationRequested)
        {
            var spec = await GetOutcomeSpecAsync(runId, _appStopping).ConfigureAwait(false);
            if (spec?.Status == "awaiting_confirmation")
            {
                var outcome = await ConfirmOutcomeSpecAsync(runId, confirmedBy, _appStopping).ConfigureAwait(false);
                if (outcome == CoordinatorGateOutcome.Accepted) return;
            }
            else if (spec?.Status is "confirmed" or "declined")
            {
                return;   // already advanced (e.g. a human confirmed first)
            }
            await Task.Delay(500, _appStopping).ConfigureAwait(false);
        }
        _logger.LogWarning("Unattended confirm timed out for coordinator run {RunId}; left for a human", runId);
    });
}
```

This reuses `ConfirmOutcomeSpecAsync` (`CoordinatorRunService.cs:161-165`) verbatim — the same resume seam a human uses — so confirmation flows through the identical, audited path, attributed to `confirmedBy = task.CapturedBy`. After confirmation, the coordinator proceeds exactly as a human-confirmed run: decompose → dispatch child runs → collective assembly (RAI/review/merge/scribe).

Principle X note on the unattended confirm: confirming the outcome spec approves a PLAN, not a destructive/irreversible action; the actual mutating work happens in child runs whose tool/permission gates remain enforced by the safety floor (auto-approve covers only allow-with-approval tools, never the destructive floor). The human-review assembly gate inside Phase 3 is governed by the project's autopilot/auto-approve settings the same way every coordinator run is — this feature changes none of that.

### 3.6 Recovery on restart (reuses Feature 008)

Heartbeat-created coordinator runs are ordinary coordinator parent runs (`ParentRunId == null`, `AgentName == "Coordinator"`, `Status == InProgress`), so the existing startup sweep `CoordinatorRunService.RecoverInterruptedRunsAsync` (`CoordinatorRunService.cs:411-430`, called in `Program.cs:184-190`) recovers them with no new orchestration code: a run interrupted before its spec was drafted resumes the spec phase, one with a work plan re-arms dispatch/assembly. The heartbeat reads only committed state, so its ordering against recovery is benign.

**Unattended-confirm recovery keyed on the durable origin marker (blocking flaw fix, FR-021, Principles IX/X/XI).** A run interrupted while still `awaiting_confirmation` must be handled differently depending on whether it was an unattended backlog pickup or an interactive run. Recovery MUST distinguish them by the persisted `runs.origin` marker (sections 1.5 and 2.3), NOT by inferring from the project's per-project pickup settings. Per-project settings are shared defaults and cannot tell apart an interactive coordinator run that merely happened to be awaiting confirmation at restart from a genuine backlog pickup; keying on them risks auto-confirming a plan and attributing it to a human who never approved unattended pickup.

Recovery therefore applies this predicate to each interrupted coordinator run whose spec is `awaiting_confirmation`:

```csharp
// Inside RecoverInterruptedRunsAsync, for each recovered coordinator parent run whose
// outcome spec is awaiting_confirmation:
if (run.Origin == RunOrigin.BacklogPickup)
{
    // Durable proof this was an unattended backlog pickup. Resolve the accountable human from
    // the 1:1 backlog task pointing at this run (backlog_tasks.run_id == run.Id), so confirmedBy
    // is exactly the task's CapturedBy (Principle IX) even across a restart.
    var task = await backlogStore.GetByRunIdAsync(run.Id, ct);   // section 2.1 helper, run-id keyed
    if (task is not null)
        ScheduleUnattendedConfirm(run.Id.ToString(), confirmedBy: task.CapturedBy);
    // If no backlog task is found (e.g. project deleted), leave the run awaiting confirmation
    // rather than auto-confirming without an accountable human.
}
else
{
    // origin == Interactive: a human owns this gate. Do NOT auto-confirm. The run stays
    // awaiting_confirmation and waits for a person to confirm/revise via the HTTP endpoints,
    // exactly as before the restart.
}
```

Key properties:
- Only `origin == BacklogPickup` runs (re)schedule `ScheduleUnattendedConfirm`. Interactive coordinator runs awaiting confirmation at restart remain awaiting human confirmation and are never auto-confirmed.
- The re-scheduled confirm attributes `confirmedBy = task.CapturedBy` (the named human accountable, Principle IX), resolved durably via `backlog_tasks.run_id == run.Id`, so the accountability binding survives a process restart.
- The Principle X safety floor is unchanged by recovery: confirming the spec approves a PLAN; destructive/irreversible tool gates and child-run tool/permission approval stay enforced, and the Phase-3 assembly human-review gate remains governed by the project's autopilot/auto-approve settings the same as any coordinator run.
- A new `IBacklogTaskStore.GetByRunIdAsync(RunId, ct)` (section 2.1) provides the durable run-id -> backlog task lookup; it reads the unique `idx_backlog_tasks_run` index, so it is exact and cheap.

This replaces the earlier inference-from-settings approach and resolves residual risk #3 (section 13).

---

## 4. Workflow-stage columns (FR-015, FR-019) — descriptor-driven, no hardcoded table

### 4.1 Source of truth

The board's workflow columns are the **coordinator topology stages**, derived from `CoordinatorGraphDescriptor` — the same descriptor the coordinator graph renderer uses (Feature 008) — never a hardcoded list (FR-015, SC-004, blocking issue #4). Because Path A makes every Ready pickup a coordinator run, the coordinator topology IS the project's effective workflow for these cards.

### 4.2 Projector — `WorkflowStageProjector`

New file `apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs`. Two pure functions over the descriptor:

1. `IReadOnlyList<WorkflowStage> GetStages()` — builds the canonical, plan-independent coordinator topology via `CoordinatorGraphDescriptor.BuildEmpty(placeholderRunId)` and projects its ordered `Nodes` into columns:

   - Walk `descriptor.Nodes` in array order (the descriptor already emits them in topology order: `coordinator`, then assembly stages — `CoordinatorGraphDescriptor.cs:78-110`).
   - **Generic filter rule (documented, single place to change)**: a node projects to a column iff `node.NodeType != "subtask"`. `subtask` nodes are per-run fan-out (dynamic, many) and are never top-level columns; they render nested under their coordinator card. Every other backbone node — including the Human Review node whose `node_type` is `gate` — IS a named workflow stage and becomes a column. (If a future coordinator topology adds a purely structural node type such as `join`/`branch`, extend the exclusion set here; no board code elsewhere changes.)
   - Each surviving node yields `WorkflowStage { Id = node.Id, Label = node.Label, Order = index }` taken straight from the descriptor — so a changed/renamed/added coordinator stage yields a changed/added column with NO board code change (SC-004).

   For `BuildEmpty`, the surviving nodes are exactly the stable backbone, with descriptor-supplied ids/labels:

   | descriptor node id        | label         | node_type |
   |---------------------------|---------------|-----------|
   | `coordinator`             | Coordinator   | agent     |
   | `planned:assembly-rai`    | RAI           | agent     |
   | `planned:assembly-review` | Human Review  | gate      |
   | `planned:assembly-merge`  | Merge         | action    |
   | `planned:assembly-scribe` | Scribe        | agent     |

   Plus one appended terminal stage `{ Id = "terminal", Label = "Done", Order = last+1 }`. The terminal stage is derived from the coordinator's own domain completion model (`WorkPlanStatus` terminal states and `AssemblyStage.Done`, ordinal 5 — `CoordinatorDispatchService.cs:766-819`), so it is part of the documented coordinator workflow rather than a hardcoded canonical stage. It is the FR-016a terminal sink that keeps completed cards visible with collapse.

   The id/label/order list is **stable** across calls (same descriptor input), satisfying SC-006 (MCP and Web render identical columns).

2. `string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage)` — maps a coordinator run's authoritative persisted state to the column id it currently occupies (FR-016, FR-017, blocking issue #5). `planStage` is the lightweight `(Status, AssemblyStage)` projection read in section 5.7.

   ```
   // terminal first
   if coordinatorRun.Status in { Completed, Merged, Failed, Declined, MergeFailed }    -> "terminal"
   if planStage?.Status in { complete, assembly_blocked, assembly_failed, assembly_declined } -> "terminal"

   // assembly in progress: the persisted AssemblyStage is the authority
   switch planStage?.AssemblyStage:
     "rai"    -> "planned:assembly-rai"
     "review" -> "planned:assembly-review"
     "merge"  -> "planned:assembly-merge"
     "scribe" -> "planned:assembly-scribe"
     "done"   -> "terminal"

   // no plan yet, or still planning/dispatching/awaiting children (AssemblyStage null)
   default -> "coordinator"
   ```

   This reads the persisted `WorkPlan.Status` + `WorkPlan.AssemblyStage` (the forward-only sticky assembly stage, `WorkPlan.cs:13-19`) and the coordinator `Run.Status`, NOT a coarse single RunStatus enum — the card sits in `coordinator` while the run decomposes/dispatches/awaits its children, then advances column-by-column as assembly progresses, then lands in `terminal`. Child subtask runs are NOT separate board cards (they are `node_type == "subtask"`, excluded as columns, and excluded from the board's run query because they have `ParentRunId != null`); they appear when a viewer expands the coordinator card via the existing `GET /api/runs/{coordinatorRunId}/graph` + `/children`.

### 4.3 Fallback (FR-019)

If `GetStages` throws or returns empty (descriptor cannot be resolved into stages), the board response sets `workflow_stages_available: false` and `columns` contains only the two `intake` columns (Backlog, Ready). Clients render an inline "Workflow columns unavailable" notice instead of a broken board.

### 4.4 Columns-only endpoint

`GET /api/projects/{projectId}/workflow-stages` — thin read for clients that want only the columns.

Response DTO `WorkflowStagesResponse`:

```jsonc
{
  "available": true,
  "stages": [
    { "id": "coordinator",             "label": "Coordinator" },
    { "id": "planned:assembly-rai",    "label": "RAI" },
    { "id": "planned:assembly-review", "label": "Human Review" },
    { "id": "planned:assembly-merge",  "label": "Merge" },
    { "id": "planned:assembly-scribe", "label": "Scribe" },
    { "id": "terminal",                "label": "Done" }
  ]
}
```

The full board endpoint (5.7) embeds the same `stages`, so the Web normally needs one call.

---

## 5. API endpoints

New file `apps/Agentweaver.Api/Endpoints/BacklogEndpoints.cs`, class `BacklogEndpoints` with `public static void MapBacklogEndpoints(this WebApplication app)`, following ProjectEndpoints conventions (per-resource extension method, Bearer auth via `ApiKeyAuthMiddleware.GetCaller`, owner/project scoping, snake_case DTOs as `public sealed record` with `[JsonPropertyName]` in `apps/Agentweaver.Api/Contracts/Dtos.cs`).

All routes are project-scoped under `/api/projects/{projectId}/backlog` plus the board/config reads. **Every mutate handler verifies `task.ProjectId == route projectId` by passing the route `projectId` into the project-scoped store method** (section 2.1), so a task can never be mutated through another project's route (blocking issue #7, SC-007). Auth: same Bearer-key scheme; project existence checked, `404` if missing, `400` on invalid ids.

### 5.1 Capture task — `POST /api/projects/{projectId}/backlog/tasks` (FR-001, FR-002, FR-003, FR-016)

Request `CaptureBacklogTaskRequest`: `{ "title": "string (required, non-whitespace)", "description": "string|null" }`.
- `400 { "error": "title is required." }` if title null/empty/whitespace (FR-002).
- New task: `State = Backlog`, `OrderKey` appended to the bottom of the project's Backlog bucket, `CapturedBy = caller.User`, `CreatedAt = now`.
- `201 Created`, `Location: /api/projects/{projectId}/backlog/tasks/{taskId}`, body = `BacklogTaskDto` (5.9).

### 5.2 Edit task — `PATCH /api/projects/{projectId}/backlog/tasks/{taskId}` (FR-005)

Request `EditBacklogTaskRequest`: `{ "title": "string", "description": "string|null" }`. Title required non-whitespace. `IBacklogTaskStore.UpdateContentAsync(projectId, id, ...)`. `200` updated `BacklogTaskDto`; `404` if not found in project. Permitted in any state; no run affected (FR-005).

### 5.3 Delete task — `DELETE /api/projects/{projectId}/backlog/tasks/{taskId}` (FR-005)

`IBacklogTaskStore.TryDeleteAsync(projectId, id)`. `204` on success; `404` if not found in project; `409 { "error": "task_claimed" }` if Claimed (run-backed tasks are not deletable, preserving provenance). No run affected.

### 5.4 Move Backlog -> Ready — `POST /api/projects/{projectId}/backlog/tasks/{taskId}/ready` (FR-006, FR-010)

Optional body `{ "target_index": int|null }` (default: bottom of Ready). Endpoint computes `newOrderKey` via `OrderKey.Between` from the Ready bucket neighbours at `target_index`, then `IBacklogTaskStore.TryMoveToReadyAsync(projectId, id, newOrderKey, committedAt: now)`.
- `200` updated `BacklogTaskDto` (now `state = "ready"`).
- `409 { "error": "not_in_backlog" }` if not currently in Backlog.
- `409 { "error": "order_conflict" }` if the store exhausts its UNIQUE-conflict retries (section 1.4).
The task becomes eligible on the next heartbeat with no further action (FR-010); actual claim timing depends on its priority vs N.

### 5.5 Move Ready -> Backlog — `POST /api/projects/{projectId}/backlog/tasks/{taskId}/backlog` (FR-007, FR-018)

`IBacklogTaskStore.TryMoveToBacklogAsync(projectId, id, newOrderKey)` (gated on `state='ready' AND run_id IS NULL`).
- `200` updated `BacklogTaskDto`.
- `409 { "error": "task_already_claimed" }` if already claimed (the unclaimed guard, FR-007/FR-018; the move-back side of the section 1.5 race).

### 5.6 Reorder within a bucket — `POST /api/projects/{projectId}/backlog/tasks/{taskId}/reorder` (FR-018a)

Request `ReorderBacklogTaskRequest`: `{ "target_index": int }` (0 = top/highest priority). Endpoint reads the task's current bucket, computes `newOrderKey` from neighbours at `target_index`, then `IBacklogTaskStore.TryReorderAsync(projectId, id, expectedState, newOrderKey)`.
- `200` updated `BacklogTaskDto`.
- `409 { "error": "task_claimed" }` if Claimed (claimed tasks are not user-reorderable).
- `409 { "error": "order_conflict" }` on retry exhaustion.
Reorder is valid only within Backlog or Ready. Ready reorder changes pickup priority (FR-018a).

### 5.7 Get the full board — `GET /api/projects/{projectId}/board` (FR-013..FR-016a, FR-019)

Single call returning columns + all cards in their current-state column. Built by `BoardProjectionService.GetBoardAsync(projectId, includeTerminalHistory)`:

- Reads `IBacklogTaskStore.ListByProjectAsync(projectId)` (Backlog + Ready cards in `order_key` order).
- Reads `SqliteRunStore.GetRunsByProjectAsync(projectId)` (top-level runs only; coordinator children excluded by default, matching "children-as-nodes").
- Workflow stage columns from `WorkflowStageProjector.GetStages()` (4.2).
- For each top-level coordinator run, reads a lightweight stage projection in one batch via new `CoordinatorRunService.GetWorkPlanStagesAsync(IReadOnlyCollection<string> coordinatorRunIds, ct) -> IReadOnlyDictionary<string, CoordinatorWorkPlanStage>` where `CoordinatorWorkPlanStage(string Status, string? AssemblyStage)` is selected from `WorkPlans` (one `WHERE coordinator_run_id IN (...)` query, no per-run round-trips). Each run is placed via `WorkflowStageProjector.CoordinatorRunToStageId(run, stage)` (4.2).
- Claimed tasks are NOT emitted as separate cards (FR-012) — they are represented by their coordinator run (joined via `backlog_tasks.run_id`); the run card carries `backlog_task_id` for provenance.

Query params:
- `include_terminal_history` (bool, default `false`): when false, the `terminal` column returns only recent/active terminal runs (last `N=20` by `EndedAt` desc) plus a `collapsed_count`; when true, returns all (FR-016a).

Response DTO `BoardDto`:

```jsonc
{
  "project_id": "guid",
  "workflow_stages_available": true,
  "columns": [
    { "id": "backlog",  "kind": "intake",   "label": "Backlog",      "cards": [ /* TaskCardDto, order_key asc */ ] },
    { "id": "ready",    "kind": "intake",   "label": "Ready",        "cards": [ /* TaskCardDto, order_key asc */ ] },
    { "id": "coordinator",             "kind": "workflow", "label": "Coordinator",  "cards": [ /* RunCardDto */ ] },
    { "id": "planned:assembly-rai",    "kind": "workflow", "label": "RAI",          "cards": [] },
    { "id": "planned:assembly-review", "kind": "workflow", "label": "Human Review", "cards": [ /* RunCardDto */ ] },
    { "id": "planned:assembly-merge",  "kind": "workflow", "label": "Merge",        "cards": [] },
    { "id": "planned:assembly-scribe", "kind": "workflow", "label": "Scribe",       "cards": [] },
    { "id": "terminal", "kind": "workflow", "label": "Done", "cards": [ /* RunCardDto, recent first */ ], "collapsed_count": 7 }
  ]
}
```

When `workflow_stages_available` is `false`, `columns` contains only the two `intake` columns (FR-019).

`TaskCardDto` (intake columns):
```jsonc
{
  "kind": "task", "task_id": "guid", "title": "string", "description": "string|null",
  "state": "backlog|ready", "order_key": "string", "captured_by": "login",
  "created_at": "iso-8601", "committed_at": "iso-8601|null"
}
```

`RunCardDto` (workflow columns):
```jsonc
{
  "kind": "run",
  "run_id": "guid",
  "workflow_run_id": "guid",
  "backlog_task_id": "guid|null",   // provenance when the run came from a Ready task
  "task": "string",
  "status": "in_progress|merged|failed|...",  // coordinator Run.Status
  "work_plan_status": "planned|dispatching|assembling|in_review|complete|...|null",
  "assembly_stage": "rai|review|merge|scribe|done|null",
  "stage_id": "coordinator|planned:assembly-rai|planned:assembly-review|planned:assembly-merge|planned:assembly-scribe|terminal",
  "agent_name": "Coordinator",
  "started_at": "iso-8601",
  "ended_at": "iso-8601|null"
}
```

### 5.8 Get/Set per-project pickup settings (FR-008a + unattended seeding)

- `GET /api/projects/{projectId}/backlog/settings` -> `BacklogSettingsDto`:
  ```jsonc
  { "max_ready_per_heartbeat": 3, "pickup_autopilot": true, "pickup_auto_approve_tools": false }
  ```
- `PUT /api/projects/{projectId}/backlog/settings` body = `BacklogSettingsDto`. Validate `max_ready_per_heartbeat` in `1..20` (`400` otherwise); the two booleans are free. Persists via `IProjectStore.UpdatePickupSettingsAsync`. Takes effect on subsequent heartbeats; already-claimed tasks/runs are untouched (FR-008a). Returns `200` with the updated `BacklogSettingsDto`.

### 5.9 `BacklogTaskDto`

Returned by capture/edit/move/reorder:
```jsonc
{
  "task_id": "guid", "project_id": "guid", "title": "string", "description": "string|null",
  "state": "backlog|ready|claimed", "order_key": "string", "captured_by": "login",
  "created_at": "iso", "committed_at": "iso|null", "claimed_at": "iso|null", "run_id": "guid|null"
}
```

### 5.10 FR -> endpoint map

| FR | Endpoint(s) |
|----|-------------|
| FR-001/002/003 | POST .../backlog/tasks |
| FR-004 | (no endpoint creates a run for Backlog; heartbeat only reads Ready) |
| FR-005 | PATCH, DELETE .../tasks/{id} |
| FR-006/010 | POST .../tasks/{id}/ready |
| FR-007/018 | POST .../tasks/{id}/backlog |
| FR-008/008a/009/011/012 | heartbeat + CoordinatorPickupService (sec 3) + GET/PUT .../backlog/settings |
| FR-013/014/015/016/016a/019 | GET .../board, GET .../workflow-stages |
| FR-017/022 | GET .../board (polled, sec 8) |
| FR-018a | POST .../tasks/{id}/reorder |
| FR-020 | MCP tools (sec 6) |
| FR-021 | reuse CoordinatorRunService coordinator pipeline (sec 3.4/3.5) |

---

## 6. MCP tools (FR-020, Principle IV)

New file `apps/Agentweaver.Mcp/Tools/BacklogTools.cs`, class `BacklogTools(AgentweaverApiClient api)` with `[McpServerToolType]`, each tool a thin `AgentweaverApiClient` call (no business logic), following `ProjectTools` exactly (try/catch -> `McpApiException`, `JsonSerializer.Serialize` of the API result). Auto-discovered via `WithToolsFromAssembly` in `apps/Agentweaver.Mcp/Program.cs`; no `.mcp.json` change required.

Re-validated parity after the endpoint changes — every endpoint has exactly one mirroring tool, no client-side logic:

| Tool name (snake_case)        | HTTP call |
|-------------------------------|-----------|
| `backlog_capture_task`        | POST `/api/projects/{project_id}/backlog/tasks` |
| `backlog_edit_task`           | PATCH `/api/projects/{project_id}/backlog/tasks/{task_id}` |
| `backlog_delete_task`         | DELETE `/api/projects/{project_id}/backlog/tasks/{task_id}` |
| `backlog_move_to_ready`       | POST `.../tasks/{task_id}/ready` |
| `backlog_move_to_backlog`     | POST `.../tasks/{task_id}/backlog` |
| `backlog_reorder_task`        | POST `.../tasks/{task_id}/reorder` |
| `backlog_get_board`           | GET `/api/projects/{project_id}/board` |
| `backlog_get_workflow_stages` | GET `/api/projects/{project_id}/workflow-stages` |
| `backlog_get_settings`        | GET `/api/projects/{project_id}/backlog/settings` |
| `backlog_set_settings`        | PUT `/api/projects/{project_id}/backlog/settings` |

The board read gives MCP clients the identical columns+cards (including coordinator `stage_id`/`assembly_stage`) the Web sees (SC-006).

---

## 7. Web — Kanban board on `ProjectPage`

React 19 + Fluent UI v2 (`@fluentui/react-components`, griffel `makeStyles` + `tokens`), `useEffect`/`useState` (no React Query). The board replaces the project homepage content at `/projects/:projectId` (ProjectPage.tsx).

### 7.1 Component breakdown

New files under `apps/web/src/components/board/`:
- `KanbanBoard.tsx` — owns board state, fetches the board via the hook (7.4), renders a horizontal scroll row of columns, hosts the capture entry. Renders the FR-019 unavailable notice when `workflow_stages_available` is false.
- `KanbanColumn.tsx` — one column; props `{ column, onDropTask }`. Intake columns (`kind: "intake"`) are drop targets; workflow columns (`kind: "workflow"`) are NOT drop targets and visibly reject drops (7.3). The `terminal` column renders a "Show older (N)" toggle bound to `include_terminal_history` (FR-016a).
- `TaskCard.tsx` — draggable card for Backlog/Ready tasks; inline edit (title/description) + delete; drag handle.
- `RunCard.tsx` — read-only coordinator-run card; links to the coordinator run/graph page; shows status + work-plan/assembly stage; expand affordance for the coordinator's children via the existing run graph view. Not draggable.
- `CaptureTaskForm.tsx` — title (required) + optional description; calls capture; optimistic add then refetch.

`ProjectPage.tsx` mounts `<KanbanBoard projectId={projectId} />` as the homepage body.

### 7.2 Dynamic columns

Columns come straight from `BoardDto.columns` (server-ordered: Backlog, Ready, then the coordinator-topology stages and Done). The Web never hardcodes stage names (FR-015, SC-004) — it renders whatever the server returns.

### 7.3 Drag-and-drop — native HTML5 drag events

Implement Backlog<->Ready and within-bucket reorder with native HTML5 drag-and-drop (`draggable`, `onDragStart`/`onDragOver`/`onDrop`), no new dependency. Justification:
- No DnD library is installed; the interaction surface is small (two intake columns + reorder; workflow columns are non-targets). Native DnD covers it without adding a dependency.
- Rejecting workflow-column drops is trivial: `KanbanColumn` registers `onDragOver`/`onDrop` only for `kind === "intake"`. Workflow columns omit those handlers, so a dropped card snaps back and a Fluent `MessageBar` explains "Only the coordinator moves work into the workflow." (FR-018). The server is the backstop: no endpoint moves a task into a workflow column.

Alternative `@dnd-kit/core` is adopted only if accessibility/keyboard-reorder is later required; if chosen, isolate it behind the same `KanbanColumn`/`TaskCard` props.

On a successful Backlog<->Ready drop or reorder, the card calls the matching API method (7.4), then triggers a board refetch (optimistic local move reconciled by the next poll).

### 7.4 API client methods + hook

Add to `apps/web/src/api/client.ts` (`AgentweaverApiClient`) and surface via the `apiClient` singleton, with types in `apps/web/src/api/types.ts`:

```ts
getBoard(projectId: string, includeTerminalHistory?: boolean): Promise<BoardDto>;
getWorkflowStages(projectId: string): Promise<WorkflowStagesResponse>;
captureBacklogTask(projectId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto>;
editBacklogTask(projectId: string, taskId: string, body: { title: string; description?: string | null }): Promise<BacklogTaskDto>;
deleteBacklogTask(projectId: string, taskId: string): Promise<void>;
moveTaskToReady(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto>;
moveTaskToBacklog(projectId: string, taskId: string, targetIndex?: number): Promise<BacklogTaskDto>;
reorderBacklogTask(projectId: string, taskId: string, targetIndex: number): Promise<BacklogTaskDto>;
getBacklogSettings(projectId: string): Promise<BacklogSettingsDto>;
setBacklogSettings(projectId: string, settings: BacklogSettingsDto): Promise<BacklogSettingsDto>;
```

New hook `apps/web/src/api/board.ts` -> `useBoard(projectId, { intervalMs })` returning `{ board, status, error, refetch }`. Fetches `getBoard` on mount and on an interval (board-level live updates, section 8), exposing `refetch` for immediate post-mutation refresh. Mirrors the existing `useRunPoll` polling style.

---

## 8. Live / streaming for the board (FR-017, FR-022)

Today SSE is strictly per-run (`GET /api/runs/{id}/stream`); there is no project-scoped event stream. To make captures, moves, pickups, and coordinator stage transitions appear without manual refresh:

**Decision: board-level polling of `GET /api/projects/{projectId}/board` on a fixed interval (default 3000ms), via `useBoard`.** Rationale:
- The data is fully materialized server state (tasks + live coordinator run/work-plan stage); no fakes or placeholders, so Principle VII holds. Per-run/coordinator streaming (Principle V) is unchanged — the coordinator SSE stream still drives the detailed coordinator graph view; the board is a coarse aggregate reflecting committed state every few seconds.
- SC-005 (run-backed cards appear in their current stage within a few seconds) and SC-001 (captured task appears immediately) are met: a 3s poll bounds staleness, and the mutating client also calls `refetch` immediately after its own capture/move so its own actions are instant; other viewers converge within one poll (FR-022).
- A project-scoped SSE stream would lower latency but requires a new project event bus + endpoint; that is more surface than this slice needs and is **deferred**, recorded as a deliberate trade-off (section 10). Path A does not change this decision: the board still reads committed coordinator state regardless of how the coordinator streams internally.

The poll interval is a single constant in `useBoard`; tests inject a short interval or call `refetch` directly to avoid timing flakiness.

---

## 9. Test plan

### 9.1 Backend (`tests/Agentweaver.Tests`, xUnit)

- `Backlog/SqliteBacklogTaskStoreTests.cs` — insert/get/list; `ListByProjectAsync` ordering by `(state, order_key)`; `UpdateContentAsync`; `TryDeleteAsync` rejects Claimed; move Backlog<->Ready gating; `TryReorderAsync` gating; **project scoping** (every method ignores a task in another project — a get/edit/delete/move with the wrong `projectId` returns false/404 — FR-003/SC-007); `ON DELETE CASCADE` on project delete; **order_key UNIQUE** index rejects a duplicate `(project_id, state, order_key)` insert and the store's retry resolves a colliding reorder (blocking issue #6).
- `Backlog/BacklogClaimReserveTests.cs` — **the critical one**: `TryClaimAndReserveCoordinatorRunAsync` returns `Won` exactly once under concurrent/overlapping calls for the same task (spawn parallel tasks; assert exactly one `Won`, one persisted coordinator run row, one `run_id`); concurrent losers return `Lost` and persist NO run row (no orphan — blocking issue #1); a claim racing `TryMoveToBacklogAsync` resolves to exactly one winner; when the project is set inactive, the method returns `ProjectUnavailable`, rolls back, and leaves the task `Ready` with its `order_key`/priority unchanged (blocking issue #2); the reserved run round-trips via `SqliteRunStore.GetAsync` with `AgentName == "Coordinator"`, `ParentRunId == null`, `Status == InProgress`, and `Origin == RunOrigin.BacklogPickup` (durable origin marker).
- `Backlog/BacklogOrderKeyTests.cs` — `OrderKey.Between` invariants: result strictly between neighbours; adjacent neighbours extend digits; ascending sort stable across many inserts/reorders; deterministic pickup tie-break `(order_key, committed_at, task_id)`.
- `Backlog/WorkflowStageProjectorTests.cs` — `GetStages` derives the ordered coordinator backbone (`coordinator, planned:assembly-rai, planned:assembly-review, planned:assembly-merge, planned:assembly-scribe, terminal`) from `CoordinatorGraphDescriptor.BuildEmpty`, excluding `node_type == "subtask"` and retaining the `gate` Human Review stage (blocking issue #4); a descriptor with an added/renamed assembly node yields a changed column with no projector change (SC-004); `CoordinatorRunToStageId` mapping table over `(Run.Status, WorkPlan.Status, AssemblyStage)` including `coordinator` while planning/dispatching and `terminal` on completion (blocking issue #5); empty/unresolvable descriptor -> `available:false` (FR-019).
- `Backlog/CoordinatorHeartbeatServiceTests.cs` — one tick claims top-N by deterministic priority and starts N coordinator reservations; remainder stays Ready (FR-008/010); inactive/unavailable-workspace project is skipped and its tasks remain Ready (FR-011); per-task error isolation; no duplicate runs across two consecutive ticks (FR-009/SC-002). Uses an actual `SqliteBacklogTaskStore` over a temp DB; the coordinator start is asserted at the reservation/claim-state level with `Coordinator:AutoDispatch=false` (keeping LLM turns out), consistent with existing coordinator test discipline.
- `Backlog/CoordinatorPickupServiceTests.cs` — `TryPickupAsync`: `Won` path persists a coordinator run + workflow_run and invokes `StartReservedCoordinatorRunAsync`; `Lost`/`ProjectUnavailable` paths persist nothing and leave the task Ready; a thrown coordinator start terminalizes the run `Failed` and leaves the task `Claimed` (FR-012, no re-queue).
- `Backlog/UnattendedConfirmTests.cs` — with `Coordinator:AutoDispatch=false`, a reserved coordinator run's spec reaches `awaiting_confirmation` and `ScheduleUnattendedConfirm` drives it to `confirmed` attributed to `confirmedBy = CapturedBy` (covers the gate that autopilot does not bypass); a run already confirmed by a human is a no-op.
- `Backlog/UnattendedRecoveryTests.cs` — **the durable-origin discrimination test** (blocking flaw fix): `TryClaimAndReserveCoordinatorRunAsync` persists the run with `Origin == RunOrigin.BacklogPickup`, while an interactive coordinator run persists `Origin == RunOrigin.Interactive`. Drive both runs to `awaiting_confirmation`, then invoke `RecoverInterruptedRunsAsync`: assert ONLY the `BacklogPickup` run is auto-confirmed (via re-scheduled `ScheduleUnattendedConfirm`, `confirmedBy` resolved through `GetByRunIdAsync(run.Id).CapturedBy`), and the `Interactive` run remains `awaiting_confirmation` and is never auto-confirmed. Also assert `GetByRunIdAsync` returns the 1:1 claimed task for a backlog-pickup run and null after the project (and its task) is deleted, in which case the backlog-pickup run is left awaiting confirmation rather than confirmed without an accountable human.
- `Backlog/BacklogEndpointsTests.cs` — capture validation (FR-002 blank title -> 400); edit/delete; move endpoints + 409 guards (`not_in_backlog`, `task_already_claimed`, `order_conflict`); reorder; **wrong-project route** returns 404 and mutates nothing (SC-007); `GET /board` shape (intake columns first, coordinator topology columns from descriptor, claimed task represented by a coordinator run card with `backlog_task_id` and `stage_id`, terminal collapse); settings get/set + range validation; **parity backstop**: assert every documented endpoint exists and returns the documented shape (FR-020).
- `Backlog/BacklogProjectScopingTests.cs` — cross-project leakage: the board for project A omits project B's tasks and coordinator runs (SC-007).

Helper: reuse `Helpers/ProjectsWebApplicationFactory.cs`.

### 9.2 Web (`apps/web/src/__tests__`, vitest + @testing-library/react)

- `KanbanBoard.test.tsx` — renders Backlog + Ready first, then the coordinator-topology columns from a mocked `BoardDto`; renders the FR-019 unavailable notice when `workflow_stages_available:false`; a coordinator run card appears in the column matching its `stage_id` (FR-016); terminal "Show older (N)" toggle calls `getBoard(..., true)` (FR-016a).
- `KanbanBoardDnd.test.tsx` — dragging Backlog->Ready calls `moveTaskToReady`; Ready->Backlog calls `moveTaskToBacklog`; dropping onto a workflow column is rejected (no API call, MessageBar shown) (FR-018); within-bucket reorder calls `reorderBacklogTask` with the target index (FR-018a).
- `CaptureTaskForm.test.tsx` — empty/whitespace title blocked client-side; valid capture calls `captureBacklogTask` then refetches (FR-001/002).
- `useBoard.test.tsx` — polls on the injected interval and exposes `refetch`; `refetch` after a mutation re-reads the board (FR-017/022).

---

## 10. Constitution compliance (v1.4.0)

- **III (API-first)**: All capture/edit/delete/move/reorder/board/settings/pickup logic lives in the API (`IBacklogTaskStore`, `BoardProjectionService`, `WorkflowStageProjector`, `CoordinatorPickupService`, heartbeat). Web and MCP hold zero business logic. Every store read/mutation is project-scoped (blocking issue #7).
- **IV (Two front-ends at parity)**: Every endpoint has a mirroring MCP tool (section 6) and a Web client method (7.4). The board read returns identical columns+cards to both (SC-006).
- **V (Observable runs)**: Pickup reuses the coordinator and its existing streams unchanged (FR-021); the board is an additive aggregate over committed coordinator state. No streaming behavior changed.
- **VI (Deployment parity)**: SQLite store, hosted `PeriodicTimer` service, and polling run identically locally and hosted; no environment special-casing.
- **VII (No mocks/fakes)**: Every layer is functional from first commit — SQLite store, atomic claim+reserve, hosted heartbeat creating actual coordinator runs, polled board over actual coordinator state. No placeholders; the slice is intentionally narrow (intake + visualization) and reuses the verified coordinator rather than re-implementing it.
- **VIII (No emojis)**: No emojis in any code, DTO, label, log, or this doc.
- **IX (Responsible AI / accountability)**: `captured_by` is the signed-in user, becomes the coordinator run's `SubmittingUser` AND the `confirmedBy` recorded on the unattended outcome-spec confirm — a named human is accountable for every heartbeat-created run and its plan confirmation, including after a restart, where recovery re-resolves `confirmedBy` durably via `backlog_tasks.run_id == run.Id` (section 3.6). Claimed tasks retain a permanent 1:1 run reference (provenance/audit).
- **X / XI (Safe execution / governance)**: Heartbeat-created runs go through the unchanged coordinator + MAF workflow, inheriting the existing sandbox, step/time limits, human-review assembly gate, audit event log, and governance. Unattended seeding is bounded: `pickup_autopilot` only auto-answers child clarifying questions; `pickup_auto_approve_tools` (default OFF) only covers allow-with-approval tools and never the destructive/irreversible safety floor; the unattended outcome-spec confirm approves a PLAN (a bounded, reversible decision attributed to a named human), not a destructive action. This feature adds no execution of its own and weakens no boundary (FR-021).

**Complexity Tracking / deviations**:
1. **Board live-updates via polling** rather than a project-scoped SSE stream (section 8). Justified: it uses fully materialized server state (Principle VII), leaves coordinator streaming untouched (Principle V), and avoids introducing a project event bus this slice does not require. A project-scoped SSE stream is the documented future upgrade. No principle is violated.
2. **`TryClaimAndReserveCoordinatorRunAsync` crosses the backlog/runs aggregate boundary inside one transaction.** Justified and required: the no-orphan invariant (blocking issue #1) demands the claim and the coordinator-run/workflow-run inserts commit atomically; SQLite single-file transactions make this safe, and the run-insert SQL mirrors the existing `SqliteRunStore.TryCreateProjectRunAsync` so there is one column-list source of truth (verified by a round-trip test).
3. **New `CoordinatorRunService.StartReservedCoordinatorRunAsync` + unattended outcome-spec confirm, gated on a durable run-origin marker.** Justified: Path A requires reusing the coordinator unattended (FR-021); the existing `StartCoordinatorRunAsync` self-generates its run id (incompatible with atomic claim binding) and waits for a human at the Phase 1 gate. The reserved variant shares the same activation body and reuses the same audited `ConfirmOutcomeSpecAsync` resume seam, so no orchestration logic is duplicated or weakened. The unattended confirm (live and on restart-recovery) fires ONLY for runs persisted with `origin = 'backlog_pickup'` (sections 1.5, 2.3, 3.6); interactive coordinator runs carry the default `origin = 'interactive'`, so a restart can never auto-confirm an interactive run that was merely awaiting confirmation. Persisting `origin` adds one TEXT column on `runs` (plus an index), set atomically inside the claim+reserve transaction. This is preferred over inferring origin from per-project pickup settings, which cannot distinguish an interactive run awaiting confirmation from a genuine backlog pickup and would mis-attribute an auto-confirm to a human who never approved unattended pickup (Principles IX/X/XI).

---

## 11. Build / verify commands

```powershell
# Backend build (Release)
dotnet build agentweaver.sln -c Release

# Backend tests (Release)
dotnet test tests/Agentweaver.Tests -c Release

# Web tests
cd apps/web; npm test
```

Run the targeted new test files during development (e.g. `dotnet test tests/Agentweaver.Tests -c Release --filter FullyQualifiedName~Backlog`) before the full suite.

---

## 12. Dependency-ordered task list for implementers

1. **Tank — Domain + Store** (no deps):
   - `BacklogTaskId`, `BacklogTaskState`, `BacklogTask`, `OrderKey`, `ClaimReserveResult`, `IBacklogTaskStore` (project-scoped signatures, incl. `GetByRunIdAsync`) in `packages/Agentweaver.Domain`.
   - `RunOrigin` enum + `Run.Origin` field (`packages/Agentweaver.Domain`); `SqliteRunStore` column mapping for `origin`.
   - `Project.MaxReadyPerHeartbeat` / `PickupAutopilot` / `PickupAutoApproveTools` fields + `IProjectStore.UpdatePickupSettingsAsync`.
   - `SqliteBacklogTaskStore` (incl. transactional `TryClaimAndReserveCoordinatorRunAsync` stamping `origin='backlog_pickup'` + UNIQUE-conflict retry + `GetByRunIdAsync`), DDL + indexes in `SqliteDb.SchemaSql`, the `runs.origin` + `idx_runs_origin_status` and three project-column `TryAlterAsync` migrations, `SqliteProjectStore` column wiring.
   - `BacklogTaskStateExtensions`. DI registrations.
2. **Tank — API** (deps: 1):
   - `WorkflowStageProjector` (descriptor-driven), `CoordinatorRunService.GetWorkPlanStagesAsync`, `BoardProjectionService`.
   - DTOs in `Dtos.cs`; `BacklogEndpoints.MapBacklogEndpoints` (all routes in section 5); `GET /workflow-stages`; register in Program.cs.
3. **Tank — Pickup + Heartbeat** (deps: 1, 2):
   - `CoordinatorRunService.StartReservedCoordinatorRunAsync` + `ScheduleUnattendedConfirm` + shared activation refactor; recovery wiring (3.6).
   - `CoordinatorPickupService`; `CoordinatorHeartbeatService` (section 3); config keys; `AddHostedService` registration.
4. **MCP** (deps: 2): `BacklogTools.cs` mirroring every endpoint (section 6).
5. **Trinity — Web** (deps: 2): client methods + types (7.4), `useBoard` hook, board components (7.1), DnD (7.3), mount on `ProjectPage`.
6. **Smith — Tests** (deps: 1-5): backend test files (9.1) and web test files (9.2). The claim+reserve, stage-projector, and unattended-confirm tests are mandatory gates.
7. **Seraph — Security** (deps: 2-5): verify project-scoped Bearer auth and project-scoped store calls on every endpoint, no cross-project access, claim/delete/order guards enforced server-side, no endpoint can move a task into a workflow column, and that the unattended seeding never auto-approves the destructive safety floor.

---

## 13. Residual risks for the rubber-duck re-review

1. **Heartbeat is one shared scheduler, "active coordinator" = project Active + workspace available** (section 3.1), because no per-project coordinator daemon exists. Confirm this satisfies FR-011's intent and that a single process-wide heartbeat servicing all active projects is acceptable.
2. **Unattended outcome-spec confirm** (section 3.5): a heartbeat-originated coordinator run auto-confirms its Phase 1 spec on behalf of `CapturedBy`. Confirm that auto-approving the PLAN (while leaving child tool/permission/destructive gates enforced and the assembly human-review governed by the project's autopilot/auto-approve settings) is the desired unattended behavior and honors Principle IX/X.
3. **Recovery of an unconfirmed heartbeat run** (section 3.6): a run interrupted at `awaiting_confirmation` re-schedules its unattended confirm on restart ONLY when it carries the durable `runs.origin = 'backlog_pickup'` marker, written atomically in the section 1.5 claim+reserve transaction. Interactive coordinator runs awaiting confirmation at restart keep `origin = 'interactive'` and are left for a human; recovery never infers origin from per-project pickup settings, and `confirmedBy` is re-resolved durably via `backlog_tasks.run_id == run.Id`. This closes the prior auto-confirm-the-wrong-run flaw; confirm the persisted-origin approach is acceptable.
4. **Terminal/"Done" column derivation** (section 4.2): the terminal column is appended from the coordinator's own completion model (`WorkPlanStatus` terminal states + `AssemblyStage.Done`) rather than a descriptor node, since the coordinator descriptor has no terminal node. Confirm this counts as descriptor/domain-derived (not hardcoded) for FR-015/SC-004, alongside the fixed intake columns.
5. **Board placement granularity while children run** (section 4.2): a coordinator card sits in the `coordinator` column for the entire decompose+dispatch+await-children window, then advances through the assembly columns. Confirm this coarse-but-authoritative placement satisfies FR-016/FR-017 (children are visible by expanding the card's coordinator graph, not as separate board cards).
6. **Terminal history cap N=20** (FR-016a): the collapse threshold is a chosen default; confirm the value and the `collapsed_count` + `include_terminal_history` behavior.
7. **Cross-aggregate transaction** (section 2.1 / Complexity Tracking #2): `TryClaimAndReserveCoordinatorRunAsync` writes `backlog_tasks`, `runs`, and `workflow_runs` in one transaction. Confirm this is acceptable given it is the mechanism that eliminates the orphan state, and that mirroring the existing run-insert SQL (verified by a round-trip test) is preferable to introducing a shared run-insert helper.
