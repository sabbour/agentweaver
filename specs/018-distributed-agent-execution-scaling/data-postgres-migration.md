# DATA-LAYER Migration Design — SQLite → Azure Database for PostgreSQL Flexible Server

**Author:** Tank (Backend Engineer, Matrix squad)
**Feature:** 018 — Distributed Agent Execution & Scaling
**Status:** DESIGN ONLY (no code changes in this doc)
**Date:** 2026-06-27
**Requested by:** Ahmed Sabbour

---

## 0. Context & cross-references

This document is the **operational-state persistence** slice of Feature 018. It is the
prerequisite that unblocks running **>1 API replica**. Today the API is pinned to a single
replica because the data layer is single-writer SQLite on an RWO PVC:

> `k8s/api-deployment.yaml:9-14` — `replicas: 1` + `strategy: Recreate`, commented
> *"Single replica: SQLite is single-writer (RWO PVC). Recreate ensures the old pod
> releases the disk before the new one attaches (no multi-attach)."*

**Cross-references (do not duplicate here):**
- **Morpheus → `spec.md` (master architecture):** owns the web/worker split, the leasing/fencing
  model, and how the coordinator becomes a horizontally-scalable worker. This doc supplies the
  *schema columns and CAS primitives* that the leasing design (§3) consumes; it does **not**
  define the lease lifecycle itself.
- **Link → `platform-deployment.md` (Azure provisioning):** owns PostgreSQL Flexible Server
  provisioning, SKU/HA tier, private endpoint, VNet integration, firewall, and Workload-Identity
  federation. This doc only states **what the app config needs** (§5) and defers all
  infra/secret-plumbing to Link.

> NOTE: At time of writing, `specs/018-distributed-agent-execution-scaling/` contains only this
> file. `spec.md` and `platform-deployment.md` are authored in parallel by Morpheus and Link;
> the cross-references above are by filename and should be reconciled at integration.

---

## 1. INVENTORY — every persistence component

There are **two physical SQLite databases** today, both under `AppPaths.DataDirectory`
(overridable by `Database:Path`):

| DB file | Owner | Access tech |
|---|---|---|
| `agentweaver.db` | `SqliteDb` (`Infrastructure/SqliteDb.cs:11-36`) | **Raw ADO.NET SQL** (`Microsoft.Data.Sqlite`) |
| `memory.db` | `MemoryDbContext` (`Memory/MemoryDbContext.cs:7`) | **EF Core** + (one) raw-SQL trespasser |

### 1a. Raw-SQL stores → `agentweaver.db` (NOT EF — must be hand-ported)

| Store (file) | Table(s) | Data held | Postgres-readiness |
|---|---|---|---|
| `SqliteRunStore.cs` | `runs`, (+ `run_revisions`) | Run records: status, worktree, tree_hash, diff, review dwell (`review_ready_at`/`review_wait_ms`), origin, parent_run_id/subtask_id, archived_at | **Low** — raw SQL, CAS `UPDATE … WHERE status=…`, TEXT datetimes |
| `SqliteRunRevisionStore.cs` | `run_revisions` | Append-only revision/reviewer comments | **Low** — relies on SQLite `RAISE(ABORT)` triggers (`SqliteDb.cs:219-229`) |
| `SqliteProjectStore.cs` | `projects` | Projects: origin, working_dir, default_branch, model defaults, state, pickup config, workflow/review-policy/sandbox profile, blueprint provenance, allowed_workflow_ids (JSON) | **Low** — raw SQL, boolean-as-INTEGER, JSON-as-TEXT |
| `SqliteBacklogTaskStore.cs` | `backlog_tasks` | Kanban backlog/ready/claimed tasks, order_key, claim→run binding, partial unique indexes | **Low** — raw SQL, **partial unique indexes**, transactional claim CAS |
| `SqliteWorkflowRunStore.cs` | `workflow_runs` | Coordinator/workflow run grouping + `orchestration_worktree_path` | **Low** — raw SQL |
| `CastProposalStore.cs` (`Casting/`) | `cast_proposals` | Cast proposals w/ expiry (`INSERT OR REPLACE`) | **Low** — raw SQL, SQLite `INSERT OR REPLACE` upsert |

`SqliteDb.cs` also owns schema bootstrap + a hand-rolled idempotent migration list
(`EnsureCreatedAsync`, `SqliteDb.cs:53-157`) using `ALTER TABLE … ADD COLUMN` + catch
`"duplicate column name"`. This whole mechanism is **SQLite-specific** and is replaced by EF
migrations (§2/§5).

### 1b. EF Core entities → `memory.db` (`MemoryDbContext`)

These already flow through the `Database:Provider` switch and have generated EF migrations.

| Entity (DbSet) | Data held | Postgres-readiness |
|---|---|---|
| `Decision` | Promoted decisions, supersede chain (`Memory/Decision.cs`) | **High** (EF) |
| `DecisionInboxEntry` | Decision inbox, unique `(ProjectId, Slug)` | **High** (EF) |
| `AgentMemory` | Per-agent memory by type | **High** (EF) |
| `SessionContext` | Session summaries, unique `(ProjectId, SessionId)` | **High** (EF) |
| `RunEventRecord` (`RunEvents`) | **Durable run event log**; unique `(RunId, Sequence)` | **Medium** — EF-mapped **but also accessed via RAW SQL** (see 1c + §4) |
| `OutcomeSpec` | Coordinator outcome spec | **High** (EF) |
| `WorkPlan` | Coordinator work plan; status/stage/assembly CAS target | **High** (EF) — extend for leasing (§3) |
| `Subtask` | Child subtasks; status pending→dispatched→running, ChildRunId | **High** (EF) — extend for leasing (§3) |
| `SubtaskDependency` | Subtask DAG edges | **High** (EF) |
| `SteeringDirective` | Queued/applied steering directives | **High** (EF) |
| `McpRefreshToken` | MCP OAuth refresh tokens, unique `TokenHash` | **High** (EF) — migration `AddMcpRefreshTokens` |
| `McpRevokedJti` | Revoked JTIs w/ expiry | **High** (EF) |
| `McpClientRegistration` | Dynamic client registrations, unique `ClientId` | **High** (EF) |

Existing EF migrations (`apps/Agentweaver.Api/Migrations/`): `AddRunEvents`, `AddOutcomeSpec`,
`AddCoordinatorWorkPlan`, `AddWorkPlanAssemblyStage`, `AddSubtaskRecovery`,
`AddSubtaskAgentCharter`, `AddWorkPlanWorkflowId`, `FixMissingSchemaFields`,
`AddMcpRefreshTokens`, `AddMcpClientRegistrations`. These are **provider-specific snapshots** —
see §2 / §5 for the Postgres regeneration approach.

### 1c. Not-a-database (no migration needed)

- **Sandbox policy** → `YamlSandboxPolicyStore.cs` is **file-based YAML** under each project's
  `.agentweaver/` directory (`File.ReadAllText`/`File.Exists`), **not** a DB store. The
  `projects.sandbox_profile` column only names a preset; the policy body is a workspace file.
  **No Postgres work**, but it raises a separate multi-replica concern (workspace files on a
  shared/RWX volume) that belongs to Morpheus's worker design, not this doc.
- Workflows / review policies are likewise loaded from `.agentweaver/` files, referenced from
  `projects` by id/name only.

### Readiness summary

- **EF (memory.db):** provider switch already exists (`Program.cs:213-239`); Postgres is a
  config flip + a Postgres migration set. **The blocker is not these.**
- **Raw SQLite (agentweaver.db + cast_proposals + the RunEvents raw trespasser):** **this is the
  real migration**. ~6 stores of hand-written SQLite SQL with SQLite-only idioms.

---

## 2. PORTING STRATEGY for the raw stores

### Recommendation: **Unify the raw stores behind EF Core (`MemoryDbContext` provider switch), one DB.**

**Decision: fold `agentweaver.db` into the EF context and retire `SqliteDb`/raw ADO.NET.**
Rationale:

1. **One provider switch, one connection story.** `Program.cs:213-239` already routes
   sqlite/sqlserver/postgres for `MemoryDbContext`. `SqliteDb` is a *parallel* hard-coded SQLite
   path (`SqliteDb.cs:29-36`) with **no provider abstraction** — keeping raw Npgsql would mean
   maintaining two dialects by hand forever.
2. **One migration mechanism.** The bespoke `ALTER TABLE … ADD COLUMN` + duplicate-column-catch
   in `EnsureCreatedAsync` (`SqliteDb.cs:60-157`) is replaced by EF migrations that the team
   already operates for `memory.db`.
3. **CAS already lives in EF.** `CoordinatorAssemblyStore` proves guarded
   `ExecuteUpdateAsync(... .Where(Status==X))` works for exactly-once CAS
   (`CoordinatorAssemblyStore.cs:30-44`). Porting `runs`/`backlog_tasks` CAS to the same idiom is
   consistent, not novel.
4. **Cross-store transactions become trivial.** The backlog claim today spans `backlog_tasks` +
   `runs` in one SQLite transaction (`SqliteBacklogTaskStore.cs:347-370`). With both tables in one
   `MemoryDbContext`/one Postgres DB, that stays a single transaction; with two physical DBs it
   would need a distributed/2-phase hack.

**Rejected alternative — raw Npgsql ports.** Lowest *immediate* churn (translate SQL string-by-
string), but permanently doubles the dialect surface, keeps two DB files/connection pools, and
forfeits EF's automatic Postgres type handling. Only justified if a store needs SQL EF can't
express — none here do (all are simple CRUD + guarded UPDATEs that map to `ExecuteUpdateAsync`).

> Pragmatic phasing: it is acceptable to ship Postgres for the EF set first (config flip), then
> port the raw stores entity-by-entity into the same context. But the **end state is one DB,
> one EF context**. Do not ship two Postgres databases.

### SQLite-isms that will NOT translate (and the Postgres mapping)

| SQLite idiom | Where it appears | Postgres / EF handling |
|---|---|---|
| `INTEGER PRIMARY KEY` rowid / implicit autoincrement | `RunEvents.Sequence` logic, EF int keys | EF `int` key → Postgres `integer GENERATED BY DEFAULT AS IDENTITY`. (Run/project PKs are app-generated GUIDs-as-TEXT → keep as `text`/`uuid`.) |
| `INSERT OR REPLACE` (upsert) | `CastProposalStore.cs:47` | Postgres `INSERT … ON CONFLICT (id) DO UPDATE` / EF "find-or-add". Semantics differ: `OR REPLACE` deletes+reinserts (fires FK cascades, resets defaults); `ON CONFLICT DO UPDATE` updates in place. **Audit FK/trigger side-effects.** |
| `INSERT OR IGNORE` | `SqliteRunEventStream.cs:198-201` | `INSERT … ON CONFLICT (RunId, Sequence) DO NOTHING`. |
| `INSERT … SELECT COALESCE(MAX(Sequence),0)+1 … RETURNING` | `SqliteRunEventStream.cs:215-220` | **Race-prone on Postgres** under MVCC/concurrent replicas (MAX+1 is not atomic across snapshots). Replace with a Postgres `sequence`/identity **per run** or an `ON CONFLICT` retry loop. See §4. |
| Dynamic typing (TEXT holds dates/bools/json) | All raw stores | Postgres is strictly typed. Must choose real column types (below). |
| Datetime as `TEXT` `"yyyy-MM-dd HH:mm:ss.fffffff"` | `SqliteRunStore`, `SqliteRunEventStream.cs:191`, `Ts()` helpers | Map to `timestamptz`. EF (Npgsql) maps `DateTimeOffset` → `timestamptz` natively (the EF entities already use `DateTimeOffset`). **Backfill must parse the TEXT format** (§5). |
| Boolean as `INTEGER NOT NULL DEFAULT 0/1` | `projects.pickup_autopilot`, `pickup_auto_approve_tools` (`SqliteDb.cs:98-99`) | Postgres `boolean`. Backfill `0/1 → false/true`. |
| JSON as `TEXT` | `projects.allowed_workflow_ids` (`SqliteDb.cs:124`), `cast_proposals.proposal_json`, `RunEvents.PayloadJson` | Prefer Postgres `jsonb` for queryable columns; `text` is acceptable for opaque blobs (PayloadJson is opaque → `text`/`jsonb` either way). |
| Partial unique indexes | `backlog_tasks` order-key + run uniqueness (`SqliteDb.cs:178-180, 280-286`) | **Postgres supports partial indexes natively** (`CREATE UNIQUE INDEX … WHERE …`). EF: `HasIndex().IsUnique().HasFilter("...")`. Good news — these port cleanly. |
| Append-only via `CREATE TRIGGER … RAISE(ABORT)` | `run_revisions` (`SqliteDb.cs:219-229`) | Postgres trigger (`BEFORE UPDATE/DELETE … RAISE EXCEPTION`) or a `REVOKE UPDATE,DELETE` grant on the table. Not expressible in EF model — add as raw SQL in the migration's `Up()`. |
| `PRAGMA journal_mode=WAL`, `PRAGMA busy_timeout`, `PRAGMA foreign_keys=ON`, shared-cache | `SqliteDb.cs:46`, `SqliteRunEventStream.cs:186` | **All vanish.** Postgres is WAL-native and MVCC; no busy-timeout (use `lock_timeout`/`statement_timeout` if needed); FKs always enforced. Delete these code paths. |
| `ALTER TABLE ADD COLUMN` idempotent bootstrap | `SqliteDb.EnsureCreatedAsync` | Replaced by EF migrations applied at startup/deploy (§5). |

---

## 3. SCHEMA ADDITIONS FOR SCALABILITY (consumed by Morpheus's leasing design)

These columns make rows **safely claimable by exactly one of N replicas**. The leasing
*lifecycle* (renew cadence, expiry sweep, hand-off) is Morpheus's; here we define the storage.

### 3a. Lease/ownership columns (add to `runs`, `WorkPlans`, `Subtasks`)

| Column | Type | Purpose |
|---|---|---|
| `owner_id` | `text NULL` | The replica/worker instance currently holding the work item (e.g. pod name / GUID). `NULL` = unowned/free. |
| `lease_expires_at` | `timestamptz NULL` | Lease deadline; an expired lease is reclaimable by any replica even if `owner_id` is set (crash recovery). |
| `heartbeat_at` | `timestamptz NULL` | Last liveness stamp from the owner; drives stall detection (today's in-memory `HeartbeatStatusStore` becomes DB-backed for cross-replica visibility). |
| `fencing_token` | `bigint NOT NULL DEFAULT 0` | Monotonic token bumped on every successful acquisition. A worker must present its token on writes; a stale (smaller) token is rejected — prevents a paused/zombie owner from clobbering a re-leased item. |
| `attempt` | `int NOT NULL DEFAULT 0` | Acquisition/execution attempt counter (bounds retries; complements existing `Subtask.RecoveryAttempts`). |

### 3b. Idempotency for child dispatch

Child-run dispatch must be **exactly-once per (coordinator, subtask, attempt)** so a re-leased
coordinator replica does not double-spawn children. Add an idempotency table:

```
dispatch_idempotency (
    coordinator_run_id  text    NOT NULL,
    subtask_id          int     NOT NULL,
    attempt             int     NOT NULL,
    child_run_id        text    NOT NULL,
    created_at          timestamptz NOT NULL,
    PRIMARY KEY (coordinator_run_id, subtask_id, attempt)
)
```

Dispatch inserts this row in the **same transaction** that flips the subtask to `dispatched`;
a duplicate insert (`ON CONFLICT DO NOTHING`) means "already dispatched — reuse `child_run_id`",
making redelivery/re-lease safe.

### 3c. CAS / expected-state updates — extend the existing pattern to DISPATCH

The codebase **already** does DB-level CAS in two places; extend it, don't invent:

- **Assembly CAS (good):** `CoordinatorAssemblyStore.TryStartAssemblyAsync`
  (`CoordinatorAssemblyStore.cs:30-44`) —
  `UPDATE WorkPlans SET Status=Assembling WHERE Id=@id AND Status=AwaitingAssembly`, returns
  `rows>0` for the single winner. **This is the template.**
- **Backlog claim CAS (good):** `SqliteBacklogTaskStore` claim
  (`SqliteBacklogTaskStore.cs:355-369`) —
  `UPDATE backlog_tasks SET state='claimed' … WHERE state='ready' AND run_id IS NULL`.

- **Dispatch (BROKEN for multi-replica):** `CoordinatorDispatchService.UpdateSubtaskAsync`
  (`CoordinatorDispatchService.cs:1156-1171`) is a **read-modify-write with NO owner/state guard**:
  it loads the row, sets `row.Status`/`row.ChildRunId`, and `SaveChangesAsync()` — **last writer
  wins**. Two replicas observing the same `pending` subtask will both dispatch.

  **Fix:** convert to a guarded `ExecuteUpdateAsync` that asserts expected state **and**
  ownership/fencing, e.g.:
  ```
  UPDATE Subtasks
     SET Status='dispatched', ChildRunId=@child, owner_id=@me,
         fencing_token=fencing_token+1, lease_expires_at=@deadline, UpdatedAt=now()
   WHERE Id=@id AND Status='pending'
     AND (owner_id IS NULL OR lease_expires_at < now());
  ```
  Only the replica that gets `rows==1` proceeds to spawn the child (and writes the
  `dispatch_idempotency` row in the same tx). All other paths in `CoordinatorDispatchService`
  /`CoordinatorRunService` that blind-overwrite `Subtask.Status` (e.g.
  `CoordinatorRunService.cs:799-802`, `CoordinatorDispatchService.cs:1164`) must adopt the same
  guarded form or be made owner-aware.

---

## 4. RUN-EVENT FAN-OUT under multiple replicas

### Today (single-process)

`SqliteRunEventStream` is two layers (`SqliteRunEventStream.cs:12-29`):

1. **Durable write-through** — synchronous insert into `RunEvents` (in `memory.db`) **before** ack
   (`AppendAsync` → `WriteThrough`, `SqliteRunEventStream.cs:79-110, 177-228`).
2. **In-process fan-out** — a process-local bounded `Channel<RunEvent>` per run
   (`_channels`, capacity 1000). `SubscribeAsync` does **replay-then-tail**
   (`SqliteRunEventStream.cs:113-151`).

`RunStreamStore` (`RunStreamStore.cs:159-201`) is a **second, fully in-memory** per-process
history+signal store used by the SSE polling loop.

**Why it breaks at >1 replica:** the channel and `RunStreamStore` are **per-process**. If the
run executes on replica A but an SSE client connects to replica B (behind the Service/LB), B's
channel never receives A's live events. Durability is fine (shared Postgres), but **live
cross-replica delivery is lost** — clients only see events when replay polling happens to re-hit
the DB.

### Two viable Postgres designs

**Option A — Durable polling (replay-only, no live push).**
Drop the in-process channel as the cross-replica mechanism. Each subscriber loops:
`SELECT … WHERE RunId=@id AND Sequence > @cursor ORDER BY Sequence` on a short interval
(this is exactly today's `LoadFromSequence`, `SqliteRunEventStream.cs:231-260`, already SQL).
- **Pros:** dead simple, no new infra, works with PgBouncer/connection pooling, naturally
  multi-replica (every replica just reads shared rows).
- **Cons:** polling latency + DB read load proportional to (subscribers × poll rate). The
  per-process channel can stay as a **same-replica fast-path optimization**, with DB polling as
  the cross-replica floor.

**Option B — Postgres `LISTEN/NOTIFY` for live push.**
On durable insert, also `NOTIFY run_events, '<runId>:<sequence>'`. Each replica holds a
`LISTEN run_events` connection; on notify, the replica wakes the relevant local subscribers,
which then read the new rows from Postgres (notify carries only the cursor, not the payload —
payloads can exceed the 8 KB NOTIFY limit).
- **Pros:** low-latency live delivery across replicas; keeps the elegant replay-then-tail UX.
- **Cons:** `LISTEN/NOTIFY` **does not pass through PgBouncer transaction-pooling** — needs a
  dedicated session-mode connection per replica (coordinate with Link's pooling choice in
  `platform-deployment.md`); notifications are **not durable** (a replica that misses a notify
  must still backstop with a periodic catch-up poll).

### Recommendation: **B with an A backstop ("notify + catch-up poll").**

Use `LISTEN/NOTIFY` for low-latency live fan-out, but **every subscriber also runs a low-
frequency catch-up poll** (e.g. 2–5 s) so a missed/un-delivered notify can never strand a client —
the cursor-based replay (`Sequence > @cursor`) makes catch-up idempotent and gapless, exactly as
the current replay path already guarantees. The per-process `Channel` is retained purely as a
same-replica latency optimization. Durability is unchanged (synchronous insert before ack).

**Sequence allocation must change.** The current `INSERT … SELECT MAX(Sequence)+1 … RETURNING`
(`SqliteRunEventStream.cs:215-220`) is **not safe** when concurrent writers/replicas touch the
same run under MVCC. Replace with one of:
- a **per-run advisory lock** (`pg_advisory_xact_lock(hashtext(runId))`) around the MAX+1 insert, or
- a dedicated `RunEventSequence` allocation (table/sequence keyed by run),
- relying on the **unique `(RunId, Sequence)` index** (already present,
  `MemoryDbContext.cs:44`) with an `ON CONFLICT` retry loop.
The unique index is the durable safety net regardless of approach.

> Single-writer simplification: if Morpheus's worker design guarantees **exactly one writer per
> run** (the leasing owner), then MAX+1 contention is only across the rare re-lease boundary, and
> the advisory-lock approach is cheap and sufficient.

---

## 5. MIGRATION MECHANICS

### EF migrations for Postgres

- Npgsql is **already referenced** (`Agentweaver.Api.csproj:18`,
  `Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4`) and wired
  (`Program.cs:226-231`, `UseNpgsql`). SqlServer is also present (`csproj:17`) but out of scope.
- **Provider-specific migration sets.** EF migration snapshots encode provider types
  (SQLite affinity vs Postgres `timestamptz`/`boolean`/`jsonb`). The existing migrations under
  `Migrations/` were generated against SQLite. For Postgres you must generate a **separate
  migrations assembly/folder** (e.g. `Migrations/Postgres/`) via
  `dotnet ef migrations add InitialPg --context MemoryDbContext` while configured for Npgsql, and
  select the set by provider at runtime (`MigrationsAssembly`). Do **not** try to run the SQLite
  snapshots against Postgres.
- Once the raw `agentweaver.db` entities are folded into `MemoryDbContext` (§2), they are included
  in the same Postgres migration set — the bespoke `SqliteDb.EnsureCreatedAsync` bootstrap is
  deleted.
- Apply migrations at deploy time (init container / `dotnet ef database update` job) rather than
  `EnsureCreated`, so schema is versioned and replica startup is race-free (one migrator, then N
  app replicas). Coordinate the migration job with Link's deployment topology.

### Data backfill

- **Greenfield for staging/prod is the recommended default.** Operational state (runs in flight,
  event logs, backlog) is largely ephemeral/regenerable, and the cutover is far simpler. If
  product wants history preserved, treat backfill as an explicit, separate task.
- **If backfill is required:** one-shot export from both SQLite files → Postgres with explicit
  type coercion:
  - TEXT datetimes (`"yyyy-MM-dd HH:mm:ss.fffffff"`, `SqliteRunEventStream.cs:191`) → parse to
    `timestamptz` (assume UTC; the code stamps `DateTime.UtcNow`).
  - INTEGER booleans (`projects.pickup_autopilot` etc.) → `boolean`.
  - TEXT JSON (`allowed_workflow_ids`, `proposal_json`, `PayloadJson`) → `jsonb`/`text`.
  - Preserve app-generated string PKs as-is; re-seed identity sequences for any int-keyed tables.
  - Re-create partial unique indexes and the `run_revisions` append-only trigger post-load.
- **Cutover:** drain runs / quiesce writers, snapshot SQLite, run backfill, flip
  `Database:Provider`, scale up. (Detailed runbook is Morpheus/Link's; this doc owns the type
  mapping.)

### App config the migration needs (infra deferred to Link)

The app already keys off these (`Program.cs:213-239`):

- `Database:Provider = Postgres` (or `postgresql`) — selects `UseNpgsql`.
- Connection string via `ConnectionStrings:MemoryDb` **or** `Database:ConnectionString`
  (`Program.cs:228-230`).
- **Managed identity vs secret:** prefer **Microsoft Entra Workload Identity** (the deployment
  already opts in — `k8s/api-deployment.yaml:24` `azure.workload.identity/use: "true"`) so the
  Postgres connection uses a federated AAD token instead of a stored password. The connection
  string then carries no secret; a token provider supplies the access token. Falling back to a
  Key Vault / CSI secret password is acceptable for early bring-up. **Provisioning, AAD admin
  role, private endpoint, and the exact connection-string assembly are Link's
  `platform-deployment.md`.** App-side note: token-based auth may need a small `Npgsql`
  `PeriodicPasswordProvider`/token-refresh hook — flag for implementation.
- **Pooling:** with N replicas, front Postgres with PgBouncer or use Npgsql pooling; **reconcile
  with §4** — `LISTEN/NOTIFY` requires session-mode (not transaction-pooled) connections.
- Once Postgres is the backend, **drop the RWO PVC**, set `replicas: N`, and change
  `strategy: Recreate` → `RollingUpdate` (`k8s/api-deployment.yaml:9-14`). Owned by Link/Morpheus.

---

## 6. EFFORT & RISKS

### Effort (S/M/L)

| Component | Effort | Notes |
|---|---|---|
| EF set → Postgres (config + Postgres migration set) | **M** | Provider switch exists; generate + validate Postgres migrations; fix any SQLite-affinity assumptions. |
| Port `SqliteRunStore` (`runs`) → EF/Postgres | **L** | Largest raw store; many CAS UPDATEs + columns; the idempotent ALTER list collapses into one migration. |
| Port `SqliteBacklogTaskStore` | **M** | Partial unique indexes + transactional claim CAS to re-express. |
| Port `SqliteProjectStore` | **M** | Many nullable/bool/JSON columns; straightforward CRUD. |
| Port `SqliteWorkflowRunStore`, `SqliteRunRevisionStore`, `CastProposalStore` | **S each** | Small; revision append-only trigger needs raw SQL in migration. |
| Run-event fan-out (NOTIFY + catch-up poll, safe sequence alloc) | **L** | New cross-replica delivery; rework `SqliteRunEventStream` + `RunStreamStore`; concurrency-sensitive. |
| Leasing columns + dispatch CAS/idempotency (§3) | **M** | Schema is small; the risk is auditing **every** blind `Subtask.Status` writer. |
| Decommission `SqliteDb`/WAL/pragmas/bootstrap | **S** | Deletion once stores are ported. |

### Risks

1. **Silent double-dispatch** if any `Subtask.Status` writer is missed when converting to guarded
   CAS (`UpdateSubtaskAsync` is the known one — there are others, §3c). **High impact.**
2. **Sequence races** on `RunEvents` MAX+1 under MVCC/multi-writer (§4) → duplicate/missing
   sequence; mitigated by the unique index + advisory lock, but must be tested under concurrency.
3. **`LISTEN/NOTIFY` × PgBouncer** incompatibility (transaction pooling) — wrong pooling mode
   silently disables live delivery; the catch-up poll masks it as "just slow", making it hard to
   detect. Coordinate pooling with Link.
4. **`INSERT OR REPLACE` semantic drift** (`CastProposalStore`) — `ON CONFLICT DO UPDATE` keeps
   the row identity (different FK/cascade/trigger behavior). Audit before swap.
5. **Datetime backfill correctness** — TEXT→`timestamptz` parsing of the custom 7-fraction format;
   timezone assumptions. Low impact if greenfield.
6. **Two-DB → one-DB consolidation** changes transaction boundaries (backlog claim spans
   `backlog_tasks`+`runs`); a partial port (one DB on Postgres, one still SQLite) **breaks that
   transaction** — port atomically or keep both in the same Postgres DB throughout.
7. **Append-only enforcement** for `run_revisions` depends on a DB trigger that must be re-created
   in the Postgres migration (EF won't model it) — easy to forget.
8. **Workspace files** (sandbox YAML, worktrees) are still local-disk and not addressed here —
   multi-replica correctness for those is Morpheus's worker/shared-storage design, not the DB
   migration. Calling it out so it isn't assumed "done" by this migration.

---

## 6a. DECISION — Q2: Can P1 (the OOM fix) ship on current SQLite + `replicas:1`, WITHOUT P2 (Postgres)?

**VERDICT: YES.** P1 ships on the current single-file SQLite + `replicas:1` (`k8s/api-deployment.yaml:9-14`). Postgres (P2) is **NOT** required in the same release, **provided the sandbox pod never touches the DB directly and every persistence write is proxied through the single API/worker process.** Nothing in P1 forces Postgres early.

### Why P1 stays single-writer-safe

P1 (`spec.md:403`) relocates only the **heavy in-process execution** — the GitHub Copilot SDK session, the in-pod MAF `InProcessRunner`, and tool/shell/file exec — into a sandbox pod via the MAF bridge (`RemoteAgentProxy` ↔ `Agentweaver.AgentHost`). It does **not** add a second API/worker replica (that is P3, `spec.md:405`) and does **not** add a second DB writer. The pod is a compute satellite; the API process remains the sole owner of all durable state.

1. **Still ONE replica → all process-local state remains valid.** P1 keeps `replicas:1`/`Recreate`, so the in-process coordination primitives still work unchanged:
   - `RunWorkflowRegistry` — process-local `ConcurrentDictionary<runId, StreamingRun>` (`RunWorkflowRegistry.cs:10-12`). The bridge keeps `WatchStreamAsync`/`SendResponseAsync` co-located in the one worker, so the registry is still authoritative.
   - Dispatch / assembly `_active` guards — in-memory `ConcurrentDictionary` (`CoordinatorDispatchService.cs:86,127`; `CoordinatorAssemblyService.cs:66,162`). The comment at `CoordinatorDispatchService.cs:202` already states the loop is the "single writer of these subtask rows" — true so long as there is one process.
   - In-memory HITL `RequestPort` gates — they live in the MAF graph **in the worker** (`RunWorkflowFactory.cs:380,1175`; `CoordinatorWorkflowFactory` `ConfirmationGateId`), and per §4.5/§4.6 the graph and its gates stay in the worker; only the leaf agent turn is remote. Suspend/resume (`SendResponseAsync`, `CoordinatorRunService.cs:377`) stay in-process.

2. **Checkpointing can stay file/SQLite-backed for P1 — pod-resumability does NOT force Postgres.** Today checkpoints are local JSON via `ResilientCheckpointStore.Create(_checkpointDir,…)` → `CheckpointManager.CreateJson` (`RunWorkflowFactory.cs:170-172`), under the data PVC (`RunOrchestrator.cs:160-162`). The brokered/DB-backed `ICheckpointStore` (`spec.md:270-287`) is only needed so a **different worker** can resume — i.e. a P3 (multi-worker) requirement. On a single worker, durable checkpointing across a **pod** restart is fully achievable: the pod forwards its serializable session blob (`CopilotAIAgent.cs:323-335`) over the bridge to the **one** worker, which persists it through the existing local store. On pod death the same worker re-claims, restores the checkpoint, and re-spawns/re-attaches a pod, replaying via `DeserializeSessionCoreAsync` (`CopilotAIAgent.cs:335`). The worker is the sole checkpoint writer → SQLite single-writer invariant is preserved.

3. **Run-event durability/replay is unchanged.** Fan-out stays in the single worker: `RecordingChannelWriter` writes through `IRunEventStream.AppendAsync` (`RunWorkflowFactory.cs:1468-1508`) to `SqliteRunEventStream` (sole writer of `RunEvents` in `memory.db`, registered singleton `Program.cs:72`) and into the in-memory `RunStreamStore` (`RunStreamStore.cs:159`). Per §4.4 the agent-host pod streams `WorkflowEvent`s + token deltas **back** to the worker, which re-injects them via `RecordNext`/`Record` (`RunStreamStore.cs:83-110`). The pod is never an event-stream writer; no second writer to `memory.db` appears. (§4 of this doc confirms the in-process channel/`RunStreamStore` only break at **>1 replica** — not at the single replica P1 keeps.)

### What in P1 would FORCE Postgres early — checklist (all must hold)

- **The pod MUST NOT open a DB connection or mount the data PVC.** It must not receive `Database__Path` / any Npgsql/SQLite connection. The only volume shared with the pod is the **workspace** PVC for the worktree (`spec.md:194`; `KubernetesSandboxExecutor` mounts workspace, not data) — that is a shared *file* path, not a second SQLite writer.
- **All checkpoint and run-event writes are proxied through the single worker** (bridge stream-back → worker persists). If any P1 increment let the pod write checkpoints/events directly to the shared SQLite file, that introduces a second cross-process writer SQLite cannot serialize → would force Postgres. Keep the broker endpoint pointing at the one worker.
- **Do NOT add a second API/worker replica in P1.** Removing `replicas:1`/`Recreate` is the P2/P3 step (`spec.md:404-405,439`). The moment a second writer process exists, single-writer SQLite breaks — that, and cross-worker checkpoint/event delivery, are the *only* things that force Postgres.

**Net:** nothing intrinsic to P1 forces Postgres. The brokered DB-backed `ICheckpointStore` (`spec.md:4.5`) is a *P3 enabler* and can be stubbed by the existing file store in P1.

### Memory relief still lands without touching the DB

The OOM fix is **orthogonal to the data layer**. The heavy objects that drive pod memory growth — the live Copilot SDK session (`CopilotAIAgent.cs:40,86-87,148,296`), the MAF runner/graph, `StreamingRun` objects, and tool execution — leave the 4Gi API process into the per-run pod, even though SQLite and `replicas:1` are unchanged. The SQLite single-writer constraint only blocks **horizontal API scale-out** (P2/P3), not the memory relief. So P1 delivers the OOM fix standalone; P2 (this doc) and P3 (Link) follow to unlock horizontal scale.

---

## 7. OPEN QUESTIONS (for Ahmed / Morpheus / Link)

1. **Greenfield vs backfill:** Is staging/prod allowed to start empty, or must existing runs,
   event logs, and backlog be preserved across cutover? (Drives whether §5 backfill is built.)
2. **Single-writer-per-run guarantee:** Will Morpheus's leasing model guarantee exactly one writer
   per run at a time? If yes, the run-event sequence allocator simplifies dramatically (§4).
3. **Pooling choice:** PgBouncer (and which pool mode) vs Npgsql built-in pooling? This directly
   gates whether `LISTEN/NOTIFY` (§4 Option B) is usable. (Link.)
4. **Auth mode at GA:** Entra Workload Identity token auth from day one, or password/Key-Vault
   secret for bring-up then migrate? Token auth needs the Npgsql token-refresh hook. (Link.)
5. **Consolidate to one DB now, or keep `agentweaver.db`+`memory.db` as two Postgres databases?**
   This doc recommends **one** (§2); confirm there's no operational reason (separate backup/retention
   policy for the event log) to keep them split.
6. **`jsonb` vs `text`** for `PayloadJson` / `proposal_json` / `allowed_workflow_ids` — do we want
   to query inside these (favors `jsonb`) or keep them opaque (favors `text`)?
7. **Migration execution:** init-container/job applying `dotnet ef database update` vs app-startup
   migration — preferred for the multi-replica rollout? (Affects Link's deploy manifests.)

---

## PLAIN-TEXT SUMMARY

Agentweaver's operational state lives in two SQLite files. `memory.db` is EF Core
(`MemoryDbContext`) and already has a working `Database:Provider` switch with Npgsql referenced
and wired — moving it to Postgres is mostly a config flip plus a Postgres-specific EF migration
set. The real work is `agentweaver.db`, which is six hand-written raw-SQLite stores (runs,
projects, backlog tasks, workflow runs, run revisions, cast proposals) full of SQLite-only idioms
(TEXT datetimes, INTEGER booleans, `INSERT OR REPLACE`/`OR IGNORE`, `MAX+1 RETURNING`, WAL/busy-
timeout pragmas, and an idempotent `ALTER TABLE` bootstrap). Recommendation: **fold these into the
EF context and run everything on one Postgres database**, reusing the CAS pattern already proven in
`CoordinatorAssemblyStore` and the backlog claim. For horizontal scaling we add owner_id /
lease_expires_at / heartbeat_at / fencing_token / attempt to runs, work plans, and subtasks; add a
`(coordinator_run_id, subtask_id, attempt)` idempotency table for child dispatch; and **convert the
dispatch path — `CoordinatorDispatchService.UpdateSubtaskAsync`, which today blind-overwrites status
with no owner check — to a guarded CAS** like assembly already uses. Run-event fan-out is currently
in-process channels that don't cross replicas; the recommendation is Postgres `LISTEN/NOTIFY` for
live delivery with a cursor-based catch-up poll as a durable backstop, plus a concurrency-safe
sequence allocator (advisory lock or the existing unique `(RunId, Sequence)` index). Provisioning,
private endpoint, pooling, and Workload-Identity plumbing are deferred to Link's
platform-deployment.md; the web/worker and leasing lifecycle are Morpheus's spec.md. App config
just needs `Database:Provider=Postgres` and a connection string (preferably via managed identity).
Greenfield is the recommended cutover; backfill is a separate, optional, type-coercion exercise.

### Open questions
1. Greenfield vs backfill for staging/prod?
2. Does leasing guarantee single-writer-per-run (simplifies event sequencing)?
3. PgBouncer pool mode — does it permit LISTEN/NOTIFY?
4. Entra Workload Identity token auth at GA, or secret first?
5. One consolidated Postgres DB, or keep the event log split out?
6. jsonb vs text for the JSON columns?
7. Migration via init-job vs app-startup for the multi-replica rollout?
