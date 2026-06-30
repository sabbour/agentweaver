# Monitor work on the project board

**Issue:** [#10](https://github.com/sabbour/agentweaver/issues/10)  
**Area:** Work intake & board

## User story

As a project owner, I want to see tasks and runs in one operational board, so that I can understand what is waiting, active, blocked, in review, or done at a glance.

## Context / problem

The board is the daily control room. It combines intake tasks with run cards so users can track work from idea through execution and cleanup.

## Scope

### In
- six-column board projection
- task cards for Backlog and Ready
- run cards for Problems, Human Review, Active, and Done
- run retry and archive actions
- collapsed older done history

### Out
- manual reassignment of run lifecycle stages
- full artifact review in board cards
- hidden workflow-state mutation from drag and drop

## Acceptance criteria

- [ ] The board shows Backlog, Ready, Problems, Human Review, Active, and Done in a consistent order.
- [ ] Task cards and run cards show the information needed to decide next action.
- [ ] Failed or merge-failed runs expose retry.
- [ ] Runs and tasks can be archived from active projections.
- [ ] Older completed items can be hidden or revealed without losing history.

## Notable edge cases

- Retry creates a new linked run rather than continuing the failed record.
- Empty columns remain understandable.
- Coordinator-owned columns cannot be manually reordered by users.
