# Tank — History (Summarized)

## 2026-06-07 through 2026-06-26 — ARCHIVED SUMMARY

**Phase 0-7 (June 7–26):** Built Scaffolder.Api infrastructure (5 endpoints, SqliteDb/EventStore/RunStore, streaming, git worktree management, security middleware). Fixed streaming security post-Morpheus (fail-closed ownership checks, atomic snapshots, 28–701 tests passing across phases). Implemented artifact browser backend (GET /artifacts, 6 security fixes). Delivered Feature 003 projects backend (SQLiteProjectStore, CRUD API). Implemented Feature 005 agent-team-casting backend (CastProposalStore, CastingService, 12 endpoints, TeamCommands CLI). Implemented Feature 008 Coordinator Agent plan revision (Round 2 rubber-duck approved), Phase 1-2 data foundation (Run domain, OutcomeSpec, WorkPlan, Subtask, SteeringDirective EF entities, 7 endpoints). Fixed board agent-rollup, MCP OAuth 2.1 backend (T1-T7, DCR, issuer/audience pinning, org handling). Fixed duplicate-default workflow card. Added GitHub accounts/repos API. Implemented org-auth rate-limit fix (authenticated public_members bucket + 429/403 discriminator). Seraph approved; RAI clean; deployed to AKS. Delivered PostgreSQL data-layer (EF stores, migrations, migrations discoverer, App:Role web/worker split, run leasing with claim/renew/release/fencing). Delivered Key Vault GitHub token-store (with Seraph/Link). Established replica-safe MCP OAuth broker pattern (MemoryDbContext over singleton state). Implemented agent-file generation (auto-gen tool-map + materialization flow). Web session exchange replica-fix (DB-backed storage, cross-replica single-use redemption).

**Key learnings:** Atomic CAS patterns for race-free state (UPDATE WHERE), compensation rollback (CreationScope), rate-limit bucket selection (auth/unauth), fail-closed gate preservation, SQLite database-is-locked incompatibility with Azure Files RWX (POSIX fcntl missing).

---

## 2026-06-29T07:22Z — Sandbox diagnosis: 40001 serialization race root cause + Postgres advisory lock fix

Diagnosed "no SandboxClaims" incident: two API pods + one worker simultaneously recovering orphaned run `13f48ed2`. Both attempted to write RunEvents → **Postgres 40001 serialization conflict**. Run was in `drafting-spec` (no checkpoint) → coordinator failed before SandboxClaim step. **RC-1/RC-2 fix deployed:** `StartupRecoveryLeader` using `pg_try_advisory_lock(0x4157524356525900)` ensures exactly one pod wins and runs `WorkflowRestartService`; non-leaders skip + log early-exit. SQLite path always acts as leader. Commit `7ccfd1a`, image `c082df5` deployed by Link; zero 40001 errors post-deploy. Tests: 39/39 green.

**Also this session:** Removed static MCP API key (branch `020-remove-static-mcp-key`, not yet deployed). Deleted `McpApiKeyRegistry`, removed path-1 static key → Auth:User from `McpBearerTokenMiddleware`. MCP now accepts OAuth paths only; internal `Auth__ApiKey` kept for loopback. Branch prep: 81 passed / 29 skipped / 0 failed.

---

## 2026-06-29T09:00Z — SQLite lock on coordinator-draft diagnosis + temp-dir fix

Diagnosed coordinator-draft SQLite lock: `CopilotCoordinatorSpecDrafter.DraftAsync` calls `SetupAsync(workingDirectory: input.RepositoryPath)` — two API pods, shared `/workspace/{projectId}` on RWX Azure Files PVC. **Azure Files does not implement POSIX fcntl locks** → SQLite database-is-locked. WAL/busy_timeout cannot work at OS level; it's filesystem incompatibility.

**Fix (Option B):** Change `CopilotCoordinatorSpecDrafter` to use per-run temp directory:
```csharp
var draftDir = Path.Combine(Path.GetTempPath(), "coordinator-draft", input.RunId);
Directory.CreateDirectory(draftDir);
await agent.SetupAsync(
    workingDirectory: draftDir,           // ← emptyDir per-pod, no sharing
    repositoryPath: input.RepositoryPath, // ← policy eval still uses real path
    userId: input.SubmittingUser);        // ← fix per-user scoping too
// cleanup draftDir in finally
```

**Not the cause:** MCP key removal (branch not deployed), 401 auth (Copilot client OK), kata capacity (healthy). **Missing userId in draft:** Expected; installation token fallback works. Should fix for per-user Copilot scoping.

---

## 2026-06-29T14:30–17:00Z — Feature 019 backend + Security fixes (Phase 2-3 delivery)

**Timeline:** Parallel to Morpheus/security work

**Scope:** Token usage backend (Feature 019 Phase 2-3), MCP route escaping security fix

**Deliverables:**

1. **Token usage backend stack (Feature 019, Phase 2-3):** Complete backend implementation of AIC and token monitoring.
   - **Table:** `token_usage_records` with org/project/run/turn hierarchy
   - **Dual-backend store:** SQLite (dev), EF (prod)
   - **Projection:** Background service consuming `agent.turn.usage` events from event stream
   - **API endpoints:** Four-level hierarchy (org/project/run/turn) with time-range aggregation
   - **Metrics extension:** Registered into MetricsService
   - **MCP tools:** Token usage tools wired into MCP
   
   All data served from persistent store; no client-side aggregation.

2. **MCP route parameter escaping (Security fix #3):** URI-escaped 86 MCP tool API paths.
   - **Routes escaped:** project_id, task_id, run_id, entry_id, decision_id, agent_name, memory_id
   - **Tools affected:** Backlog, Coordinator, Memory, Project, Run, Team, Workflow, Workspace
   - **Admin bypass removal:** Hardcoded `string.Equals(caller.User, "admin", ...)` removed from ProjectEndpoints, TeamEndpoints, RunEndpoints, BacklogEndpoints
   - **Validation:** Grep confirmed no remaining hardcoded admin comparisons; all builds pass

**Key learnings:**
- Token data must be persisted in a durable store for multi-replica deployment (no in-process aggregation).
- Four-level hierarchy (org/project/run/turn) matches operator mental model for cost allocation and usage visibility.
- MCP path traversal vulnerability closed by consistent URI-escaping on all route parameters.
- Admin bypass removal requires endpoint-by-endpoint audit (grep-validated).

**Testing & validation:**
- Build: 0 errors, 0 warnings
- Feature 019 backend tests: all passing
- MCP escaping: path-traversal test coverage added, all tests green
- Security audit: hardcoded admin removal validated

**Build:** 0 errors, 0 warnings.

## 2026-07-05T13:17:12-07:00 — Three-issue parallel fixes

Tank completed backend fixes for #175 and #174. #175 adds newly saved workflow ids to `AllowedWorkflowIds` before registry sync and improves reload diagnostics; PR #177 approved by Smith. #174 emits approval-resolved SSE events on all resolution paths and improves request resolution diagnostics; PR #182 approved by Smith and Seraph. No PRs merged; coordinator validates on staging first.

## 2026-07-05T14:16:02-07:00 — Issue #183 lockout revision owner
Tank owned the #183 revision after Morpheus was locked out by Smith's rejection. Tank added dual-path workflow-selection response capture, final-message-only regression tests, `InternalsVisibleTo`, and Smith's stripped-text last-resort parse suggestion; build was clean and WorkflowSelect passed 41/41.


## 2026-07-05T20:40:00-07:00 — v0.7.11 release batch
Redesigned Create blank/Create from GitHub dialogs and added `POST /api/blueprints/suggest` with GitHub repo analysis and graceful Templates fallback. Feature is merged into `release/v0.7.0` and deployed to staging as `v0.7.11`.


## 2026-07-06T22:05:00Z — v0.7.12 new-project dialogs v2

Delivered shared-base refactor for Blank and From-GitHub creation dialogs: one shell, shared Blueprint panel/tabs, Templates parity, fixed blank right-column scrolling/clipping, wired View all templates, single footer No-blueprint affordance, personal repos via user-first GitHub accounts/repos, and Suggested-only recommendation view. Commits `112addc`, `b066eed`, `0e7d92f`; merged to `release/v0.7.0` and deployed to staging.

## 2026-07-11T00:00:00Z — Dependency-base propagation fix shipped to staging

Implemented the backend dependency-base propagation fix after rubber-duck GO-WITH-CHANGES and code-review CLEAN: inclusion now trusts committed branch/tree validity instead of run.Diff, integration branches must contain satisfied dependency heads before dependent dispatch, and final assembly uses the same inclusion authority. Link included the change in local-only staging release v0.9.19-rc1 for Ahmed validation.

📌 Team update (2026-07-10T05:55:00-07:00): #207 roots in 28 unbounded non-idempotent final Scribes executing in the API. The first design was rejected; Tank is locked out from revision and Morpheus owns the independent redesign. — decided by Rubber-duck and Seraph


## 2026-07-12T06:33:29-07:00 — #187 Build & Test gate design

Delivered `files/design-187.md`, proposing a shared gate runner for consistent Build & Test gate execution and policy handling. Preserved an unresolved design conflict for rubber-duck: approved-only preview activation contradicts the North Star requirement that preview be available at `awaiting_review`. Preview timing is not final until that criterion is resolved.


## 2026-07-13T23:59:00-07:00 — E2E validation
#213 was confirmed stale and closed. Run `18cdc7ce-6649-4b60-b001-17c317bcd281` confirmed parallelism works; stale outcome-plan UI behavior maps to #290. Filed #305 for revision children inheriting a prior sibling worktree branch instead of their own authoritative branch.

## 2026-07-14T02:35:00-07:00 — Batch merge: #270 revalidation, #175 workflow save, live-run diagnoses
Scribe merged inbox notes: #270 revalidation confirms Kata bwrap root-cause fix holds; #175 workflow editor save-path fix; Hank/Skyler run-order and arrow-occlusion UI findings closed as non-bugs; #211 AgentHost sandbox fix live-validated; #226 human-steer redirect drop at assembly review fixed.

## 2026-07-14T03:05:00-07:00 — #1 API-driven persona harness pivot, Priya + Jordan playbooks
Prioritized an API-driven persona harness as the primary E2E track for #1; Playwright is secondary. Built scripts/persona-harness/ and two scenario playbooks (Priya ticket-triage, Jordan blank-to-plan) — both PASS against staging, proving the drive+judge engine generalizes across personas via data alone.

## 2026-07-14T10:15:00-07:00
Batch: landed #208 cancellation-telemetry (needs peer review), #309 steer-redirect validation (ready for review), continuing #242 investigation. Persona-harness (#1) API-driven redesign now at increments 1-3 of 5.


## 2026-07-14T10:15:00-07:00 (late arrival, tank-2 instance)
#267 A2A regression investigation: root cause not pinpointed, diagnostic instrumentation added only (no masking). Staging repro still needed. Needs peer review.


## 2026-07-14T11:05:00-07:00
Process note: #311, #208, #310 (among Tank's recent items) reopened by coordinator pending live v0.9.50-rc1 deploy validation, per Seraph's pass-3 closure-discipline finding. No new Tank work landed this pass.

## 2026-07-14T15:15:00Z — Persona harness pivot + bounded reliability follow-ups
Tank's brief-driven persona harness pivot was validated across Priya/Jordan/Maya, with #315 filed for revision regressions and a WIP safety branch created for the harness directory. The same batch also recorded the bounded #242 fix and the final #267 escalation to packet-capture follow-up.
