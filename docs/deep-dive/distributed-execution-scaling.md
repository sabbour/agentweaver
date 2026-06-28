# Distributed Execution & Scaling — Conceptual Deep Dive

## Purpose and Mental Model

Agentweaver starts life as a single API pod that does everything: it serves HTTP and the live event stream, runs the orchestration graph for every run, and writes all durable state to a single-writer SQLite file. That shape is simple and correct for one instance, but it has a ceiling. This document tells the **scaling story** — why the single pod has to give way, and the phased path that turns one vertical box into a horizontally scalable system.

The mental model has three moving parts:

1. **A data layer** that can take writes from more than one process at a time.
2. **A topology** that separates the work that fans out widely (serving API and event streams) from the work that must own a run end-to-end (the orchestration loop).
3. **A coordination primitive** — a durable lease — that lets many identical worker processes share the pool of runs without two of them ever grabbing the same one.

A rebuild should keep these three concerns distinct. The data layer answers "where does state live and who may write it?"; the topology answers "which process does which job?"; and leasing answers "who owns this run right now?".

This page is concept-first. For the exhaustive store inventory, schema additions, and provisioning notes see [Scaling data layer reference](../reference/scaling-data-layer.md); for the operator's view see [Scaling operations](../experience/scaling-operations.md).

## Why move off the single API pod

Two pressures push execution out of one process, and they turn out to be the *same* fix.

### Memory: the OOM

The single pod runs every run's heavy execution state **in-process**. Each active run holds a live model SDK session, an in-process orchestration graph, per-run event channels, and a bounded in-memory history of recently completed runs. Memory therefore scales with *concurrent + recently-completed runs* multiplied by *(SDK session + graph + event history)*. Inside a fixed container memory limit, enough parallel runs eventually exhaust it and the pod is OOM-killed.

The pod cannot simply be scaled out to relieve the pressure, because the data layer underneath it is single-writer SQLite on a ReadWriteOnce volume. That constraint pins the deployment to one replica with a recreate (not rolling) update strategy, so the old pod releases the disk before the new one attaches. Vertical growth is the only lever, and it has run out.

### Isolation: the security boundary

Separately, each run's tool, shell, and model execution wants its own isolation boundary so that one run cannot observe or interfere with another. The natural place to put that boundary is a per-run sandbox pod.

The key insight is that **memory relief and isolation are the same move**. Relocating the heavy execution — the model SDK session, the in-pod runner, and tool/shell/file execution — into a per-run [sandbox pod](./sandbox-pod-execution.md) simultaneously evicts the dominant per-run footprint from the API process *and* gives each run its own isolated boundary. After the move, the API tier becomes a thin orchestrator: HTTP, event relay, and database. This is the foundation everything else builds on.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart LR
    subgraph Before["Single API pod — replicas:1, fixed memory"]
        H1["HTTP + SSE"] --> G1["Orchestration graph"]
        G1 --> S1["Model SDK session + tool exec<br/>HEAVY, in-process"]
        G1 --> D1[("SQLite — single writer")]
    end
    subgraph After["Thin orchestrator + sandbox pods"]
        H2["HTTP + SSE"] --> G2["Orchestration graph"]
        G2 -. bridge .-> P2["Sandbox pod<br/>SDK session + tool exec"]
        G2 --> D2[("Postgres — multi-writer")]
    end
    Before ==>|"evict heavy state"| After

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;
    class H1 svc;
    class G1 core;
    class S1 runtime;
    class D1 data;
    class H2 svc;
    class G2 core;
    class P2 runtime;
    class D2 data;
```

## The phased rollout

The scaling story does not land in one release. It is three phases, each independently shippable, each behind a flag that defaults to today's in-process behavior so any step can be reverted instantly.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TD
    P1["P1 — Agent execution in pods<br/>THE OOM fix<br/>stays on SQLite, replicas:1"]
    P2["P2 — Azure PostgreSQL<br/>multi-writer data layer<br/>drop RWO PVC, allow replicas above 1"]
    P3["P3 — Web/worker split + run leasing<br/>horizontal scale of both tiers"]
    P1 --> P2 --> P3
    P1 -. "OOM relieved here" .-> Done1(["stops OOM"])
    P3 -. "scale-out here" .-> Done2(["horizontal scale"])

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;
    class P1 svc;
    class P2 svc;
    class P3 core;
    class Done1 evt;
    class Done2 evt;
```

### P1 — agent execution in pods (the OOM fix)

P1 relocates only the heavy execution into sandbox pods over a thin agent bridge. It keeps a **single** orchestrating process and the existing SQLite file. This is deliberate and safe: the pod is a *compute satellite*, never a database writer. Every checkpoint and run-event write is proxied back through the one worker, which remains the sole owner of durable state. Because there is still exactly one writer, SQLite's single-writer invariant holds and nothing forces Postgres yet.

P1 stops the OOM on its own. The dominant per-run footprint — the live model session plus its tool buffers — leaves the API process and dies with the pod. The orchestration graph, the watch loop, and the bounded event history that stay behind are comparatively light.

The rule that keeps P1 single-writer-safe is precise: the pod must never open a database connection or mount the data volume, all checkpoint and event writes must be proxied through the single worker, and no second orchestrating replica may be added. Only the introduction of a *second writer process* would force the data-layer migration early.

### P2 — Azure Database for PostgreSQL Flexible Server

P2 swaps the backing store from SQLite to **Azure Database for PostgreSQL Flexible Server** (the current, locked direction). This is the step that removes the single-writer constraint. Once a real multi-writer database is underneath, the ReadWriteOnce data volume can be dropped, the deployment can move from a recreate to a rolling update strategy, and `replicas` can exceed one.

P2 is mostly invisible to end users — the run/review model and the public API and event contracts do not change. What changes is *where* state lives and *who may write it concurrently*. The migration folds the previously separate raw stores into a single provider-agnostic data context so there is one connection story and one migration mechanism; the [data-layer reference](../reference/scaling-data-layer.md) covers exactly which stores move and how.

### P3 — web/worker split + durable run leasing

P3 takes the now-stateless tier and splits it by role, then adds the coordination primitive that lets many copies of the worker role run at once. This is where horizontal scale actually arrives, and it is the heart of the topology.

## The web/worker deployment split

Once SQLite is gone, the orchestrator's two jobs have very different scaling shapes, and it pays to separate them into two deployments built from the **same image**, differentiated only by a role flag.

- **Web tier** — serves the REST API, authentication, and the live event (SSE) relay to clients. It is stateless: it holds no run's orchestration graph in memory. It scales with *request and connection load* and can grow freely to N replicas.

- **Worker tier** — owns the orchestration loop. A worker claims a run, runs its orchestration graph in-process, drives the agent turns over the bridge to sandbox pods, and performs the durable checkpoint and run-event writes. It scales with *run backlog depth*, not raw CPU, because claim-then-dispatch work is I/O-bound.

The division of labor is the important idea: **web pods touch clients but never own runs; worker pods own runs but are not on the request hot path.** A client can connect to any web pod and still observe a run that a completely different worker pod is executing — which is exactly what the event fan-out (below) has to make true.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TB
    Client["Clients<br/>browser · API"]

    subgraph WebTier["Web tier — stateless, scales on request load"]
        WebA["Web pod A<br/>REST · auth · SSE relay"]
        WebB["Web pod B<br/>REST · auth · SSE relay"]
    end
    subgraph WorkerTier["Worker tier — owns runs, scales on backlog depth"]
        W1["Worker pod A<br/>orchestration graph"]
        W2["Worker pod B<br/>orchestration graph"]
    end
    subgraph Pods["Sandbox pods — one per run"]
        Pod1["AgentHost + CopilotAIAgent"]
        Pod2["AgentHost + CopilotAIAgent"]
    end
    DB[("Azure PostgreSQL<br/>runs · run-event log · leases")]

    Client -->|HTTP + SSE| WebA
    Client -->|HTTP + SSE| WebB
    WebA -->|enqueue · read events| DB
    WebB -->|enqueue · read events| DB
    W1 -->|claim+reserve · checkpoint · event writes| DB
    W2 -->|claim+reserve · checkpoint · event writes| DB
    W1 -. agent bridge — A2A .-> Pod1
    W2 -. agent bridge — A2A .-> Pod2
    DB -. events tailed by cursor .-> WebA
    DB -. events tailed by cursor .-> WebB

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;
    class Client client;
    class WebA svc;
    class WebB svc;
    class W1 core;
    class W2 core;
    class Pod1 runtime;
    class Pod2 runtime;
    class DB data;
```

## Durable run leasing

With more than one worker, the central question becomes: **how do N identical workers share one pool of runs without two of them grabbing the same run?** A blind read-modify-write cannot answer this. If two workers both read a run as "unowned" and both write themselves as owner, the last writer wins and the run is dispatched twice — the classic double-dispatch bug. (The orchestration codebase has exactly this hazard today in the path that flips a subtask to *dispatched*: it loads the row, mutates it, and saves, with no ownership guard. Under multiple replicas that is unsafe and must become guarded.)

The fix is a **durable lease** expressed as a *guarded compare-and-set* (CAS) on the work item's row. Instead of "read then write," a worker issues a single conditional update: claim this run **only if** it is currently unowned or its lease has expired, and stamp my identity and a fresh deadline in the same statement. The database guarantees that exactly one worker's update affects a row; every other worker sees zero rows changed and moves on. The winner — and only the winner — proceeds to execute.

Leasing rests on a small set of per-row ideas:

- **Ownership** — which worker currently holds the run (its identity, e.g. a pod name), or nothing if the run is free.
- **Expiry** — a lease deadline. An expired lease is reclaimable by *any* worker even if an owner is still nominally stamped. This is what makes crash recovery automatic: a worker that dies stops renewing, its lease lapses, and another worker re-claims the run.
- **Heartbeat** — a liveness stamp the owner refreshes while it works, so stalls are visible across the fleet rather than only inside one process.
- **A fencing token** — a number that increments on every successful acquisition. A worker must present its token when it writes; a stale (smaller) token is rejected. This stops a paused or zombie former owner from waking up and clobbering a run that has since been re-leased to someone else.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant A as Worker A
    participant B as Worker B
    participant DB as Postgres (run row)
    A->>DB: UPDATE ... SET owner=A, token+1 WHERE owner IS NULL OR lease_expired
    DB-->>A: rows = 1  (A wins)
    B->>DB: UPDATE ... SET owner=B WHERE owner IS NULL OR lease_expired
    DB-->>B: rows = 0  (already owned — B steps aside)
    Note over A: A renews heartbeat while it works
    A--xA: A crashes (stops renewing)
    Note over DB: lease expires
    B->>DB: UPDATE ... WHERE lease_expired
    DB-->>B: rows = 1  (B re-claims — crash recovery)
```

A second, related guarantee covers **child dispatch**: a coordinator that spawns child runs must do so *exactly once* per (coordinator, subtask, attempt), even if it is re-leased mid-flight to another worker. An idempotency record written in the same transaction that flips the subtask to *dispatched* makes redelivery safe — a duplicate attempt simply discovers the existing child and reuses it rather than spawning a second one.

**Affinity is acceptable and even desirable.** Because a worker that holds a lease also holds that run's in-process orchestration graph and its HITL gates, work for a given run prefers to stay on its owning worker. Affinity is an optimization layered on top of leasing, not a replacement for it: the lease remains the source of truth, so if the owning worker dies, any other worker can still take over.

## Run-event fan-out under multiple replicas

The live event stream is what makes a run watchable in real time. In a single process this is easy: the producer writes events into a process-local channel and the SSE relay reads from the same channel. The moment there are multiple replicas, that breaks — a run executes on worker A, but an SSE client may be connected to web pod B, whose local channel never sees A's events. Durability is fine because every event is written through to the shared database before it is acknowledged; what is lost is **live cross-replica delivery**.

The reconstruction principle is *durable-write-through plus cross-replica notification, with a polling backstop*:

- Every event is synchronously persisted to the shared event log **before** it is acknowledged — this never changes, and it is what makes replay possible.
- On each durable write, the producing worker emits a lightweight cross-replica notification carrying only a cursor (run id and sequence), not the payload. Any replay with subscribers for that run wakes them, and they read the new rows from the database.
- Every subscriber *also* runs a low-frequency catch-up poll: "give me everything for this run after my cursor." Because reads are cursor-based and the event log enforces a unique (run, sequence) ordering, catch-up is idempotent and gapless. A missed notification can never strand a client — it only delays delivery until the next poll.

The process-local channel does not disappear; it is retained as a **same-replica fast path** so that a client connected to the worker actually executing a run still gets the lowest-latency stream. The cross-replica notification plus the catch-up poll are the *floor* that guarantees correctness everywhere else.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart LR
    Prod["Worker producing events"] -->|"1. durable write-through"| Log[("Run-event log<br/>unique (RunId, Sequence)")]
    Prod -->|"2. notify cursor"| Bus{{"cross-replica notify"}}
    Bus --> Sub["Subscriber on another replica"]
    Log -->|"3. cursor read after last seen"| Sub
    Sub -. "catch-up poll (backstop)" .-> Log
    Prod -->|"same-replica fast path"| LocalSub["Local subscriber"]

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;
    class Prod core;
    class Log data;
    class Bus evt;
    class Sub svc;
    class LocalSub svc;
```

One subtlety the data layer must handle: **sequence allocation has to stay correct under concurrent writers.** A naive "max sequence + 1" is racy across database snapshots once more than one writer can touch the same run. The unique (run, sequence) index is the durable safety net, backed by a per-run allocation strategy; the reference doc covers the mechanics. If leasing guarantees exactly one writer per run, contention only exists across the rare re-lease boundary, which keeps the allocation cheap.

## How the pieces reinforce each other

The three concerns are not independent features bolted together — each one unblocks the next:

- Moving execution into pods (P1) is what makes the orchestrator *thin enough* to be stateless.
- A multi-writer database (P2) is what makes "more than one orchestrator" legal at all.
- Leasing is what makes "more than one orchestrator" *safe*, and the lease's owner identity is what affinity and the brokered checkpoint store key off of.
- Event fan-out is what keeps the user experience identical once a run and its watcher can land on different pods.

Take any one away and the rest cannot stand: leasing without a multi-writer store has nothing to coordinate; multiple workers without event fan-out break live watching; pods without a thin orchestrator do not actually relieve the memory pressure that started the whole story.

## Related reading

- [Scaling data layer reference](../reference/scaling-data-layer.md) — the exhaustive store inventory, leasing schema, fan-out mechanism, and provisioning.
- [Scaling operations](../experience/scaling-operations.md) — what scaling looks like to an operator.
- [Sandbox pod execution](./sandbox-pod-execution.md) — where the heavy agent execution actually runs.
- [Agent communication](./agent-communication.md) and the [A2A bridge](./a2a-bridge.md) — how the worker drives an agent turn inside a pod.
- [Data & persistence](./data-persistence.md) — the durable domain model the migration carries forward.
- [Infrastructure & deployment](./infra-deployment.md) and [AKS architecture](../architecture-aks.md) — the cluster this runs on.
