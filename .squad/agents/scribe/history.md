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


## 2026-06-28T16:05:00-07:00 — Web session exchange deployment Scribe pass

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
