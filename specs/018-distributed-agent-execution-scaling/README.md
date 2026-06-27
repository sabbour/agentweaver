# 018 — Distributed Agent Execution + Scalability

**Status:** Design (no implementation). **DB decision: LOCKED — Azure Database for PostgreSQL Flexible Server.**

Moving agent execution out of the single API pod into per-run sandbox pods is simultaneously the **OOM/memory-relief fix** and the **security-isolation fix**. The coordinator's orchestration loop stays in the API/worker tier; only its agent turns are sandboxed.

## Documents

| Doc | Owner | Scope |
|-----|-------|-------|
| [spec.md](./spec.md) | Morpheus (runtime) | Master architecture: MAF bridge (`RemoteAgentProxy` + in-pod `AgentHost`), agent execution in pods, credential model (no broker), phasing, file-by-file changes, test plan |
| [data-postgres-migration.md](./data-postgres-migration.md) | Tank (backend) | SQLite → Azure PostgreSQL: store inventory, porting strategy, leasing schema, run-event fan-out, migration mechanics |
| [platform-deployment.md](./platform-deployment.md) | Link (platform) | Azure Postgres provisioning, web/worker split, sandbox-pod identity/quota, transport networking, AKS rollout sequencing |

## Converged decisions
1. Agent execution (workers + coordinator agent-turns) → sandbox pods via thin **MAF bridge** (reuse MAF streaming/checkpointing/RequestPort; no bespoke protocol).
2. Coordinator **orchestration loop stays** in API/worker tier (not pod-per-coordinator).
3. **No capability-token broker** — pods may hold a run-scoped credential / use AKS workload identity. Dropped: `CapabilityTokenService`, coordinator-as-pod recursion, `ISandboxAgentHost` duplex protocol.
4. Scale path: **Azure PostgreSQL Flexible Server**, then **web/worker split + durable run leasing** (affinity ok).
5. Phasing behind `Sandbox:AgentExecutionMode` (default `in-api` for rollback): **P1** agent execution in pods [OOM fix] → **P2** Postgres → **P3** web/worker + leasing.

## Resolved decisions

- **Q1 — Bridge transport: ADOPT A2A (Agent Framework's Agent2Agent), message/stream mode.** A2A ships in .NET Agent Framework — host side `Microsoft.Agents.AI.Hosting.A2A(.AspNetCore)` (`app.MapA2A(agent, path, agentCard)`, SSE via `v1/message:stream`), client side `Microsoft.Agents.AI.A2A` (`A2AAgent : AIAgent` + `IA2AClient.AsAIAgent()`). We remote at the **AIAgent leaf seam**, so the MAF graph + all `WorkflowEvent`/`RequestPort` (HITL/review) logic stays in the worker and never crosses the wire — **no MAF↔A2A translation layer**. A2A is the **standardized realization of Option C** (the prior hand-rolled HTTP/2+SSE); we use **message-mode only** (no A2A task model), so no task-model tax. Durable resume stays our DB-backed `ICheckpointStore` + serialized `CopilotAIAgent` session blob (A2A's `contextId` history is ephemeral by design — we bypass it). **Rejected:** plain/bespoke HTTP/2+SSE (A2A supersedes it for free), gRPC. **kube-exec-stdio (A)** retained as the degraded-mode fallback. Caveat: the A2A package line is `-preview`.

  **Security verdict (Seraph, conditioned GO):** A2A is the **sole** transport (kube-exec-stdio rejected entirely per user directive). The **rollback path is the `Sandbox:AgentExecutionMode=in-api` flag** (revert to today's in-process execution) — NOT a second wire transport. A2A ships only when all of H1–H7 hold: **H1** TLS + workload-identity-bound pod server cert (mTLS/SPIFFE preferred; bearer only if short-lived + run/audience-scoped + per-run validated — naked bearer rejected); **H2** NetworkPolicy ingress scoped to worker-pod→sandbox:port only (plain + Cilium); **H3** `/v1/card` authz-gated + minimized (no anonymous discovery); **H4** explicit Kestrel timeout/body/stream limits + SSE heartbeats (don't let request-timeout kill the long stream); **H5** resume via our DB checkpoint with idempotent sequence-based re-injection, re-drive turn from last checkpoint on mid-turn drop (A2A `message:stream` has no Last-Event-ID replay); **H6** no sandbox egress broadening; **H7** preview lib pinned by exact version+hash, behind the flag, tracked to GA (rollback = `in-api` mode). AgentCard bearer/OAuth2 alone does NOT satisfy H1. **Residual risk accepted:** A2A `-preview` lib on the hot path with no alternate wire transport, mitigated by pinning + the `in-api` rollback flag.
- **Q2 — P1 ships on current SQLite/`replicas:1`: YES.** The OOM fix does NOT require Postgres in the same release, provided: the pod **never touches the DB directly**, all checkpoint/event writes proxy through the **single worker**, and no second replica is added. Only adding a second SQLite writer would force Postgres early. (Tank, `data-postgres-migration.md §6a`.)
- **Q3 — Pod granularity: HYBRID (pod-per-run + release-on-suspend).** Pod-per-run while a run has an active/imminent agent turn; **checkpoint-and-release** the pod when the MAF graph suspends on a `RequestPort` (HITL/review) or the coordinator idles awaiting children; re-claim a warm pod and rehydrate on resume. Release boundary = graph suspension on an external gate, NOT inter-turn. Checkpoint must carry the serialized `CopilotAIAgent` session blob + MAF superstep state (incl. suspended `ExternalRequest` correlation id). Flags: `Sandbox:AgentExecutionMode=pod-per-run` + `Sandbox:ReleasePodOnSuspend` (default true). (Morpheus, `spec.md §9/§12.2/§13`.)

## Still open (lower-stakes, decide during implementation)
- **Credential injection:** workload identity vs claim-time token (Link leans projected workload-identity token per pod).
- **Raw store porting:** unify the 6 raw SQLite stores into the EF context (Tank's recommendation) vs Npgsql-raw.
- **Q1 final confirmation:** C-now vs A-first — Ahmed leans C; A remains the pre-approved fallback.

## Known bugs surfaced (fix during implementation)
- `CoordinatorDispatchService.UpdateSubtaskAsync` (~1156-1171): blind read-modify-write, no owner check → **double-dispatch across replicas**; must become guarded CAS.
- `KubernetesSandboxExecutor`: **zero test coverage**.
- `SandboxEscapeEndToEndTests`: silent false-positives (`return;` without `RUN_LIVE_PROVIDER_TESTS`) → convert to `Assert.Skip`.
