# Create and open Agentweaver projects

**Issue:** [#4](https://github.com/sabbour/agentweaver/issues/4)  
**Area:** Projects & workspace

## User story

As a project owner, I want to create blank or GitHub-backed projects and open them from a gallery, so that I can anchor Agentweaver work to the repository I care about quickly and safely.

## Context / problem

Projects are the front door to Agentweaver. A project names the work, links it to a repository workspace, and carries defaults used by runs, teams, workflows, and memory.

## Scope

### In
- project gallery
- blank project creation
- GitHub-backed project creation
- project availability display
- project switching by stable identity

### Out
- remote pull request creation
- destroying repository files from the gallery
- deep runtime configuration

## Acceptance criteria

- [ ] Users can see existing projects with names, source information, workspace location, and availability.
- [ ] Users can create a blank project when required fields are provided.
- [ ] Users can create a project from a GitHub repository when repository information is provided.
- [ ] New projects open into the project experience with their identity and workspace preserved.
- [ ] Unavailable workspaces remain visible so users can recover them.

## Notable edge cases

- Empty gallery offers the same creation actions as a populated gallery.
- GitHub repository lists may be unavailable while manual owner/repo entry remains possible.
- Creating into an invalid or occupied workspace fails with a clear error.
