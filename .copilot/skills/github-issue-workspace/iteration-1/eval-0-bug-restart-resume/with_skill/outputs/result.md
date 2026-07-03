# Simulated GitHub Issue Filing

## Classification

- **Type**: Bug
- **Domain**: MAF / run orchestration / recovery → `squad:morpheus` (primary); API backend → `squad:tank` (secondary domain signal, trigger source)
- **Co-assignee**: `squad:smith` (always added for bugs — leads RCA)
- **`go:` label**: `go:yes` — clearly reproducible bug, enough context to start immediately
- **Priority**: `priority:p1` — significant UX regression, data loss perception; not a p0 (no production outage, workaround exists: start new task)
- **Release**: `release:backlog`

---

## `gh` Command

```bash
gh issue create \
  --title "Bug: API pod restart permanently breaks runs in awaiting_confirmation state" \
  --label "type:bug,squad:morpheus,squad:tank,squad:smith,go:yes,priority:p1,release:backlog" \
  --body "## Summary
API pod restart leaves runs in \`awaiting_confirmation\` permanently stuck — the user cannot confirm or resume the run even though MAF has serialized all state to disk.

## Run / context
https://agentweaver.example.io/projects/abc123/orchestrations/run-xyz

## Steps to reproduce
1. Start a run that reaches the \`awaiting_confirmation\` state (paused, waiting for user to confirm next step).
2. Restart the API pod (e.g., rolling deployment, pod eviction, OOMKill, or crash-restart).
3. Return to the run UI and attempt to confirm.

## Expected behavior
The run should be resumable after the API pod restarts. MAF serializes all run state to disk, so the \`awaiting_confirmation\` checkpoint should survive a pod restart and allow the user to confirm and continue the orchestration normally.

## Actual behavior
The user sees:

> \"This run was interrupted and can no longer be confirmed. Please start a new task.\"

The run is permanently stuck in a terminal error state and cannot be confirmed, resumed, or retried. All in-flight progress is lost from the user's perspective.

## Technical notes
- MAF (Multi-Agent Framework) serializes run state to disk — the data is not lost at the storage layer.
- The bug is likely in how the API layer restores or re-hydrates run state after a pod restart: the \`awaiting_confirmation\` state transition may not be correctly recovered from persistent storage on startup.
- Possible root causes to investigate:
  - In-memory confirmation token or session handle not re-created from the persisted snapshot on pod boot.
  - API handler for the confirmation endpoint checking a volatile (non-persisted) flag that gets cleared on restart.
  - Race condition between state recovery and the first incoming confirmation request post-restart.
- This is an unacceptable UX regression — users lose in-flight orchestration work on any routine pod restart (deployments, scaling events, etc.).

## Reported by
@sabbour — 2026-07-01"
```

---

## Full Issue Body Text

```markdown
## Summary
API pod restart leaves runs in `awaiting_confirmation` permanently stuck — the user cannot confirm or resume the run even though MAF has serialized all state to disk.

## Run / context
https://agentweaver.example.io/projects/abc123/orchestrations/run-xyz

## Steps to reproduce
1. Start a run that reaches the `awaiting_confirmation` state (paused, waiting for user to confirm next step).
2. Restart the API pod (e.g., rolling deployment, pod eviction, OOMKill, or crash-restart).
3. Return to the run UI and attempt to confirm.

## Expected behavior
The run should be resumable after the API pod restarts. MAF serializes all run state to disk, so the `awaiting_confirmation` checkpoint should survive a pod restart and allow the user to confirm and continue the orchestration normally.

## Actual behavior
The user sees:

> "This run was interrupted and can no longer be confirmed. Please start a new task."

The run is permanently stuck in a terminal error state and cannot be confirmed, resumed, or retried. All in-flight progress is lost from the user's perspective.

## Technical notes
- MAF (Multi-Agent Framework) serializes run state to disk — the data is not lost at the storage layer.
- The bug is likely in how the API layer restores or re-hydrates run state after a pod restart: the `awaiting_confirmation` state transition may not be correctly recovered from persistent storage on startup.
- Possible root causes to investigate:
  - In-memory confirmation token or session handle not re-created from the persisted snapshot on pod boot.
  - API handler for the confirmation endpoint checking a volatile (non-persisted) flag that gets cleared on restart.
  - Race condition between state recovery and the first incoming confirmation request post-restart.
- This is an unacceptable UX regression — users lose in-flight orchestration work on any routine pod restart (deployments, scaling events, etc.).

## Reported by
@sabbour — 2026-07-01
```

---

## Simulated `gh` Output

```
https://github.com/asabbour/agentweaver/issues/42
```

---

## Dispatch Block

```
✅ Filed #42: Bug: API pod restart permanently breaks runs in awaiting_confirmation state
   https://github.com/asabbour/agentweaver/issues/42

📋 Dispatch:
   squad:smith   — Lead RCA: trace the awaiting_confirmation→stuck transition after pod restart;
                   identify whether the failure is in state re-hydration, confirmation token
                   reconstruction, or the API handler's volatile flag logic. Report findings on #42.
   squad:morpheus — Own the fix: once Smith's RCA identifies the layer, implement run-state
                   recovery so awaiting_confirmation runs survive pod restarts and remain
                   confirmable. Verify MAF's on-disk snapshot is correctly reloaded on startup.
   squad:tank    — Supporting: review the confirmation API endpoint for any in-process/volatile
                   state that is not re-seeded from persistent storage after a pod restart.

🏷️  Labels: type:bug, squad:morpheus, squad:tank, squad:smith, go:yes, priority:p1, release:backlog
```
