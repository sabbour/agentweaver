# Send addressed messages to teammates

**Issue:** [#1406](https://github.com/sabbour/agentweaver/issues/1406)

**Area:** Orchestration & runs

## User story

As a working agent, I want to send a question or finding to one teammate and track its receipt and correlated reply, without turning it into a decision or changing task ownership.

## Context / problem

The decision inbox stores shared proposals, not recipient-specific delivery or acknowledgment. Steering is operator-to-coordinator direction, and backlog tasks represent work rather than conversation.

## Scope

### In
- Persisted, authenticated, one-recipient messages linked to project and run identities.
- Stable thread, reply, optional task/finding reference, and idempotency identities.
- Leased and fenced delivery, receipt acknowledgment, expiry and failure diagnostics.
- Delivery at recipient turn boundaries and bounded, authorized idle wake through the existing scheduler.
- Equivalent REST, MCP, native tool and operator inspection surfaces.

### Out
- Broadcast, task ownership changes, automatic decision approval, A2A peer chat or a second scheduler.

## Acceptance criteria

- [x] Sends persist before acceptance and replay by sender/idempotency key without creating another message.
- [x] Messages record project, sender, recipient, source and target run, thread, reply, reference and receipt state.
- [x] Claims have leases and fences; expired claims can be reclaimed.
- [x] Recipient acknowledgment records receipt without completing a task or approving a decision.
- [x] The runtime injects messages at safe boundaries without requiring the recipient to poll manually on an active worker turn.
- [ ] The scheduling owner wakes eligible idle recipients within concurrency and turn limits.
- [x] REST, MCP, native and operator UI offer send, reply, inspect and retry workflows for their authorized callers.

## Notable edge cases

- Retired members, cancelled or completed runs, late replies, cross-project IDs and duplicate sends must not grant delivery authority.
- A crash before or after prompt presentation must not silently lose or duplicate the logical message.
- A message reference must retain the existing backlog/work-plan identity; the mailbox must not own task state.
- Worker-turn replay retains the same message ID; physical presentation can recur if a
  worker crashes after showing the prompt but before committing the receipt. The
  runtime does not claim exactly-once model observation.
- Idle wake remains blocked on a reusable recovery-owner hook. The heartbeat's
  `CoordinatorPickupService` can only reserve a **new coordinator run** for a Ready
  backlog task; it cannot wake the existing recipient run. The
  `RunOrchestrator.RestartInterruptedChildRunAsync` path replays an interrupted
  child's task and is not authorized to start an extra turn for an idle, live
  recipient. `RunWorkflowRegistry` holds only process-local active workflows.
  The scheduling/recovery owner needs a durable, project- and recipient-run-scoped
  `RequestWake`/`ClaimWake`/`CompleteWake` contract and an execution entry point
  that resumes that **same** eligible run for a safe extra turn. Admission must
  persist before acceptance; enforce active membership, run/project ownership,
  an explicit concurrent-wake cap and turn budget; fence and lease the claim;
  and persist terminal failure/retry state. It must recover a crash before or
  after claim/presentation without creating another logical delivery. Until
  that owner interface exists, an accepted message can wait for a natural turn
  boundary, but no endpoint may report that it scheduled an idle wake.
