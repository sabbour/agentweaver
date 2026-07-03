# Squad Coordinator Response — Bug-First Sprint
**Squad v0.10.0**
💡 Say "squad commands" to see what I can do.

---

Hey Ahmed! Backlog scanned. 3 bugs in the queue, all tagged `go:yes`. Chores go on hold — bugs first.

---

## 🐛 Bug Triage

| # | Issue | Owner(s) | Priority |
|---|-------|----------|----------|
| #95 | confirm button does not disable after click | ⚛️ Trinity | P2 — simple fix |
| #99 | preview sandbox button hidden for completed runs | ⚛️ Trinity | P2 — gate condition |
| #98 | preview sandbox missing on orchestration run page | 🧪 Smith (RCA) + 🔧 Tank (API review) + ⚛️ Trinity (frontend) | P2 — multi-agent |

---

## 🚀 Wave 1 — Launching Now (Parallel)

Three streams kick off simultaneously:

```
⚛️ Trinity   — #95 + #99   Fix confirm button loading state & preview gate condition (WorkflowRunPage.tsx)
🧪 Smith     — #98 (RCA)   Verify POST /runs/{runId}/sandbox/port-forward accepts orchestration runIds
🔧 Tank      — #98 (API)   Confirm port-forward endpoint is run-type agnostic; review backend contract
```

### ⚛️ Trinity — Issues #95 & #99

**Branch:** `fix/trinity-run-page-bugs` (worktree: `.worktrees/trinity-run-page-bugs`)

**#95 — Confirm button (`apps/web/src/pages/WorkflowRunPage.tsx`):**
- Locate the Confirm button's click handler
- Add `isConfirming` state (useState<boolean>)
- Set `isConfirming = true` immediately on click; reset on response (success or error)
- Gate `disabled` and `loading` props on `isConfirming`
- Acceptance: button becomes disabled/loading on first click, re-enables only after server responds

**#99 — Preview sandbox gate (`apps/web/src/pages/WorkflowRunPage.tsx` line ~836):**
- Current gate: `isKubernetesSandbox && (runActive || !!previewSession)`
- Fix (Option A): Change to `isKubernetesSandbox` — always show button for k8s sandbox runs
- Fix (Option B — preferred): Show always; when `!runActive && !previewSession`, show disabled with tooltip "Run is complete — sandbox may still be accessible"
- Acceptance: Preview button persists after run completes and after page reload

**Done when:** both fixes implemented, existing tests pass, PR opened referencing #95 and #99.

---

### 🧪 Smith — Issue #98 (RCA)

**Branch:** `fix/smith-98-rca` (read-only investigation, no code changes)

**Task:** Confirm whether `POST /runs/{runId}/sandbox/port-forward` (implemented in `apps/web/src/api/client.ts` lines 741–756) accepts orchestration run IDs the same as workflow run IDs.

1. Inspect the backend route handler for `/runs/{runId}/sandbox/port-forward`
2. Check if there's a run-type discriminator that would reject orchestration runIds
3. Confirm the API is (or isn't) agnostic to run type
4. Document RCA finding as a comment on issue #98
5. Flag to Coordinator: ✅ API works OR ❌ API needs backend fix first

**Done when:** RCA comment posted on #98; go/no-go signal returned to Coordinator.

---

### 🔧 Tank — Issue #98 (Backend Review)

**Branch:** `fix/tank-98-backend-review` (read-only review, no code changes unless RCA finds a gap)

**Task:** Confirm the port-forward endpoint is run-type agnostic.

1. Read the backend route for `POST /runs/{runId}/sandbox/port-forward`
2. Verify it does not filter/reject orchestration runIds
3. If a gap is found, implement the fix (minimal — remove discriminator or broaden the query)
4. Comment on issue #98 with review outcome and any fix details
5. Flag to Coordinator: ✅ no backend work needed OR ❌ fix committed, ready for Trinity

**Done when:** backend confirmed agnostic (or fixed); signal returned to Coordinator.

---

## ⏸️ Wave 2 — Queued (Pending Wave 1)

Once Smith + Tank return their #98 signals AND Trinity finishes Wave 1:

```
⚛️ Trinity   — #98 (frontend)   Port Preview button + dialog from WorkflowRunPage into CoordinatorRunPage.tsx
📋 Scribe    — session log      Merge decisions, log Wave 1 outcomes
🛡️ Rai       — pre-merge        RAI review before any PR merges
```

**Trinity's #98 frontend work is gated on Smith's RCA confirming the API is safe to use.** If Tank finds a backend gap, Trinity waits for Tank's fix to land first.

---

## 🗂️ Chores — Deferred Until Bugs Clear

| # | Issue | Owner | Status |
|---|-------|-------|--------|
| #100 | graph-view: zoom, navigation, scroll indicator | ⚛️ Trinity | ⏸️ queued after bugs |
| #101 | docs: block-beta architecture diagrams | 📋 Scribe / ⚛️ Trinity | ⏸️ queued after bugs |

---

## 📊 Status Board

| Agent | Current Work | State |
|-------|-------------|-------|
| ⚛️ Trinity | #95 + #99 (WorkflowRunPage fixes) | 🟡 In Progress |
| 🧪 Smith | #98 RCA | 🟡 In Progress |
| 🔧 Tank | #98 backend review | 🟡 In Progress |
| 📋 Scribe | Standing by for Wave 1 completion | ⏸️ Waiting |
| 🛡️ Rai | Background | ⏸️ Waiting |
| 🔗 Link | No active work | 💤 Idle |
| 🔮 Morpheus | No active work | 💤 Idle |

---

## 📝 Coordinator Notes

- **#98 has a hard dependency tree:** Smith RCA → (Tank backend if needed) → Trinity frontend. Trinity cannot open the CoordinatorRunPage PR until the API is confirmed safe.
- **#95 and #99** are fully independent of #98 and can merge whenever Trinity finishes them.
- All PRs should reference their issue numbers (`Closes #95`, `Closes #99`, `Closes #98`).
- I'll update you when Wave 1 agents report back. If anything blocks, I'll surface it immediately.

---

*Scribe will log this dispatch to `.squad/orchestration-log/` once Wave 1 completes.*
