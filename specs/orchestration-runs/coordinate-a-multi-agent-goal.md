# Coordinate a multi-agent goal

**Issue:** [#15](https://github.com/sabbour/agentweaver/issues/15)  
**Area:** Orchestration & runs

## User story

As a project owner, I want to turn a broad goal into confirmed intent, specialist subtasks, and one assembled result, so that large work can run in parallel without losing human accountability or coherence.

## Context / problem

Coordinator orchestration is Agentweaver’s broad-work experience. It creates a confirmation gate, decomposes work, dispatches child runs, assembles results, and presents one reviewable outcome.

## Scope

### In
- starting a coordinator run from a goal
- drafting and confirming an OutcomeSpec
- revising the OutcomeSpec before dispatch
- creating and tracking a dependency-aware work plan
- dispatching child runs and assembling one collective result

### Out
- dispatching child runs before confirmation
- per-child human merge approval
- unbounded ad hoc agent fan-out without a plan

## Acceptance criteria

- [ ] The coordinator drafts a readable outcome spec before dispatch.
- [ ] Users can confirm or request revisions to the spec.
- [ ] No child work starts until the spec is confirmed.
- [ ] Confirmed goals produce visible subtasks with agents, statuses, and dependencies.
- [ ] The coordinator assembles child outputs into one final review path.

## Notable edge cases

- Revision returns the run to the confirmation gate.
- If workflow selection fails, a safe default is used.
- Blocked or failed subtasks keep the coordinator state explicit instead of disappearing.
