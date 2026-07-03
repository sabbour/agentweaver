# Parallel Work Analysis: Issues #98 and #100

**Date:** 2026-07-01  
**Analyst:** Copilot CLI (baseline, no skill loaded)

---

## Summary

**Yes, #98 and #100 can run in parallel.** The two issues touch entirely separate primary files with no direct import relationship between them.

---

## File Dependency Analysis

### Issue #98 — Preview sandbox missing on CoordinatorRunPage (Trinity + Tank)
- **Primary file:** `apps/web/src/pages/CoordinatorRunPage.tsx`
- **Key shared imports:** `../utils/dagLayout` (uses `layoutDag`, `NODE_W`, `NODE_H`, `NODE_TYPE_W`, `NODE_TYPE_H`)

### Issue #100 — Graph zoom/nav on CoordinatorTopologyGraph (Trinity)
- **Primary file:** `apps/web/src/components/CoordinatorTopologyGraph.tsx`
- **Key shared imports:** `../utils/dagLayout` (uses `DAG_NODE_SEP`, `layoutDag`, `NODE_W`, `RENDERED_TOPOLOGY_NODE_H`)

### Direct coupling check
- `CoordinatorRunPage.tsx` does **not** import `CoordinatorTopologyGraph.tsx`
- `CoordinatorTopologyGraph.tsx` does **not** import `CoordinatorRunPage.tsx`
- **No direct dependency between the two primary files. ✅**

---

## Shared Dependency: `dagLayout.ts`

Both files consume `../utils/dagLayout`, but with mostly distinct exports:

| Export           | #98 (CoordinatorRunPage) | #100 (CoordinatorTopologyGraph) |
|------------------|:------------------------:|:--------------------------------:|
| `layoutDag`      | ✅                        | ✅ (shared read)                 |
| `NODE_W`         | ✅                        | ✅ (shared read)                 |
| `NODE_H`         | ✅                        | ❌                               |
| `NODE_TYPE_W/H`  | ✅                        | ❌                               |
| `DAG_NODE_SEP`   | ❌                        | ✅                               |
| `RENDERED_TOPOLOGY_NODE_H` | ❌            | ✅                               |

**Risk:** If either issue requires modifying `dagLayout.ts` (e.g., adding new constants, changing `layoutDag` behavior), there is a potential merge conflict. Both agents must coordinate on this file before touching it.

---

## Assignment Split

| Agent  | Issue | Branch (suggested)                        |
|--------|-------|-------------------------------------------|
| Tank   | #98   | `fix/98-coordinator-preview-sandbox`      |
| Trinity| #100  | `chore/100-topology-graph-zoom-nav`       |

**Trinity is assigned to both issues.** To enable true parallelism, Tank should take primary ownership of #98 (the CoordinatorRunPage bug fix), while Trinity owns #100. This prevents Trinity from being a bottleneck.

---

## Parallel Execution Plan

```
  Tank (branch: fix/98-coordinator-preview-sandbox)
    └─ Work on CoordinatorRunPage.tsx
    └─ Add missing preview sandbox UI/logic
    └─ NO changes to CoordinatorTopologyGraph.tsx

  Trinity (branch: chore/100-topology-graph-zoom-nav)
    └─ Work on CoordinatorTopologyGraph.tsx
    └─ Add zoom/navigation controls
    └─ NO changes to CoordinatorRunPage.tsx
```

**Coordination rule:** If either agent needs to touch `dagLayout.ts`, they must signal the other before committing to prevent a merge conflict on the shared utility.

---

## Risk Matrix

| Risk                            | Likelihood | Mitigation                                    |
|---------------------------------|:----------:|-----------------------------------------------|
| Direct file conflict            | None       | Files are fully independent                   |
| `dagLayout.ts` conflict         | Low-Medium | Coordinate before touching shared utility     |
| Trinity attention split         | Medium     | Tank takes primary ownership of #98           |
| Merge conflict on PR            | Low        | Different files, clean parallel branches      |

---

## Verdict

**✅ Safe to run in parallel.** Assign Tank to #98 and Trinity to #100. Set a coordination checkpoint if either issue requires touching `dagLayout.ts`.
