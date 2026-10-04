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
- [ ] The runtime injects messages at safe boundaries without requiring the recipient to poll manually.
- [ ] The scheduling owner wakes eligible idle recipients within concurrency and turn limits.
- [ ] REST, MCP, native and operator UI offer equivalent send, reply, inspect and retry workflows.

## Notable edge cases

- Retired members, cancelled or completed runs, late replies, cross-project IDs and duplicate sends must not grant delivery authority.
- A crash before or after prompt presentation must not silently lose or duplicate the logical message.
- A message reference must retain the existing backlog/work-plan identity; the mailbox must not own task state.
