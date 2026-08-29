# Tank — History (Summarized)

## 2026-07-16 (later same day) — P0 regression from v0.9.68 session-store flip → emergency revert (v0.9.69)

**What happened:** The `EnableSessionStore=true`/`InfiniteSessions=true` flip below (v0.9.68) caused
a live P0 within minutes of deploy: every new operator-assistant run failed with
`System.InvalidOperationException: Session error: Execution failed: Error: database is locked`.

**Root cause:** `RunTurnAsync` creates a brand-new Copilot SDK session on **every turn** (calls
`agent.CreateSessionAsync()`, never resumes). With the session store on, every turn of every
concurrent conversation in a pod hammered the same pod-local SQLite session-store file —
concurrent-write contention. This means the "one-shot ephemeral containers only" framing from
github/copilot-sdk#1814 was incomplete: any workload creating many concurrent fresh sessions
against one local SQLite file hits this, not just one-shot containers. My original architectural
note (below) was wrong for this agent specifically.

**Fix:** Reverted `EnableSessionStore`/`InfiniteSessions` to `false`/disabled in
`OperatorAssistantAgent.BuildSessionConfig`, with an updated code comment recording the real
mechanism. Committed directly to main (`ee1c8044`, no worktree — P0), shipped as v0.9.69.

**Next queued (not started by me — do not pick up without checking decisions.md first):**
Implement real SDK session resumption in `RunTurnAsync` (resume the deterministic
`SessionId=agentweaver-operator-{conversationId}` on turn 2+ instead of always calling
`CreateSessionAsync()`, mirroring `CopilotAIAgent.ResumeSessionAsync`). Only after that lands and
is tested should the session store be re-enabled for this agent.

---

## 2026-07-16 — Assistant session recall and backend endpoint

**Task:** Implement durable rehydration for operator-assistant conversations.

**Work:** Added `IRunEventStream.GetPersistedEventsAsync(runId, fromSequence)` for point-in-time history replay (distinct from live-tail `SubscribeAsync`). On cache-miss in `RunTurnAsync`, rehydrate `OperatorRunState.History` from persisted `agent.message` events, rebuild context from `Run` record, and transition status from `Completed` back to `InProgress` if new messages arrive. Rehydration does NOT count against `MaxConcurrentRunsPerUser` quota. Flipped `OperatorAssistantAgent`'s `EnableSessionStore` and `InfiniteSessions` flags to `true` (safe for long-lived in-process agents; kept `false` for one-shot/ephemeral agents per SDK issue context). Added backend `GET /api/assistant/runs` endpoint for Trinity's Sessions page.

**Outcome:** 24/24 targeted tests passed (3 new rehydration tests); merged to main at commit `79f0d393` as part of v0.9.68; deployed and verified live on staging.

**Architectural note:** Copilot SDK's session-store disable (from github/copilot-sdk#1814) was never a general best-practice — only specific to one-shot pod churn. The risk/benefit calculation differs per agent type: re-enable for long-lived in-process, keep disabled for one-shot/ephemeral.

---

## 2026-06-07 through 2026-06-26 — ARCHIVED SUMMARY

**Phase 0-7 (June 7–26):** Built Scaffolder.Api infrastructure (5 endpoints, SqliteDb/EventStore/RunStore, streaming, git worktree management, security middleware). Fixed streaming security post-Morpheus (fail-closed ownership checks, atomic snapshots, 28–701 tests passing across phases). Implemented artifact browser backend (GET /artifacts, 6 security fixes). Delivered Feature 003 projects backend (SQLiteProjectStore, CRUD API). Implemented Feature 005 agent-team-casting backend (CastProposalStore, CastingService, 12 endpoints, TeamCommands CLI). Implemented Feature 008 Coordinator Agent plan revision (Round 2 rubber-duck approved), Phase 1-2 data foundation (Run domain, OutcomeSpec, WorkPlan, Subtask, SteeringDirective EF entities, 7 endpoints). Fixed board agent-rollup, MCP OAuth 2.1 backend (T1-T7, DCR, issuer/audience pinning, org handling). Fixed duplicate-default workflow card. Added GitHub accounts/repos API. Implemented org-auth rate-limit fix (authenticated public_members bucket + 429/403 discriminator). Seraph approved; RAI clean; deployed to AKS. Delivered PostgreSQL data-layer (EF stores, migrations, migrations discoverer, App:Role web/worker split, run leasing with claim/renew/release/fencing). Delivered Key Vault GitHub token-store (with Seraph/Link). Established replica-safe MCP OAuth broker pattern (MemoryDbContext over singleton state). Implemented agent-file generation (auto-gen tool-map + materialization flow). Web session exchange replica-fix (DB-backed storage, cross-replica single-use redemption).

**Key learnings:** Atomic CAS patterns for race-free state (UPDATE WHERE), compensation rollback (CreationScope), rate-limit bucket selection (auth/unauth), fail-closed gate preservation, SQLite database-is-locked incompatibility with Azure Files RWX (POSIX fcntl missing).

---

## 2026-06-29 through 2026-07-13 — ARCHIVED SUMMARY

Diagnosed and fixed two Postgres/SQLite concurrency issues: (1) startup-recovery race on orphaned runs — Postgres advisory-lock leader election (`StartupRecoveryLeader`, `pg_try_advisory_lock`) ensures exactly one pod recovers, zero 40001 errors post-deploy (commit `7ccfd1a`); (2) coordinator-draft SQLite lock on shared RWX Azure Files — root cause is Azure Files' missing POSIX fcntl support (not a WAL/busy_timeout issue), fixed via per-run temp directory instead of shared workspace path. Removed static MCP API key (OAuth-only paths). Delivered Feature 019 token-usage backend (org/project/run/turn hierarchy, dual-backend store, MCP tools) and an MCP route-parameter escaping security fix (86 paths, admin-bypass removal across 4 endpoint files). Delivered three-issue parallel fixes (#175, #174), owned #183 lockout revision after Morpheus was rejected (41/41 tests), shipped v0.7.11/v0.7.12 new-project dialog releases, delivered the dependency-base propagation fix (rubber-duck GO-WITH-CHANGES + clean code review, shipped in v0.9.19-rc1), delivered #187 Build & Test gate design (preview-timing conflict left open for rubber-duck), and closed out #213 as stale while filing #305 for revision-child branch inheritance.

**Key learnings:** Postgres advisory locks (`pg_try_advisory_lock`) are the reliable pattern for single-leader election across replicas; SQLite + Azure Files RWX PVC is fundamentally incompatible (no POSIX fcntl) — always use per-pod temp/emptyDir for SQLite-backed work; durable persisted stores (not in-process aggregation) are required for any multi-replica usage/metrics tracking; MCP path traversal must be closed via consistent URI-escaping at the route-parameter level.

---

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

## 2026-07-14T10:37:40-07:00 — Persona harness: judge-gated API approval driving (#1)
Closed the harness gap where runs stalled waiting on approvals: added a DETECT->JUDGE->EXECUTE loop that drives tool/shell/coordinator-child approval gates via the real API only after a judge decides. New lib/approvals.mjs (deterministic gate detection off the run events feed) + lib/approval-judge.mjs (narrow in-the-loop approve/deny/defer judge contract, pluggable judge, default DEFER), new check-approvals/resolve-approval driver commands, optional driveApprovals runner hook (OFF by default), and full audit trail (turn.approval + evidence.approvalDecisions). Driver-only boundary preserved: zero heuristic judgment in the driver. 62/62 tests pass (22 new). Backend gap: /api/notifications tool_approval type still reserved/unemitted (#247 fast-follow) — used the events feed instead; no backend change needed. Committed to main b4ac1104; decision recorded.


## 2026-07-14 Session: 3-Harness Design Spec & Implementation Kickoff

**Major Deliverable:** docs/api-test-harness-plan.md (full design spec, all 9 sections); reconciled 5 shared-layer naming conflicts with Trinity/Morpheus; authored GitHub Copilot CLI Skill two-file design; implemented approval-driving for API harness (b4ac1104, 62/62 tests). Spec-only; no refactoring yet. Phase 2 extraction coordinated at safe checkpoint.

**Blocking security findings (Seraph):** Target-host allowlist hardening + prompt-injection threat model — design-level fixes pre-req for implementation start.

**Next:** Address Seraph findings in specs; Phase 1 parallel scaffolding (Trinity UI auth, Morpheus MCP client, Smith persona generation); Phase 2 single coordinated extraction by Tank.


---

## 2026-07-14: Fleet-Mode Harness Build Wave — API Harness & Security Integration

**Wave:** Full fleet-mode harness infrastructure implementation (API/UI/MCP + shared + security review)

**Contribution:** Led API harness track: migrated scripts/persona-harness to scripts/api-harness, wired shared persona-briefs + harness-judge, added request-changes approval decision semantics, fixed #318 fixture drift. Folded Seraph's pre-implementation security findings (1-5) into API harness spec: target-host allowlist (unconditional at AgentweaverClient construction), prompt-injection threat model (untrusted delimiters + judge-not-sole-authority validation), credential isolation advisory, Squad trust boundary (verdict schema versioning), and governance guardrails (zero GitHub tools/credentials in Harness agent scope).

**Outcome:** API harness driver + security guardrails complete; npm tests 46/46 passing (including request-changes gate flow). #318 migrator fixture drift fixed (2/2 tests passing). Ready for staging E2E on approval-gate flows + request-changes feedback loops.

**Coordination:** Lockstep with Trinity (UI) and Morpheus (MCP) on two-file skill structure, target-guard.mjs shared implementation, untrusted-delimiter contract, and verdict schema versioning.

**Follow-ups:** Live-staging E2E on actual Agentweaver API gateway + approval decision flows. Hostile-content self-test (injected approve in API event body; verify driver/judge don't follow it). Credential isolation architectural review before deep runs.

- 2026-07-29: Verified `tank-9` via combined full build + batch validation (solution build 0 errors; backend tests 3082 passed / 108 skipped / 7 pre-existing Docker-env failures; frontend lint clean; frontend tests 929/930 passed with 1 pre-existing unrelated `SkillsPage.test.tsx` failure). Work remains pending final security review before commit/PR.
- 2026-07-29: Verified `tank-10` via combined full build + batch validation (solution build 0 errors; backend tests 3082 passed / 108 skipped / 7 pre-existing Docker-env failures; frontend lint clean; frontend tests 929/930 passed with 1 pre-existing unrelated `SkillsPage.test.tsx` failure). Work remains pending final security review before commit/PR.
- 2026-07-29: Verified `tank-11` via combined full build + batch validation (solution build 0 errors; backend tests 3082 passed / 108 skipped / 7 pre-existing Docker-env failures; frontend lint clean; frontend tests 929/930 passed with 1 pre-existing unrelated `SkillsPage.test.tsx` failure). Work remains pending final security review before commit/PR.
