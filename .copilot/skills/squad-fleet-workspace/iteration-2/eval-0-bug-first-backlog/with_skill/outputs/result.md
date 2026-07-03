# Squad Fleet — Iteration 2 · Bug-First Backlog
**Squad v0.10.0** · 2026-07-01T09:19:35 PDT
💡 Say "squad commands" to see what I can do.

---

## Step 1 — Current Board

> Fetched from `gh issue list --label "go:yes" --repo sabbour/agentweaver --state open`
> No open PRs. No existing worktrees for any of these issues.

| # | Title | Assigned | Priority | Type |
|---|-------|----------|----------|------|
| #95 | bug(run-page): confirm button does not disable immediately after click | Trinity | — | bug |
| #98 | bug(run-page): preview sandbox not available on orchestration run page | Trinity + Tank | p2 | bug |
| #99 | bug(run-page): preview sandbox button hidden for completed runs | Trinity | p2 | bug |
| #100 | chore(graph-view): zoom-in button, animated card navigation, and scroll indicator | Trinity | p2 | chore |
| #101 | docs(architecture): replace AKS flowchart diagrams with block architecture diagrams | Scribe | p2 | chore/docs |

---

## Step 2 — Work Queue (sorted, bugs first per Ahmed's request)

Priority order applied (fleet skill §Step 2 + Ahmed: "fix bugs first"):

| Rank | # | Title | Type | Priority | Assignee |
|------|---|-------|------|----------|----------|
| 1 | #95 | bug(run-page): confirm button does not disable immediately after click | bug | — (≈p2) | Trinity |
| 2 | #98 | bug(run-page): preview sandbox not available on orchestration run page | bug | p2 | Trinity + Tank |
| 3 | #99 | bug(run-page): preview sandbox button hidden for completed runs | bug | p2 | Trinity |
| 4 | #100 | chore(graph-view): zoom-in button, animated card navigation, scroll indicator | chore | p2 | Trinity |
| 5 | #101 | docs(architecture): replace AKS flowchart with block-beta diagrams | docs/chore | p2 | Scribe |

---

## Step 3 — Conflict Analysis

Routing each issue to its likely file(s) via `.squad/routing.md` domain ownership:

| # | Assignee | Primary File(s) | Notes |
|---|----------|-----------------|-------|
| #95 | Trinity (frontend-engineer) | `apps/web/src/pages/WorkflowRunPage.tsx` | Confirm button disable logic |
| #98 | Trinity + Tank | `apps/web/src/pages/CoordinatorRunPage.tsx` | Port Preview button from WorkflowRunPage; Tank reviews backend endpoint only (no code write) |
| #99 | Trinity (frontend-engineer) | `apps/web/src/pages/WorkflowRunPage.tsx` line 836 | Preview gate condition fix |
| #100 | Trinity (frontend-engineer) | graph-view component(s) — distinct from run pages | Zoom/nav/indicator additions |
| #101 | Scribe | `README.md`, `docs/guide/architecture-aks.md` | Docs only, never conflicts |

**Conflict matrix:**

| | #95 | #98 | #99 | #100 | #101 |
|---|---|---|---|---|---|
| **#95** | — | ✅ safe (diff files) | ⚠️ **CONFLICT** (same WorkflowRunPage.tsx) | ✅ safe | ✅ safe |
| **#98** | ✅ safe | — | ✅ safe (diff files) | ✅ safe | ✅ safe |
| **#99** | ⚠️ **CONFLICT** | ✅ safe | — | ✅ safe | ✅ safe |
| **#100** | ✅ safe | ✅ safe | ✅ safe | — | ✅ safe |
| **#101** | ✅ safe | ✅ safe | ✅ safe | ✅ safe | — |

**Conflicts found:**
- `#95` ↔ `#99`: Both modify `apps/web/src/pages/WorkflowRunPage.tsx` → **serialize**

---

## Step 4 — Fleet Batch Plan

```
🚀 Fleet plan (bugs first):

   Batch 1 (parallel — conflict-safe):
     #95  Trinity         WorkflowRunPage.tsx (confirm button)
     #98  Trinity + Tank  CoordinatorRunPage.tsx (orchestration preview)
     #101 Scribe          README.md + docs/guide/architecture-aks.md

   Batch 2 (after Batch 1 merges):
     #99  Trinity         WorkflowRunPage.tsx (preview gate — conflicts with #95)

   Batch 3 (chores, after bugs land):
     #100 Trinity         graph-view component (zoom / nav / scroll indicator)
```

> `#99` is serialized after `#95` because both modify `WorkflowRunPage.tsx`. A merge conflict there wastes more time than one sequential run.
> `#101` (docs) is bundled into Batch 1 — Scribe never conflicts with code.
> `#100` (chore) is pushed to Batch 3 per Ahmed's "fix bugs first" directive.

---

## Step 5 — Worktrees and Branch Names

### Batch 1

**Issue #95**
```bash
ISSUE=95
SLUG="confirm-button-disable-on-click"
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="C:\Users\asabbour\Git\agentweaver-issue-95"
BRANCH="squad/issue-95-confirm-button-disable-on-click"

git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main
cmd /c "mklink /J ${WORKTREE_PATH}\apps\web\node_modules ${REPO_DIR}\apps\web\node_modules" 2>nul || true
```

**Issue #98**
```bash
ISSUE=98
SLUG="orchestration-run-preview-sandbox"
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="C:\Users\asabbour\Git\agentweaver-issue-98"
BRANCH="squad/issue-98-orchestration-run-preview-sandbox"

git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main
cmd /c "mklink /J ${WORKTREE_PATH}\apps\web\node_modules ${REPO_DIR}\apps\web\node_modules" 2>nul || true
```

**Issue #101**
```bash
ISSUE=101
SLUG="architecture-block-diagrams"
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="C:\Users\asabbour\Git\agentweaver-issue-101"
BRANCH="squad/issue-101-architecture-block-diagrams"

git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main
# No node_modules link needed — docs only
```

### Batch 2 (after Batch 1 merges)

**Issue #99**
```bash
ISSUE=99
SLUG="preview-button-completed-runs"
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="C:\Users\asabbour\Git\agentweaver-issue-99"
BRANCH="squad/issue-99-preview-button-completed-runs"

git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main
cmd /c "mklink /J ${WORKTREE_PATH}\apps\web\node_modules ${REPO_DIR}\apps\web\node_modules" 2>nul || true
```

### Batch 3 (after Batch 2 merges)

**Issue #100**
```bash
ISSUE=100
SLUG="graph-zoom-navigation-indicator"
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="C:\Users\asabbour\Git\agentweaver-issue-100"
BRANCH="squad/issue-100-graph-zoom-navigation-indicator"

git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main
cmd /c "mklink /J ${WORKTREE_PATH}\apps\web\node_modules ${REPO_DIR}\apps\web\node_modules" 2>nul || true
```

---

## Step 6 — Agent Spawn Prompts (Batch 1)

---

### 🔵 Agent: Trinity — Issue #95

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-95
TEAM_ROOT: C:\Users\asabbour\Git\agentweaver\.squad
BRANCH: squad/issue-95-confirm-button-disable-on-click
CURRENT_DATETIME: 2026-07-01T09:19:35-07:00

Issue #95: bug(run-page): confirm button does not disable immediately after click

## Summary
Clicking the **Confirm** button on a run does not disable it immediately. The UI state
lags, allowing the user to click Confirm multiple times before the server acknowledges
the first click.

## Expected behavior
The button should enter a disabled/loading state immediately on click and remain disabled
until the confirmation response is received (success or error).

## Actual behavior
Button remains active after click — possible to submit duplicate confirmation requests.

## Reported by
Ahmed Sabbour (asabbour) — 2026-07-01

---

Your job:
1. Work ENTIRELY inside WORKTREE_PATH — never switch branches or touch other worktrees.
2. Locate the Confirm button in `apps/web/src/pages/WorkflowRunPage.tsx` (or related
   component). Add immediate disabled/loading state on click using a local React state
   variable (e.g. `isConfirming`). Set it to true on click, reset on response/error.
3. Run relevant tests:
     npm --prefix apps\web test -- --run --testPathPattern=WorkflowRunPage
4. Commit:
     git -C WORKTREE_PATH commit -m "bug(run-page): disable confirm button on click (#95)"
5. Push:
     git -C WORKTREE_PATH push -u origin squad/issue-95-confirm-button-disable-on-click
6. Open PR:
     gh pr create \
       --title "bug(run-page): disable confirm button on click (#95)" \
       --body "$(cat <<'EOF'
Closes #95

## What changed
Added immediate `isConfirming` state to the Confirm button. The button is disabled
and shows a loading indicator from the moment the user clicks until the server
acknowledges the request (success or error).

## Testing
npm --prefix apps/web test -- --run --testPathPattern=WorkflowRunPage
[all tests passed]
EOF
)" \
       --base main \
       --head squad/issue-95-confirm-button-disable-on-click
7. Report back: issue number, files changed, test results, PR URL.

Docs disposition: No docs needed — internal UI state change.
```

---

### 🔵 Agent: Trinity — Issue #98 (frontend portion)

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-98
TEAM_ROOT: C:\Users\asabbour\Git\agentweaver\.squad
BRANCH: squad/issue-98-orchestration-run-preview-sandbox
CURRENT_DATETIME: 2026-07-01T09:19:35-07:00

Issue #98: bug(run-page): preview sandbox not available on orchestration run page

## Summary
CoordinatorRunPage has no Preview button; WorkflowRunPage already has the full dialog
(lines 836–845). Users navigating to /projects/:projectId/orchestrations/:runId have no
way to launch a preview sandbox.

## Expected behavior
A Preview button (and port-forward dialog) should appear on the orchestration run page
for kubernetes-sandbox runs, matching the experience on WorkflowRunPage.

## Actual behavior
No Preview button is rendered on CoordinatorRunPage.

## Technical notes
- Backend API already implemented: POST /runs/{runId}/sandbox/port-forward in
  apps/web/src/api/client.ts lines 741–756. startPortForward call exists — not wired to
  CoordinatorRunPage.
- Working implementation to copy from: apps/web/src/pages/WorkflowRunPage.tsx lines
  836–845 (Preview button + dialog).
- **File to fix:** apps/web/src/pages/CoordinatorRunPage.tsx
- Acceptance criteria:
  - [x] Preview button appears on orchestration run page for kubernetes-sandbox runs
  - [x] Clicking it opens the port-forward dialog
  - [x] Works for both active AND completed runs

## Dispatch
- squad:smith — RCA: confirm POST /runs/{runId}/sandbox/port-forward accepts
  orchestration runIds (coordinate with Smith separately — you do the frontend impl)
- squad:trinity — implement: port the Preview button + dialog from WorkflowRunPage into
  CoordinatorRunPage (THIS IS YOUR TASK)
- squad:tank — review: confirm backend endpoint is run-type-agnostic (Tank will review
  your PR)

---

Your job:
1. Work ENTIRELY inside WORKTREE_PATH — never switch branches or touch other worktrees.
2. Open apps/web/src/pages/CoordinatorRunPage.tsx. Study the Preview button implementation
   in apps/web/src/pages/WorkflowRunPage.tsx lines 836–845 (READ only — do NOT modify it).
3. Port the Preview button, port-forward dialog, and relevant state (previewSession,
   isKubernetesSandbox detection) into CoordinatorRunPage.tsx.
4. Ensure the Preview button shows for both active AND completed runs (do not gate it on
   runActive — learn from issue #99 what NOT to do).
5. Run relevant tests:
     npm --prefix apps\web test -- --run --testPathPattern=CoordinatorRunPage
6. Commit:
     git -C WORKTREE_PATH commit -m "bug(run-page): add preview sandbox to orchestration run page (#98)"
7. Push:
     git -C WORKTREE_PATH push -u origin squad/issue-98-orchestration-run-preview-sandbox
8. Open PR:
     gh pr create \
       --title "bug(run-page): add preview sandbox to orchestration run page (#98)" \
       --body "$(cat <<'EOF'
Closes #98

## What changed
Ported the Preview button and port-forward dialog from WorkflowRunPage into
CoordinatorRunPage. The button is visible for all kubernetes-sandbox orchestration
runs (both active and completed). Wired to the existing startPortForward API
(apps/web/src/api/client.ts lines 741–756).

## Testing
npm --prefix apps/web test -- --run --testPathPattern=CoordinatorRunPage
[all tests passed]
EOF
)" \
       --base main \
       --head squad/issue-98-orchestration-run-preview-sandbox
9. Report back: issue number, files changed, test results, PR URL.

Docs disposition: No docs needed — parity fix (feature already documented for WorkflowRunPage).
```

---

### 🟡 Agent: Tank — Issue #98 (backend review / RCA assist)

```
You are Tank, the Backend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-98
TEAM_ROOT: C:\Users\asabbour\Git\agentweaver\.squad
BRANCH: squad/issue-98-orchestration-run-preview-sandbox
CURRENT_DATETIME: 2026-07-01T09:19:35-07:00

Issue #98: bug(run-page): preview sandbox not available on orchestration run page
(Backend review task — Trinity owns the code change)

Your job is a REVIEW task, not a code-writing task:
1. Read apps/web/src/api/client.ts lines 741–756 (startPortForward implementation).
2. Confirm whether the POST /runs/{runId}/sandbox/port-forward endpoint is agnostic to
   run type — i.e., does it accept orchestration runIds the same way it handles
   workflow runIds?
3. If you find a backend gap (e.g., the endpoint only accepts workflow runs), document
   what needs changing in apps/api/ and flag it to Squad Coordinator.
4. When Trinity opens her PR for this issue, review it and approve if the backend
   contract is satisfied.
5. Do NOT commit or push code changes unless a backend fix is actually needed.

Report back: your findings on backend endpoint compatibility, any required backend
changes (file + line), and your PR review decision.
```

---

### 📝 Agent: Scribe — Issue #101

```
You are Scribe, the Session Logger and Docs specialist.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-101
TEAM_ROOT: C:\Users\asabbour\Git\agentweaver\.squad
BRANCH: squad/issue-101-architecture-block-diagrams
CURRENT_DATETIME: 2026-07-01T09:19:35-07:00

Issue #101: docs(architecture): replace AKS flowchart diagrams with block architecture
diagrams in README and docs

## Summary
Both README.md and docs/guide/architecture-aks.md use Mermaid `flowchart` syntax for
the "block diagram" section, which renders as a flowchart with directional arrows — not
a block architecture diagram. Ahmed wants a true block architecture diagram using Mermaid
`block-beta` (or Excalidraw) showing component groupings without flow arrows.

## Files to update
- `README.md` lines 113–165: replace the table + flowchart section with a `block-beta`
  diagram
- `docs/guide/architecture-aks.md` lines 13–69: replace the first `flowchart TB`
  (the simple component diagram) with a `block-beta` diagram. Keep the detailed
  networking flowcharts lower in the doc — those show data flow and are intentionally
  flowcharts.

## Done when
- [ ] README "Block diagram" section uses `block-beta` Mermaid syntax with no
      directional arrows
- [ ] `architecture-aks.md` "Component diagram" section uses `block-beta` syntax
- [ ] Diagrams render correctly in GitHub markdown preview and VitePress
- [ ] No broken links or missing sections

---

Your job:
1. Work ENTIRELY inside WORKTREE_PATH — never switch branches or touch other worktrees.
2. Read README.md lines 113–165 and docs/guide/architecture-aks.md lines 13–69.
3. Replace the `flowchart` Mermaid syntax in those sections with valid `block-beta`
   diagrams that show component groupings (AKS cluster, Agentweaver components,
   external services) without directional flow arrows.
4. Verify diagrams are syntactically valid Mermaid block-beta — check with:
     npx @mermaid-js/mermaid-cli -i README.md 2>&1 | head -20
   (or validate manually by reading the block-beta spec).
5. Commit:
     git -C WORKTREE_PATH commit -m "docs(architecture): replace AKS flowchart diagrams with block-beta (#101)"
6. Push:
     git -C WORKTREE_PATH push -u origin squad/issue-101-architecture-block-diagrams
7. Open PR:
     gh pr create \
       --title "docs(architecture): replace AKS flowchart diagrams with block-beta (#101)" \
       --body "$(cat <<'EOF'
Closes #101

## What changed
Replaced `flowchart TB` Mermaid diagrams in README.md and
docs/guide/architecture-aks.md with `block-beta` syntax to produce true block
architecture diagrams (component groupings, no directional arrows). Detailed
networking flowcharts elsewhere in the doc are unchanged.

## Testing
Diagrams validated as syntactically correct Mermaid block-beta.
EOF
)" \
       --base main \
       --head squad/issue-101-architecture-block-diagrams
8. Report back: files changed, sections updated, PR URL.
```

---

## Step 7 — Commit and PR Format (all issues)

| # | Commit / PR Title | PR Body "Closes" | Docs Note |
|---|-------------------|------------------|-----------|
| #95 | `bug(run-page): disable confirm button on click (#95)` | Closes #95 | No docs needed |
| #98 | `bug(run-page): add preview sandbox to orchestration run page (#98)` | Closes #98 | No docs needed (parity fix) |
| #99 | `bug(run-page): fix preview button visibility for completed runs (#99)` | Closes #99 | No docs needed |
| #100 | `chore(graph-view): add zoom-in, card navigation and scroll indicator (#100)` | Closes #100 | No docs needed (UI enhancement) |
| #101 | `docs(architecture): replace AKS flowchart diagrams with block-beta (#101)` | Closes #101 | This IS the docs work |

---

## Step 8 — Batch 2 Spawn Prompt (queued, runs after Batch 1 merges)

### 🔵 Agent: Trinity — Issue #99

```
You are Trinity, the Frontend Engineer.
WORKTREE_PATH: C:\Users\asabbour\Git\agentweaver-issue-99
TEAM_ROOT: C:\Users\asabbour\Git\agentweaver\.squad
BRANCH: squad/issue-99-preview-button-completed-runs
CURRENT_DATETIME: {datetime at spawn time}

Issue #99: bug(run-page): preview sandbox button hidden for completed runs — no
re-launch possible

## Summary
On WorkflowRunPage, the Preview button is gated on `runActive || !!previewSession`.
For completed runs, `runActive=false` and `previewSession` resets to `undefined` on
every page load, so users can never re-launch a preview after navigating away.

## Steps to reproduce
1. Start a kubernetes-sandbox workflow run and open the Preview sandbox
2. Wait for the run to complete (or let it reach a `completed` state)
3. Navigate away from the page, then navigate back
4. Observe that the Preview button is gone

## Expected behavior
The Preview button should remain visible (or at minimum show as a disabled button with
tooltip) for kubernetes-sandbox runs even after the run completes.

## Technical notes
- **File to fix:** apps/web/src/pages/WorkflowRunPage.tsx line 836
- Gate condition: `{isKubernetesSandbox && (runActive || !!previewSession)}`
- Suggested fix (option A): Change gate to `isKubernetesSandbox` alone
- Suggested fix (option B): Show disabled button with tooltip when !runActive &&
  !previewSession

---

NOTE: Batch 1 (#95) has already merged by the time you are spawned. Pull latest main
into your worktree before starting:
  git -C WORKTREE_PATH fetch origin main
  git -C WORKTREE_PATH merge origin/main

Your job:
1. Work ENTIRELY inside WORKTREE_PATH — never switch branches or touch other worktrees.
2. Fix the gate condition at apps/web/src/pages/WorkflowRunPage.tsx line 836 using
   Option A (simplest) or Option B if a tooltip is cleaner UX.
3. Run relevant tests:
     npm --prefix apps\web test -- --run --testPathPattern=WorkflowRunPage
4. Commit:
     git -C WORKTREE_PATH commit -m "bug(run-page): fix preview button visibility for completed runs (#99)"
5. Push:
     git -C WORKTREE_PATH push -u origin squad/issue-99-preview-button-completed-runs
6. Open PR:
     gh pr create \
       --title "bug(run-page): fix preview button visibility for completed runs (#99)" \
       --body "$(cat <<'EOF'
Closes #99

## What changed
Fixed the Preview button gate condition in WorkflowRunPage.tsx. The button now
appears for all kubernetes-sandbox runs regardless of `runActive` state, so
users can re-launch the sandbox preview after a run completes.

## Testing
npm --prefix apps/web test -- --run --testPathPattern=WorkflowRunPage
[all tests passed]
EOF
)" \
       --base main \
       --head squad/issue-99-preview-button-completed-runs
7. Report back: issue number, files changed, test results, PR URL.
```

---

## Step 9 — Post-Batch Docs Pass

After Batch 1 and 2 merge:

| # | Docs disposition | Action |
|---|-----------------|--------|
| #95 | No docs needed — internal UI state | Skip |
| #98 | No docs needed — parity fix, already documented | Skip |
| #99 | No docs needed — bug fix | Skip |
| #100 | No docs needed — UI enhancement | Skip |
| #101 | This issue IS the docs work | Already done |

No Scribe docs-sync or docs-feature passes required for this batch.

---

## Summary to Ahmed

Hey Ahmed — here's the plan:

🐛 **Bugs first, as requested.** Three bugs in the queue, two chores.

**Batch 1 (launching now — parallel):**
- `#95` → **Trinity** — Confirm button gets immediate disabled state
- `#98` → **Trinity + Tank** — Preview sandbox wired into CoordinatorRunPage
- `#101` → **Scribe** — Architecture diagrams converted to block-beta (bundled in since it never conflicts)

**Batch 2 (after #95 merges):**
- `#99` → **Trinity** — Preview button gate fixed for completed runs *(serialized because it edits the same WorkflowRunPage.tsx as #95)*

**Batch 3 (after bugs land):**
- `#100` → **Trinity** — Graph zoom/nav/scroll indicator chore

I'll keep you updated as each batch completes. ✅
