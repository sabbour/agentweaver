# Approve, request changes, or decline a run

**Issue:** [#18](https://github.com/sabbour/agentweaver/issues/18)  
**Area:** Review & merge

## User story

As a human reviewer, I want to choose whether a run should merge, revise, or stop, so that human intent controls the irreversible boundary between proposed work and repository history.

## Context / problem

The review gate is a real decision point. Approval authorizes merge of the reviewed content; change requests start another revision; decline ends the attempt.

## Scope

### In
- Commit and Merge approval
- feedback-bearing request changes
- decline/reject decision
- reviewer identity and one-time decision behavior
- content-bound approval semantics

### Out
- agent self-approval
- silent merge without review
- remote branch protection administration

## Acceptance criteria

- [ ] Approval only applies when the run is awaiting review.
- [ ] Approved content is checked against the reviewed candidate before merge.
- [ ] Request changes records feedback and returns the run to agent revision.
- [ ] Decline ends the run without changing the originating branch.
- [ ] Duplicate or stale review decisions do not produce multiple outcomes.

## Notable edge cases

- Merge conflicts or unsafe repository states become visible merge problems.
- A revised run returns to review with a fresh candidate.
- The producing agent cannot bypass the human review boundary.
