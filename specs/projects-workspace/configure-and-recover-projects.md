# Configure and recover projects

**Issue:** [#5](https://github.com/sabbour/agentweaver/issues/5)  
**Area:** Projects & workspace

## User story

As a project owner, I want to rename, relink, configure, and delete project records, so that Agentweaver can keep working when repositories move or defaults need to change.

## Context / problem

A project outlives a single run. Users need to adjust the project name, model defaults, review defaults, and workspace location without losing the project identity.

## Scope

### In
- renaming projects
- relinking unavailable workspaces
- updating project model/provider defaults
- review and sandbox settings visible from project settings
- deleting project records from Agentweaver

### Out
- deleting remote repositories
- editing hidden deployment secrets
- changing historical run authorship

## Acceptance criteria

- [ ] Renaming changes the display name while preserving the same project identity.
- [ ] Relinking points an unavailable project at a usable workspace without creating a new project.
- [ ] Provider and model defaults can be updated for future work.
- [ ] Project settings separate normal configuration from destructive actions.
- [ ] Deleting a project removes it from active Agentweaver management and cancels active project work when applicable.

## Notable edge cases

- Relink failures leave the previous project record intact.
- Unavailable projects explain the recovery path.
- Danger-zone actions require deliberate user intent.
