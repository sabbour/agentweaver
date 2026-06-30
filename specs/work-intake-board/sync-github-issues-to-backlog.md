# Sync connected repository issues with the backlog

**Issue:** _to be assigned_  
**Area:** Work intake & board

## User story

As a project owner whose project is connected to a GitHub repository, I want open repository issues to appear as backlog work and my backlog items to be reflected back as repository issues, so that the team has one shared source of truth instead of re-entering and reconciling work by hand.

## Context / problem

When a project is connected to a repository, work often already lives as repository issues. Without synchronization, the backlog and the repository drift apart and owners maintain the same work twice. Keeping them reconciled lets Agentweaver orchestrate real, existing work and report progress where stakeholders already look.

## Scope

### In
- pulling open repository issues into the backlog for a connected project
- reflecting backlog items back to the connected repository as issues
- keeping title, description, and open/closed state reconciled over time
- periodic (heartbeat) synchronization for connected projects
- enabling, disabling, and bounding sync per project

### Out
- real-time or event-driven (webhook) synchronization — future phase
- conflict-resolution prompts for simultaneous edits on both sides
- synchronizing repositories that are not the project's connected repository
- replacing board state for unconnected projects

## Acceptance criteria

- [ ] Open issues from a connected repository appear as backlog items without manual import.
- [ ] Each synced item maps cleanly to its repository issue and back, with no duplicates on repeat sync.
- [ ] Backlog items created in Agentweaver for a connected project are reflected as repository issues.
- [ ] Title, description, and open/closed state stay reconciled across both sides over time.
- [ ] Synchronization runs periodically and recovers on the next cycle after a transient failure.
- [ ] Owners can enable, disable, and bound synchronization per project.

## Notable edge cases

- A closed or reopened repository issue is reflected as the corresponding backlog state change.
- Re-running synchronization never creates duplicate backlog items or duplicate issues.
- Synchronization only ever touches the project's own connected repository.
- A transient failure leaves both sides unchanged and is retried on the next cycle.

## Phase / future

- **Phase 1 (this story):** periodic (heartbeat) synchronization between repository issues and the backlog.
- **Future:** real-time, event-driven (webhook) synchronization is out of scope for this story.
