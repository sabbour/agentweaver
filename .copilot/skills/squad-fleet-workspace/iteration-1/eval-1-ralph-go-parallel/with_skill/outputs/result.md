# Ralph Fleet Plan — sabbour/agentweaver
**Generated:** 2026-07-01T09:06:56-07:00  
**Trigger:** "Ralph, go. Work on the open issues in sabbour/agentweaver. Bugs first, then chores. Work in parallel where you can."  
**Skill:** `.copilot/skills/squad-fleet/SKILL.md`

---

## Step 1 — Current Board

| # | Title | Squad | Priority | Type |
|---|-------|-------|----------|------|
| 95 | bug(run-page): confirm button does not disable immediately after click | Trinity | — | bug |
| 98 | bug(run-page): preview sandbox not available on orchestration run page | Trinity + Tank | p2 | bug |
| 99 | bug(run-page): preview sandbox button hidden for completed runs — no re-launch possible | Trinity | p2 | bug |
| 100 | chore(graph-view): zoom-in button, animated card navigation, and scroll indicator | Trinity | p2 | chore |
| 101 | docs(architecture): replace AKS flowchart diagrams with block architecture diagrams in README and docs | Scribe | p2 | chore |

**Existing PRs:** none  
**Active worktrees:** none (main only)

---

## Step 2 — Priority-Sorted Work Queue

Sorting rule: p0 bugs → p1 bugs → p0/p1 chores → p2 bugs → features/remaining chores → spikes.  
Within each tier: issue number ascending (oldest first).  
#95 carries no priority label; treated as p2 (same tier as the other bugs) given its bug type.

| Queue | # | Title | Type | Priority | Agent |
|-------|---|-------|------|----------|-------|
| 1 | 95 | confirm button does not disable immediately after click | bug | (unset → p2) | Trinity |
| 2 | 98 | preview sandbox not available on orchestration run page | bug | p2 | Trinity + Tank |
| 3 | 99 | preview sandbox button hidden for completed runs | bug | p2 | Trinity |
| 4 | 100 | zoom-in button, animated card navigation, scroll indicator | chore | p2 | Trinity |
| 5 | 101 | replace AKS flowchart diagrams with block architecture diagrams | chore | p2 | Scribe |

---

## Step 3 — Conflict Analysis

### File-domain mapping (from routing.md + issue bodies)

| # | Files touched | Domain |
|---|--------------|--------|
| 95 | `apps/web/src/pages/WorkflowRunPage.tsx` (confirm button component) | frontend — run-page |
| 98 | `apps/web/src/pages/CoordinatorRunPage.tsx` (Preview button port) | frontend — coordinator-page |
| 98 | `apps/api/` port-forward endpoint review (read-only review, Tank) | backend |
| 99 | `apps/web/src/pages/WorkflowRunPage.tsx` line 836 (gate condition) | frontend — run-page |
| 100 | `apps/web/src/pages/` graph view component(s) (zoom/nav/indicator) | frontend — graph-view |
| 101 | `README.md`, `docs/guide/architecture-aks.md` | docs |

### Conflict matrix

| Issues | Overlap? | Reason |
|--------|----------|--------|
| #95 ↔ #99 | **CONFLICT** | Both edit `WorkflowRunPage.tsx` |
| #95 ↔ #98 | safe | Different page files |
| #95 ↔ #100 | safe | Different components |
| #95 ↔ #101 | safe | Code vs docs |
| #98 ↔ #99 | safe | `CoordinatorRunPage.tsx` vs `WorkflowRunPage.tsx` |
| #98 ↔ #100 | safe | Different UI areas (run-page vs graph-view) |
| #99 ↔ #100 | safe | `WorkflowRunPage.tsx` vs graph components |
| #100 ↔ #101 | safe | Code vs docs |
| Any ↔ #101 | safe | Docs-only, isolated file tree |

### Conclusion

#95 and #99 **must be serialized** — both write to `WorkflowRunPage.tsx`.  
All other combinations are safe to parallelize.

---

## Step 4 — Batch Grouping

```
🚀 Fleet plan:

   Batch 1 (parallel — 3 agents):
     #98  Trinity  —  bug(run-page): preview sandbox on CoordinatorRunPage
     #100 Trinity  —  chore(graph-view): zoom/navigation/scroll indicator
     #101 Scribe   —  docs(architecture): block architecture diagrams

   Batch 2a (after Batch 1 merges — solo):
     #95  Trinity  —  bug(run-page): confirm button disable

   Batch 2b (after Batch 2a merges — solo):
     #99  Trinity  —  bug(run-page): preview button for completed runs
```

> **Why bugs #95 and #99 are in Batch 2, not Batch 1:**  
> Both edit `WorkflowRunPage.tsx`. Running them in parallel guarantees a merge conflict.  
> Serializing them is the only safe path even though they are higher-tier work.  
> #100 and #101 are conflict-safe with all other issues so they are pulled forward into Batch 1
> to maximize throughput.

---

## Step 5 — Worktrees and Branch Names

| # | Worktree path | Branch |
|---|--------------|--------|
| 95 | `C:\Users\asabbour\Git\agentweaver-issue-95` | `squad/issue-95-confirm-button-disable` |
| 98 | `C:\Users\asabbour\Git\agentweaver-issue-98` | `squad/issue-98-preview-sandbox-coordinator` |
| 99 | `C:\Users\asabbour\Git\agentweaver-issue-99` | `squad/issue-99-preview-button-completed-runs` |
| 100 | `C:\Users\asabbour\Git\agentweaver-issue-100` | `squad/issue-100-graph-view-zoom-navigate` |
| 101 | `C:\Users\asabbour\Git\agentweaver-issue-101` | `squad/issue-101-block-architecture-diagrams` |

**Worktree creation commands (Batch 1 — run simultaneously):**

```bash
git -C "C:\Users\asabbour\Git\agentweaver" worktree add "C:\Users\asabbour\Git\agentweaver-issue-98"  -b squad/issue-98-preview-sandbox-coordinator  main
git -C "C:\Users\asabbour\Git\agentweaver" worktree add "C:\Users\asabbour\Git\agentweaver-issue-100" -b squad/issue-100-graph-view-zoom-navigate      main
git -C "C:\Users\asabbour\Git\agentweaver" worktree add "C:\Users\asabbour\Git\agentweaver-issue-101" -b squad/issue-101-block-architecture-diagrams    main

# Optional: junction node_modules to avoid reinstall
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-98\apps\web\node_modules  C:\Users\asabbour\Git\agentweaver\apps\web\node_modules" 2>nul
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-100\apps\web\node_modules C:\Users\asabbour\Git\agentweaver\apps\web\node_modules" 2>nul
```

**Worktree creation commands (Batch 2a — after Batch 1 merges):**

```bash
git -C "C:\Users\asabbour\Git\agentweaver" worktree add "C:\Users\asabbour\Git\agentweaver-issue-95"  -b squad/issue-95-confirm-button-disable          main
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-95\apps\web\node_modules  C:\Users\asabbour\Git\agentweaver\apps\web\node_modules" 2>nul
```

**Worktree creation commands (Batch 2b — after Batch 2a merges):**

```bash
git -C "C:\Users\asabbour\Git\agentweaver" worktree add "C:\Users\asabbour\Git\agentweaver-issue-99"  -b squad/issue-99-preview-button-completed-runs    main
cmd /c "mklink /J C:\Users\asabbour\Git\agentweaver-issue-99\apps\web\node_modules  C:\Users\asabbour\Git\agentweaver\apps\web\node_modules" 2>nul
```

---

## Step 6 — Spawn Prompts (Batch 1)

### Agent 1 — Trinity on #98

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-98
TEAM_ROOT:     C:\Users\asabbour\Git\agentweaver\.squad
BRANCH:        squad/issue-98-preview-sandbox-coordinator
CURRENT_DATETIME: 2026-07-01T09:06:56-07:00

Issue #98: bug(run-page): preview sandbox not available on orchestration run page

## Summary
CoordinatorRunPage has no Preview button; WorkflowRunPage already has the full dialog
(lines 836–845). Users navigating to orchestration runs at /projects/:projectId/orchestrations/:runId
have no way to launch a preview sandbox.

## Technical notes
- Backend API already implemented: POST /runs/{runId}/sandbox/port-forward in
  apps/web/src/api/client.ts lines 741–756. The startPortForward call exists.
- Working implementation to copy from: apps/web/src/pages/WorkflowRunPage.tsx lines 836–845
  (Preview button + dialog).
- **File to fix:** apps/web/src/pages/CoordinatorRunPage.tsx
- Acceptance criteria:
  - [ ] Preview button appears on orchestration run page for kubernetes-sandbox runs
  - [ ] Clicking it opens the port-forward dialog
  - [ ] Works for both active AND completed runs

## Dispatch for this agent (Trinity)
Implement: port the Preview button + dialog from WorkflowRunPage into CoordinatorRunPage.
Note: Tank will verify the backend endpoint is run-type agnostic (separate review task).

## Your job
1. Work entirely inside WORKTREE_PATH — never switch branches or touch other worktrees
2. Read WorkflowRunPage.tsx lines 836–845 for the reference implementation
3. Port Preview button + port-forward dialog into CoordinatorRunPage.tsx
4. Gate the button on isKubernetesSandbox — show for both active AND completed runs
5. Run relevant tests:
   npm --prefix apps\web test -- --run --testPathPattern=CoordinatorRunPage
6. Commit: git -C WORKTREE_PATH commit -m "bug(run-page): add preview sandbox to orchestration run page (#98)"
7. Push: git -C WORKTREE_PATH push -u origin squad/issue-98-preview-sandbox-coordinator
8. Open PR:
   gh pr create \
     --title "bug(run-page): add preview sandbox to orchestration run page (#98)" \
     --body "Closes #98\n\n## What changed\nPorted Preview button and port-forward dialog from WorkflowRunPage into CoordinatorRunPage. Button gated on isKubernetesSandbox, visible for active and completed runs.\n\n## Testing\nnpm --prefix apps/web test -- --run --testPathPattern=CoordinatorRunPage" \
     --base main \
     --head squad/issue-98-preview-sandbox-coordinator
9. Report: issue number, files changed, test results, PR URL

Docs disposition: no docs needed (internal implementation parity fix).
```

---

### Agent 2 — Trinity on #100

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-100
TEAM_ROOT:     C:\Users\asabbour\Git\agentweaver\.squad
BRANCH:        squad/issue-100-graph-view-zoom-navigate
CURRENT_DATETIME: 2026-07-01T09:06:56-07:00

Issue #100: chore(graph-view): zoom-in button, animated card navigation, and scroll indicator

## Summary
The orchestration graph has no way to zoom in or navigate between cards. Three improvements requested:
1. Zoom-in button — snaps viewport to readable zoom (~0.75–1.0 scale); Back-to-overview returns
   to full-graph fit-view.
2. Animated card navigation — Next/Prev control (or arrow keys) pans to next card in pipeline
   order with 300–400ms ease animation.
3. Scroll indicator — fade-out edge, arrow badge, or minimap dot when graph content overflows viewport.

## Done when
- [ ] "Zoom in" button snaps to readable zoom level (~0.75–1.0 scale)
- [ ] "Fit view" / "Back to overview" returns to full-graph view
- [ ] Next/Prev navigation pans to the next card with a smooth animation (300–400ms ease)
- [ ] Scroll indicator visible when graph overflows viewport
- [ ] All existing graph tests pass

## Your job
1. Work entirely inside WORKTREE_PATH
2. Locate the graph view component(s) in apps/web/src/ (likely under components/ or pages/)
3. Implement the three features
4. Run existing graph tests:
   npm --prefix apps\web test -- --run --testPathPattern=graph
5. Commit: git -C WORKTREE_PATH commit -m "chore(graph-view): zoom-in button, animated card navigation, and scroll indicator (#100)"
6. Push: git -C WORKTREE_PATH push -u origin squad/issue-100-graph-view-zoom-navigate
7. Open PR:
   gh pr create \
     --title "chore(graph-view): zoom-in button, animated card navigation, and scroll indicator (#100)" \
     --body "Closes #100\n\n## What changed\nAdded zoom-in/fit-view buttons, Next/Prev animated card navigation, and scroll overflow indicator to the orchestration graph view.\n\n## Testing\nnpm --prefix apps/web test -- --run --testPathPattern=graph" \
     --base main \
     --head squad/issue-100-graph-view-zoom-navigate
8. Report: issue number, files changed, test results, PR URL

Docs disposition: no docs needed (UI enhancement, internal).
```

---

### Agent 3 — Scribe on #101

```
You are Scribe, the Docs specialist.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-101
TEAM_ROOT:     C:\Users\asabbour\Git\agentweaver\.squad
BRANCH:        squad/issue-101-block-architecture-diagrams
CURRENT_DATETIME: 2026-07-01T09:06:56-07:00

Issue #101: docs(architecture): replace AKS flowchart diagrams with block architecture diagrams in README and docs

## Summary
README.md and docs/guide/architecture-aks.md use Mermaid flowchart syntax for component overview
sections. Replace these with Mermaid block-beta diagrams (component groupings, no flow arrows).

## Files to update
- README.md lines 113–165: replace table + flowchart section with block-beta diagram
- docs/guide/architecture-aks.md lines 13–69: replace first flowchart TB (simple component diagram)
  with block-beta diagram. Keep the detailed networking flowcharts lower in the doc — those are
  intentional data-flow diagrams.

## Done when
- [ ] README "Block diagram" section uses block-beta Mermaid syntax with no directional arrows
- [ ] architecture-aks.md "Component diagram" section uses block-beta syntax
- [ ] Diagrams render correctly in GitHub markdown preview and VitePress
- [ ] No broken links or missing sections

## Your job
1. Work entirely inside WORKTREE_PATH
2. Read README.md lines 113–165 and docs/guide/architecture-aks.md lines 13–69
3. Replace flowchart syntax with block-beta equivalents preserving all components/labels
4. Verify Mermaid block-beta renders (check syntax against mermaid.js.org/syntax/block.html)
5. Commit: git -C WORKTREE_PATH commit -m "docs(architecture): replace AKS flowchart diagrams with block architecture diagrams (#101)"
6. Push: git -C WORKTREE_PATH push -u origin squad/issue-101-block-architecture-diagrams
7. Open PR:
   gh pr create \
     --title "docs(architecture): replace AKS flowchart diagrams with block architecture diagrams (#101)" \
     --body "Closes #101\n\n## What changed\nReplaced Mermaid flowchart syntax in README and architecture-aks.md component sections with block-beta diagrams. Detailed networking flowcharts preserved as-is.\n\n## Testing\nManual: verified Mermaid block-beta renders in GitHub preview." \
     --base main \
     --head squad/issue-101-block-architecture-diagrams
8. Report: issue number, files changed, PR URL

Docs disposition: this IS the docs work. No secondary docs pass needed.
```

---

## Spawn Prompts (Batch 2a — after Batch 1 merges)

### Agent 4 — Trinity on #95

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-95
TEAM_ROOT:     C:\Users\asabbour\Git\agentweaver\.squad
BRANCH:        squad/issue-95-confirm-button-disable
CURRENT_DATETIME: {datetime at spawn time}

Issue #95: bug(run-page): confirm button does not disable immediately after click

## Summary
Clicking the Confirm button on a run does not disable it immediately. The UI state lags,
allowing the user to click Confirm multiple times before the server acknowledges the first click.

## Expected behavior
Button enters disabled/loading state immediately on click and remains disabled until the
confirmation response is received (success or error).

## Actual behavior
Button remains active after click — duplicate confirmation requests are possible.

## Your job
1. Work entirely inside WORKTREE_PATH
2. Locate the Confirm button in apps/web/src/pages/WorkflowRunPage.tsx (or its child component)
3. Add an isConfirming local state; set to true on click, false on response/error
4. Disable the button when isConfirming === true
5. Run relevant tests:
   npm --prefix apps\web test -- --run --testPathPattern=WorkflowRunPage
6. Commit: git -C WORKTREE_PATH commit -m "bug(run-page): disable confirm button on click (#95)"
7. Push: git -C WORKTREE_PATH push -u origin squad/issue-95-confirm-button-disable
8. Open PR:
   gh pr create \
     --title "bug(run-page): disable confirm button on click (#95)" \
     --body "Closes #95\n\n## What changed\nAdded isConfirming state to disable the Confirm button immediately on click, preventing duplicate submissions.\n\n## Testing\nnpm --prefix apps/web test -- --run --testPathPattern=WorkflowRunPage" \
     --base main \
     --head squad/issue-95-confirm-button-disable
9. Report: issue number, files changed, test results, PR URL

Docs disposition: no docs needed (bug fix).
```

---

## Spawn Prompts (Batch 2b — after Batch 2a merges)

### Agent 5 — Trinity on #99

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-99
TEAM_ROOT:     C:\Users\asabbour\Git\agentweaver\.squad
BRANCH:        squad/issue-99-preview-button-completed-runs
CURRENT_DATETIME: {datetime at spawn time}

Issue #99: bug(run-page): preview sandbox button hidden for completed runs — no re-launch possible

## Summary
WorkflowRunPage gates the Preview button on `runActive || !!previewSession`. For completed runs,
runActive=false and previewSession resets to undefined on every page load. Once a run completes
the Preview button is permanently hidden on reload.

## Technical notes
- File to fix: apps/web/src/pages/WorkflowRunPage.tsx line 836
- Gate condition: {isKubernetesSandbox && (runActive || !!previewSession)}
- Suggested fix Option A: Change gate to isKubernetesSandbox alone — always show for k8s sandbox
- Suggested fix Option B: Show disabled button with tooltip when !runActive && !previewSession

## Your job
1. Work entirely inside WORKTREE_PATH
2. Read WorkflowRunPage.tsx around line 836 to understand the full gate logic
3. Implement Option A or B (prefer A — simpler, UX guidance from Ahmed)
4. Ensure the sandbox pod may still be accessible message is clear if Option B
5. Run relevant tests:
   npm --prefix apps\web test -- --run --testPathPattern=WorkflowRunPage
6. Commit: git -C WORKTREE_PATH commit -m "bug(run-page): show preview button for completed runs (#99)"
7. Push: git -C WORKTREE_PATH push -u origin squad/issue-99-preview-button-completed-runs
8. Open PR:
   gh pr create \
     --title "bug(run-page): show preview button for completed runs (#99)" \
     --body "Closes #99\n\n## What changed\nChanged Preview button gate from (runActive || !!previewSession) to isKubernetesSandbox alone so the button remains visible after run completion.\n\n## Testing\nnpm --prefix apps/web test -- --run --testPathPattern=WorkflowRunPage" \
     --base main \
     --head squad/issue-99-preview-button-completed-runs
9. Report: issue number, files changed, test results, PR URL

Docs disposition: no docs needed (bug fix).
```

---

## Step 7 — Commit Format Summary

| # | Commit / PR title |
|---|-------------------|
| 98 | `bug(run-page): add preview sandbox to orchestration run page (#98)` |
| 100 | `chore(graph-view): zoom-in button, animated card navigation, and scroll indicator (#100)` |
| 101 | `docs(architecture): replace AKS flowchart diagrams with block architecture diagrams (#101)` |
| 95 | `bug(run-page): disable confirm button on click (#95)` |
| 99 | `bug(run-page): show preview button for completed runs (#99)` |

**PR body template (all agents):**
```markdown
Closes #{issue_number}

## What changed
{brief description}

## Testing
{test command + result}
```

---

## Step 8 — Docs Pass (after all batches)

| # | Docs disposition | Action |
|---|-----------------|--------|
| 95 | no docs needed | skip |
| 98 | no docs needed | skip |
| 99 | no docs needed | skip |
| 100 | no docs needed | skip |
| 101 | IS the docs work | no secondary pass needed |

No secondary Scribe pass required after this iteration.

---

## Execution Summary

```
📋 Open board: 5 issues (3 bugs, 2 chores)

🚀 Fleet plan:

   Batch 1 (parallel — start now):
     #98  Trinity  bug(run-page): preview sandbox on CoordinatorRunPage
     #100 Trinity  chore(graph-view): zoom/navigation/scroll indicator
     #101 Scribe   docs(architecture): block-beta diagrams

   Batch 2a (after Batch 1 merges — solo):
     #95  Trinity  bug(run-page): confirm button disable

   Batch 2b (after Batch 2a merges — solo):
     #99  Trinity  bug(run-page): preview button for completed runs

⚠️  Serialization reason: #95 and #99 both edit WorkflowRunPage.tsx line 836 region.
    Running them in parallel guarantees a merge conflict.

📌 Note on #98 dispatch:
    Tank (backend) performs a read-only review of the port-forward endpoint — no
    separate worktree needed for Tank. Smith's RCA is a verification task that can
    run as a quick probe before Trinity's PR merges.
```
