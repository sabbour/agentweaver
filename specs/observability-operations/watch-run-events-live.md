# Watch run events live and replay history

**Issue:** [#27](https://github.com/sabbour/agentweaver/issues/27)  
**Area:** Observability & operations

## User story

As a operator or reviewer, I want to stream and replay ordered run events, so that agent work is transparent during execution and auditable after restarts or completion.

## Context / problem

A run is both state machine and story. Users need a live timeline while it happens and durable history when they reconnect or inspect later.

## Scope

### In
- live watch timeline
- agent messages, tool calls, lifecycle events, approvals, and results
- ordered replay after reconnect
- workflow and coordinator graph updates
- terminal history inspection

### Out
- debug-only internal traces
- client-invented event ordering
- hidden model output unrelated to a run

## Acceptance criteria

- [ ] Opening a run shows current state and begins streaming new events.
- [ ] Reconnecting resumes from durable history without losing ordering.
- [ ] Timeline items make tool calls, messages, approvals, and lifecycle changes understandable.
- [ ] Workflow and coordinator graphs reflect persisted state plus live updates.
- [ ] Completed runs remain replayable for audit and review.

## Notable edge cases

- Stream interruptions show reconnect/error states.
- Events are ordered by sequence rather than wall-clock assumptions.
- A run with no events or artifacts displays a useful empty state.
