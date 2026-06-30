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
