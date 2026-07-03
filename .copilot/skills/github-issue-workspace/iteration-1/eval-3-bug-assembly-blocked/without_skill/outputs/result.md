# Simulated GitHub Issue

**Simulation note:** `gh` was not run. The following is the issue that would be filed.

---

## Issue Title

`bug(orchestration): collective assembly blocked with ineligible_subtasks error`

## Labels

- `type:bug`
- `release:backlog`

## Body

### Summary

A collective assembly stopped unexpectedly with the error `assembly_blocked: ineligible_subtasks [59,60,61,62]`. The orchestration did not complete and left subtasks 59, 60, 61, and 62 in an ineligible state, halting the entire run.

**Failing run:**
https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/projects/f88ff808-2efa-428e-8f6a-9e67a6296b70/orchestrations/2afef9f5-109f-4310-9fea-00db92f67ac7

### Expected behavior

The collective assembly should complete successfully, or — if individual subtasks are ineligible — it should skip or gracefully degrade around them rather than blocking and halting the entire orchestration.

### Current behavior

The orchestration terminated with:

```
assembly_blocked: ineligible_subtasks [59,60,61,62]
```

Subtasks 59, 60, 61, and 62 were flagged as ineligible, causing the assembly step to block. No further progress was made on the orchestration.

### Steps to reproduce / trigger

1. Open the failing orchestration run linked above.
2. Observe the orchestration status — it is stopped at the assembly phase.
3. The error message `assembly_blocked: ineligible_subtasks [59,60,61,62]` is visible in the run log.

### Impact / severity

`P1`

Orchestrations cannot complete when multiple subtasks are flagged ineligible simultaneously; the entire collective assembly is blocked rather than only the affected subtasks.

### Affected area

- Orchestration assembly / collective-assembly logic
- Subtask eligibility evaluation
- Project: `f88ff808-2efa-428e-8f6a-9e67a6296b70`
- Orchestration: `2afef9f5-109f-4310-9fea-00db92f67ac7`

### Environment

Staging — `westus2.staging.aksapp.io`

---

## Reasoning / approach taken

Without the `github` skill loaded, this issue was composed manually based on:

1. **Error message parsed:** `assembly_blocked: ineligible_subtasks [59,60,61,62]` — the assembly phase blocks when any subtask is ineligible, rather than skipping/degrading.
2. **Bug template followed:** Used `.github/ISSUE_TEMPLATE/bug.yml` fields (Summary, Expected behavior, Current behavior, Steps to reproduce, Impact/severity, Affected area, Environment).
3. **Labels applied:** `type:bug` and `release:backlog` per the template defaults.
4. **Severity set to P1:** A full orchestration block (not a partial failure) in staging warrants high priority.
5. **`gh` not executed** per task constraints — output is a simulation only.
