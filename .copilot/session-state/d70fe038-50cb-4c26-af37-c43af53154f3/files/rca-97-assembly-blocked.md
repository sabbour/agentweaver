# RCA #97 — `assembly_blocked: ineligible_subtasks`

## Executive summary

`ineligible_subtasks` is a pure **status gate**, not a git/worktree/lock gate. Collective assembly only starts when **every** subtask status is `assemble_ready` or `completed`. Any other status blocks the whole plan (`pending`, `dispatched`, `running`, `pending_capacity`, `rai_flagged`, `failed`, `blocked`, or any future non-whitelisted value). The block is emitted from `CoordinatorAssemblyService.RunAssemblyCoreAsync()` before any integration-branch work starts, so missing branches/worktrees and ref locks are **not** what make a subtask “ineligible.”  

The orchestration then parks in `assembly_blocked` and waits for steering; it does **not** auto-retry eligibility failures. A manual `send` can force a rerun of assembly, but for `ineligible_subtasks` it reruns against the **same persisted subtask statuses**, so it usually blocks again. A page reload can show the opaque UI fallback because the detailed `coordinator.assembly_blocked` event is only persisted when the stream later completes; while the run is still parked, the UI may have only `workPlan.status=assembly_blocked` and no reason/detail payload.

---

## 1) What makes a subtask “ineligible”?

### Exact predicate

The predicate is in `AssemblyPlanning.IsEligible()`:

- eligible: `assemble_ready`, `completed`
- ineligible: **everything else**

Code:

- `apps/Agentweaver.Api/Coordinator/AssemblyPlanning.cs:14-37`
- `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:7-32`

```csharp
public static bool IsEligible(string status) =>
    status is SubtaskStatus.AssembleReady or SubtaskStatus.Completed;
```

`RunAssemblyCoreAsync()` builds `statusById`, calls `AssemblyPlanning.IneligibleSubtasks(statusById)`, and immediately blocks if the returned list is non-empty:

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:556-585`

That means `ineligible_subtasks` is caused by **subtask status only**.

### What statuses can cause it?

From the codebase:

- `pending`, `dispatched`, `running`, `pending_capacity`, `rai_flagged`, `failed`, `blocked`
  - `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:9-32`

Important nuance: `AssemblyPlanning` uses a strict allowlist, so even statuses not named in its XML comment are still ineligible if they are not `assemble_ready`/`completed`. That includes `pending_capacity` and `blocked`.

### What puts subtasks into those statuses?

- normal child terminal mapping:
  - `assemble_ready`, `rai_flagged`, `completed`, `failed`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:640-660`
- stalled child causes upstream subtask `failed`, dependents `blocked`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:373-379`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:761-815`
- failed / RAI-flagged child causes dependents to become `failed`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:656-660`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:761-815`
- cluster capacity exhaustion parks subtask in `pending_capacity`, then eventually `failed`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:946-1017`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1035-1067`

### What does **not** make a subtask ineligible?

Not:

- missing worktree branch
- missing diff
- git lock conflict
- merge conflict

Those are handled later, after the eligibility gate:

- branch/diff are only used to decide whether a subtask contributes a branch to collective integration
  - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:701-729`
- integration branch git failures are retried in `BuildIntegrationBranchWithRetryAsync()`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1247-1285`
- final repository merge uses `RepositoryMergeLock`
  - `apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:110-141`

So for #97, “ineligible” means **the subtask statuses were not all ready/no-op**, not that git state was missing.

---

## 2) Is this transient or permanent?

## Short answer

For `failed`, `rai_flagged`, and `blocked`: effectively **permanent without user intervention**.  
For `pending`/`dispatched`/`running`/`pending_capacity`: potentially transient in theory, but **the parked assembly loop does not watch subtasks and does not auto-recover**, so in practice it also stays stuck until steering or another actor changes plan state.

## Why

When assembly hits the gate, `BlockAsync()`:

1. sets `WorkPlan.Status = assembly_blocked`
2. emits `coordinator.assembly_blocked`
3. waits for steering input

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1287-1305`

The blocked wait loop only watches:

- terminal coordinator run status
- whether the work plan status changed away from `assembly_blocked`
- new steering directives

It does **not** re-read subtask statuses and does **not** poll for eligibility becoming true:

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1683-1721`

After restart/re-arm, `RunAssemblyAsync()` does not recompute eligibility either if the persisted plan is already `assembly_blocked`; it simply re-enters the blocked wait:

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:479-483`
- `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:912-917`
- `apps/Agentweaver.Api/Coordinator/CoordinatorReconciler.cs:349-367`

So once the plan is parked at `assembly_blocked`, it stays there until:

- operator sends `redirect`/`amend`/`stop`, or
- operator sends `send` (manual assembly retry), or
- steering timeout terminalizes the run

The timeout path is:

- config default `Coordinator:AssemblyBlockedSteeringTimeoutMinutes = 10`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:139-142`
- timeout terminalization:
  - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1672-1679`

---

## 3) Where is `assembly_blocked` set?

## Call chain

1. dispatch finishes and hands off to assembly  
   - `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:463-517`
2. `CoordinatorAssemblyService.StartAssembly()` launches background assembly  
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:211-245`
3. `RunAssemblyAsync()` loads the plan and calls `RunAssemblyCoreAsync()`  
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:465-508`
4. `RunAssemblyCoreAsync()` runs eligibility gate  
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:556-585`
5. on failure, `BlockAsync()` sets `WorkPlanStatus.AssemblyBlocked` and emits `coordinator.assembly_blocked`  
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1287-1305`

Event constant:

- `packages/Agentweaver.Domain/EventTypes.cs:331-334`

Persisted work-plan status constant:

- `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1973-1977`

---

## 4) Why no retry?

## What retry exists today

There are two different retry mechanisms today:

1. **integration-branch build retry**  
   Only for exceptions during integration build, capped at 3 attempts with cleanup/backoff.
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1247-1285`

2. **manual blocked-assembly retry via steering `send`**  
   If a new steering directive of kind `send` appears while parked, the blocked loop returns `RetryAssembly`.
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1702-1708`

That path then does:

- set plan back to `awaiting_assembly`
- reload subtasks
- rerun `RunAssemblyCoreAsync()`

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1654-1664`

## Why that does not help `ineligible_subtasks`

The `send` retry does **not** mutate subtasks. It re-runs assembly against the same persisted statuses. So if the block reason is `ineligible_subtasks`, retrying assembly alone normally reproduces the same gate failure.

By contrast, `redirect` recovery resets affected subtasks back to `pending`, bumps recovery attempts, flips the plan back to `dispatching`, and re-arms dispatch:

- `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs:606-767`

That is why the current design requires **user intervention** for ineligible subtasks: the system has no automatic rule that decides whether it should re-dispatch failed/flagged/blocked subtasks, and no automatic eligibility recheck loop.

## What Tank should implement

### Retry recommendation

Split eligibility failures into two buckets:

1. **transient ineligible statuses**  
   `pending`, `dispatched`, `running`, `pending_capacity`
2. **terminal/manual-recovery statuses**  
   `failed`, `rai_flagged`, `blocked`

Then:

- if **all** ineligible subtasks are transient, do **automatic bounded recheck/retry**
  - keep plan in a recoverable non-terminal state
  - reload subtasks on a timer/backoff
  - rerun eligibility gate
  - cap attempts and then fall back to `assembly_blocked`
- if **any** ineligible subtask is terminal/manual-recovery, skip auto-retry and surface actionable recovery UI immediately

### Concrete implementation sketch

Add a dedicated helper in `CoordinatorAssemblyService` around the D2 gate:

1. compute ineligible subtasks
2. classify by status
3. if transient-only:
   - sleep/backoff
   - reload subtasks from DB
   - retry gate up to N attempts
4. else:
   - block immediately

Persist retry count either:

- on `WorkPlan` (preferred, restart-safe), or
- in a dedicated assembly-retry table / durable record

Suggested defaults:

- max attempts: 3-5
- exponential backoff similar to integration build retry
- new config key, e.g. `Coordinator:AssemblyEligibilityRetryMaxAttempts`

For terminal/manual-recovery statuses, keep using the existing steering recovery path and its existing cap:

- `CoordinatorSteeringService.MaxRecoveryAttempts = 3`
  - `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs:606-607`

---

## 5) Why does the UI show “The collective assembly could not complete.”?

The string comes from:

- `apps/web/src/pages/CoordinatorRunPage.tsx:193-209`

```ts
default:
  return reason ? `The collective assembly stopped: ${reason}.`
                : 'The collective assembly could not complete.';
```

## Why the generic fallback happens

The UI derives orchestration state in this order:

1. latest live `coordinator.assembly_*` event
2. `coordinator_status` / `coordinator_status_reason`
3. work-plan status only

- `apps/web/src/pages/CoordinatorRunPage.tsx:346-375`

If the UI only has `workPlan.status = assembly_blocked` and **no reason**, `deriveOrchState()` returns `{ phase: 'blocked' }`, and `friendlyAssemblyReason(undefined)` produces the generic fallback.

Why can reason/detail be missing? Because blocked assembly events are only durably persisted when the stream completes:

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1474-1525`

While the run is parked waiting for steering, the detailed `coordinator.assembly_blocked` payload can exist only in the evictable in-memory stream. On reload / reconnect / different replica, the UI may lose:

- `reason`
- `ineligibleSubtaskIds`
- `ineligibleSubtasks`

and fall back to the generic message.

## Additional surfacing gaps

1. `friendlyAssemblyReason()` normalizes only the prefix `assembly_blocked:`.  
   If the run result is `assembly_blocked: ineligible_subtasks [59,60,61,62]`, it will not match the `ineligible_subtasks` case exactly.
   - `apps/web/src/pages/CoordinatorRunPage.tsx:193-209`

2. The best structured data (`ineligibleSubtasks`, `ineligibleSubtaskIds`) exists only in the event payload, not in a durable API field on the parked work plan / run detail.
   - event read path: `apps/web/src/pages/CoordinatorRunPage.tsx:221-255`
   - blocked event emit path: `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:561-583`

## UI fix recommendation

Tank should implement both:

### A. Persist blocked reason/details immediately

On `BlockAsync()`:

- persist the `coordinator.assembly_blocked` event immediately, or
- persist structured blocked metadata on `WorkPlan` (reason code + ids + detail rows), or
- expose it from a dedicated API field

This removes dependence on the live in-memory stream for blocked-state diagnosis.

### B. Surface structured info, not parsed strings

Expose and render:

- reason code: `ineligible_subtasks`
- subtask ids: `[59,60,61,62]`
- enriched rows: title, agent, status, recoveryGuidance

And in UI:

- strip the `[ids]` suffix when mapping reason code to friendly text, or
- stop parsing `result` strings entirely and use structured reason fields

---

## Root cause

The root cause is a design gap between the **strict no-partial eligibility gate** and the **recovery model**:

1. assembly eligibility is strict allowlist-only (`assemble_ready` / `completed`)
2. `ineligible_subtasks` blocks before any git work begins
3. blocked assembly waits only for steering, not for subtask-state recovery
4. manual `send` retries assembly without changing subtask state
5. blocked detail is not durably surfaced while the run is parked

Result: the run appears permanently halted, and the UI can degrade to a generic message if the live blocked event payload is unavailable.

---

## Recommended fix summary for Tank

1. **Add bounded auto-retry for transient eligibility states only**
   - recheck/reload subtasks
   - backoff + cap
   - do not auto-retry terminal statuses

2. **Keep manual recovery for failed/RAI-flagged/blocked subtasks**
   - continue using `redirect` recovery
   - honor `MaxRecoveryAttempts`

3. **Persist blocked metadata immediately**
   - reason code
   - subtask ids
   - enriched subtask detail rows

4. **Improve UI reason handling**
   - render structured blocked details from durable data
   - normalize `ineligible_subtasks [ids]`
   - never fall back to the generic message when blocked detail exists

