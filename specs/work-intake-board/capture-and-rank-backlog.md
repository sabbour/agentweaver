# Capture and rank backlog work

**Issue:** [#9](https://github.com/sabbour/agentweaver/issues/9)  
**Area:** Work intake & board

## User story

As a project owner, I want to capture, edit, rank, and promote tasks before execution, so that work can be shaped and ordered before the coordinator spends time or credits on it.

## Context / problem

Agentweaver separates intake from execution. Backlog and Ready columns let users decide what is merely being considered and what is committed enough to be claimed.

## Scope

### In
- capturing tasks into Backlog or Ready
- editing titles and descriptions before claim
- dragging and reordering Backlog and Ready tasks
- bulk promotion to Ready
- archiving unneeded tasks

### Out
- manually moving active runs across workflow stages
- editing claimed task content as if it were still intake
- issue tracker replacement beyond Agentweaver board state

## Acceptance criteria

- [ ] Users can add valid tasks to Backlog or Ready.
- [ ] Users can edit and reorder unclaimed tasks.
- [ ] Users can move tasks between Backlog and Ready while preserving order.
- [ ] Users can promote all backlog items safely.
- [ ] Archived tasks leave active board projections.

## Notable edge cases

- Empty titles are rejected.
- Claimed tasks cannot be deleted as ordinary backlog items.
- Dragging intake cards into execution-owned columns is rejected with an explanatory message.
