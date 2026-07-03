# Simulated GitHub Issue

## Title
Chore: remove dead `AgentPodsTable` component from `ClusterPage.tsx`

## Labels
`chore`, `tech-debt`, `frontend`

## Body

### Summary
The `Active agent pods` section was removed from `ClusterPage` (see comment on line 427: *"Active agent pods removed — already captured in Sandbox claims"*), but the `AgentPodsTable` component defined in the same file was not cleaned up — it still renders the **Orphaned agent pods** section (line 433).

### Investigation findings

**File:** `apps/web/src/pages/ClusterPage.tsx`

| Item | Lines | Status |
|---|---|---|
| `AgentPodsTable` component definition | 152–179 | Still present |
| `AgentPodInfoDto` type import | 24 | Still present (used by `AgentPodsTable`) |
| "Active agent pods" render site | (removed) | ✅ Gone (comment at line 427) |
| "Orphaned agent pods" render site using `AgentPodsTable` | 433 | Still active |

**Key observation:** `AgentPodsTable` is **not** an external import — it is a locally-defined component in the same file. It was originally used for both the *Active* and *Orphaned* agent pod sections. After the Active section was removed, the component was retained to render orphaned pods, so it is **not fully dead code yet**.

### Proposed cleanup

Two options depending on intent:

**Option A — Remove orphaned-pods section too** (preferred if orphaned pods are already visible via the Sandbox claims table):
- Delete the `AgentPodsTable` function (lines 152–179)
- Remove the orphaned-pods `<div className={styles.section}>` block (lines 430–435)
- Remove the `AgentPodInfoDto` type from the import (line 24)
- Remove the `orphaned_agent_pods` KPI card (line 401) if no longer relevant

**Option B — Keep orphaned-pods section, leave `AgentPodsTable` in place**:
- Close this issue; no action needed.

### Acceptance criteria
- [ ] Decision documented (Option A or B) in this issue
- [ ] If Option A: `AgentPodsTable` and all references removed; `AgentPodInfoDto` import removed if unused; existing tests pass
- [ ] No TypeScript errors (`tsc --noEmit`)

### Related
- Comment at `ClusterPage.tsx:427`: `// Active agent pods removed — already captured in Sandbox claims`
