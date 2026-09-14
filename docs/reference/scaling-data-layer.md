# Scaling Data Layer — Reference

See Cross-replica cursor polling and authoritative sequence allocation for the shared visual model.

This reference is the exhaustive companion to the [Distributed execution & scaling deep dive](../deep-dive/distributed-execution-scaling.md). It documents the data and topology layer of horizontal scaling: the SQLite ↔ Azure Database for PostgreSQL Flexible Server **provider switch**, the store inventory and how the raw stores unify into one context, the leasing schema, the run-event stream, the web/worker deployment topology, provisioning and connectivity, and the configuration flags that gate each phase.

The database target is **Azure Database for PostgreSQL Flexible Server** — the current, locked direction. The data layer is **provider-aware** (`Database:Provider`): `postgres`/`postgresql` routes durable state through the EF Core `MemoryDbContext` and EF-backed stores; the default `sqlite` keeps the single-writer SQLite stores. Sections below note where a capability is implemented versus a documented design target.

## 1. Store inventory

The original shape was two physical SQLite databases under the data directory: one holding the operational control plane reached through hand-written SQL, and one holding the memory/orchestration plane reached through the EF Core context. Under Postgres both are unified behind **one** EF `MemoryDbContext` and **one** database; the SQLite providers retain the original split.

### 1a. Operational stores (raw SQLite → EF, now provider-aware)

These six stores began as hand-written SQL against the operational database. They now have **provider-aware** equivalents: under `Database:Provider=postgres` they are served by EF-backed stores over `MemoryDbContext` (`EfRunStore`, `EfRunRevisionStore`, `EfWorkflowRunStore`, `EfBacklogTaskStore`, `EfCastProposalStore`); under the default `sqlite` the `Sqlite*` stores remain. The SQLite-specific idioms below describe the original raw form and how each maps onto Postgres/EF.

| Store | Tables | Data held |
| --- | --- | --- |
| Run store | `runs` (+ `run_revisions`) | Run records: status, worktree, tree hash, diff, review dwell timings, origin, parent/subtask linkage, archive state. Uses CAS-style `UPDATE ... WHERE status=...`. |
| Run-revision store | `run_revisions` | Append-only reviewer/revision comments, enforced append-only by a database trigger. |
| Project store | `projects` | Projects: origin, working dir, default branch, model defaults, state, pickup config, workflow/review-policy/sandbox profile, blueprint provenance, allowed workflow ids (JSON). |
| Backlog-task store | `backlog_tasks` | Kanban backlog/ready/claimed tasks, order key, claim→run binding, **partial unique indexes**, transactional claim CAS. |
| Workflow-run store | `workflow_runs` | Coordinator/workflow run grouping plus the shared orchestration worktree path. |
| Cast-proposal store | `cast_proposals` | Cast proposals with expiry, written via SQLite `INSERT OR REPLACE` upsert. |

The operational database also owns schema bootstrap via a hand-rolled idempotent migration list (`ALTER TABLE ... ADD COLUMN` plus a duplicate-column catch). This mechanism is SQLite-specific and is replaced by EF migrations.

### 1b. EF Core entities (already provider-switchable)

These already flow through the `Database:Provider` switch and carry generated EF migrations. They are **not** the blocker — Postgres for this set is a config flip plus a Postgres migration set.

| Entity | Data held |
| --- | --- |
| `Decision` | Promoted decisions and supersede chain. |
| `DecisionInboxEntry` | Decision inbox, unique `(ProjectId, Slug)`. |
| `AgentMemory` | Per-agent memory by type. |
| `SessionContext` | Session summaries, unique `(ProjectId, SessionId)`. |
| `RunEventRecord` | Durable run-event log, unique `(RunId, Sequence)` — also accessed via raw SQL (see §4). |
| `OutcomeSpec` | Coordinator outcome spec. |
| `WorkPlan` | Coordinator work plan; status/stage CAS target — extended for leasing (§3). |
| `Subtask` | Child subtasks; status pending→dispatched→running, child run id — extended for leasing (§3). |
| `SubtaskDependency` | Subtask DAG edges. |
| `SteeringDirective` | Queued/applied steering directives. |
| `McpRefreshToken` / `McpRevokedJti` / `McpClientRegistration` | MCP OAuth state with their respective unique keys. |
| `OAuthState` / `McpPendingAuthorization` / `McpAuthorizationCode` | Short-lived, replica-safe OAuth CSRF state and authorization codes. |
| `WebSessionExchangeCode` | Short-lived (60s), single-use one-time code issued at the OAuth callback to carry the session token to the browser without placing it in the redirect URL. Backed by Postgres so `POST /api/auth/session/exchange` can land on any replica. |
| `WorkflowCheckpointRecord` | Shared MAF workflow checkpoints (`workflow_checkpoints`), PK `(store_name, session_id, checkpoint_id)`, `jsonb` payload. Postgres-only (`model.Ignore<>()`d on SQLite); replaces the per-pod file checkpoint store so both `replicas: 2` share checkpoints with no exclusive lock (see the [Agent Framework deep dive](../deep-dive/agent-framework.md#checkpointing-durable-resume)). |

### 1c. Not a database (no migration)

Sandbox policy, workflows, and review policies are **file-based** under each project's `.agentweaver/` directory; the `projects` table only names a preset. They need no Postgres work, but they do raise a separate multi-replica concern (workspace files belong on a shared/RWX volume), which the topology section addresses.

## 2. The six operational stores in one EF context

Under Postgres the operational stores are served behind the EF Core `MemoryDbContext` rather than the hand-written ADO.NET path, giving **one provider switch, one connection story, one migration mechanism, one database**. `MemoryDbContext` maps the migrated entities (`RunRecord`→`runs`, `RunRevisionRecord`→`run_revisions`, `ProjectRecord`→`projects`, `BacklogTaskRecord`→`backlog_tasks`, `WorkflowRunRecord`→`workflow_runs`, `CastProposalRecord`→`cast_proposals`) with explicit snake_case column names, and `model.Ignore<>()`s all six on non-Npgsql providers so the SQLite migration snapshot is unaffected. The rationale:

1. **One provider switch.** The EF context already routes sqlite/sqlserver/postgres. The raw operational path has no provider abstraction; keeping it as raw Npgsql would mean maintaining two dialects by hand indefinitely.
2. **One migration mechanism.** The bespoke `ALTER TABLE` bootstrap is replaced by EF migrations the team already operates for the memory database.
3. **CAS already lives in EF.** The coordinator assembly store already proves that a guarded `ExecuteUpdateAsync(... .Where(Status == X))` delivers exactly-once CAS. Porting the `runs` and `backlog_tasks` claims to the same idiom is consistent, not novel.
4. **Cross-store transactions stay trivial.** The backlog claim spans `backlog_tasks` + `runs` in one transaction today. With both tables in one context and one database, it stays a single transaction; splitting them across two databases would require a distributed/two-phase hack.

The PostgreSQL path is implemented: operational stores, memory/orchestration entities, durable run events and workflow checkpoints use the shared database. The SQLite idiom table is migration/background guidance, not an outstanding staged-port plan. Changing providers does not transfer existing data.

### SQLite idioms and their Postgres mapping

Postgres is strictly typed and MVCC-based, so several SQLite idioms do not translate directly:

| SQLite idiom | Postgres / EF handling |
| --- | --- |
| Implicit autoincrement rowid | EF `int` key → `integer GENERATED BY DEFAULT AS IDENTITY`. App-generated string/GUID PKs stay `text`. |
| `INSERT OR REPLACE` (upsert) | `INSERT ... ON CONFLICT (id) DO UPDATE`. Semantics differ — `OR REPLACE` deletes+reinserts (fires cascades, resets defaults) whereas `ON CONFLICT DO UPDATE` updates in place. Audit FK/trigger side-effects before swapping. |
| `INSERT OR IGNORE` | `INSERT ... ON CONFLICT (RunId, Sequence) DO NOTHING`. |
| Dynamic typing (TEXT holds dates/bools/JSON) | Choose real column types (below). |
| Datetime stored as TEXT | `timestamptz`. The EF entities already use `DateTimeOffset`, which maps natively; backfill must parse the prior text format. |
| Boolean stored as INTEGER `0/1` | `boolean`; backfill `0/1 → false/true`. |
| JSON stored as TEXT | `jsonb` for queryable columns; `text` is fine for opaque blobs. |
| Partial unique indexes | Supported natively (`CREATE UNIQUE INDEX ... WHERE ...`; EF `HasIndex().IsUnique().HasFilter(...)`). These port cleanly. |
| Append-only via `RAISE(ABORT)` trigger | Postgres `BEFORE UPDATE/DELETE ... RAISE EXCEPTION` trigger (or `REVOKE UPDATE,DELETE`). Not expressible in the EF model — add as raw SQL in the migration. |
| `PRAGMA journal_mode=WAL`, `busy_timeout`, `foreign_keys=ON`, shared-cache | All vanish. Postgres is WAL-native and MVCC; FKs are always enforced; use `lock_timeout`/`statement_timeout` if needed. |
| `ALTER TABLE ADD COLUMN` idempotent bootstrap | Replaced by EF migrations applied at deploy time (§5). |

## 3. Leasing schema additions

These columns describe run leases. Coordinator plan ownership also exists, using `WorkPlan.CoordinatorPodId` and `UpdatedAt`: CAS acquisition protects a fresh owner, heartbeat renews ownership, and loss to a peer fences the dispatch loop. The separate per-subtask fencing/idempotency schema below remains design guidance.

### 3a. Lease / ownership columns

| Column | Type | Purpose |
| --- | --- | --- |
| `owner_id` | `text NULL` | The replica/worker currently holding the item (e.g. pod name/GUID). `NULL` = free. |
| `lease_expires_at` | `timestamptz NULL` | Lease deadline; an expired lease is reclaimable by any replica even if `owner_id` is set (crash recovery). |
| `heartbeat_at` | `timestamptz NULL` | Last liveness stamp from the owner; drives cross-replica stall detection. |
| `fencing_token` | `bigint NOT NULL DEFAULT 0` | Monotonic token bumped on every acquisition. The lease store checks owner and fencing token for renewal/release and exposes an ownership check; consumers must explicitly use these guards, preventing a zombie owner from clobbering a re-leased item. |
| `attempt` | `int NOT NULL DEFAULT 0` | Acquisition/execution attempt counter; bounds retries. |

### 3b. Idempotency for child dispatch *(design target — not yet implemented)*

Child-run dispatch should be exactly-once per `(coordinator, subtask, attempt)` so a re-leased coordinator does not double-spawn children. The intended design is a dispatch-idempotency table keyed on `(coordinator_run_id, subtask_id, attempt)` recording the resulting `child_run_id`, inserted in the **same transaction** that flips the subtask to `dispatched`; a duplicate insert (`ON CONFLICT DO NOTHING`) means "already dispatched — reuse the existing child." This table and its guard do **not** exist in the schema yet.

### 3c. Guarded CAS — implemented for runs/assembly/backlog; subtask dispatch outstanding

Three places already do DB-level CAS correctly:

- **Run lease CAS** — `PostgresRunLeaseStore` issues `UPDATE runs SET owner_id=@me, lease_expires_at=@deadline, fencing_token=fencing_token+1, attempt=attempt+1 WHERE run_id=@id AND (owner_id IS NULL OR lease_expires_at < now())`; the single winner sees `rows > 0`.
- **Assembly CAS** — `UPDATE WorkPlans SET Status=Assembling WHERE Id=@id AND Status=AwaitingAssembly`.
- **Backlog claim CAS** — `UPDATE backlog_tasks SET state='claimed' ... WHERE state='ready' AND run_id IS NULL`.

Do not confuse shipped plan-level coordinator ownership with a per-subtask dispatch-idempotency transaction. The following SQL is a proposed stronger contract, not the current schema, a migration, or an instruction to execute against production.

```
UPDATE Subtasks
   SET Status='dispatched', ChildRunId=@child, owner_id=@me,
       fencing_token=fencing_token+1, lease_expires_at=@deadline, UpdatedAt=now()
 WHERE Id=@id AND Status='pending'
   AND (owner_id IS NULL OR lease_expires_at < now());
```

The checked-in deployment already runs multiple replicas. Current plan ownership and heartbeat protections apply; evaluate the proposed per-subtask transaction separately rather than treating coordinator ownership as absent.

## 4. Run-event stream

### Current implementation

The run-event stream is durable write-through plus shared-store cursor polling. The Postgres implementation is `EfRunEventStream` (registered as `IRunEventStream` when `Database:Provider=postgres`; the SQLite path remains separate). Each append writes to `RunEvents` before acknowledgement, and each subscriber reads rows after its cursor from the shared table. Source: `apps/Agentweaver.Api/Program.cs:534`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:63`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:71`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:77`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:84`.

| Behavior | Contract | Source |
|---|---|---|
| Shared store append | `WriteThroughAsync` inserts a `RunEventRecord` with run id, sequence, event type, JSON payload, and timestamp. | `EfRunEventStream.cs:114`, `EfRunEventStream.cs:150`, `EfRunEventStream.cs:159` |
| Sequence safety | PostgreSQL appends take a per-run advisory transaction lock and allocate `MAX(Sequence)+1` in a ReadCommitted transaction. Supported transient/conflict failures get at most four attempts. An explicit sequence is idempotent only for a matching event; conflicting content is rejected. | `EfRunEventStream.cs:32`, `EfRunEventStream.cs:121`, `EfRunEventStream.cs:128`, `EfRunEventStream.cs:141`, `EfRunEventStream.cs:163` |
| Cross-replica subscribe | `SubscribeAsync` loads rows where `RunId` matches and `Sequence > lastSeen`, orders by sequence, and polls every `250 ms` when no row is available. | `EfRunEventStream.cs:33`, `EfRunEventStream.cs:77`, `EfRunEventStream.cs:96`, `EfRunEventStream.cs:180` |
| Local mirror | `RunStreamEntry.RecordNext` / `Record` still keep local history, but each append mirrors into `IRunEventStream` through `PersistBestEffort`. | `RunStreamStore.cs:87`, `RunStreamStore.cs:98`, `RunStreamStore.cs:106`, `RunStreamStore.cs:115`, `RunStreamStore.cs:164` |
| SSE fallback | A replica without a local entry streams from `IRunEventStream.SubscribeAsync` using the `Last-Event-ID` cursor. | `RunEndpoints.cs:416`, `RunEndpoints.cs:423`, `RunEndpoints.cs:429`, `RunEndpoints.cs:431` |

This means cross-replica live watching is implemented without sticky sessions: any web replica can observe events written by another worker by polling the shared event table from its cursor. The test `SubscribeAsync_TailsEventsWrittenByAnotherStreamInstance` covers this by using two stream instances over the same database. Source: `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:27`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:30`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:37`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:42`.

### Operational notes

- The polling floor is intentionally database-backed and payload-safe; it does not depend on process-local channels, `LISTEN/NOTIFY`, or an external bus. Source: `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:77`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:96`.
- Subscribers emit the full loaded replay batch before closing on a terminal event, preserving persisted diagnostics after that entry. Retryable `coordinator.assembly_blocked` does not itself close the stream.
- Terminal backfill still re-appends the full local history through `IRunEventStream`; duplicates are skipped by the stream implementation, so this reconciles missed best-effort mirrors. Source: `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:287`, `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:296`, `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:298`, `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:301`.

## 5. Deployment topology — web vs worker

Both tiers run from the **same image**, differentiated by the role flag `App:Role` (env `App__Role`, values `web` vs `worker`; resolved by `AppRole` and branched on in `Program.cs`). No second image is built or pushed.

### Web tier

- Serves HTTP/auth/UI/MCP ingress and the SSE relay; keeps the gateway ingress rules and OAuth/secret config as today.
- The checked-in `agentweaver-api` and `agentweaver-worker` Deployments use the same image and start at two replicas with RollingUpdate. The worker sets `App:Role=worker`. API retains HTTP/auth/SSE and sandbox/preview control; shared heartbeat services still perform pickup/reconciliation. This is not an API-enqueue-only separation.
- Stateless once SQLite is gone → safe to scale `2..N`. Autoscales on request load (HPA on CPU and/or a request-rate metric).

### Worker tier

- **Claims runs via leasing** (§3), runs the orchestration loop in-process, and dispatches per-run sandbox pods.
- API retains sandbox and preview responsibilities and their permissions. Removing sandbox RBAC or workspace access would require an explicit product change, not merely a deployment-role setting.
- Autoscales on **run/queue depth**, not CPU. The preferred mechanism is a KEDA PostgreSQL scaler querying unleased/queued run depth (roughly `SELECT count(*) FROM runs WHERE state='queued' AND lease IS NULL`), with scale-to-min (never zero — workers must keep leasing and draining). If KEDA is unavailable, fall back to an HPA on a "queued runs" custom metric exported via the existing OTEL/Prometheus path.

### Disruption and graceful drain

The worker PDB uses `minAvailable: 1`. A 30-second preStop delay and 120-second termination grace are configured. The five-minute run lease TTL exceeds that grace period; orderly shutdown and lease-expiry recovery are distinct mechanisms.

### Volumes

API and worker no longer mount the SQLite data PVC. The `agentweaver-data` RWO claim remains as a rollback resource, not an active PostgreSQL dependency. Both retain the RWX Azure Files workspace at `/workspace`; worker HOME remains `/workspace/.home`. Implementation children execute in pod-local scratch and publish verified Git writeback to authoritative shared worktrees.

Sandbox pods do not connect directly to PostgreSQL. API and worker mediate durable state. Both can call AgentHost control/A2A endpoints; AgentHost tools call run-scoped API callbacks. API also retains preview and sandbox lifecycle responsibilities.

## 6. Provisioning & connectivity (Azure PostgreSQL Flexible Server)

The DB-side connection details are summarized here; the full platform runbook lives alongside the [AKS deployment guide](../guide/deployment-aks.md) and [infrastructure deep dive](../deep-dive/infra-deployment.md).

- **Connectivity — private access via VNet integration (recommended).** Inject the Flexible Server into a dedicated delegated subnet in the AKS VNet and reach it over a private IP with the `privatelink.postgres.database.azure.com` Private DNS zone linked to the VNet. This matches the cluster's "no public app surface" posture. A Private Endpoint is an acceptable alternative when VNet injection is blocked by subnet topology; public access plus firewall allowlists is not recommended for production.
- Passwordless Entra database authentication is migration guidance, not the current deployment contract. Provisioning generates an administrator password; API/worker consume the `agentweaver-postgres` Secret connection string. Passwordless authentication requires explicit provider/token-refresh and database-role work.
- **High availability.** Zone-redundant HA (primary + standby in different zones) on a General Purpose (or higher) tier, paired with zone-redundant backups and a 7–35 day point-in-time-restore retention window.
- **Pooling.** Front Postgres with PgBouncer or use Npgsql pooling for N replicas; the shipped run-event stream uses cursor polling against shared `RunEvents`, so it does not require session-mode `LISTEN/NOTIFY` connections.
- **Migrations at deploy time.** Apply EF migrations from an init container/migration job rather than `EnsureCreated`, so schema is versioned and replica startup is race-free. Generate a **separate Postgres migrations set** (the existing snapshots encode SQLite affinity and must not be run against Postgres) and select it by provider at runtime. With multiple replicas the init container runs per-pod; rely on EF's migration-history table for idempotency.

## 7. Configuration flags that gate the phases

Each phase is reversible via a flag defaulting to today's behavior:

| Flag | Values (default) | Gates |
| --- | --- | --- |
| `Sandbox:AgentExecutionMode` | `in-api` *(default)* / `pod-per-run` | P1 — `pod-per-run` activates agent execution in sandbox pods over the bridge; `in-api` is the instant rollback to in-process execution. |
| `Sandbox:ReleasePodOnSuspend` | `true` *(default)* / `false` | P1 tuning — release the pod when the graph suspends on a HITL gate or the coordinator idles; `false` keeps the pod warm for low-latency resume/debug. |
| `Database:Provider` | `sqlite` *(default)* / `postgres` | P2 — selects the EF provider. Selects persistence provider, without copying state. SQLite rollback requires an explicit restore/data plan and single-writer deployment; it does not preserve PostgreSQL leasing. |
| `ConnectionStrings:MemoryDb` / `Database:ConnectionString` | connection string | P2 — the Postgres connection (carries no secret under passwordless Entra auth). |
| `App:Role` | `web` *(default)* / `worker` | P3 — selects the deployment role from the shared image (env `App__Role`; unset = `web`). |

## 8. Rollout sequencing

This is historical migration guidance, not an unshipped-feature checklist or a promise of flag-only reversal. Current manifests include PostgreSQL, two API/worker replicas, RollingUpdate, and worker HPA. Cutover and rollback require explicit state handling. Each step lands as reviewed YAML and `scripts/azure` edits applied through the existing render-and-apply pipeline — never an ad-hoc live patch. Cross-reference the [scaling deep dive's phased rollout](../deep-dive/distributed-execution-scaling.md#the-phased-rollout).

1. **Provision Postgres** (no app cutover) — VNet subnet delegation, Flexible Server with zone-redundant HA, Private DNS zone link, Entra admin + UAMI DB role. App still on SQLite. *Rollback: none needed.*
2. **Identity wiring** — add the worker SA federated credential (and a sandbox-runner SA if pods authenticate to the model with a projected token) and the DB role grant. *Rollback: drop the federated credentials.*
3. **Provider switch behind the flag** — ship the EF Postgres provider behind `Database:Provider`, run migrations against Postgres on a single replica first, backfill if required. *Rollback: flip the provider flag to `sqlite`.*
4. **Drop `/data` PVC, enable RollingUpdate** — once Postgres reads/writes are verified, remove the data PVC and mounts, set `strategy: RollingUpdate`, `replicas: 2`. *Rollback: re-add the PVC + Recreate + `provider=sqlite` (back up first — this step is data-loss-aware).*
5. **Web/worker split** — add the web and worker deployments (role flag), worker PDB, split SAs/RBAC. *Rollback: scale workers to 0; Role selection and `Sandbox:AgentExecutionMode` are separate controls; scaling workers to zero is not a verified execution-topology rollback.*
6. **Autoscaling** — add the web HPA and the worker KEDA ScaledObject (or HPA fallback). *Rollback: delete them; fixed replicas remain.*

The data backfill is greenfield by default: operational state (in-flight runs, event logs, backlog) is largely ephemeral, so a clean cutover is the recommended path. If history must be preserved, treat backfill as an explicit task with the type coercions from §2 (TEXT datetimes → `timestamptz`, INTEGER booleans → `boolean`, TEXT JSON → `jsonb`/`text`), re-created partial indexes, and the re-created append-only trigger on `run_revisions`.

## Related reading

- [Distributed execution & scaling deep dive](../deep-dive/distributed-execution-scaling.md) — the concept-first scaling story.
- [Scaling operations](../experience/scaling-operations.md) — the operator's view.
- [Data & persistence deep dive](../deep-dive/data-persistence.md) — the durable domain model.
- [Infrastructure & deployment deep dive](../deep-dive/infra-deployment.md)
- [Sandbox pod execution](../deep-dive/sandbox-pod-execution.md) and the [A2A bridge](../deep-dive/a2a-bridge.md) — where and how agent execution runs in pods.

The active worker HPA ranges from two to three replicas, targeting CPU 70% and memory 80%. Queue-depth KEDA and an API HPA remain guidance, not active resources in the checked-in manifests.

The PostgreSQL migrations assembly and init-container bundle path already exist. API/worker invoke `efbundle --postgres-migrations`; sequencing and backfill remain explicit deployment operations, not effects of a provider flag.

<details id="diagram-context-canonical-durable-event-stream">
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Postgres is the event relay</td></tr>
<tr><td>subtitle</td><td>Any API replica can serve a cursor over durable RunEvents—no sticky session required.</td></tr>
<tr><td>group-title0</td><td>Write path · replica A</td></tr>
<tr><td>group-title1</td><td>Read path · replica B</td></tr>
<tr><td>Run producer</td><td>Run producer</td></tr>
<tr><td>Run producer</td><td>Append a structured event</td></tr>
<tr><td>Run producer</td><td>runId + type + payload</td></tr>
<tr><td>EF event stream</td><td>EF event stream</td></tr>
<tr><td>EF event stream</td><td>Serialize writes per run</td></tr>
<tr><td>EF event stream</td><td>pg_advisory_xact_lock</td></tr>
<tr><td>RunEvents</td><td>RunEvents</td></tr>
<tr><td>RunEvents</td><td>Shared PostgreSQL table</td></tr>
<tr><td>RunEvents</td><td>(RunId, Sequence)</td></tr>
<tr><td>Web / MCP watcher</td><td>Web / MCP watcher</td></tr>
<tr><td>Web / MCP watcher</td><td>Consume ordered events</td></tr>
<tr><td>Web / MCP watcher</td><td>last delivered cursor</td></tr>
<tr><td>SSE endpoint</td><td>SSE endpoint</td></tr>
<tr><td>SSE endpoint</td><td>Emit id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>ordered response frames</td></tr>
<tr><td>EF subscriber</td><td>EF subscriber</td></tr>
<tr><td>EF subscriber</td><td>Read Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>idle poll: 250 ms</td></tr>
<tr><td>e1</td><td>append</td></tr>
<tr><td>e2</td><td>commit</td></tr>
<tr><td>e3</td><td>ordered batch</td></tr>
<tr><td>e4</td><td>yield</td></tr>
<tr><td>e5</td><td>SSE frames</td></tr>
<tr><td>assurance-title</td><td>POSTGRES LANE ONLY</td></tr>
<tr><td>assurance-line1</td><td>SQLite register-channel / replay / tail is a separate implementation—not this architecture.</td></tr>
<tr><td>assurance-line2</td><td>Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>Run producer</td><td>Input</td></tr>
<tr><td>Run producer</td><td>RunStreamEntry</td></tr>
<tr><td>Run producer</td><td>Identity</td></tr>
<tr><td>Run producer</td><td>runId + event type</td></tr>
<tr><td>Run producer</td><td>Body</td></tr>
<tr><td>Run producer</td><td>Structured payload</td></tr>
<tr><td>Run producer</td><td>Ack</td></tr>
<tr><td>Run producer</td><td>After durable commit</td></tr>
<tr><td>EF event stream</td><td>Lock</td></tr>
<tr><td>EF event stream</td><td>Per-run advisory lock</td></tr>
<tr><td>EF event stream</td><td>Next</td></tr>
<tr><td>EF event stream</td><td>MAX(Sequence) + 1</td></tr>
<tr><td>EF event stream</td><td>Write</td></tr>
<tr><td>EF event stream</td><td>Save transaction</td></tr>
<tr><td>EF event stream</td><td>Commit</td></tr>
<tr><td>EF event stream</td><td>Before acknowledgement</td></tr>
<tr><td>RunEvents</td><td>Table</td></tr>
<tr><td>RunEvents</td><td>Key</td></tr>
<tr><td>RunEvents</td><td>RunId + Sequence</td></tr>
<tr><td>RunEvents</td><td>Order</td></tr>
<tr><td>RunEvents</td><td>Ascending sequence</td></tr>
<tr><td>RunEvents</td><td>Reuse</td></tr>
<tr><td>RunEvents</td><td>Same type / payload</td></tr>
<tr><td>Web / MCP watcher</td><td>Client</td></tr>
<tr><td>Web / MCP watcher</td><td>Web or MCP</td></tr>
<tr><td>Web / MCP watcher</td><td>Resume</td></tr>
<tr><td>Web / MCP watcher</td><td>Last delivered cursor</td></tr>
<tr><td>Web / MCP watcher</td><td>Replica</td></tr>
<tr><td>Web / MCP watcher</td><td>No sticky requirement</td></tr>
<tr><td>Web / MCP watcher</td><td>History</td></tr>
<tr><td>Web / MCP watcher</td><td>Durable ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Frame</td></tr>
<tr><td>SSE endpoint</td><td>id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>Cursor</td></tr>
<tr><td>SSE endpoint</td><td>Last-Event-ID</td></tr>
<tr><td>SSE endpoint</td><td>Delivery</td></tr>
<tr><td>SSE endpoint</td><td>Yield ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Close</td></tr>
<tr><td>SSE endpoint</td><td>After batch is drained</td></tr>
<tr><td>EF subscriber</td><td>Query</td></tr>
<tr><td>EF subscriber</td><td>Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>Idle</td></tr>
<tr><td>EF subscriber</td><td>Poll after 250 ms</td></tr>
<tr><td>EF subscriber</td><td>State</td></tr>
<tr><td>EF subscriber</td><td>Shared durable table</td></tr>
<tr><td>EF subscriber</td><td>Blocked</td></tr>
<tr><td>EF subscriber</td><td>Retryable: keep open</td></tr>
<tr><td>producer</td><td>Coordinator or run execution; Acknowledgement follows commit</td></tr>
<tr><td>append</td><td>Allocate MAX(Sequence) + 1; Save and commit transaction</td></tr>
<tr><td>store</td><td>Cross-replica ordered history; Explicit duplicates must match payload</td></tr>
<tr><td>client</td><td>Reconnect from the cursor; No local channel dependency</td></tr>
<tr><td>sse</td><td>Cursor advances after delivery; Drain batch before terminal close</td></tr>
<tr><td>reader</td><td>Query the shared durable table; Retryable assembly_blocked stays open</td></tr>
<tr><td>notes</td><td>POSTGRES LANE ONLY; SQLite register-channel / replay / tail is a separate implementation—not this architecture.; Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>groups</td><td>Write path · replica A; Read path · replica B</td></tr>
</tbody></table>
</details>

<details id="diagram-context-reference-scaling-data-layer-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Scale durable state, isolate execution</td></tr>
<tr><td>subtitle</td><td>Checked-in AKS topology, not a live-cluster observation or a future migration proposal.</td></tr>
<tr><td>platform-title</td><td>PLATFORM REPLICAS</td></tr>
<tr><td>platform-subtitle</td><td>Same application image / RollingUpdate</td></tr>
<tr><td>sandbox-title</td><td>PER-RUN KATA POD</td></tr>
<tr><td>sandbox-subtitle</td><td>AgentHost control + model-execution sidecar</td></tr>
<tr><td>API</td><td>API</td></tr>
<tr><td>API</td><td>HTTP / auth / SSE Sandbox + preview control</td></tr>
<tr><td>API</td><td>agentweaver-api</td></tr>
<tr><td>Worker</td><td>Worker</td></tr>
<tr><td>Worker</td><td>Runs / orchestration Validate + apply writeback</td></tr>
<tr><td>Worker</td><td>agentweaver-worker</td></tr>
<tr><td>AgentHost</td><td>AgentHost</td></tr>
<tr><td>AgentHost</td><td>One-time configure; A2A turns Returns prepared-writeback receipt</td></tr>
<tr><td>AgentHost</td><td>listener :8088</td></tr>
<tr><td>Pod-local checkout</td><td>Pod-local checkout</td></tr>
<tr><td>Pod-local checkout</td><td>Verified source commit + tree Model tools edit detached checkout</td></tr>
<tr><td>Pod-local checkout</td><td>/local-workspace</td></tr>
<tr><td>PostgreSQL</td><td>PostgreSQL</td></tr>
<tr><td>PostgreSQL</td><td>State / memory / events Checkpoints + leases</td></tr>
<tr><td>Azure Files</td><td>Azure Files</td></tr>
<tr><td>Azure Files</td><td>Shared repo + authoritative child worktrees</td></tr>
<tr><td>hpa-detail</td><td>HPA: CPU 70%, memory 80% Worker PDB: minAvailable 1</td></tr>
<tr><td>lease-detail</td><td>Run lease: 5 min; renew halfway. Plan ownership: CAS + heartbeat.</td></tr>
<tr><td>storage-label</td><td>SHARED PERSISTENCE</td></tr>
<tr><td>database-boundary</td><td>No sandbox DB connection.</td></tr>
<tr><td>writeback-title</td><td>VERIFIED PUBLICATION</td></tr>
<tr><td>writeback-detail</td><td>Temporary ref goes to the shared repo, not GitHub. Worker verifies receipt, then applies --ff-only. An unchanged tree produces a no-change receipt.</td></tr>
<tr><td>footer</td><td>SQLite RWO PVC is retained but unmounted. HPA is active; queue-depth KEDA remains guidance. Shared RWX storage is persistence, not a per-run isolation boundary.</td></tr>
<tr><td>database-access</td><td>state / leases</td></tr>
<tr><td>workspace-access</td><td>workspace</td></tr>
<tr><td>a2a-control</td><td>A2A</td></tr>
<tr><td>Pod-local checkout</td><td>execute</td></tr>
<tr><td>source-fetch</td><td>fetch</td></tr>
<tr><td>temp-ref</td><td>temp ref</td></tr>
</tbody></table>
</details>
