# Import and sync skills into a project catalog

**Issue:** Draft (not opened)  
**Area:** Agent configuration

## User story

As a project owner, I want to import, upload, and sync reusable skills into a project catalog, so that project agents can be assigned proven guidance without recreating it by hand.

## Context / problem

Agentweaver needs a reliable way to acquire standards-compatible skills before they can be assigned to agents. Skills may already exist in other repositories, in local files, or in the repository connected to the project. Once acquired, they should be validated, tracked to their source, and kept usable by the sibling assignment story.

## Scope

### In
- importing a skill from a Git repository and optional repository path
- uploading a skill file, skill folder, or archived skill folder
- discovering skills already present in the connected project repository at recognized skill locations
- validating required skill metadata and bundled resources
- tracking source, provenance, and sync status for each catalog skill
- idempotent re-import and re-sync behavior for unchanged skills
- update behavior when a source skill changes
- making acquired skills available for assignment through [Assign skills to project agents](./assign-skills-to-agents.md)

### Out
- assigning skills to agents
- curating or embedding a starter skill catalog
- turning a project skill catalog into a public marketplace
- executing bundled scripts outside existing tool and approval boundaries
- rewriting upstream skill repositories

## Acceptance criteria

- [ ] Users can import a standards-compatible skill from a Git repository.
- [ ] Users can choose a skill within a repository when the repository contains more than one candidate.
- [ ] Users can upload a standards-compatible skill directly, including optional bundled resources.
- [ ] Agentweaver can discover skills already present in the project's connected repository at recognized Agent Skills and Copilot skill locations.
- [ ] Imported, uploaded, and synced skills expose name and description metadata for progressive disclosure.
- [ ] Malformed skills are rejected with clear feedback and are not added silently.
- [ ] Re-importing or re-syncing an unchanged skill does not create duplicates.
- [ ] When a source skill changes, users can see and apply the catalog update.
- [ ] Each catalog skill records whether it came from a repo import, file upload, or connected-repo sync.
- [ ] Acquired skills become available to assign through the sibling assignment capability.

## Notable edge cases

- If two sources provide the same skill name, the project has a deterministic way to keep, replace, or reject the duplicate.
- If a synced skill disappears from the connected repository, the catalog reports the missing source before disrupting assignments.
- If an uploaded archive contains multiple skills, the user can choose which valid skills to add.
- If an imported repository cannot be reached or authenticated, the existing catalog remains unchanged.
- If a skill's bundled resources are too large or unsafe, the skill is rejected with a clear reason.
