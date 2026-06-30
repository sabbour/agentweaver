# Manage agent roster and charters

**Issue:** [#8](https://github.com/sabbour/agentweaver/issues/8)  
**Area:** Squad casting

## User story

As a project owner, I want to inspect, add, retire, re-role, and update agents in the project roster, so that the squad can evolve while preserving accountability and history.

## Context / problem

Teams change as project needs change. Users need a readable roster with charters and safe lifecycle actions that do not erase past identity.

## Scope

### In
- Agents page roster
- agent detail drawer
- charter viewing and editing for project agents
- adding project members
- retiring and re-roling project agents
- syncing team file changes

### Out
- editing built-in system-agent charters
- deleting historical identity records
- running built-in support agents directly

## Acceptance criteria

- [ ] Roster cards show agent identity, role, status, and system/project distinction.
- [ ] Users can inspect an agent charter and recent history.
- [ ] Project agents can be added, retired, re-roled, and have editable charters.
- [ ] Retired agents remain visible in history-aware filters.
- [ ] Team file sync exposes pending team changes separately from unrelated work.

## Notable edge cases

- Built-in agents are read-only where appropriate.
- Retiring an agent does not free the name for a different identity.
- Invalid charter or role updates fail without corrupting the roster.
