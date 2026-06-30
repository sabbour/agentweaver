# Automate pickup of Ready work

**Issue:** [#13](https://github.com/sabbour/agentweaver/issues/13)  
**Area:** Workflows & automation

## User story

As a project owner, I want Ready tasks to be claimed by background automation within safe limits, so that the team can keep moving through ranked work without manual run starts for every task.

## Context / problem

Ready is a commitment signal. The coordinator heartbeat turns eligible Ready tasks into coordinator work according to project settings and workflow triggers.

## Scope

### In
- pickup settings for Ready work
- heartbeat-driven claim of Ready tasks
- workflow override and default selection during pickup
- autopilot and auto-approval defaults within safety bounds
- visibility in Heartbeat and board states

### Out
- claiming Backlog tasks directly
- unbounded parallel pickup
- bypassing destructive-action safety requirements

## Acceptance criteria

- [ ] Users can configure bounded pickup behavior for a project.
- [ ] Ready tasks are claimed in rank order when heartbeat is enabled and capacity allows.
- [ ] Task workflow overrides are honored when valid.
- [ ] The project default is used as a safe fallback.
- [ ] Claimed tasks become accountable runs visible on the board.

## Notable edge cases

- Invalid or stale workflow overrides fall back safely.
- Settings reject values outside supported bounds.
- If heartbeat is disabled, Ready tasks remain queued and visible.
