# Steer and recover active orchestrations

**Issue:** [#16](https://github.com/sabbour/agentweaver/issues/16)  
**Area:** Orchestration & runs

## User story

As a project owner, I want to intervene in an active coordinator run when scope, priority, or failures change, so that long-running multi-agent work can be corrected without starting over blindly.

## Context / problem

Orchestrations can uncover ambiguity, blockers, or changing priorities. Users need controlled intervention points that preserve the audit trail.

## Scope

### In
- steering messages to the coordinator
- stopping or course-correcting active orchestration
- observing steering status in the topology/timeline
- retrying failed runs as linked attempts
- recovering from blocked or failed frontier states when supported

### Out
- editing completed historical records
- silently rewriting child outputs without trace
- manual direct mutation of dependency state

## Acceptance criteria

- [ ] Users can send a course-correction to an active coordinator run.
- [ ] Steering actions are visible in the run history.
- [ ] The coordinator incorporates valid steering into subsequent planning or dispatch decisions.
- [ ] Failed runs can be retried as new linked records.
- [ ] Stopped or declined work reaches a clear terminal state.

## Notable edge cases

- Steering after a terminal state is rejected or reported as no longer applicable.
- Repeated or conflicting steering remains auditable.
- Retry does not erase the failed original run.
