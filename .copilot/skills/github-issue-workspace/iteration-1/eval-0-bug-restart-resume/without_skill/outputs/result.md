# Simulated GitHub Issue Filing

## gh Command

```bash
gh issue create \
  --repo sabbour/agentweaver \
  --title "Bug: Runs stuck in 'awaiting_confirmation' after API pod restart are not resumable" \
  --label "bug,run-lifecycle,resumability" \
  --body "$(cat issue-body.md)"
```

---

## Issue Body

**Title:** Bug: Runs stuck in `awaiting_confirmation` after API pod restart are not resumable

**Labels:** `bug`, `run-lifecycle`, `resumability`

**Repo:** `sabbour/agentweaver`

---

### Summary

When the API pod restarts, runs that were in the `awaiting_confirmation` state become permanently stuck. The user sees:

> _"This run was interrupted and can no longer be confirmed. Please start a new task."_

This is unacceptable UX. Since MAF serializes all run state to disk, the run should be fully resumable after a pod restart.

---

### Steps to Reproduce

1. Start a run that reaches the `awaiting_confirmation` state (e.g., a Coordinator has paused and is waiting for user confirmation before proceeding).
2. Restart the API pod while the run is in that state.
3. Attempt to confirm the run via the UI or API.

**Expected:** The run resumes from `awaiting_confirmation` as if nothing happened — state is rehydrated from disk and the confirmation is accepted.

**Actual:** The run is marked as non-resumable and the user sees the error message above.

---

### Affected Run

- **Run URL:** https://agentweaver.example.io/projects/abc123/orchestrations/run-xyz
- **State at time of pod restart:** `awaiting_confirmation`

---

### Root Cause Hypothesis

The `awaiting_confirmation` state is likely held in-memory (e.g., in a dictionary, a `CancellationTokenSource`, or a `TaskCompletionSource` keyed by run ID) rather than being fully rehydrated from the MAF disk store on pod startup. When the pod restarts:

1. The in-memory confirmation handle is lost.
2. Incoming confirmation requests fail to find a matching pending handle.
3. The system falls through to an error path that marks the run as non-resumable instead of attempting rehydration.

---

### Expected Behavior

On pod startup, the orchestration host should:
1. Scan disk for runs in `awaiting_confirmation` (and any other interruptible states).
2. Re-register the confirmation handles so that subsequent user confirmations can proceed.
3. Return the run to normal operation — no data loss, no forced restart.

---

### Impact

- **Severity:** High — any pod restart (rolling update, OOM kill, node eviction) silently destroys in-progress user work.
- **User impact:** User must discard work and restart their task from scratch.
- **Frequency:** Every pod restart affects all concurrent runs in `awaiting_confirmation`.

---

### Additional Context

- MAF (Multi-Agent Framework) serializes run state to disk, so the data exists — the issue is purely in state rehydration at startup.
- This may also affect other transient states that rely on in-memory coordination (e.g., `awaiting_tool_result`, `paused`).
- Related area: orchestration lifecycle management, pod startup/recovery logic.

---

### Dispatch Notes

**Suggested assignees / areas to investigate:**

| Area | Notes |
|------|-------|
| Orchestration host startup | Look for the recovery/rehydration path on `IOrchestrationHost` or equivalent startup hook |
| `awaiting_confirmation` handler | Find where confirmation handles are registered in memory and ensure they are re-registered on rehydration |
| MAF disk store | Confirm that `awaiting_confirmation` state is written before the pod reaches the point where it could be interrupted |
| Error path | The "can no longer be confirmed" message should be a last resort, not triggered by a missing in-memory handle |

**Priority:** P1 — fix before next production deployment.
