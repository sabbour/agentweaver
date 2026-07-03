# Squad Simulation — Bug-First Backlog Run
_Baseline (no skill loaded) · 2026-07-01_

---

## 1. Backlog Scan

Fetched open issues from `sabbour/agentweaver`. Applying **bug-first** filter:

| # | Priority | Title | Labels |
|---|----------|-------|--------|
| 97 | **p1** | `bug(orchestration)`: assembly failure shows opaque error with no retry | `squad:morpheus`, `squad:smith`, `go:needs-research` |
| 98 | p2 | `bug(run-page)`: preview sandbox not available on orchestration run page | `squad:tank`, `squad:trinity`, `go:yes` |
| 99 | p2 | `bug(run-page)`: preview sandbox button hidden for completed runs | `squad:trinity`, `go:yes` |
| 95 | p2 | `bug(run-page)`: confirm button does not disable immediately after click | `squad:trinity`, `go:yes` |

Non-bug issues (#100, #101, #59, #56, #53, etc.) are **deferred** — bugs take priority.

---

## 2. Triage & Routing

### Issue #97 — p1 · `assembly_blocked` opaque error + no retry
- **RCA first (Smith):** Trace where `assembly_blocked: ineligible_subtasks` is set; determine if failure is transient or permanent.
- **Fix (Morpheus):** Add auto-retry logic (capped) in the orchestration run-state machine; bubble up `ineligible_subtasks` list to the UI error surface.
- **Note:** `go:needs-research` — Smith must complete RCA before Morpheus codes.

### Issue #98 — p2 · No Preview button on CoordinatorRunPage
- **RCA (Smith):** Confirm `POST /runs/{runId}/sandbox/port-forward` accepts orchestration runIds identically to workflow runIds.
- **Implement (Trinity):** Port the Preview button + port-forward dialog from `WorkflowRunPage.tsx` (lines 836–845) into `CoordinatorRunPage.tsx`.
- **Review (Tank):** Confirm backend port-forward endpoint is run-type agnostic.
- Smith RCA can run **in parallel** with Trinity's implementation (implementation is low-risk port).

### Issue #99 — p2 · Preview button hidden after run completes
- **Fix (Trinity):** In `WorkflowRunPage.tsx` line 836, change gate from `isKubernetesSandbox && (runActive || !!previewSession)` → `isKubernetesSandbox` (Option A), or add disabled state with tooltip (Option B).
- Straightforward single-file change. Can start immediately.

### Issue #95 — p2 · Confirm button allows double-submit
- **Fix (Trinity):** Add optimistic disabled state on the Confirm button — set `isConfirming = true` immediately on click, before the API response returns.
- Note: branch `squad/issue-82-confirm-ui-freeze` already exists from a prior session — **check for relevant work there before starting fresh**.

---

## 3. Parallel Execution Plan

```
Wave 1 — Launch in parallel (all independent start points):

  [Smith]    issue-97-rca    →  Trace assembly_blocked in codebase
                                 Answer: what makes a subtask ineligible?
                                 Output: RCA findings, then unblock Morpheus

  [Trinity]  issue-98-impl   →  Port Preview button to CoordinatorRunPage.tsx
                                 Copy from WorkflowRunPage.tsx lines 836–845
                                 Wire startPortForward already in api/client.ts

  [Trinity]  issue-99-fix    →  Fix gate condition WorkflowRunPage.tsx:836
                                 (Same file as #98 — sequence after #98 or branch from same base)

  [Trinity]  issue-95-fix    →  Disable Confirm button optimistically on click
                                 Check squad/issue-82-confirm-ui-freeze for prior work

Wave 2 — Unblocked after Wave 1:

  [Morpheus] issue-97-fix    →  Implement retry logic + better error surfacing
                                 Requires Smith's RCA output

  [Tank]     issue-98-review →  Review Tank's backend endpoint for run-type agnosticism
                                 Requires Trinity's branch to review

  [Seraph]   pre-merge-rai   →  RAI scan all branches before merge
```

---

## 4. Branch / Worktree Strategy

> **Simulation only — no branches created**

```
# One worktree per issue (branches off main):
git worktree add .worktrees/issue-97  -b squad/issue-97-assembly-retry
git worktree add .worktrees/issue-98  -b squad/issue-98-coordinator-preview
git worktree add .worktrees/issue-99  -b squad/issue-99-preview-completed
git worktree add .worktrees/issue-95  -b squad/issue-95-confirm-disable

# For issue-95: check if squad/issue-82-confirm-ui-freeze has reusable commits:
git --no-pager log --oneline squad/issue-82-confirm-ui-freeze
# Cherry-pick or branch from it if relevant work already exists.
```

Each agent gets its own worktree → zero merge conflicts during parallel edits.

---

## 5. Key Files Involved

| Issue | File(s) |
|-------|---------|
| #97 | Runtime orchestration source (needs Smith RCA to pinpoint); likely under `apps/api/` |
| #98 | `apps/web/src/pages/CoordinatorRunPage.tsx` |
| #99 | `apps/web/src/pages/WorkflowRunPage.tsx` line 836 |
| #95 | `apps/web/src/pages/WorkflowRunPage.tsx` (Confirm button handler) |

---

## 6. Status Summary

| Issue | Assigned | Status | Blocker |
|-------|----------|--------|---------|
| #97 | Smith → Morpheus | 🔴 Needs RCA before fix | `go:needs-research` |
| #98 | Trinity + Tank | 🟡 Ready (Trinity starts, Tank reviews after) | None |
| #99 | Trinity | 🟢 Ready | None |
| #95 | Trinity | 🟢 Ready (check issue-82 branch first) | None |

**Recommended first wave:** Start #99 and #95 immediately (single-file, clear fixes). Run #98 Trinity impl in parallel. Dispatch Smith on #97 RCA concurrently — Morpheus queued behind it.

---

## 7. What's Missing Without a Skill

Without the Squad skill loaded, this simulation required:
- **Manual issue triage** — no automatic routing engine; had to read labels/dispatch fields by hand
- **No squad_state tools** — cannot read existing session state, memory, or prior decisions to avoid duplicating work already done in squad/issue-82 or other branches
- **No parallel agent dispatch** — described the plan but cannot actually launch Trinity/Smith/Morpheus as concurrent agent sessions
- **No Scribe auto-log** — session decisions and routing choices not persisted to `.squad/decisions.md`
- **No worktree lifecycle management** — can describe but not execute `git worktree add` + cleanup hooks

With the Squad skill, the Coordinator would: read squad_state → check open worktrees → dispatch agents to worktrees → monitor completion → trigger Scribe → auto-clean finished worktrees.
