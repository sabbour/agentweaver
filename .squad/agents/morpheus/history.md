# Morpheus — History (Summarized)

## 2026-06-07 through 2026-06-18 — ARCHIVED SUMMARY

**Early features (001–008, June 7–18):**
- Wave 1 domain/runtime: RunId, RunStatus, EventType (15 payloads), SandboxPathValidator, SandboxedFileTools, MAF loop, Copilot/Foundry providers, content-safety checker
- Wave 2 MAF HITL rewrite: RunOrchestrator → MAF graph, 4 integration tests, unified IMergeCoordinator
- Sandboxed execution: 4-platform ISandboxExecutor, 9 SandboxedTools, SandboxOutputRedactor, ANSI sanitizer, per-sandbox temp isolation
- Feature 005 team casting: Delivered EmbeddedCatalog (9 groupings, 31 roles), UniversePools (14 pools), 430 tests passing
- Feature 008 coordinator: CoordinatorRunService, OutcomeSpec, MAF integration, Phase 1-2 delivery (dispatch, steering, child-run pipeline, smoke-test remediation, confirm-gate race fix, child identity + events endpoint, node_type taxonomy, GraphDescriptor)
- Commits: 231e987, c0abc92, 6e546fe, 6129733, a49cdf8, a05e46a, 3053741, d70d1a9, bd0ddba, 7c10047
- Build track record: consistently 236–430 tests passing

---

## 2026-06-26T11-44-02-07-00 — Workspace-as-persistent-sandbox architectural walkthrough

Evaluated moving workspace creation from the API into the sandbox pod (Model B). Recommendation: **keep workspace creation in the API**.

Three reasons for rejection of Model B:
1. **Bug relocation** — the CIFS mount-readiness bug (`.NET statx(2)` / ENOENT) would move to the sandbox scheduling seam without being fixed at the root
2. **Threat model regression** — sandbox is the per-run untrusted isolation boundary; giving it workspace-creation authority broadens attack surface
3. **No isolation benefit** — sandbox mounts the same Azure Files RWX PVC already mounted by the API

**Phase 1 direction (adopted):** per-project RWO volumes + per-project worktree namespace; keep compute ephemeral; persistent compute as opt-in tier later. The current mount-readiness bug should not gate this direction.

Decision recorded in decisions.md: "Workspace-as-persistent-sandbox (Model B) evaluation — adopt per-project isolation half now, keep compute ephemeral" (2026-06-26T18-06-21).

- 2026-06-26T12:18:19-07:00: Workspace-as-persistent-sandbox design walkthrough (Model B) — split into Phase 1 (per-project storage, ship with 4Gi bump + worktree-recovery fix) and Phase 2 (agent-compute-in-sandbox, defer). Recommendation: memory bump 2Gi → 4Gi + worktree-recovery fix + redeploy now; full compute redesign deferred post-deploy stabilization.

---

## 2026-06-27T02-23-10 — Sandbox Architecture: Option A Pod-per-Run Agent Host

Investigated the current sandbox model and designed Option A (pod-per-run agent host). Found: today only `run_command` crosses into the sandbox; the agent loop, file tools, governance, and coordinator planning all run in the API process. Documented all gaps. Initially recommended Option B, then authored the full Option A design after Ahmed Sabbour's directive.

**Option A adopted.** The entire `CopilotAIAgent` host moves into a per-run pod. API becomes a thin control plane. New seams: `ISandboxAgentHost`, `AgentHostBroker`, `CapabilityTokenService`. Coordinator's planning turn runs in-pod; dispatch/DB ops brokered. `SandboxGovernance` stays in the API as out-of-band approver. Rollout behind `Sandbox:AgentExecutionMode` flag, phased (Phase 0: image; Phase 1: duplex+governance; Phase 2: full brokering; Phase 3: coordinator; Phase 4: scale).

Security verdict: 🟢 GREEN (Seraph, hardened). Key constraint: *relocate the reasoning, never the keys.*

---

## 2026-06-27T02:44:51Z — Fleet deep-dive documentation effort complete

Coordinated parallel 12-agent fleet (background mode) to author deep-dive documentation under docs/deep-dive/. Tank (tank-6), Seraph (seraph-1), Link (link-3), Morpheus (morpheus-3), Trinity (trinity-4) all contributed specialized deep-dive docs alongside 7 other agents. All 13 files verified complete; all 12 todos marked done; no source modifications. Cross-agent decision processing: 4 inbox entries merged (3 copilot directives + morpheus-option-a-plan, 25.3 KB). Scribe logs: 10 orchestration entries + 1 session log written. See .squad/log/2026-06-27T02-44-51Z-fleet-deep-dive-docs.md.

---

## 2026-06-27T03:05:00-07:00 — Spec 018 convergence

Spec 018 locked the distributed execution architecture: all agent execution turns run in sandbox pods via a thin MAF bridge; coordinator orchestration remains in API/worker tier; no bespoke capability-token broker; operational state moves to Azure PostgreSQL Flexible Server; web/worker split uses durable leasing with acceptable run/project affinity.

---

## 2026-06-27T03:15:00-07:00 — spec018 Q1/Q2/Q3 resolved

- Q1: Seraph resolved transport: HTTP/2+SSE (Option C) is acceptably safe and preferred over gRPC if mTLS/SPIFFE, scoped worker-only NetworkPolicy ingress, Last-Event-ID resume, bounded listener, and strict egress allowlist all hold. Kube-exec-stdio remains the minimalist fallback; gRPC is rejected.
- Q2: Tank resolved P1 may ship on SQLite with `replicas:1` without PostgreSQL if pods never touch DB directly, writes proxy through the single worker/API process, and no second replica is introduced.
- Q3: Morpheus resolved hybrid pod granularity: pod-per-run during active bursts; checkpoint-and-release on RequestPort/HITL or coordinator child-await suspension; re-claim and rehydrate on resume.

---

## 2026-06-27T03:35:00-07:00 — Spec 018 Q1 transport

A2A is now the live sanctioned worker -> agent-host transport upgrade path, superseding the earlier custom HTTP/2+SSE Option C decision. Remoting stays at the `AIAgent` leaf seam; MAF graph, WorkflowEvents, and RequestPort remain worker-local; durable resume stays in AgentWeaver DB checkpoints.

---

## 2026-06-27T10:38:23-07:00 — Q1 final override

Coordinator/user directive finalized A2A as the sole worker -> agent-host transport. Do not build kube-exec-stdio as default or fallback; rollback is `Sandbox:AgentExecutionMode=in-api`. Seraph H1-H7 still gate A2A, with H7 fallback changed to in-api rollback.

---

## 2026-06-27T15:40:00-07:00 — Gap #4 solved: shared RWX token store for agent-host pod

morpheus-5 implemented the production-grade solution for delivering GitHub tokens to sandbox pods: the agent-host mounts the shared Azure Files RWX PVC with the same HOME path the API uses, and reads the user's token file in-place — no secret injection, no token movement. New files: `SharedTokenStorePaths.cs`, `SharedHomeGitHubTokenStore.cs` (read-only, no-op Set/SignOut), `SharedUserScopeProvider.cs`. Program.cs wired via `AgentHost:UseSharedTokenStore=true`. Built + pushed `agentweaver-agent-host:be1e28fa`. A2A round-trip + AgentHost tests pass; 0 build errors. Changes committed by Coordinator as `37fc1cd`.

---

## 2026-06-28 — Sandbox docs regrounding + Preview/port-forward (docs reconciliation fleet)

Grounded sandbox docs (5 files) against real code. KEY CLARIFICATION: MXC (local-host runtime, Sabbour.Mxc.Sdk + wxc-exec) and agent-sandbox controller (upstream kubernetes-sigs/agent-sandbox, v0.4.6, in-cluster only) are TWO DISTINCT RUNTIMES. Documented both accurately. Documented Preview/port-forward feature: POST/GET/DELETE /api/runs/{runId}/sandbox/port-forward; caps 3 per-run, 20 global; in-memory no-TTL; preview_url web-only DTO field. Decision logged in decisions.md:2026-06-28T08-21-00.

📌 Team update (2026-06-28T05:10:00-07:00): Sandbox browser preview shipped to main (`373f544`) and deployed with `SANDBOX_PREVIEW_ENABLED=true`. B1 root cause was per-process `PodNameRegistry` at replicas:2; Tank fixed preview start with SandboxClaim cluster-state resolution. Live AKS has no Istio CRDs, so Telemetry was dropped and NetworkPolicy is same-namespace gateway podSelector-only.

---

## 2026-06-28: Autonomous preview demo paused on model credential

Sandbox/preview infrastructure is live, but the demo is paused because Agentweaver's custom GitHub App token lacks Copilot entitlement. Preview exposure and sandbox runtime should not be treated as the current blocker; the remaining issue is model credential provider selection, with Microsoft Foundry recommended.

---

## 2026-06-29T00:57:04-07:00 — Postgres 40001 race diagnosis

Root-caused the "no SandboxClaims" incident: a rolling deployment (`28d0cfb`) caused two API pods + one worker to simultaneously recover orphaned coordinator run `13f48ed2`. Both pods attempted to write `RunEvents` via `EfRunEventStream.WriteThroughAsync`, triggering Postgres 40001 serialization errors. The run failed at spec-draft phase (no checkpoint), so SandboxClaim creation was never reached. Warm pool was healthy throughout (3/3 ready, unbound). Finding passed to Tank for RC-1/RC-2 fix. Pre-existing Copilot model auth blocker (404 on token retrieval) noted as separate open risk.

---

## 2026-06-29T14:30–17:00Z — Feature 019 + Security fixes (Phase 1 delivery)

**Timeline:** Parallel to Feature 018 delivery

**Scope:** AIC capture via GitHub Copilot SDK, per-user token scoping, A2A per-run bearer token

**Deliverables:**

1. **AIC capture (Feature 019, Phase 1):** Detects `AssistantUsageEvent.RawRepresentation` in `StreamTurnOnceAsync` chunk loop; accumulates `TotalNanoAiu` (cast from double to long) per-turn. Emits `agent.turn.usage` event at `ExecuteStreamingLoopAsync` end. Per-turn boundary prevents double-counting on retry loops — critical for accurate billing.

2. **Per-user GitHub token scoping (Security fix #1):** OAuth callback now writes to `GitHubTokenScope.ForUser(login)` only; throws InvalidOperationException on missing login. `IGitHubTokenScopeProvider` config-driven (default: CallerTokenScopeProvider, opt-in: installation scope). Removes shared-directory user discovery; fallback logs warning, uses installation scope.

3. **A2A per-run bearer token (Security fix #2):** Per-run 256-bit random token protects AgentHost A2A turn submissions. Token generated per pod launch, injected via `AgentHost__TurnBearerToken`, registered by run ID, applied as default Authorization header in `RemoteAgentProxy`. Failure path clears token on pod cleanup.

**Key learnings:**
- `TotalNanoAiu` is a double in the actual SDK (not long per docs); explicit cast required.
- Per-turn accumulation (not per-chunk) is essential for usage-based billing accuracy.
- Per-user credential scoping must be enforced at OAuth callback time (not downstream lookup).
- A2A per-run bearer token provides runtime-generated per-run isolation; registry lifecycle must match pod cleanup.

**Testing & validation:**
- Feature 019 Phase 1: agent.turn.usage event emission verified, token accumulation tested.
- Per-user scoping: all builds pass, token-scoping tests green.
- A2A bearer token: Spec018P1Tests updated, lifecycle tests green.

**Build:** 0 errors, all targeted tests passing.
