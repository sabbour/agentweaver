# Squad Fleet — Conflict Analysis: Issues #98 & #100

**Requested:** Run #98 (bug, Trinity + Tank) and #100 (chore, Trinity) in parallel.  
**Analysis date:** 2026-07-01

---

## Issue Summary

| # | Title | Type | Priority | Assigned | Primary file |
|---|-------|------|----------|----------|-------------|
| 98 | bug(run-page): preview sandbox not available on orchestration run page | bug | p2 | Trinity (implement), Tank (review), Smith (RCA) | `apps/web/src/pages/CoordinatorRunPage.tsx` |
| 100 | chore(graph-view): zoom-in button, animated card navigation, and scroll indicator | chore | p2 | Trinity | `apps/web/src/components/CoordinatorTopologyGraph.tsx` |

---

## Conflict Analysis Reasoning

### File ownership

The skill's conflict matrix checks for **shared file ownership**:

| Files | #98 | #100 |
|-------|-----|------|
| `apps/web/src/pages/CoordinatorRunPage.tsx` | ✅ primary edit | ❌ not touched |
| `apps/web/src/components/CoordinatorTopologyGraph.tsx` | ❌ not touched | ✅ primary edit |

→ **No shared file edits.** The two issues edit disjoint files.

### Domain routing

Per `.squad/routing.md`:
- Both issues route to **frontend-engineer (Trinity)** for implementation.
- #98 also routes Tank (backend-engineer) for a read-only review of the port-forward endpoint — Tank does **not** edit `CoordinatorRunPage.tsx`; this is a code review, not a code change.
- #98 also routes Smith (qa-engineer) for RCA — confirming the API accepts orchestration runIds.

Even though Trinity appears in both, the files she edits are different:
- In #98 she adds a Preview button + port-forward dialog to the **page** (`pages/CoordinatorRunPage.tsx`).
- In #100 she adds zoom controls + card navigation to the **component** (`components/CoordinatorTopologyGraph.tsx`).

### Integration risk assessment

`CoordinatorRunPage.tsx` likely imports `CoordinatorTopologyGraph.tsx`. The concern is whether #100 changes the component's **public props interface** in a way that would break the page currently being edited by #98's branch.

#100's scope is adding **internal UI controls** (Zoom In button, Next/Prev nav, scroll indicator) — these are additive to the component's internal state/layout, not changes to its prop signature. No props are being removed or made required. The integration risk is **low**.

If #100 does add new required props, the #98 branch would need a trivial one-line usage update. This is a standard rebase situation, not a blocking conflict.

### Skill conflict-safe defaults check

| Rule | Applies? | Decision |
|------|----------|----------|
| Same React component file | ❌ Different files | → Parallelize |
| Both route to same squad member (Trinity) | ✅ Both Trinity | → Check files |
| Files are actually different | ✅ Confirmed | → Parallelize |
| Any p0 issue present | ❌ Both p2 | → No serialization required |
| Frontend + backend (different trees) | Partial — #98 has Tank reviewing, not editing | → Parallelize |

---

## Conclusion

**✅ PARALLEL SAFE.**

Issues #98 and #100 edit non-overlapping files. Despite both assigning Trinity as frontend implementer, she works in `pages/` for #98 and `components/` for #100 — there is zero file-level conflict. The only integration surface is that `CoordinatorRunPage.tsx` consumes `CoordinatorTopologyGraph.tsx`, but #100 makes no breaking prop changes. Both can be worked simultaneously in separate worktrees.

---

## Worktree Paths & Branch Names

| Issue | Worktree Path | Branch |
|-------|--------------|--------|
| #98 | `C:\Users\asabbour\Git\agentweaver-issue-98` | `squad/issue-98-preview-sandbox-coordinator-run-page` |
| #100 | `C:\Users\asabbour\Git\agentweaver-issue-100` | `squad/issue-100-graph-zoom-nav` |

---

## Spawn Plan

```
🚀 Fleet plan:

   Batch 1 (parallel — start simultaneously):
     #98  Trinity + Tank + Smith  →  bug(run-page): preview sandbox on CoordinatorRunPage
     #100 Trinity                 →  chore(graph-view): zoom/nav on CoordinatorTopologyGraph

   Batch 2: none — no conflicts require serialization
```

### Worktree setup commands

```powershell
$REPO = "C:\Users\asabbour\Git\agentweaver"

# Issue #98
git -C $REPO worktree add "$REPO-issue-98" -b "squad/issue-98-preview-sandbox-coordinator-run-page" main
cmd /c "mklink /J $REPO-issue-98\apps\web\node_modules $REPO\apps\web\node_modules"

# Issue #100
git -C $REPO worktree add "$REPO-issue-100" -b "squad/issue-100-graph-zoom-nav" main
cmd /c "mklink /J $REPO-issue-100\apps\web\node_modules $REPO\apps\web\node_modules"
```

### Agent assignment

| Agent | Issue | Role | Files to edit |
|-------|-------|------|--------------|
| Trinity | #98 | Implement Preview button + dialog | `apps/web/src/pages/CoordinatorRunPage.tsx` (port from `WorkflowRunPage.tsx` lines 836–845) |
| Tank | #98 | Review / confirm backend port-forward endpoint is run-type agnostic | `apps/api/` (read-only review; no edits expected) |
| Smith | #98 | RCA: confirm `POST /runs/{runId}/sandbox/port-forward` accepts orchestration runIds | Test/contract validation only |
| Trinity | #100 | Implement zoom, Next/Prev nav, scroll indicator | `apps/web/src/components/CoordinatorTopologyGraph.tsx` |

> Note: Tank and Smith on #98 do **not** produce file edits in the same worktree as Trinity — Tank reviews the backend API (different package) and Smith validates endpoint behaviour. No write collision.

---

## Risk log

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| #100 adds required props to `CoordinatorTopologyGraph`, breaking #98's usage | Low | #98 branch rebases on #100 before merge, or #100 merges first |
| Both Trinity worktrees diverge on shared utilities (e.g. `apps/web/src/api/client.ts`) | Very Low | #98 only reads `client.ts` (already implemented per issue body); #100 doesn't touch it |
