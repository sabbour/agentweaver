# Browse project and run workspaces read-only

**Issue:** [#6](https://github.com/sabbour/agentweaver/issues/6)  
**Area:** Projects & workspace

## User story

As a reviewer or project owner, I want to inspect repository files and active run worktrees from Agentweaver, so that I can understand context and artifacts without leaving the product or changing files accidentally.

## Context / problem

Users often need to inspect files before creating tasks, during review, or after a run. The workspace browser provides context without becoming an editing surface.

## Scope

### In
- project workspace browsing
- branch or worktree selection
- read-only file viewing
- importing selected specification content into backlog intake when supported

### Out
- editing files directly from the workspace browser
- review approval controls
- remote repository browsing unrelated to the project workspace

## Acceptance criteria

- [ ] Users can browse the project repository tree from the Workspace page.
- [ ] Users can switch among available refs or run worktrees.
- [ ] Selecting a readable file shows its content or preview without modifying it.
- [ ] Binary or oversized files are identified rather than rendered incorrectly.
- [ ] Workspace browsing is visually distinct from review and merge decisions.

## Notable edge cases

- Missing refs or inaccessible workspaces show recoverable empty/error states.
- Large files do not freeze the UI.
- Run worktrees disappear from active choices when no longer available.
