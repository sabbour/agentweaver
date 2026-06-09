# Design: MAF Workflow-Native HITL + No-Changes Skip

| Field | Value |
|-------|-------|
| Author | Morpheus (Runtime Engineer) |
| Status | Draft |
| Date | 2026-06-08 |
| Spec | specs/001-single-agent-run/spec.md |
| Package | Microsoft.Agents.AI.Workflows 1.9.0 |

---

## 1. Workflow Shape

### Executors

| Executor | Input | Output | File |
|----------|-------|--------|------|
| `AgentTurnExecutor` | `AgentTurnInput` | `AgentTurnOutput` | `packages/Scaffolder.AgentRuntime/Workflow/AgentTurnExecutor.cs` |
| `MergeExecutor` | `MergeInput` | `MergeOutput` | `packages/Scaffolder.AgentRuntime/Workflow/MergeExecutor.cs` |

### RequestPort

```csharp
RequestPort.Create<ReviewRequest, ReviewDecision>("review-gate")
```

### Message Types (JSON-serializable, checkpoint-safe)

```csharp
public sealed record AgentTurnInput(
    string RunId,
    string Task,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch,
    string ModelSource);

public sealed record AgentTurnOutput(
    string RunId,
    string TreeHash,
    string Diff,           // empty string when no changes
    int StepCount,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch);

public sealed record ReviewRequest(
    string RunId,
    string TreeHash,
    string Diff,
    int StepCount);

public sealed record ReviewDecision(bool Approved);

public sealed record MergeInput(
    string RunId,
    string TreeHash,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch);

public sealed record MergeOutput(string RunId, string Status, string? MergeResult);
```

### Graph Edges

```
AgentTurnExecutor
  |
  |--[diff is empty]--> Terminal output: CompletedNoOp
  |
  |--[diff non-empty]--> RequestPort("review-gate")
                              |
                              |--[Approved=true]--> MergeExecutor --> Terminal output
                              |--[Approved=false]--> Terminal output: Declined
```

Built with:

```csharp
var reviewPort = RequestPort.Create<ReviewRequest, ReviewDecision>("review-gate");

var wf = new WorkflowBuilder(agentTurnExecutor)
    .AddEdge(agentTurnExecutor, reviewPort,
        condition: output => !string.IsNullOrEmpty(((AgentTurnOutput)output).Diff))
    .AddEdge(agentTurnExecutor, terminalNoOp,
        condition: output => string.IsNullOrEmpty(((AgentTurnOutput)output).Diff))
    .AddEdge(reviewPort, mergeExecutor,
        condition: response => ((ReviewDecision)response).Approved)
    .AddEdge(reviewPort, terminalDeclined,
        condition: response => !((ReviewDecision)response).Approved)
    .WithOutputFrom(mergeExecutor)
    .WithOutputFrom(terminalNoOp)
    .WithOutputFrom(terminalDeclined)
    .Build();
```

> Note: The exact conditional-edge API may use `AddConditionalEdge` or a predicate overload. Confirm against the 1.9.0 API before implementation. The semantic intent is clear; adapt syntax to actual API surface.

---

## 2. Event Bridging Decision

### Recommendation: HYBRID (Option A)

The agent token/delta stream continues to flow through the existing `RunStreamEntry` / `RecordingChannelWriter` side-channel. The MAF workflow owns **only** the lifecycle, HITL gate, checkpointing, and merge orchestration.

### Rationale

| Criterion | Hybrid (recommended) | Full-workflow routing |
|-----------|---------------------|---------------------|
| Checkpoint bloat | Minimal: only 2-3 checkpoints per run (post-agent, post-review, post-merge) | Every token delta would generate a checkpoint or bloat the workflow state |
| Latency | Zero added latency on token stream; events written directly to in-memory list | Each event would traverse workflow event routing |
| Complexity | Low: workflow receives only structured lifecycle outputs | High: must define a streaming executor that forwards thousands of events |
| SSE compatibility | Unchanged: existing poll loop on RunStreamEntry works as-is | Requires new event-to-SSE adapter |
| Determinism constraint | Agent turn is non-replayable; checkpoint is taken AFTER it completes | Same constraint either way |

### Integration Point

`AgentTurnExecutor.ExecuteAsync` internally:
1. Creates `RecordingChannelWriter(entry)` where `entry` is the existing `RunStreamEntry` obtained from `RunStreamStore`.
2. Calls `IAgentRunner.ExecuteAsync(...)` — tokens stream to SSE via the existing path.
3. On return, commits worktree, computes diff, returns `AgentTurnOutput`.

The workflow runtime sees only the structured `AgentTurnOutput` at the edge boundary. Token events are invisible to MAF.

---

## 3. Checkpoint Store

### Recommendation: `FileSystemJsonCheckpointStore` (built-in)

### Rationale

| Factor | FileSystemJsonCheckpointStore | Custom SQLite ICheckpointStore |
|--------|-------------------------------|-------------------------------|
| Implementation cost | Zero — ships with 1.9.0 | Must implement `ICheckpointStore<JsonElement>` (3 methods) |
| Durability | JSON file per run; survives process restart | Same durability |
| Single-source-of-truth conflict | None — SQLite remains the source of truth for **run status**; checkpoint store holds **workflow resumption data** only | Mixing checkpoint blobs into the runs DB complicates schema and row-level locking |
| Disk layout | `{DataDirectory}/checkpoints/{runId}/{stepN}.json` | N/A |
| Query need | Checkpoints are never queried by users or the API; only used for restart resume | Overhead of SQLite query for a blob-read is unnecessary |
| Cleanup | Delete checkpoint directory on run terminal transition | DELETE FROM checkpoints WHERE run_id = ? |

SQLite remains the queryable source of truth for run status (used by GET /api/runs, list queries, status filtering). The checkpoint store is an implementation detail of the workflow runtime, never exposed to API consumers.

### Configuration

```csharp
var checkpointStore = new FileSystemJsonCheckpointStore(
    Path.Combine(AppPaths.DataDirectory, "checkpoints"));
var checkpointManager = new CheckpointManager(checkpointStore);
```

---

## 4. Live Run Registry + Event Translation

### RunWorkflowRegistry (new class)

File: `apps/Scaffolder.Api/Runs/RunWorkflowRegistry.cs`

```csharp
public sealed class RunWorkflowRegistry
{
    // Key: runId (string), Value: active StreamingRun
    private readonly ConcurrentDictionary<string, StreamingRun> _runs = new();

    public void Register(string runId, StreamingRun run);
    public StreamingRun? Get(string runId);
    public void Remove(string runId);
}
```

### Event Watch Loop

After calling `InProcessExecution.RunStreamingAsync(...)`:

```csharp
var streamingRun = await InProcessExecution.RunStreamingAsync(workflow, input, checkpointManager, runId, ct);
registry.Register(runId, streamingRun);

await foreach (var evt in streamingRun.WatchStreamAsync(ct))
{
    switch (evt)
    {
        case RequestInfoEvent rie:
            // Workflow paused on review-gate.
            // Store the ExternalRequest for later SendResponseAsync.
            pendingRequests.AddOrUpdate(runId, rie.Request);
            // Emit SSE event (review.requested) with the RequestId.
            var seq = entry.NextSequence();
            entry.Record(new RunEvent(seq, EventTypes.ReviewRequested, new
            {
                tree_hash = agentOutput.TreeHash,
                request_id = rie.Request.RequestId.ToString()
            }));
            break;

        case WorkflowOutputEvent woe:
            // Terminal state reached. Emit appropriate SSE event.
            EmitTerminalEvent(entry, woe);
            streamStore.Complete(runId);
            registry.Remove(runId);
            break;
    }
}
```

### PendingRequestStore (new class)

File: `apps/Scaffolder.Api/Runs/PendingRequestStore.cs`

```csharp
public sealed class PendingRequestStore
{
    private readonly ConcurrentDictionary<string, ExternalRequest> _pending = new();

    public void Set(string runId, ExternalRequest request);
    public ExternalRequest? Get(string runId);
    public void Remove(string runId);
}
```

---

## 5. Review Endpoint Mapping

### Current Contract

```
POST /api/runs/{id}/review
Body: { "approved": true|false }
```

### Decision: Server-side requestId resolution (NO frontend change)

The `PendingRequestStore` maps `runId -> ExternalRequest`. The review endpoint:

1. Validates auth, ownership, run status (unchanged).
2. Retrieves `ExternalRequest` from `PendingRequestStore.Get(runId)`.
3. Creates the response: `externalRequest.CreateResponse(new ReviewDecision(request.Approved))`.
4. Calls `streamingRun.SendResponseAsync(response)` to resume the workflow.
5. The workflow resumes into MergeExecutor (approve) or terminal-declined edge.

### Why NOT require frontend to echo requestId

- Simpler API contract (no breaking change).
- One pending request per run at a time (1:1 mapping is guaranteed by the workflow shape).
- The `PendingRequestStore` is populated atomically when `RequestInfoEvent` fires.

### Sequence Diagram

```
Frontend                 API Server                    MAF Workflow
   |                         |                             |
   |--POST /review---------->|                             |
   |                         |--PendingRequestStore.Get--->|
   |                         |--CreateResponse(decision)-->|
   |                         |--SendResponseAsync--------->|
   |                         |                             |--resumes-->
   |                         |<--WorkflowOutputEvent-------|
   |<--SSE: merge.completed--|                             |
```

### Breaking Change Assessment

**None.** The `review.requested` SSE event gains an optional `request_id` field (additive, non-breaking). The REST request body remains `{ "approved": bool }`. Frontend changes are optional (can display requestId for debugging but does not need to send it).

---

## 6. Restart Recovery

### Current (to be replaced)

`RunOrchestrator.RestartRecoveryAsync`:
- Fails stranded InProgress runs.
- Reverts Merging -> AwaitingReview.
- Re-creates in-memory stream entries.

### New: Checkpoint-Based Resume

File: `apps/Scaffolder.Api/Runs/WorkflowRestartService.cs`

```csharp
public sealed class WorkflowRestartService
{
    public async Task RecoverAsync(CancellationToken ct)
    {
        // 1. Fail stranded InProgress runs that have NO checkpoint.
        //    (Agent was mid-execution when process died; non-replayable.)
        var inProgress = await runStore.GetByStatusAsync(RunStatus.InProgress, ct);
        foreach (var run in inProgress)
        {
            if (!checkpointStore.HasCheckpoint(run.Id.ToString()))
            {
                await runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, now, ct);
                continue;
            }
            // InProgress WITH a checkpoint: impossible in normal flow
            // (status transitions to AwaitingReview before checkpoint is taken).
            // Defensive: fail it.
            await runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, now, ct);
        }

        // 2. Revert Merging -> AwaitingReview (unchanged logic).
        var merging = await runStore.GetByStatusAsync(RunStatus.Merging, ct);
        foreach (var run in merging)
            await runStore.RevertMergingAsync(run.Id, ct);

        // 3. Resume AwaitingReview runs from checkpoint.
        var awaiting = await runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct);
        foreach (var run in awaiting)
        {
            var checkpointInfo = await checkpointManager.GetLatestAsync(run.Id.ToString(), ct);
            if (checkpointInfo is null)
            {
                // No checkpoint found — cannot resume. Re-create stream entry only.
                var entry = streamStore.Create(run.Id.ToString(), run.SubmittingUser);
                entry.MarkAwaitingReview();
                continue;
            }

            var entry = streamStore.Create(run.Id.ToString(), run.SubmittingUser);
            entry.MarkAwaitingReview();

            var streamingRun = await InProcessExecution.ResumeStreamingAsync(
                workflow, checkpointInfo, checkpointManager, ct);

            registry.Register(run.Id.ToString(), streamingRun);

            // Re-populate PendingRequestStore from the resumed run's status.
            var status = await streamingRun.GetStatusAsync(ct);
            if (status.PendingRequests?.Count > 0)
            {
                pendingRequests.Set(run.Id.ToString(), status.PendingRequests[0]);
            }

            // Start watch loop (fire-and-forget on background task).
            _ = Task.Run(() => WatchWorkflowEventsAsync(run.Id.ToString(), streamingRun, entry, ct));
        }
    }
}
```

### Checkpoint Timing

Checkpoints are taken at super-step boundaries by the MAF runtime:
1. **After AgentTurnExecutor completes** (captures AgentTurnOutput including diff, tree hash).
2. **After RequestPort pauses** (captures the pending ExternalRequest).
3. **After MergeExecutor completes** (final checkpoint before terminal output).

On restart, `ResumeStreamingAsync` starts from checkpoint 2 — the workflow is already paused at the review gate. The agent turn is NOT re-executed.

---

## 7. No-Changes Skip

### Condition

```csharp
bool hasChanges = !string.IsNullOrEmpty(agentTurnOutput.Diff);
```

Equivalently: `agentTurnOutput.TreeHash != baseTreeHash` (where baseTreeHash is the originating branch tip's tree SHA). Using diff emptiness is simpler and already computed.

### Behavior

When `hasChanges == false`:
1. The conditional edge routes directly to `terminalNoOp` (a pass-through executor that returns a terminal output).
2. No `RequestPort` pause occurs — no checkpoint at the review gate.
3. SQLite status transitions: `InProgress -> Completed` (not AwaitingReview).
4. SSE event emitted: `run.completed` with `{ "result": "no_changes" }`.
5. Worktree is cleaned up immediately (no branch to preserve).

### New Status Value

Reuse existing `RunStatus.Completed` with a result string `"no_changes"`. No schema change needed. The `Completed` status already exists for backward compatibility.

---

## 8. SQLite Status Sync

Executors update SQLite status at boundary points. The workflow's internal state is NOT queryable by API consumers.

| Workflow Point | SQLite Transition | Responsible Code |
|----------------|-------------------|-----------------|
| Run created | `Pending` | `POST /api/runs` handler |
| `AgentTurnExecutor` starts | `Pending -> InProgress` | `AgentTurnExecutor.ExecuteAsync` entry |
| `AgentTurnExecutor` completes + no changes | `InProgress -> Completed` | Event watch loop on `WorkflowOutputEvent` |
| `AgentTurnExecutor` completes + has changes | `InProgress -> AwaitingReview` | Event watch loop on `RequestInfoEvent` |
| Review approved (before merge) | `AwaitingReview -> Merging` | `MergeExecutor` entry (CAS gate, unchanged) |
| Merge succeeds | `Merging -> Merged` | `MergeExecutor` |
| Merge conflicts | `Merging -> MergeFailed` | `MergeExecutor` |
| Review declined | `AwaitingReview -> Declined` | Event watch loop on `WorkflowOutputEvent` |
| Agent error | `InProgress -> Failed` | Event watch loop on exception/error output |

### Key Principle

Status updates are performed by the **event watch loop** (for transitions visible externally) or by **executors internally** (for CAS-guarded transitions like Merging). The watch loop is the single translation layer between workflow events and SQLite + SSE.

---

## 9. Migration / Cutover Plan

### Phase 1: Add new files (build stays green)

| Step | Action | Files |
|------|--------|-------|
| 1a | Add message types | `packages/Scaffolder.AgentRuntime/Workflow/WorkflowMessages.cs` |
| 1b | Add `AgentTurnExecutor` | `packages/Scaffolder.AgentRuntime/Workflow/AgentTurnExecutor.cs` |
| 1c | Add `MergeExecutor` | `packages/Scaffolder.AgentRuntime/Workflow/MergeExecutor.cs` |
| 1d | Add `RunWorkflowRegistry` | `apps/Scaffolder.Api/Runs/RunWorkflowRegistry.cs` |
| 1e | Add `PendingRequestStore` | `apps/Scaffolder.Api/Runs/PendingRequestStore.cs` |
| 1f | Add `WorkflowRestartService` | `apps/Scaffolder.Api/Runs/WorkflowRestartService.cs` |
| 1g | Add workflow builder factory | `apps/Scaffolder.Api/Runs/RunWorkflowFactory.cs` |

### Phase 2: Wire up behind feature flag (build stays green)

| Step | Action |
|------|--------|
| 2a | Register new services in DI (`Program.cs`). |
| 2b | Add `UseWorkflowHitl` bool config flag. |
| 2c | Branch in `StartRunAsync`: if flag is on, use workflow path; else existing path. |

### Phase 3: Switch default to workflow path

| Step | Action |
|------|--------|
| 3a | Set `UseWorkflowHitl = true` as default. |
| 3b | Update review endpoint to use `PendingRequestStore` + `SendResponseAsync` when workflow path is active. |
| 3c | Replace `RestartRecoveryAsync` call with `WorkflowRestartService.RecoverAsync`. |

### Phase 4: Remove hand-rolled code

| Step | Action |
|------|--------|
| 4a | Remove feature flag. |
| 4b | Delete: the manual state machine logic in `RunOrchestrator.RunTurnAsync` (lines 66-129). |
| 4c | Delete: `RunOrchestrator.RestartRecoveryAsync` (lines 138-168). |
| 4d | Simplify `RunOrchestrator` to a thin launcher that creates workflow input and calls `RunStreamingAsync`. |

---

## 10. Contract Changes

### SSE Events

| Event | Change | Breaking? |
|-------|--------|-----------|
| `review.requested` | Adds optional field `request_id: string` (GUID) | No (additive) |
| `run.completed` | Now also emitted for no-changes runs with `{ "result": "no_changes" }` | No (new terminal path) |

### REST Endpoints

| Endpoint | Change | Breaking? |
|----------|--------|-----------|
| `POST /api/runs/{id}/review` | No request body change. Response unchanged. | No |
| `GET /api/runs/{id}` | `status` may now return `"completed"` for no-changes runs (previously impossible — all runs went to `awaiting_review`) | No (status was already in enum) |

### Frontend Touch-Points

| File | Change |
|------|--------|
| `apps/web/src/components/ReviewPanel.tsx` | No change required (request body unchanged). |
| `apps/web/src/components/LifecycleEventCard.tsx` | Optional: render `run.completed` with `result=no_changes` distinctly. |
| `apps/web/src/api/client.ts` | No change required. |

### Domain

| File | Change |
|------|--------|
| `packages/Scaffolder.Domain/EventTypes.cs` | No new constants needed. `RunCompleted` already exists. |
| `packages/Scaffolder.Domain/RunStatus.cs` | No change. `Completed` already exists. |

---

## 11. Risks and Open Questions

| # | Risk/Question | Severity | Mitigation / Ask |
|---|---------------|----------|------------------|
| R1 | **Conditional-edge API uncertainty.** The exact method signature for conditional routing in `WorkflowBuilder` is inferred from the assembly metadata. If 1.9.0 does not support predicate-based edge selection natively, we may need a "router executor" that inspects `AgentTurnOutput.Diff` and emits to different output ports. | Medium | Spike: write a minimal console app that builds a conditional-edge workflow. Confirm before Phase 1. |
| R2 | **Checkpoint file accumulation.** `FileSystemJsonCheckpointStore` writes JSON files per super-step. Long-lived processes with many runs will accumulate files. | Low | Add a cleanup task: delete checkpoint directory for a runId once it reaches a terminal state (Merged/Declined/Failed/Completed/MergeFailed). Run on a timer or inline at terminal transition. |
| R3 | **MergeExecutor and the per-repo lock.** The merge still needs `RepositoryMergeLock` + CAS gate. Since `MergeExecutor` runs inside the workflow, it needs access to these services via DI. Verify that executor construction supports DI injection (or pass via closure). | Low | Executors are instantiated by our code and passed to the builder — DI injection is straightforward via constructor. |
| R4 | **Stale PendingRequestStore after restart.** If the process crashes between checkpoint-write and `PendingRequestStore` population, the resumed run re-populates from `GetStatusAsync().PendingRequests`. Verify that the resumed `StreamingRun` exposes pending requests immediately. | Medium | Integration test: kill process at review-pending, restart, verify `GetStatusAsync` returns the pending request. |
| R5 | **Content-safety flag gating.** Today, flagged runs do not serve diff. The no-changes-skip path must still respect safety flags — if the agent produced content-safety violations but no file changes, the run should still fail (not silently complete). | Low | `AgentTurnExecutor` checks safety flags before returning output. If flagged, emit `Failed` status regardless of diff. |

### Open Questions for Human

1. **Should no-changes runs clean up the worktree immediately, or preserve it for debugging?** Current design: immediate cleanup. If debugging is preferred, we keep it and add a TTL-based cleanup later.
2. **Should the `review.requested` event's `request_id` field be documented as stable API or internal?** Recommendation: document as informational/debug; do not require it on the review request body.

---

## 12. Test Plan

### Backend Tests (xUnit, in `tests/Scaffolder.Tests/`)

| Test | Validates |
|------|-----------|
| `WorkflowHitl_AgentProducesChanges_PausesAtReviewGate` | Workflow pauses, RequestInfoEvent fires, SSE emits review.requested |
| `WorkflowHitl_ApproveResumesAndMerges` | SendResponseAsync(Approved) -> MergeExecutor -> Merged status + SSE merge.completed |
| `WorkflowHitl_DeclineResumesTerminal` | SendResponseAsync(Declined) -> terminal -> Declined status + SSE review.declined |
| `WorkflowHitl_MergeConflict_TransitionsToMergeFailed` | Diverged branch -> MergeFailed status + SSE merge.failed |
| `WorkflowHitl_NoChanges_SkipsReview` | Empty diff -> Completed status + SSE run.completed(no_changes), no RequestPort pause |
| `WorkflowHitl_RestartResume_AtReviewGate` | Kill process at AwaitingReview, restart, verify workflow resumes at pending request without re-running agent |
| `WorkflowHitl_RestartResume_MergingReverts` | Merging state on restart -> reverts to AwaitingReview, resumes from checkpoint |
| `WorkflowHitl_AgentFailure_TransitionsToFailed` | Agent throws -> Failed status + SSE run.failed |
| `WorkflowHitl_Auth403_OnUnauthorizedReview` | Non-owner POST /review -> 403 (unchanged behavior) |
| `WorkflowHitl_ContentSafetyFlag_BlocksDiff` | Flagged run -> diff withheld from GET response (unchanged) |
| `WorkflowHitl_CASGuard_ConcurrentApprove` | Two concurrent approvals -> only one wins CAS gate |
| `WorkflowHitl_CheckpointCleanup_OnTerminal` | Terminal state -> checkpoint files deleted |

### Frontend Tests (Vitest, in `apps/web/`)

| Test | Validates |
|------|-----------|
| `ReviewPanel renders for awaiting_review` | Unchanged behavior |
| `LifecycleEventCard renders run.completed with no_changes` | New no-op path displayed correctly |
| `submitReview body unchanged` | No request_id in body (regression guard) |

---

## File Summary

| New File | Purpose |
|----------|---------|
| `packages/Scaffolder.AgentRuntime/Workflow/WorkflowMessages.cs` | All message records |
| `packages/Scaffolder.AgentRuntime/Workflow/AgentTurnExecutor.cs` | Wraps IAgentRunner + worktree commit/diff |
| `packages/Scaffolder.AgentRuntime/Workflow/MergeExecutor.cs` | Wraps merge logic (extracted from review endpoint) |
| `apps/Scaffolder.Api/Runs/RunWorkflowRegistry.cs` | Maps runId -> StreamingRun |
| `apps/Scaffolder.Api/Runs/PendingRequestStore.cs` | Maps runId -> ExternalRequest |
| `apps/Scaffolder.Api/Runs/WorkflowRestartService.cs` | Checkpoint-based restart recovery |
| `apps/Scaffolder.Api/Runs/RunWorkflowFactory.cs` | Builds the Workflow instance + DI wiring |

| Deleted (Phase 4) | Reason |
|--------------------|--------|
| `RunOrchestrator.RunTurnAsync` (lines 66-129) | Replaced by AgentTurnExecutor + workflow event loop |
| `RunOrchestrator.RestartRecoveryAsync` (lines 138-168) | Replaced by WorkflowRestartService |
| Manual state transitions in review endpoint | Replaced by SendResponseAsync -> workflow edges |

---
