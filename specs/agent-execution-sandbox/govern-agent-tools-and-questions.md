# Govern agent tool use and questions

**Issue:** [#19](https://github.com/sabbour/agentweaver/issues/19)  
**Area:** Agent execution & sandbox

## User story

As a operator or reviewer, I want to approve sensitive agent actions and answer agent questions during a run, so that agents can make progress while humans retain control over risky or ambiguous actions.

## Context / problem

Agents need tools to inspect, edit, run commands, fetch URLs, and ask for help. Some actions require explicit user approval or answers before the run continues.

## Scope

### In
- pending tool and shell approvals
- approval and denial decisions
- agent questions and user answers
- per-run auto-approve for eligible low-risk tool calls
- approval indicators on board and run views

### Out
- auto-approving destructive actions below the safety floor
- answering questions after the run no longer awaits them
- granting approvals across unrelated runs

## Acceptance criteria

- [ ] Runs surface pending approvals and questions where users can respond.
- [ ] Approving a request allows the corresponding action to continue.
- [ ] Denying a request reports the denial to the agent and preserves the run record.
- [ ] Answers to agent questions resume the waiting run.
- [ ] Auto-approve settings apply only to eligible requests and remain visible.

## Notable edge cases

- Expired or stale requests cannot be approved as if current.
- Approval requests are scoped to the owning run.
- Denials and answers are recorded as part of the timeline.
