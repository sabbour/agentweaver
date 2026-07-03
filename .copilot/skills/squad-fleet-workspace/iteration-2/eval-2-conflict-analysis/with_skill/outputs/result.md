# Squad Fleet — Conflict Analysis & Spawn Plan
**Squad v0.10.0** | Session: 2026-07-01 | Requested by: Ahmed Sabbour

---

## Issues in Scope

| # | Title | Type | Assigned | Primary File |
|---|-------|------|----------|-------------|
| #98 | `bug(run-page): preview sandbox not available on orchestration run page` | bug | Trinity + Tank | `apps/web/src/pages/CoordinatorRunPage.tsx` |
| #100 | `chore(graph-view): zoom-in button, animated card navigation, scroll indicator` | chore | Trinity | `apps/web/src/components/CoordinatorTopologyGraph.tsx` |

---

## Step 1 — File-Level Conflict Analysis

### File Ownership Mapping

Using routing rules from `.squad/routing.md` — domain ownership derived from member charters:

| Issue | Files Expected to Change | Domain | Routed To |
|-------|--------------------------|--------|-----------|
| #98 | `apps/web/src/pages/CoordinatorRunPage.tsx` | react-19-fluent2, ux-flow-implementation | Trinity (frontend-engineer) |
| #98 | `apps/api/…` (sandbox availability endpoint / SSE plumbing) | sse-streaming, backend-api | Tank (backend-engineer) |
| #100 | `apps/web/src/components/CoordinatorTopologyGraph.tsx` | react-19-fluent2, live-step-rendering | Trinity (frontend-engineer) |

### Overlap Check

```
#98  touches → apps/web/src/pages/CoordinatorRunPage.tsx
                apps/api/... (backend, Tank's domain)

#100 touches → apps/web/src/components/CoordinatorTopologyGraph.tsx
```

| File Pair | Shared? |
|-----------|---------|
| `CoordinatorRunPage.tsx` (pages/) vs `CoordinatorTopologyGraph.tsx` (components/) | ❌ Different files, different directories |
| Tank's API-layer files vs Trinity's component files | ❌ Entirely different file trees |

**No shared files detected.** The `pages/` and `components/` directories are distinct subtrees under `apps/web/src/`. Trinity's two assignments (one page, one component) do not overlap.

> The squad-fleet skill serializes when "two issues both route to `squad:trinity` AND likely edit the **same component**." These issues edit *different* files — the rule does not trigger.

---

## Step 2 — Conclusion

**✅ PARALLEL SAFE — Both issues may run simultaneously in Batch 1.**

Rationale:
- #98 and #100 touch **zero overlapping files**.
- Trinity's two frontend tasks are in separate subtrees (`pages/` vs `components/`), so intra-agent file contention is also avoided — Tank and Trinity in worktree #98 never touch `CoordinatorTopologyGraph.tsx`.
- No `priority:p0` escalation (neither issue is marked p0), so no forced serialization.

---

## Step 3 — Worktree Paths & Branch Names

Using the naming convention from the squad-fleet skill:
```
WORKTREE_PATH = {REPO_DIR}-issue-{N}           # sibling directory
BRANCH        = squad/issue-{N}-{slug}          # kebab-case from title
REPO_DIR      = C:\Users\asabbour\Git\agentweaver
```

### Issue #98

| Field | Value |
|-------|-------|
| Slug | `preview-sandbox-run-page` |
| Worktree path | `C:\Users\asabbour\Git\agentweaver-issue-98` |
| Branch | `squad/issue-98-preview-sandbox-run-page` |
| Base | `main` |

### Issue #100

| Field | Value |
|-------|-------|
| Slug | `graph-view-zoom-animated-nav` |
| Worktree path | `C:\Users\asabbour\Git\agentweaver-issue-100` |
| Branch | `squad/issue-100-graph-view-zoom-animated-nav` |
| Base | `main` |

---

## Step 4 — Spawn Plan

```
🚀 Fleet plan:
   Batch 1 (parallel — no file conflicts):
     #98  → Worktree: agentweaver-issue-98   | Trinity (frontend) + Tank (backend)
     #100 → Worktree: agentweaver-issue-100  | Trinity (frontend)

   Batch 2: none queued
```

### Agent Assignments

#### Worktree `agentweaver-issue-98` — Issue #98 (bug)

**Trinity** (frontend-engineer) — primary owner of this worktree

- Owns: `apps/web/src/pages/CoordinatorRunPage.tsx`
- Task: Restore preview sandbox availability in the run page UI — detect when the sandbox is unavailable and surface the correct error/fallback state using react-19-fluent2 components.
- Commit format: `bug(run-page): restore preview sandbox on orchestration run page (#98)`

**Tank** (backend-engineer) — collaborating in the same worktree

- Owns: `apps/api/…` (whichever controller/service exposes sandbox availability to the run page)
- Task: Fix the backend plumbing — ensure the sandbox-available signal (SSE event or REST response) is correctly emitted so the frontend has data to display.
- Commit format: `bug(run-page): emit sandbox availability in run SSE stream (#98)` *(if separate commit needed)*

> **Note:** Both agents work inside `agentweaver-issue-98`. Trinity drives the PR; Tank's backend commit lands in the same branch. One PR closes #98.

#### Worktree `agentweaver-issue-100` — Issue #100 (chore)

**Trinity** (frontend-engineer) — sole owner

- Owns: `apps/web/src/components/CoordinatorTopologyGraph.tsx`
- Task: Add zoom-in button, implement animated card navigation, add scroll indicator to the topology graph component.
- Commit format: `chore(graph-view): zoom-in button, animated card navigation, scroll indicator (#100)`
- Opens separate PR, closes #100.

> Trinity runs in two worktrees simultaneously — one per issue. Each worktree is a fully independent git checkout, so Trinity's work on `CoordinatorRunPage.tsx` and `CoordinatorTopologyGraph.tsx` cannot interfere.

---

## Simulation Notes (not executed)

The following commands would be run in a real fleet launch — **not executed here per simulation mode**:

```bash
# Worktree for #98
git -C "C:\Users\asabbour\Git\agentweaver" worktree add \
  "C:\Users\asabbour\Git\agentweaver-issue-98" \
  -b "squad/issue-98-preview-sandbox-run-page" main

# Worktree for #100
git -C "C:\Users\asabbour\Git\agentweaver" worktree add \
  "C:\Users\asabbour\Git\agentweaver-issue-100" \
  -b "squad/issue-100-graph-view-zoom-animated-nav" main

# Symlink node_modules (Windows) for each worktree
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-98\apps\web\node_modules ^
               C:\Users\asabbour\Git\agentweaver\apps\web\node_modules"
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-100\apps\web\node_modules ^
               C:\Users\asabbour\Git\agentweaver\apps\web\node_modules"
```

Agents would then be spawned in parallel (background mode) with the minimal prompt template from the squad-fleet skill, each receiving their `WORKTREE_PATH`, `TEAM_ROOT`, `BRANCH`, issue body, and role context.

---

*Generated by Squad v0.10.0 — squad-fleet skill (`.copilot/skills/squad-fleet/SKILL.md`)*
