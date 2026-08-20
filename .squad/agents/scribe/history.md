# Project Context

- **Project:** scaffolders
- **Created:** 2026-06-07

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-06-07

**2026-06-11:** Recorded FR-005 refinement (Seraph): unified GitHub sign-in via OAuth device flow grants both repo access and Copilot authorization (replaces separate key entry). Global/installation-wide credential storage, never in project records. Planning notes: OAuth scope minimization, secure token storage, token refresh/revocation.

## Learnings

Initial setup complete.


**2026-06-27:** Deep-Dive Documentation Initiative & Deployment Recovery

Coordinated fleet of 7 background agents (niobe, ghost, sparks, lock, roland, zee, soren) to author comprehensive deep-dive documentation (19 total pages: 12 rewritten + 7 new). 

Key outcomes:
1. **OAuth Auth Break Root-Caused & Fixed:** RFC 8252 loopback redirect_uri fix committed (6583370a) but never deployed to production. Live pods ran pre-fix version (6d4d7c20). Diagnosed OAuth session ownership, verified no OAuth code modifications needed, deployed fix.
2. **Two Production Releases (AKS Staging):**
   - e22acbd: OAuth fix + pod-name UI + 12 doc rewrites (concept-first, 0 code-line refs)
   - 921fedc: 7 new deep-dives + VitePress reorganization into 4 themed groups (Foundations, Orchestration & Agents, Execution & Integration, Data & Platform)
3. **Documentation Standards:** All 19 pages follow concept-first approach, user-level language, no AI filler, no references to removed/planned-but-not-done behavior
4. **Branch:** sabbour/mcp-oauth; commits e22acbd5, 921fedca; no remote configured

Documentation Quality Checkpoint:
- Deep-dive pages: review-merge, workflow-engine, git-integration (push/PR Unverified), events-observability, memory-decisions, coordinator-internals, testing-strategy
- All indexed in README and sidebar
- Integration with existing deployment pipeline verified


**2026-06-27:** Recorded the MAF/MXC/Feature 017 preview documentation pass: Tank authored two Microsoft Agent Framework docs, Coordinator wired navigation and removed competitive-landscape links, and commit e851bd4 passed docs validation.


## 2026-06-28 — Session logging + decision merging (docs reconciliation fleet, Scribe role)

Merged 8 inbox decisions into decisions.md (Trinity IA restructure, Cypher A2A regrounding, Morpheus sandbox/preview, Dozer workflow selection, Link install-oneliners, Tank reference refresh, Mouse screenshots, Cypher re-grounded duplicate); deleted processed inbox entries. Wrote 7 per-agent orchestration logs + 1 coordinator summary (Task 2). Wrote session log (Task 3). Appended brief updates to agent history files (Task 4). All using squad_state tools (no git notes, no hand-edits). Build verified green (29.6s). Decision merged without timestamp (coordinator final pass, existing Coordinator-history relation).


## 2026-06-28 — Copilot auth blocker Scribe pass

Processed Coordinator + Link (`link-deploy-smoke`) batch. Health check returned FSStorageProvider; archive/history gates did not require summarization. Merged decisions inbox into consolidated decisions covering agent preview, A2A readiness, Postgres checkpoints, AgentHost user scoping, Kata/NAP, and the Copilot-auth blocker. Wrote orchestration/session logs and updated affected agent histories. Demo paused because the custom Agentweaver GitHub App cannot mint Copilot-entitled tokens; Microsoft Foundry recommended.


---

## 2026-07-16T19-15-00Z — Assistant session persistence & UI (v0.9.68 release)

**Health check:** Pre-flight state tools confirmed ready.

**Scope:** Merged two companion features for operator-assistant conversation lifecycle (durable persistence + delete/list UI), released as v0.9.68, deployed and verified live on staging.

**Work completed:**

1. **Decision Inbox Merge:** 3 inbox entries merged into decisions.md:
   - Tank: Durable session rehydration + `GET /api/assistant/runs` backend endpoint
   - Trinity: Sessions page + delete action on each conversation
   - Architectural note: Copilot SDK session-store flags differ by agent type (re-enable for long-lived, disable for one-shot)

2. **Orchestration Logs:** Created 2 files (Tank, Trinity) per spawn manifest, including branch/commit/validation/merge status for each.

3. **Session Log:** Wrote summary of v0.9.68 release: two features shipped, deployed, 4/4 images verified via provenance check.

4. **Agent History Updates:**
   - **Tank:** Recorded durable rehydration work, SDK session-store architectural decision, new `GetPersistedEventsAsync` method, backend endpoint addition. 24/24 tests passed.
   - **Trinity:** Recorded Sessions page + delete UI, LeftNav integration, removed Operator-dock nav entry. 5/5 SessionsPage tests + 81 full suite passing; followed up on console-panel cleanup decision for Coordinator.

**Size gates:** decisions.md = 43,919 bytes (below 51,200, no archival needed); history files within bounds.

**Merge & Deploy:**
- Coordinator merged both branches into main at `79f0d393` (disjoint files, no conflicts).
- VERSION 0.9.67 → 0.9.68, tagged, GitHub release created.
- All 4 images built, pushed to ACR, deployed to staging.
- Provenance verification: `25-verify-image-provenance.sh` returned 4 passed, 0 failed.
- **Status: LIVE** ✓

**Inbox entries processed and deleted:** 3 files
- tank-assistant-recall.md
- trinity-assistant-ui-bugs-346.md
- trinity-assistant-ui-second-pass-346.md

## 2026-06-28T16:05:00-07-00 — Web session exchange deployment Scribe pass

Processed Tank + Link batch: health check confirmed FSStorageProvider, Tank inbox entry merged, orchestration/session logs written, Tank/Link/Scribe histories updated, and summarization gates checked. No summarization required; remaining note is user re-auth with `copilot` scope or Foundry for model credentials.

---

## 2026-06-29T18:15:00-07:00 — Security audit completion + Feature 019 deployment Scribe pass

**Health check:** FSStorageProvider confirmed.

**Scope:** Comprehensive security hardening session (5 critical findings fixed + 1 post-deployment finding + MCP assessment findings).

**Work completed:**

1. **Decision Inbox Merge:** 7 pending inbox entries merged into decisions.md:
   - Link: Per-pod CSI SPC for AgentHost token isolation
   - Morpheus: Per-user GitHub token scoping + diskMirror disable
   - Morpheus: Per-run bearer token on A2A turn endpoint
   - Morpheus: AIC capture via AssistantUsageEvent (Feature 019)
   - Tank: Token usage backend stack (Feature 019)
   - Tank: MCP route parameter escaping + admin bypass removal
   - Trinity: Token usage frontend (Feature 019)

2. **Session Log:** Wrote comprehensive summary (2026-06-29T18-15-00Z-security-session-complete.md)
   - Timeline: 5 critical audit findings → fixes → deployment → post-deployment assessment → MCP assessment
   - 8 fixes deployed (5 initial + A2A bearer + MCP path escaping + admin bypass)
   - Feature 019 (AIC monitoring) fully deployed
   - All tests passing; 0 security regressions

3. **Orchestration Logs:** Created 2 background task orchestration entries
   - trinity-docs (GPT-5.5, background): Full docs pass for security fixes
   - tank-docs (GPT-5.5, background): Full docs pass for MCP hardening + Feature 019

4. **Agent History Updates:**
   - **Morpheus:** Per-user token scoping, A2A bearer token, Feature 019 AIC integration, key learnings on per-turn accumulation
   - **Tank:** MCP path escaping (86 fixes), admin bypass removal (4 files), Feature 019 backend stack, removed static MCP key, key learnings on authorization patterns
   - **Link:** Per-pod CSI SPC lifecycle, dev secrets documentation, A2A bearer token integration, three-pool AKS layout
   - **Trinity:** Token usage frontend (Feature 019), documentation pass in progress

**Key learnings recorded in agent histories:**
- Per-user credential scoping requires explicit enforcement at OAuth callback time
- MCP path traversal must be URI-escaped at tool level
- Run-scoped resource isolation (SPC/template/pool) must have explicit lifecycle management
- Per-turn AIC accumulation avoids double-counting on retry loops
- Authorization must be derived from service layer on every code path

**Documentation standards enforced:**
- Describe what users can do right now with shipped code
- No legacy details or planned-but-not-done references
- No AI marketing terms; clear technical description
- Written at reader level (not developer level)

**Final state:**
- Deployment commit: 5373893
- All 8 security fixes deployed and verified
- Feature 019 deployed and functional
- Post-deployment assessment: PASSED
- Background docs tasks: IN_PROGRESS (trinity-docs, tank-docs)

## 2026-07-14T15:15:00Z — v0.9.50-rc1 release documentation pass
Merged a 57-file decisions inbox, wrote the release/session/orchestration logs, and updated cross-agent histories for the v0.9.50-rc1 staging ship. Archive and history summarization gates were checked; no additional summarization was needed this pass.

## 2026-07-16T17-19-26-07-00 — v0.9.68 P0 regression → v0.9.69 hotfix → v0.9.70 stale-image fix Scribe pass

**Health check:** Reviewed git log/VERSION/live code against the incident summary provided —
confirmed `OperatorAssistantAgent.cs` code state, commit hashes (`ee1c8044`, `4c276761`,
`59a90c14`), and VERSION=0.9.70 all match the reported narrative exactly. No discrepancies or
regressions found in the verification pass.

**Scope:** Logged the full incident/resolution arc: v0.9.68's session-store re-enable caused a
live P0 (`database is locked`, every new assistant run failing) within minutes of deploy; root
cause was `RunTurnAsync` creating a fresh SDK session every turn against one pod-local SQLite
session-store file; emergency-reverted in v0.9.69 (committed direct to main, no worktree, P0);
two false rollout-failure alarms from `30-deploy.ps1` (transient node-scheduling latency, not code
regressions); a user-requested docs-landing merge caused a stale-image false-negative catch
(#251 failure mode) fixed via v0.9.70 selective frontend rebuild; final 4/4 provenance-verified
and live E2E smoke test confirmed both the regression is resolved and the original v0.9.68
durable-rehydration feature works correctly in production.

**Work completed:**
1. **Decision entry:** Added one comprehensive entry to `decisions.md` covering root cause, fix,
   both operational false-alarm patterns, and the queued follow-up (real SDK session resumption
   for `OperatorAssistantAgent`, owner: Tank, not started).
2. **History summarization gate:** Tank's `history.md` was at 16,084 bytes (over the 15,360-byte
   hard gate) — condensed the 2026-06-29→2026-07-13 entries into one archived-summary block
   (preserving key learnings) before appending the new incident entry. Result: 12,008 bytes.
3. **Agent history updates:**
   - **Tank:** Recorded the regression, root cause, revert, and the queued session-resumption
     follow-up (explicitly flagged as not-yet-started, do-not-pick-up-without-checking-decisions).
   - **Link:** Recorded both deploy false-alarms and the stale-image/provenance fix as reusable
     operational patterns.
4. **No regressions found during review:** cross-checked the narrative against
   `OperatorAssistantAgent.cs` (comment + flag state matches exactly), git log (`ee1c8044`,
   `4c276761`, `59a90c14` all present in the expected order), and `VERSION` (0.9.70, matches).
   One unrelated, pre-existing local change was noted but not touched: `.learnings/LEARNINGS.md`
   has an uncommitted local addition (`LRN-20260716-001`, about deployment-source attribution) from
   a separate session — out of scope for this log pass, left as-is.

**Size gates:** `decisions.md` grew to include the new entry; still within the 30-day/20KB
archival threshold reset on 2026-07-14 (nothing in the active file is older than 30 days, so no
archival was triggered this pass).

📌 Team update (2026-08-14T01:32:00+03:00): merged 6 inbox decisions, wrote Trinity/link-harness-auth/harness orchestration logs, and cleared the inbox — decided by Scribe.

- 2026-08-14: PR #766, Edge Default + CDP staging auth, and the API harness seam PASS on v0.18.1 were captured; hygiene pass completed and temp-squad-noise stash was left untouched.
